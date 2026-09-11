using Chatter.MessageBrokers.AzureServiceBus.Options;
using Chatter.MessageBrokers.AzureServiceBus.Receiving;
using Chatter.MessageBrokers.AzureServiceBus.Tests.Receiving;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using FluentAssertions;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
// Disambiguate the local ServiceBusReceiver (system under test) from the SDK type of the same name
// pulled in by `using Azure.Messaging.ServiceBus;` (CS0104).
using ServiceBusReceiver = Chatter.MessageBrokers.AzureServiceBus.Receiving.ServiceBusReceiver;
using ServiceBusClient = Azure.Messaging.ServiceBus.ServiceBusClient;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Receiving.UsingServiceBusReceiver
{
    // Pins ServiceBusReceiver's IDeliveryReleaseSignal implementation: the core worker's finally tells the
    // infrastructure a delivery is finished with, and a session-mode receiver forwards that to its inner session
    // port so the multiplexer can free the session slot the delivery occupied (ADR-0014).
    //
    // The release hook reads the inner receiver FIELD, never the lazily-constructing InnerReceiver property:
    // during teardown the property would build a brand-new receiver — opening a connection and, in session mode,
    // accepting sessions — just to tell it about a delivery it never handled.
    public class WhenReleasingSessionDeliveries : Testing.Core.Context
    {
        private const string _connectionString =
            "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=key;SharedAccessKey=secret";

        // The shared ServiceBusClient the receiver consumes from DI in production. A placeholder SAS
        // connection string opens no connection (the SDK connects lazily), so it is a valid stand-in here.
        private static ServiceBusClient CreateClient() => new ServiceBusClient(_connectionString);

        private static IBodyConverterFactory JsonFactory()
        {
            var factory = new Mock<IBodyConverterFactory>();
            factory.Setup(f => f.CreateBodyConverter(It.IsAny<string>())).Returns(new JsonBodyConverter());
            return factory.Object;
        }

        private static InboundBrokeredMessageFactory CreateInboundFactory()
            => new InboundBrokeredMessageFactory(JsonFactory(), Mock.Of<ILogger>());

        private static ServiceBusReceiver CreateSut(Func<ReceiverOptions, ServiceBusReceiveMode, IServiceBusMessageReceiver> receiverFactory)
        {
            var serviceBusOptions = new ServiceBusOptions { ConnectionString = _connectionString };
            var logger = new Mock<ILogger<ServiceBusReceiver>>();
            return new ServiceBusReceiver(CreateClient(), serviceBusOptions, new MessageBrokerOptions(), logger.Object, CreateInboundFactory(), receiverFactory);
        }

        private static async Task<ServiceBusReceiver> InitializedSutOverAsync(IServiceBusMessageReceiver innerReceiver)
        {
            var sut = CreateSut((_, __) => innerReceiver);
            await sut.InitializeAsync(new ReceiverOptions { MessageReceiverPath = "receiver", TransactionMode = TransactionMode.ReceiveOnly }, CancellationToken.None);
            return sut;
        }

        // A message broker context carrying no ServiceBusReceivedMessage at all — the shape a context has when
        // something other than this receiver produced it.
        private static MessageBrokerContext CreateEmptyContext()
            => new MessageBrokerContext("message-id", new byte[] { 1 }, null, "receiver", CancellationToken.None, new JsonBodyConverter());

        [Fact]
        public async Task MustForwardTheReleasedDeliveryToTheSessionReceiver()
        {
            var message = ServiceBusMessageFactory.ReceivedMessage(sessionId: "session-a");
            var sessionReceiver = new RecordingSessionMessageReceiver(message);
            var sut = await InitializedSutOverAsync(sessionReceiver);
            var context = await sut.ReceiveMessageAsync(new TransactionContext("receiver"), CancellationToken.None);

            sut.DeliveryReleased(context);

            // BY REFERENCE: the multiplexer keys its session slots on the received message object, so an equal
            // copy would free nothing.
            sessionReceiver.ReleasedDeliveries.Should().ContainSingle().Which.Should().BeSameAs(message);
        }

        [Fact]
        public async Task MustForwardNothingWhenTheContextCarriesNoReceivedMessage()
        {
            var sessionReceiver = new RecordingSessionMessageReceiver(deliverable: null);
            var sut = await InitializedSutOverAsync(sessionReceiver);
            // Resolves the inner receiver without delivering anything, so the field is set and the absence of a
            // received message is the only reason nothing is forwarded.
            await sut.ReceiveMessageAsync(new TransactionContext("receiver"), CancellationToken.None);

            Action act = () => sut.DeliveryReleased(CreateEmptyContext());

            act.Should().NotThrow();
            sessionReceiver.ReleasedDeliveries.Should().BeEmpty();
        }

        [Fact]
        public async Task MustIgnoreTheReleasedDeliveryWhenTheInnerReceiverIsNotSessionMode()
        {
            var inMemory = new InMemoryServiceBusMessageReceiver();
            inMemory.EnqueueMessage(ServiceBusMessageFactory.ReceivedMessage());
            var sut = await InitializedSutOverAsync(inMemory);
            var context = await sut.ReceiveMessageAsync(new TransactionContext("receiver"), CancellationToken.None);

            Action act = () => sut.DeliveryReleased(context);

            act.Should().NotThrow();
        }

        [Fact]
        public async Task MustNotBuildAnInnerReceiverToReleaseADeliveryItNeverHandled()
        {
            var message = ServiceBusMessageFactory.ReceivedMessage(sessionId: "session-a");
            var neverHandledContext = CreateInboundFactory().CreateContext(message, "receiver", CancellationToken.None);
            var receiverFactoryInvocations = 0;
            var sut = CreateSut((_, __) =>
            {
                receiverFactoryInvocations++;
                return new RecordingSessionMessageReceiver(message);
            });
            await sut.InitializeAsync(new ReceiverOptions { MessageReceiverPath = "receiver", TransactionMode = TransactionMode.ReceiveOnly }, CancellationToken.None);

            Action act = () => sut.DeliveryReleased(neverHandledContext);

            act.Should().NotThrow();
            receiverFactoryInvocations.Should().Be(0);
        }

        [Fact]
        public async Task MustFreeTheMultiplexedSessionSlotWhenTheDeliveryIsReleased()
        {
            // End to end over the production composition: the receiver hands the release to the multiplexer, which
            // frees the child that delivered the message, so the child is armed again on the next receive. Without
            // the release the child stays busy and is never re-armed.
            var child = new InMemorySessionMessageReceiver();
            var multiplexer = new SessionReceiverMultiplexer(1, () => child, "receiver", Mock.Of<ILogger>());
            var sut = await InitializedSutOverAsync(multiplexer);
            using var receiveSource = new CancellationTokenSource();

            var firstReceive = sut.ReceiveMessageAsync(new TransactionContext("receiver"), receiveSource.Token);
            await child.WaitForReceiveCountAsync(1, TimeSpan.FromSeconds(10));
            child.YieldMessage(ServiceBusMessageFactory.ReceivedMessage(sessionId: "session-a"));
            var context = await firstReceive;

            sut.DeliveryReleased(context);

            var secondReceive = sut.ReceiveMessageAsync(new TransactionContext("receiver"), receiveSource.Token);
            await child.WaitForReceiveCountAsync(2, TimeSpan.FromSeconds(10));

            receiveSource.Cancel();
            (await secondReceive).Should().BeNull();
            await sut.StopReceiver();
        }

        // Records the delivery releases the receiver forwards to the session port and delivers at most one
        // message. Moq cannot stand in here: the DynamicProxyGenAssembly2 InternalsVisibleTo grant on the adapter
        // assembly is strong-name-keyed, so a Castle proxy over this internal port fails to load.
        private sealed class RecordingSessionMessageReceiver : IServiceBusSessionMessageReceiver
        {
            private readonly ServiceBusReceivedMessage _deliverable;
            private bool _delivered;

            public RecordingSessionMessageReceiver(ServiceBusReceivedMessage deliverable) => _deliverable = deliverable;

            public List<ServiceBusReceivedMessage> ReleasedDeliveries { get; } = new List<ServiceBusReceivedMessage>();

            public bool IsClosedOrClosing => false;

            // ServiceBusSessionReceiver is sealed with no accessible constructor and no ServiceBusModelFactory
            // entry point, so a held SDK session receiver cannot be faked; the null answer is the only one
            // available here.
            public ServiceBusSessionReceiver SessionReceiverFor(ServiceBusReceivedMessage message) => null;

            public void DeliveryReleased(ServiceBusReceivedMessage message) => ReleasedDeliveries.Add(message);

            public Task<ServiceBusReceivedMessage> ReceiveAsync(CancellationToken cancellationToken)
            {
                if (_delivered)
                {
                    return Task.FromResult<ServiceBusReceivedMessage>(null);
                }

                _delivered = true;
                return Task.FromResult(_deliverable);
            }

            public Task CompleteAsync(ServiceBusReceivedMessage message) => Task.CompletedTask;

            public Task AbandonAsync(ServiceBusReceivedMessage message, IDictionary<string, object> propertiesToModify) => Task.CompletedTask;

            public Task DeadLetterAsync(ServiceBusReceivedMessage message, string deadLetterReason, string deadLetterErrorDescription) => Task.CompletedTask;

            public Task CloseAsync() => Task.CompletedTask;
        }
    }
}
