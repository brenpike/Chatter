using Chatter.MessageBrokers.AzureServiceBus.Options;
using Chatter.MessageBrokers.AzureServiceBus.Receiving;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Receiving.UsingServiceBusReceiver
{
    // Characterization tests pinning the ack/nack/deadletter guard branches that return BEFORE
    // touching InnerReceiver (which would open a live connection):
    //   1. When the effective ReceiveMode != PeekLock (i.e. TransactionMode.None => ReceiveAndDelete)
    //      every ack/nack/deadletter returns false without inspecting the context container.
    //   2. When in PeekLock mode but no Microsoft.Azure.ServiceBus.Message is in the context
    //      container, every ack/nack/deadletter returns false and logs a warning.
    // The InitializeAsync receive-mode flip is observed indirectly: initializing with a non-None
    // TransactionMode flips the receiver into PeekLock, so guard branch (2) becomes reachable.
    public class WhenAcknowledgingMessage : Testing.Core.Context
    {
        private static MessageBrokerContext CreateEmptyContext()
            => new MessageBrokerContext("message-id", new byte[] { 1 }, null, "receiver", CancellationToken.None, new JsonBodyConverter());

        // MessageBrokerOptions.TransactionMode is internal-set in the core assembly (no IVT to the
        // test assembly), so it is left at its default (TransactionMode.None => ReceiveAndDelete).
        private static ServiceBusReceiver CreateSut()
        {
            var serviceBusOptions = new ServiceBusOptions
            {
                ConnectionString = "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=key;SharedAccessKey=secret",
                TokenProvider = new NullTokenProvider(),
            };
            var messageBrokerOptions = new MessageBrokerOptions();
            var logger = new Mock<ILogger<ServiceBusReceiver>>();
            var bodyConverterFactory = new Mock<IBodyConverterFactory>();

            return new ServiceBusReceiver(serviceBusOptions, messageBrokerOptions, logger.Object, bodyConverterFactory.Object);
        }

        private static async Task<ServiceBusReceiver> CreatePeekLockSutAsync()
        {
            // Construct in None (ReceiveAndDelete), then flip to PeekLock via InitializeAsync.
            var sut = CreateSut();
            await sut.InitializeAsync(new ReceiverOptions { MessageReceiverPath = "receiver", TransactionMode = TransactionMode.ReceiveOnly }, CancellationToken.None);
            return sut;
        }

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
    }
}
