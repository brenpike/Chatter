using Chatter.MessageBrokers.RabbitMQ.Configuration;
using Chatter.MessageBrokers.RabbitMQ.Receiving;
using Chatter.MessageBrokers.RabbitMQ.Tests.Receiving;
using Chatter.MessageBrokers.Receiving;
using Microsoft.Extensions.Logging;
using Moq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Receiving.UsingRabbitMqReceiver
{
    // Shared setup for the RabbitMqReceiver suites: wires a real receiver onto the in-memory connection source,
    // initializes it (registering the recording consumer), and exposes helpers to push a delivery and pull the
    // resulting MessageBrokerContext — all without a live broker.
    internal sealed class ReceiverHarness
    {
        public const string ReceiverPath = "orders-queue";
        public const string DeadLetterPath = "orders-deadletter";
        public const string ErrorPath = "orders-error";

        // Native quorum-queue redelivery counter the broker increments per redelivery.
        public const string NativeDeliveryCountHeader = "x-delivery-count";

        public InMemoryRabbitMqConnectionSource ConnectionSource { get; } = new InMemoryRabbitMqConnectionSource();
        public RabbitMqReceiver Receiver { get; }

        private ReceiverHarness(RabbitMqOptions options, ReceiverOptions receiverOptions)
        {
            Receiver = new RabbitMqReceiver(ConnectionSource, options, new RabbitMqBodyConverter(), Mock.Of<ILogger<RabbitMqReceiver>>());
            Receiver.InitializeAsync(receiverOptions, CancellationToken.None).GetAwaiter().GetResult();
        }

        public static ReceiverHarness Create(QueueType queueType = QueueType.Quorum,
                                             string deadLetterQueuePath = null,
                                             string errorQueuePath = ErrorPath)
        {
            var options = new RabbitMqOptions(hostName: "localhost", queueType: queueType);
            var receiverOptions = new ReceiverOptions
            {
                MessageReceiverPath = ReceiverPath,
                DeadLetterQueuePath = deadLetterQueuePath,
                ErrorQueuePath = errorQueuePath
            };
            return new ReceiverHarness(options, receiverOptions);
        }

        public Task PushAsync(ulong deliveryTag,
                              byte[] body = null,
                              IDictionary<string, object> headers = null,
                              bool redelivered = false,
                              string messageId = null)
            => ConnectionSource.PushDeliveryAsync(
                deliveryTag,
                body ?? new byte[] { 1, 2, 3 },
                exchange: "",
                routingKey: ReceiverPath,
                headers: headers,
                redelivered: redelivered,
                messageId: messageId);

        public Task<Chatter.MessageBrokers.Context.MessageBrokerContext> ReceiveAsync()
            => Receiver.ReceiveMessageAsync(transactionContext: null, CancellationToken.None);
    }
}
