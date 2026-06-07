using Chatter.MessageBrokers.AzureServiceBus.Options;
using Chatter.MessageBrokers.AzureServiceBus.Receiving;
using Chatter.MessageBrokers.AzureServiceBus.Tests.Receiving;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using FluentAssertions;
using Microsoft.Azure.ServiceBus;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Receiving.UsingServiceBusReceiver
{
    // Executable receive/ack/nack/deadletter tests driven through the internal IServiceBusMessageReceiver
    // seam with an in-memory double. These exercise the full ReceiveMessageAsync ladder (happy path,
    // null message, transient-rethrow, disposed-reset) and the ack-eligibility + container guards that
    // a live-connection-bound SDK receiver previously blocked from coverage.
    public class WhenReceivingThroughInMemoryReceiver : Testing.Core.Context
    {
        private static IBodyConverterFactory JsonFactory()
        {
            var factory = new Mock<IBodyConverterFactory>();
            factory.Setup(f => f.CreateBodyConverter(It.IsAny<string>())).Returns(new JsonBodyConverter());
            return factory.Object;
        }

        private static ServiceBusReceiver CreateSut(InMemoryServiceBusMessageReceiver inMemory, out InboundBrokeredMessageFactory inboundFactory)
        {
            var serviceBusOptions = new ServiceBusOptions
            {
                ConnectionString = "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=key;SharedAccessKey=secret",
                TokenProvider = new NullTokenProvider(),
            };
            var logger = new Mock<ILogger<ServiceBusReceiver>>();
            inboundFactory = new InboundBrokeredMessageFactory(JsonFactory(), Mock.Of<ILogger>());
            return new ServiceBusReceiver(serviceBusOptions, new MessageBrokerOptions(), logger.Object, inboundFactory, (_, __) => inMemory);
        }

        private static async Task<ServiceBusReceiver> InitializedPeekLockSutAsync(InMemoryServiceBusMessageReceiver inMemory)
        {
            var sut = CreateSut(inMemory, out _);
            await sut.InitializeAsync(new ReceiverOptions { MessageReceiverPath = "receiver", TransactionMode = TransactionMode.ReceiveOnly }, CancellationToken.None);
            return sut;
        }

        [Fact]
        public async Task MustReturnNullWhenNoMessageAvailable()
        {
            var inMemory = new InMemoryServiceBusMessageReceiver();
            inMemory.EnqueueNull();
            var sut = await InitializedPeekLockSutAsync(inMemory);

            var result = await sut.ReceiveMessageAsync(new TransactionContext("receiver"), CancellationToken.None);

            result.Should().BeNull();
        }

        [Fact]
        public async Task MustShapeReceivedMessageIntoContext()
        {
            var inMemory = new InMemoryServiceBusMessageReceiver();
            inMemory.EnqueueMessage(ServiceBusMessageFactory.ReceivedMessage(messageId: "the-id"));
            var sut = await InitializedPeekLockSutAsync(inMemory);

            var result = await sut.ReceiveMessageAsync(new TransactionContext("receiver"), CancellationToken.None);

            result.Should().NotBeNull();
            result.BrokeredMessage.MessageId.Should().Be("the-id");
        }

        [Fact]
        public async Task MustIncludeReceiverInTransactionContainer()
        {
            var inMemory = new InMemoryServiceBusMessageReceiver();
            inMemory.EnqueueMessage(ServiceBusMessageFactory.ReceivedMessage());
            var sut = await InitializedPeekLockSutAsync(inMemory);
            var transactionContext = new TransactionContext("receiver");

            await sut.ReceiveMessageAsync(transactionContext, CancellationToken.None);

            transactionContext.Container.TryGet<IServiceBusMessageReceiver>(out var contained).Should().BeTrue();
            contained.Should().BeSameAs(inMemory);
        }

        [Fact]
        public async Task MustRethrowTransientServiceBusException()
        {
            var inMemory = new InMemoryServiceBusMessageReceiver();
            inMemory.EnqueueThrow(new ServiceBusException(true, "transient"));
            var sut = await InitializedPeekLockSutAsync(inMemory);

            Func<Task> act = () => sut.ReceiveMessageAsync(new TransactionContext("receiver"), CancellationToken.None);

            await act.Should().ThrowAsync<ServiceBusException>();
        }

        [Fact]
        public async Task MustRethrowNonTransientException()
        {
            var inMemory = new InMemoryServiceBusMessageReceiver();
            inMemory.EnqueueThrow(new InvalidOperationException("fatal"));
            var sut = await InitializedPeekLockSutAsync(inMemory);

            Func<Task> act = () => sut.ReceiveMessageAsync(new TransactionContext("receiver"), CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        [Fact]
        public async Task MustReturnNullAndResetWhenReceiverDisposedWhileClosing()
        {
            var inMemory = new InMemoryServiceBusMessageReceiver { IsClosedOrClosing = true };
            inMemory.EnqueueThrow(new ObjectDisposedException("receiver"));
            var sut = await InitializedPeekLockSutAsync(inMemory);

            var result = await sut.ReceiveMessageAsync(new TransactionContext("receiver"), CancellationToken.None);

            result.Should().BeNull();
        }

        [Fact]
        public async Task MustRethrowObjectDisposedWhenCancellationRequested()
        {
            var inMemory = new InMemoryServiceBusMessageReceiver { IsClosedOrClosing = true };
            inMemory.EnqueueThrow(new ObjectDisposedException("receiver"));
            var sut = await InitializedPeekLockSutAsync(inMemory);
            var cancelled = new CancellationToken(canceled: true);

            Func<Task> act = () => sut.ReceiveMessageAsync(new TransactionContext("receiver"), cancelled);

            await act.Should().ThrowAsync<ObjectDisposedException>();
        }

        [Fact]
        public async Task MustCompleteMessageOnAckInPeekLock()
        {
            var inMemory = new InMemoryServiceBusMessageReceiver();
            var message = ServiceBusMessageFactory.ReceivedMessage();
            inMemory.EnqueueMessage(message);
            var sut = await InitializedPeekLockSutAsync(inMemory);
            var transactionContext = new TransactionContext("receiver");
            var context = await sut.ReceiveMessageAsync(transactionContext, CancellationToken.None);

            var result = await sut.AckMessageAsync(context, transactionContext, CancellationToken.None);

            result.Should().BeTrue();
            inMemory.CompletedLockTokens.Should().ContainSingle().Which.Should().Be(message.SystemProperties.LockToken);
        }

        [Fact]
        public async Task MustAbandonMessageOnNackInPeekLock()
        {
            var inMemory = new InMemoryServiceBusMessageReceiver();
            var message = ServiceBusMessageFactory.ReceivedMessage();
            inMemory.EnqueueMessage(message);
            var sut = await InitializedPeekLockSutAsync(inMemory);
            var transactionContext = new TransactionContext("receiver");
            var context = await sut.ReceiveMessageAsync(transactionContext, CancellationToken.None);

            var result = await sut.NackMessageAsync(context, transactionContext, CancellationToken.None);

            result.Should().BeTrue();
            inMemory.AbandonedLockTokens.Should().ContainSingle().Which.Should().Be(message.SystemProperties.LockToken);
        }

        [Fact]
        public async Task MustDeadletterMessageInPeekLock()
        {
            var inMemory = new InMemoryServiceBusMessageReceiver();
            var message = ServiceBusMessageFactory.ReceivedMessage();
            inMemory.EnqueueMessage(message);
            var sut = await InitializedPeekLockSutAsync(inMemory);
            var transactionContext = new TransactionContext("receiver");
            var context = await sut.ReceiveMessageAsync(transactionContext, CancellationToken.None);

            var result = await sut.DeadletterMessageAsync(context, transactionContext, "reason", "description", CancellationToken.None);

            result.Should().BeTrue();
            inMemory.DeadLetteredLockTokens.Should().ContainSingle()
                .Which.Should().Be((message.SystemProperties.LockToken, "reason", "description"));
        }

        [Fact]
        public async Task MustCloseReceiverOnStop()
        {
            var inMemory = new InMemoryServiceBusMessageReceiver();
            inMemory.EnqueueMessage(ServiceBusMessageFactory.ReceivedMessage());
            var sut = await InitializedPeekLockSutAsync(inMemory);
            await sut.ReceiveMessageAsync(new TransactionContext("receiver"), CancellationToken.None);

            await sut.StopReceiver();

            inMemory.CloseCount.Should().Be(1);
        }
    }
}
