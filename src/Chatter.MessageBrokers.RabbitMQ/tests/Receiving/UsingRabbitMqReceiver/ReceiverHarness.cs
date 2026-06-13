using Chatter.MessageBrokers;
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
            // The real core factory over the RabbitMQ + core JSON converters, so the receiver resolves the same
            // converter the production wiring would for the configured MessageBodyType.
            var bodyConverterFactory = new BodyConverterFactory(new IBrokeredMessageBodyConverter[]
            {
                new RabbitMqBodyConverter(),
                new JsonBodyConverter()
            });
            Receiver = new RabbitMqReceiver(ConnectionSource, options, bodyConverterFactory, Mock.Of<ILogger<RabbitMqReceiver>>());
            Receiver.InitializeAsync(receiverOptions, CancellationToken.None).GetAwaiter().GetResult();
        }

        public static ReceiverHarness Create(QueueType queueType = QueueType.Quorum,
                                             string deadLetterQueuePath = null,
                                             string errorQueuePath = ErrorPath,
                                             int prefetch = 1,
                                             int maxConcurrentCalls = 1)
        {
            var options = new RabbitMqOptions(hostName: "localhost", prefetch: prefetch, queueType: queueType);
            var receiverOptions = new ReceiverOptions
            {
                MessageReceiverPath = ReceiverPath,
                DeadLetterQueuePath = deadLetterQueuePath,
                ErrorQueuePath = errorQueuePath,
                MaxConcurrentCalls = maxConcurrentCalls
            };
            return new ReceiverHarness(options, receiverOptions);
        }

        // Drives ONLY InitializeAsync against a fresh receiver, returning the thrown exception (or null), so a test
        // can assert the receiver REJECTS a misconfiguration at registration time — before any delivery is consumed —
        // without the ctor's GetAwaiter().GetResult() surfacing the throw as a harness-construction failure.
        public static System.Exception CaptureInitException(string deadLetterQueuePath, string errorQueuePath)
        {
            var connectionSource = new InMemoryRabbitMqConnectionSource();
            var bodyConverterFactory = new BodyConverterFactory(new IBrokeredMessageBodyConverter[]
            {
                new RabbitMqBodyConverter(),
                new JsonBodyConverter()
            });
            var receiver = new RabbitMqReceiver(connectionSource, new RabbitMqOptions(hostName: "localhost"), bodyConverterFactory, Mock.Of<ILogger<RabbitMqReceiver>>());
            var receiverOptions = new ReceiverOptions
            {
                MessageReceiverPath = ReceiverPath,
                DeadLetterQueuePath = deadLetterQueuePath,
                ErrorQueuePath = errorQueuePath
            };

            try
            {
                receiver.InitializeAsync(receiverOptions, CancellationToken.None).GetAwaiter().GetResult();
                return null;
            }
            catch (System.Exception ex)
            {
                return ex;
            }
        }

        public Task PushAsync(ulong deliveryTag,
                              byte[] body = null,
                              IDictionary<string, object> headers = null,
                              bool redelivered = false,
                              string messageId = null,
                              string expiration = null,
                              byte? priority = null,
                              global::RabbitMQ.Client.AmqpTimestamp? timestamp = null,
                              string type = null,
                              string appId = null,
                              string contentEncoding = null,
                              string contentType = null,
                              string correlationId = null)
            => ConnectionSource.PushDeliveryAsync(
                deliveryTag,
                body ?? new byte[] { 1, 2, 3 },
                exchange: "",
                routingKey: ReceiverPath,
                headers: headers,
                redelivered: redelivered,
                messageId: messageId,
                expiration: expiration,
                priority: priority,
                timestamp: timestamp,
                type: type,
                appId: appId,
                contentEncoding: contentEncoding,
                contentType: contentType,
                correlationId: correlationId);

        // Pushes a delivery with its header values presented VERBATIM at their CLR type (no AMQP longstr
        // coercion), for tests asserting the marshaller's verbatim-preservation of unknown keys or pushing a
        // header at a specific pre-wire type.
        public Task PushVerbatimAsync(ulong deliveryTag,
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
                messageId: messageId,
                coerceStringHeadersToBytes: false);

        public Task<Chatter.MessageBrokers.Context.MessageBrokerContext> ReceiveAsync()
            => Receiver.ReceiveMessageAsync(transactionContext: null, CancellationToken.None);
    }
}
