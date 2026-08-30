using Chatter.MessageBrokers.AzureServiceBus.Exceptions;
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
    //      ReceiveAndDelete) every ack/nack/deadletter returns false without inspecting the container:
    //      there is no lock to settle, so the no-op is correct.
    //   2. When in PeekLock mode but no ServiceBusReceivedMessage is in the context container, every
    //      ack/nack/deadletter throws ServiceBusMessageSettlementException and settles nothing. The
    //      lock exists but cannot be released, so silently reporting false would strand the delivery.
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

        private const string AckOperation = "ack";
        private const string NackOperation = "nack";
        private const string DeadletterOperation = "deadletter";

        // Mirrors the raw substrings DefaultExceptionsPredicateProvider matches against Exception.Message.
        private static readonly string[] DefaultRetryPredicateSubstrings =
        {
            "retry", "timeout", "time out", "rerun", "internal server error", "waiting", "wait until", "service unavailable",
        };

        private static Func<Task> CreateSettlementCall(ServiceBusReceiver sut, string operation, MessageBrokerContext context, TransactionContext transactionContext)
            => operation switch
            {
                AckOperation => () => sut.AckMessageAsync(context, transactionContext, CancellationToken.None),
                NackOperation => () => sut.NackMessageAsync(context, transactionContext, CancellationToken.None),
                DeadletterOperation => () => sut.DeadletterMessageAsync(context, transactionContext, "reason", "description", CancellationToken.None),
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown settlement operation."),
            };

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

        [Theory]
        [InlineData(AckOperation)]
        [InlineData(NackOperation)]
        [InlineData(DeadletterOperation)]
        public async Task MustThrowSettlementExceptionWhenPeekLockButNoMessageInContext(string operation)
        {
            var sut = await CreatePeekLockSutAsync();

            var settle = CreateSettlementCall(sut, operation, CreateEmptyContext(), new TransactionContext("receiver"));

            await settle.Should().ThrowAsync<ServiceBusMessageSettlementException>();
        }

        [Theory]
        [InlineData(AckOperation)]
        [InlineData(NackOperation)]
        [InlineData(DeadletterOperation)]
        public async Task MustNotSettleAnythingWhenPeekLockButNoMessageInContext(string operation)
        {
            var inMemory = new InMemoryServiceBusMessageReceiver();
            var sut = CreateInMemorySut(inMemory);
            await sut.InitializeAsync(new ReceiverOptions { MessageReceiverPath = "receiver", TransactionMode = TransactionMode.ReceiveOnly }, CancellationToken.None);

            var settle = CreateSettlementCall(sut, operation, CreateEmptyContext(), new TransactionContext("receiver"));

            await settle.Should().ThrowAsync<ServiceBusMessageSettlementException>();
            inMemory.CompletedMessages.Should().BeEmpty();
            inMemory.AbandonedMessages.Should().BeEmpty();
            inMemory.DeadLetteredMessages.Should().BeEmpty();
        }

        // Regression guard on the settlement message TEXT, which is a contract rather than prose:
        // DefaultExceptionsPredicateProvider is registered unconditionally and matches these raw
        // case-insensitive substrings against Exception.Message, so a reworded message containing any
        // of them would silently convert this fail-fast into a retry-to-exhaustion and rewrite the
        // error.type telemetry attribute to MaxRetryAttemptsExceededException.
        [Theory]
        [InlineData(AckOperation)]
        [InlineData(NackOperation)]
        [InlineData(DeadletterOperation)]
        public async Task MustNotUseSettlementMessageMatchingTheDefaultRetryPredicates(string operation)
        {
            var sut = await CreatePeekLockSutAsync();

            var settle = CreateSettlementCall(sut, operation, CreateEmptyContext(), new TransactionContext("receiver"));

            var thrown = await settle.Should().ThrowAsync<ServiceBusMessageSettlementException>();
            var message = thrown.Which.Message.ToLowerInvariant();
            foreach (var retryTriggeringSubstring in DefaultRetryPredicateSubstrings)
            {
                message.Should().NotContain(retryTriggeringSubstring);
            }
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
