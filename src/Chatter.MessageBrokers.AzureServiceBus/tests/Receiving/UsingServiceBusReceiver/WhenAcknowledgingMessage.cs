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
using System.Threading;
using System.Threading.Tasks;
using Xunit;
// Disambiguate the local ServiceBusReceiver (system under test) from the SDK type of the same name
// pulled in by `using Azure.Messaging.ServiceBus;` (CS0104).
using ServiceBusReceiver = Chatter.MessageBrokers.AzureServiceBus.Receiving.ServiceBusReceiver;
using ServiceBusClient = Azure.Messaging.ServiceBus.ServiceBusClient;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Receiving.UsingServiceBusReceiver
{
    // Characterization tests pinning the ack/nack/deadletter guard branches that return BEFORE
    // touching InnerReceiver (which would open a live connection):
    //   1. When the effective ServiceBusReceiveMode != PeekLock (i.e. TransactionMode.None =>
    //      ReceiveAndDelete) every ack/nack/deadletter returns false without inspecting the container.
    //   2. When in PeekLock mode but no ServiceBusReceivedMessage is in the context container, every
    //      ack/nack/deadletter returns false and logs a warning.
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

        // Drives the deadletter path through the in-memory IServiceBusMessageReceiver double so the
        // capped description handed to DeadLetterAsync is captured via DeadLetteredMessages.
        private static ServiceBusReceiver CreateInMemorySut(InMemoryServiceBusMessageReceiver inMemory)
        {
            var serviceBusOptions = new ServiceBusOptions
            {
                ConnectionString = _connectionString,
            };
            var logger = new Mock<ILogger<ServiceBusReceiver>>();
            var factory = new Mock<IBodyConverterFactory>();
            factory.Setup(f => f.CreateBodyConverter(It.IsAny<string>())).Returns(new JsonBodyConverter());
            var inboundFactory = new InboundBrokeredMessageFactory(factory.Object, Mock.Of<ILogger>());
            return new ServiceBusReceiver(CreateClient(), serviceBusOptions, new MessageBrokerOptions(), logger.Object, inboundFactory, (_, __) => inMemory);
        }

        private static async Task<(ServiceBusReceiver sut, MessageBrokerContext context, TransactionContext transactionContext)> ReceivedPeekLockMessageAsync(InMemoryServiceBusMessageReceiver inMemory)
        {
            inMemory.EnqueueMessage(ServiceBusMessageFactory.ReceivedMessage());
            var sut = CreateInMemorySut(inMemory);
            await sut.InitializeAsync(new ReceiverOptions { MessageReceiverPath = "receiver", TransactionMode = TransactionMode.ReceiveOnly }, CancellationToken.None);
            var transactionContext = new TransactionContext("receiver");
            var context = await sut.ReceiveMessageAsync(transactionContext, CancellationToken.None);
            return (sut, context, transactionContext);
        }

        private const int MaxDeadLetterErrorDescriptionLength = 4096;

        [Fact]
        public async Task MustReturnFalseFromAckWhenNotPeekLock()
        {
            var sut = CreateSut();
            var result = await sut.AckMessageAsync(CreateEmptyContext(), new TransactionContext("receiver"), CancellationToken.None);
            result.Should().BeFalse();
        }

        [Fact]
        public async Task MustReturnFalseFromNackWhenNotPeekLock()
        {
            var sut = CreateSut();
            var result = await sut.NackMessageAsync(CreateEmptyContext(), new TransactionContext("receiver"), CancellationToken.None);
            result.Should().BeFalse();
        }

        [Fact]
        public async Task MustReturnFalseFromDeadletterWhenNotPeekLock()
        {
            var sut = CreateSut();
            var result = await sut.DeadletterMessageAsync(CreateEmptyContext(), new TransactionContext("receiver"), "reason", "description", CancellationToken.None);
            result.Should().BeFalse();
        }

        [Fact]
        public async Task MustReturnFalseFromAckWhenPeekLockButNoMessageInContext()
        {
            var sut = await CreatePeekLockSutAsync();
            var result = await sut.AckMessageAsync(CreateEmptyContext(), new TransactionContext("receiver"), CancellationToken.None);
            result.Should().BeFalse();
        }

        [Fact]
        public async Task MustReturnFalseFromNackWhenPeekLockButNoMessageInContext()
        {
            var sut = await CreatePeekLockSutAsync();
            var result = await sut.NackMessageAsync(CreateEmptyContext(), new TransactionContext("receiver"), CancellationToken.None);
            result.Should().BeFalse();
        }

        [Fact]
        public async Task MustReturnFalseFromDeadletterWhenPeekLockButNoMessageInContext()
        {
            var sut = await CreatePeekLockSutAsync();
            var result = await sut.DeadletterMessageAsync(CreateEmptyContext(), new TransactionContext("receiver"), "reason", "description", CancellationToken.None);
            result.Should().BeFalse();
        }

        [Fact]
        public async Task MustForwardSubLimitDeadletterDescriptionUnchanged()
        {
            var inMemory = new InMemoryServiceBusMessageReceiver();
            var (sut, context, transactionContext) = await ReceivedPeekLockMessageAsync(inMemory);
            var description = new string('a', MaxDeadLetterErrorDescriptionLength - 1);

            var result = await sut.DeadletterMessageAsync(context, transactionContext, "reason", description, CancellationToken.None);

            result.Should().BeTrue();
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

            result.Should().BeTrue();
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

            result.Should().BeTrue();
            var captured = inMemory.DeadLetteredMessages.Should().ContainSingle().Subject.description;
            captured.Length.Should().BeLessThanOrEqualTo(MaxDeadLetterErrorDescriptionLength);
            captured.Should().StartWith(new string('a', 10));
        }
    }
}
