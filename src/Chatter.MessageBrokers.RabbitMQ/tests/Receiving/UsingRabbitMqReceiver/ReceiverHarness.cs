using Chatter.MessageBrokers;
using Chatter.MessageBrokers.RabbitMQ.Configuration;
using Chatter.MessageBrokers.RabbitMQ.Receiving;
using Chatter.MessageBrokers.RabbitMQ.Tests.Receiving;
using Chatter.MessageBrokers.Receiving;
using Microsoft.Extensions.Logging;
using Moq;
using System.Collections.Generic;
using System.Linq;
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

        // The queue names the receiver's startup poison-destination existence gate passively declared during
        // InitializeAsync, in declare order. Captured here because the gate legitimately rents a PUBLISH channel,
        // and the settlement suites assert their republishes off ConnectionSource.PublishChannels — so the
        // startup probe's recording is lifted out (see the ctor) and surfaced through this property instead.
        public IReadOnlyList<string> StartupPassiveDeclares { get; }

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

            // Lift the startup existence gate's publish-channel recording off the source: the gate rents a publish
            // channel to run its passive declares, and every settlement suite asserts its republishes off
            // ConnectionSource.PublishChannels (Single() / BeEmpty()). Snapshot the probed queue names onto
            // StartupPassiveDeclares, then drop the startup channel so PublishChannels witnesses only the
            // republishes the test itself provoked.
            StartupPassiveDeclares = ConnectionSource.PublishChannels
                .SelectMany(channel => channel.PassiveDeclaredQueues)
                .ToList();
            ConnectionSource.PublishChannels.Clear();
        }

        public static ReceiverHarness Create(QueueType queueType = QueueType.Quorum,
                                             string deadLetterQueuePath = null,
                                             string errorQueuePath = ErrorPath,
                                             int prefetch = 1,
                                             int maxConcurrentCalls = 1,
                                             TransactionMode? transactionMode = null)
        {
            var options = new RabbitMqOptions(hostName: "localhost", prefetch: prefetch, queueType: queueType);
            var receiverOptions = new ReceiverOptions
            {
                MessageReceiverPath = ReceiverPath,
                DeadLetterQueuePath = deadLetterQueuePath,
                ErrorQueuePath = errorQueuePath,
                MaxConcurrentCalls = maxConcurrentCalls,
                // The core normalizes ReceiverOptions.TransactionMode (folding in the global default) BEFORE it
                // calls InitializeAsync; the production receiver reads it for the at-most-once init gate, so the
                // harness sets it directly to exercise that normalized state.
                TransactionMode = transactionMode
            };
            return new ReceiverHarness(options, receiverOptions);
        }

        // Drives ONLY InitializeAsync against a fresh receiver, returning the thrown exception (or null), so a test
        // can assert the receiver REJECTS a misconfiguration at registration time — before any delivery is consumed —
        // without the ctor's GetAwaiter().GetResult() surfacing the throw as a harness-construction failure.
        // <paramref name="configure"/> runs against the fresh source BEFORE InitializeAsync, so a test can arm a
        // RecordingChannel fault (via OnPublishChannelCreated) and keep a reference to the source in order to assert
        // what the rejected initialization left behind — e.g. that no consumer was ever registered.
        public static System.Exception CaptureInitException(string deadLetterQueuePath,
                                                            string errorQueuePath,
                                                            System.Action<InMemoryRabbitMqConnectionSource> configure = null)
        {
            var connectionSource = new InMemoryRabbitMqConnectionSource();
            configure?.Invoke(connectionSource);
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
