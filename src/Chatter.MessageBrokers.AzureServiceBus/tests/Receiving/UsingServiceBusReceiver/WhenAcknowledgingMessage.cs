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
    // Characterization tests pinning the ack/nack/deadletter guard branches that resolve BEFORE
    // touching InnerReceiver (which would open a live connection):
    //   1. When the effective ServiceBusReceiveMode != PeekLock (i.e. TransactionMode.None =>
    //      ReceiveAndDelete) every ack/nack/deadletter reports NotRequired without inspecting the
    //      container: Azure Service Bus removed the delivery on receipt, so no settlement is owed.
    //   2. When in PeekLock mode but no ServiceBusReceivedMessage is in the context container, every
    //      ack/nack/deadletter reports Failed and settles nothing. The lock exists but cannot be
    //      released, and reporting NotRequired would claim nothing was owed when a settlement was.
    //   3. A fault the infrastructure RAISES still propagates rather than being reported as Failed, so
    //      Recovery keeps its chance to retry a transient broker fault.
    // The InitializeAsync receive-mode flip is observed indirectly: initializing with a non-None
    // TransactionMode flips the receiver into PeekLock, so guard branch (2) becomes reachable.
    public class WhenAcknowledgingMessage : Testing.Core.Context
    {
        private const string _connectionString =
            "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=key;SharedAccessKey=secret";

        // The shared ServiceBusClient the receiver consumes from DI in production. A placeholder SAS
        // connection string opens no connection (the SDK connects lazily), so it is a valid stand-in here.
        private static ServiceBusClient CreateClient() => new ServiceBusClient(_connectionString);

        private static MessageBrokerContext CreateEmptyContext()
            => new MessageBrokerContext("message-id", new byte[] { 1 }, null, "receiver", CancellationToken.None, new JsonBodyConverter());

        // MessageBrokerOptions.TransactionMode is internal-set in the core assembly (no IVT to the
        // test assembly), so it is left at its default (TransactionMode.None => ReceiveAndDelete).
        private static ServiceBusReceiver CreateSut()
        {
            var serviceBusOptions = new ServiceBusOptions
            {
                ConnectionString = _connectionString,
            };
            var messageBrokerOptions = new MessageBrokerOptions();
            var logger = new Mock<ILogger<ServiceBusReceiver>>();
            var bodyConverterFactory = new Mock<IBodyConverterFactory>();

            return new ServiceBusReceiver(CreateClient(), serviceBusOptions, messageBrokerOptions, logger.Object, bodyConverterFactory.Object);
        }

        private static async Task<ServiceBusReceiver> CreatePeekLockSutAsync()
        {
            // Construct in None (ReceiveAndDelete), then flip to PeekLock via InitializeAsync.
            var sut = CreateSut();
            await sut.InitializeAsync(new ReceiverOptions { MessageReceiverPath = "receiver", TransactionMode = TransactionMode.ReceiveOnly }, CancellationToken.None);
            return sut;
        }

        // Drives the settlement paths through an injected IServiceBusMessageReceiver double so what the
        // receiver hands the infrastructure — and what the infrastructure raises back — is observable.
        private static ServiceBusReceiver CreateSutOver(IServiceBusMessageReceiver innerReceiver)
        {
            var serviceBusOptions = new ServiceBusOptions
            {
                ConnectionString = _connectionString,
            };
            var logger = new Mock<ILogger<ServiceBusReceiver>>();
            var factory = new Mock<IBodyConverterFactory>();
            factory.Setup(f => f.CreateBodyConverter(It.IsAny<string>())).Returns(new JsonBodyConverter());
            var inboundFactory = new InboundBrokeredMessageFactory(factory.Object, Mock.Of<ILogger>());
            return new ServiceBusReceiver(CreateClient(), serviceBusOptions, new MessageBrokerOptions(), logger.Object, inboundFactory, (_, __) => innerReceiver);
        }

        private static async Task<(ServiceBusReceiver sut, MessageBrokerContext context, TransactionContext transactionContext)> ReceivedPeekLockMessageAsync(InMemoryServiceBusMessageReceiver inMemory)
        {
            inMemory.EnqueueMessage(ServiceBusMessageFactory.ReceivedMessage());
            var sut = CreateSutOver(inMemory);
            await sut.InitializeAsync(new ReceiverOptions { MessageReceiverPath = "receiver", TransactionMode = TransactionMode.ReceiveOnly }, CancellationToken.None);
            var transactionContext = new TransactionContext("receiver");
            var context = await sut.ReceiveMessageAsync(transactionContext, CancellationToken.None);
            return (sut, context, transactionContext);
        }

        // Delivers one received message and then raises the supplied fault from every settlement call.
        // Moq cannot stand in here: the DynamicProxyGenAssembly2 InternalsVisibleTo grant on the adapter
        // assembly is strong-name-keyed, so a Castle proxy over this internal port fails to load.
        private class SettlementFaultingServiceBusMessageReceiver : IServiceBusMessageReceiver
        {
            private readonly Exception _settlementFault;
            private bool _delivered;

            public SettlementFaultingServiceBusMessageReceiver(Exception settlementFault)
                => _settlementFault = settlementFault;

            public bool IsClosedOrClosing => false;

            public Task<ServiceBusReceivedMessage> ReceiveAsync(CancellationToken cancellationToken)
            {
                if (_delivered)
                {
                    return Task.FromResult<ServiceBusReceivedMessage>(null);
                }

                _delivered = true;
                return Task.FromResult(ServiceBusMessageFactory.ReceivedMessage());
            }

            public Task CompleteAsync(ServiceBusReceivedMessage message) => throw _settlementFault;

            public Task AbandonAsync(ServiceBusReceivedMessage message, IDictionary<string, object> propertiesToModify) => throw _settlementFault;

            public Task DeadLetterAsync(ServiceBusReceivedMessage message, string deadLetterReason, string deadLetterErrorDescription) => throw _settlementFault;

            public Task CloseAsync() => Task.CompletedTask;
        }

        private const int MaxDeadLetterErrorDescriptionLength = 4096;

        private const string AckOperation = "ack";
        private const string NackOperation = "nack";
        private const string DeadletterOperation = "deadletter";

        private static Func<Task<SettlementResult>> CreateSettlementCall(ServiceBusReceiver sut, string operation, MessageBrokerContext context, TransactionContext transactionContext)
            => operation switch
            {
                AckOperation => () => sut.AckMessageAsync(context, transactionContext, CancellationToken.None),
                NackOperation => () => sut.NackMessageAsync(context, transactionContext, CancellationToken.None),
                DeadletterOperation => () => sut.DeadletterMessageAsync(context, transactionContext, "reason", "description", CancellationToken.None),
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown settlement operation."),
            };

        [Theory]
        [InlineData(AckOperation)]
        [InlineData(NackOperation)]
        [InlineData(DeadletterOperation)]
        public async Task MustReportNotRequiredWhenNotPeekLock(string operation)
        {
            var sut = CreateSut();

            var result = await CreateSettlementCall(sut, operation, CreateEmptyContext(), new TransactionContext("receiver"))();

            result.Outcome.Should().Be(SettlementOutcome.NotRequired);
            result.Reason.Should().NotBeNullOrWhiteSpace();
        }

        [Theory]
        [InlineData(AckOperation)]
        [InlineData(NackOperation)]
        [InlineData(DeadletterOperation)]
        public async Task MustReportFailedNamingTheUnsettledDeliveryWhenPeekLockButNoMessageInContext(string operation)
        {
            var sut = await CreatePeekLockSutAsync();

            var result = await CreateSettlementCall(sut, operation, CreateEmptyContext(), new TransactionContext("receiver"))();

            result.Outcome.Should().Be(SettlementOutcome.Failed);
            result.Reason.Should().Contain(nameof(ServiceBusReceivedMessage));
            result.Reason.Should().Contain("was not settled");
        }

        [Theory]
        [InlineData(AckOperation)]
        [InlineData(NackOperation)]
        [InlineData(DeadletterOperation)]
        public async Task MustNotSettleAnythingWhenPeekLockButNoMessageInContext(string operation)
        {
            var inMemory = new InMemoryServiceBusMessageReceiver();
            var sut = CreateSutOver(inMemory);
            await sut.InitializeAsync(new ReceiverOptions { MessageReceiverPath = "receiver", TransactionMode = TransactionMode.ReceiveOnly }, CancellationToken.None);

            var result = await CreateSettlementCall(sut, operation, CreateEmptyContext(), new TransactionContext("receiver"))();

            result.Outcome.Should().Be(SettlementOutcome.Failed);
            inMemory.CompletedMessages.Should().BeEmpty();
            inMemory.AbandonedMessages.Should().BeEmpty();
            inMemory.DeadLetteredMessages.Should().BeEmpty();
        }

        // The boundary between a RETURNED Failed and a THROWN fault: Recovery wraps the settlement call, so a
        // thrown fault is retried and a returned outcome is not. Only the deterministic missing-delivery case
        // is answered as Failed; a fault the infrastructure raises must keep leaving the receiver as it is.
        [Theory]
        [InlineData(AckOperation)]
        [InlineData(NackOperation)]
        [InlineData(DeadletterOperation)]
        public async Task MustPropagateSettlementFaultRaisedByTheInfrastructure(string operation)
        {
            var faulting = new SettlementFaultingServiceBusMessageReceiver(
                new ServiceBusException(isTransient: true, "the broker was unreachable", null, ServiceBusFailureReason.ServiceCommunicationProblem));
            var sut = CreateSutOver(faulting);
            await sut.InitializeAsync(new ReceiverOptions { MessageReceiverPath = "receiver", TransactionMode = TransactionMode.ReceiveOnly }, CancellationToken.None);
            var transactionContext = new TransactionContext("receiver");
            var context = await sut.ReceiveMessageAsync(transactionContext, CancellationToken.None);

            var settle = CreateSettlementCall(sut, operation, context, transactionContext);

            await settle.Should().ThrowAsync<ServiceBusException>();
        }

        [Fact]
        public async Task MustForwardSubLimitDeadletterDescriptionUnchanged()
        {
            var inMemory = new InMemoryServiceBusMessageReceiver();
            var (sut, context, transactionContext) = await ReceivedPeekLockMessageAsync(inMemory);
            var description = new string('a', MaxDeadLetterErrorDescriptionLength - 1);

            var result = await sut.DeadletterMessageAsync(context, transactionContext, "reason", description, CancellationToken.None);

            result.Outcome.Should().Be(SettlementOutcome.Settled);
            inMemory.DeadLetteredMessages.Should().ContainSingle()
                .Which.description.Should().Be(description);
        }

        [Fact]
        public async Task MustForwardExactlyLimitDeadletterDescriptionUnchanged()
        {
            var inMemory = new InMemoryServiceBusMessageReceiver();
            var (sut, context, transactionContext) = await ReceivedPeekLockMessageAsync(inMemory);
            var description = new string('a', MaxDeadLetterErrorDescriptionLength);

            var result = await sut.DeadletterMessageAsync(context, transactionContext, "reason", description, CancellationToken.None);

            result.Outcome.Should().Be(SettlementOutcome.Settled);
            inMemory.DeadLetteredMessages.Should().ContainSingle()
                .Which.description.Should().Be(description);
        }

        [Fact]
        public async Task MustCapOverLimitDeadletterDescriptionWithoutThrowing()
        {
            var inMemory = new InMemoryServiceBusMessageReceiver();
            var (sut, context, transactionContext) = await ReceivedPeekLockMessageAsync(inMemory);
            var description = new string('a', MaxDeadLetterErrorDescriptionLength + 100);

            var result = await sut.DeadletterMessageAsync(context, transactionContext, "reason", description, CancellationToken.None);

            result.Outcome.Should().Be(SettlementOutcome.Settled);
            var captured = inMemory.DeadLetteredMessages.Should().ContainSingle().Subject.description;
            captured.Length.Should().BeLessThanOrEqualTo(MaxDeadLetterErrorDescriptionLength);
            captured.Should().StartWith(new string('a', 10));
        }
    }
}
