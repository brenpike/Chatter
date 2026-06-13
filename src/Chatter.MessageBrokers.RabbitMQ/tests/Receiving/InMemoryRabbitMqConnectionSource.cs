using Chatter.MessageBrokers.RabbitMQ.Configuration;
using Chatter.MessageBrokers.RabbitMQ.Receiving;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Receiving
{
    // In-memory IRabbitMqConnectionSource double that drives RabbitMqReceiver/RabbitMqSender without a live
    // broker. It mirrors the real seam exactly: a settable receive-channel epoch (so a test can FORCE a stale
    // epoch and pin the false-ack guard), a RunOnReceiveChannelAsync that invokes the operation with a single
    // recording IChannel and the CURRENT epoch (read at invocation time, like the production gate), and an
    // AcquirePublishChannelAsync that hands back a recording publish channel through the real
    // RabbitMqPublishChannelRental. The same RecordingChannel records every ack/nack/publish so receiver and
    // sender behavior is asserted off the recordings. Deliveries are pushed by capturing the receiver's
    // AsyncEventingBasicConsumer at BasicConsumeAsync and raising its ReceivedAsync via HandleBasicDeliverAsync.
    internal sealed class InMemoryRabbitMqConnectionSource : IRabbitMqConnectionSource
    {
        private long _currentReceiveChannelEpoch;
        private readonly RabbitMqPublishChannelRentalFactory _rentalFactory = new RabbitMqPublishChannelRentalFactory();

        public InMemoryRabbitMqConnectionSource()
        {
            ReceiveChannel = new RecordingChannel();
        }

        // The single serialized receive channel handed to RunOnReceiveChannelAsync. Records ack/nack and
        // captures the consumer registered by the receiver's InitializeAsync.
        public RecordingChannel ReceiveChannel { get; }

        // The publish channels handed out by AcquirePublishChannelAsync, in acquisition order, so a sender or
        // republish test can assert the publish recordings.
        public List<RecordingChannel> PublishChannels { get; } = new List<RecordingChannel>();

        public int RunOnReceiveChannelCount { get; private set; }
        public int AcquirePublishChannelCount { get; private set; }

        public long CurrentReceiveChannelEpoch => Interlocked.Read(ref _currentReceiveChannelEpoch);

        // Forces the receive-channel epoch forward, modelling a channel recycle (e.g. after automatic recovery)
        // so a settlement carrying the prior epoch becomes stale and the receiver's guard makes it a no-op.
        public void AdvanceEpoch()
            => Interlocked.Increment(ref _currentReceiveChannelEpoch);

        public Task<TResult> RunOnReceiveChannelAsync<TResult>(Func<IChannel, long, Task<TResult>> operation,
                                                               CancellationToken cancellationToken)
        {
            if (operation is null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            RunOnReceiveChannelCount++;
            return operation(ReceiveChannel, Interlocked.Read(ref _currentReceiveChannelEpoch));
        }

        public Task<RabbitMqPublishChannelRental> AcquirePublishChannelAsync(CancellationToken cancellationToken)
        {
            AcquirePublishChannelCount++;
            var channel = new RecordingChannel();
            PublishChannels.Add(channel);
            return Task.FromResult(_rentalFactory.Create(channel));
        }

        // Pushes a delivery through the captured consumer, exactly as the broker's push consumer would, so the
        // receiver's BufferDeliveryAsync enqueues a ReceivedMessage carrying the registration-time epoch.
        public async Task PushDeliveryAsync(ulong deliveryTag,
                                            byte[] body,
                                            string exchange = "",
                                            string routingKey = "queue",
                                            IDictionary<string, object> headers = null,
                                            bool redelivered = false,
                                            string messageId = null)
        {
            if (ReceiveChannel.RegisteredConsumer is null)
            {
                throw new InvalidOperationException("No consumer registered; call the receiver's InitializeAsync first.");
            }

            var properties = new BasicProperties
            {
                Headers = headers,
                MessageId = messageId
            };

            await ReceiveChannel.RegisteredConsumer.HandleBasicDeliverAsync(
                consumerTag: "in-memory-consumer",
                deliveryTag: deliveryTag,
                redelivered: redelivered,
                exchange: exchange,
                routingKey: routingKey,
                properties: properties,
                body: new ReadOnlyMemory<byte>(body),
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
            => default;
    }

    // Builds the production RabbitMqPublishChannelRental (sealed concrete type the production
    // AcquirePublishChannelAsync returns; its ctor is internal) so the in-memory source hands back the SAME
    // type the receiver/sender await-using. The test assembly has InternalsVisibleTo for the production
    // assembly, so the internal ctor is reachable.
    //
    // The rental's DisposeAsync calls back into a REAL RabbitMqConnectionSource.ReturnPublishChannel, which
    // Releases the source's publish-pool semaphore. To keep that Release balanced (a bare Release on a full
    // semaphore throws SemaphoreFullException), this factory owns one real source and reflectively Waits its
    // pool gate once per rental created. Because each RecordingChannel reports IsClosed (IsOpen == false),
    // ReturnPublishChannel disposes the recording rather than re-pooling it — leaving the recording intact for
    // the publish assertions. No live broker is ever contacted: only the rental ctor and the gate are used.
    internal sealed class RabbitMqPublishChannelRentalFactory
    {
        private readonly RabbitMqConnectionSource _backingSource;
        private readonly SemaphoreSlim _poolGate;
        private readonly ConstructorInfo _rentalCtor;

        public RabbitMqPublishChannelRentalFactory()
        {
            // A large pool capacity so many rentals can be created across a test without exhausting the gate.
            _backingSource = new RabbitMqConnectionSource(new RabbitMqOptions(hostName: "in-memory"), publishChannelPoolCapacity: 1024);
            _poolGate = (SemaphoreSlim)typeof(RabbitMqConnectionSource)
                .GetField("_publishPoolGate", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(_backingSource);
            _rentalCtor = typeof(RabbitMqPublishChannelRental).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(RabbitMqConnectionSource), typeof(IChannel) },
                modifiers: null);
        }

        public RabbitMqPublishChannelRental Create(IChannel channel)
        {
            // Match the Release the rental's DisposeAsync will perform via ReturnPublishChannel.
            _poolGate.Wait();
            return (RabbitMqPublishChannelRental)_rentalCtor.Invoke(new object[] { _backingSource, channel });
        }
    }
}
