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
    // broker. It mirrors the real seam exactly, INCLUDING the closed-by-construction epoch lifecycle: the source
    // OWNS the receive channel + consumer lifecycle, re-running a stored consume-registration delegate on every
    // (re)creation with the freshly-bumped epoch. A test can FORCE a bare stale epoch (AdvanceEpoch, no
    // re-register) to pin the false-ack guard on an already-buffered delivery, OR SimulateRecoveryAsync to model a
    // real recovery (new channel + bumped epoch + re-registered consumer) so a post-recovery PushDeliveryAsync is
    // stamped with the NEW epoch (settles) while a delivery buffered before recovery keeps the OLD epoch (no-ops).
    // RunOnReceiveChannelAsync invokes the operation with the CURRENT recording channel and the CURRENT epoch
    // (read at invocation time, like the production gate). AcquirePublishChannelAsync hands back a recording
    // publish channel through the real RabbitMqPublishChannelRental. RecordingChannel records every ack/nack/
    // publish so behavior is asserted off the recordings. Deliveries are pushed by raising the registered
    // consumer's ReceivedAsync via HandleBasicDeliverAsync.
    internal sealed class InMemoryRabbitMqConnectionSource : IRabbitMqConnectionSource
    {
        private long _currentReceiveChannelEpoch;
        private readonly RabbitMqPublishChannelRentalFactory _rentalFactory = new RabbitMqPublishChannelRentalFactory();

        // One shared monotonic clock for every recording channel this source hands out (receive + publish), so a
        // test can witness the RELATIVE order of a publish-channel republish and a receive-channel ack across the
        // two separate channels — the confirm-before-ack ordering (ADR-0001) is otherwise unobservable per-channel.
        private readonly OperationSequencer _sequencer = new();

        // The stored consume-registration delegate, mirroring the production source. Re-run on every receive
        // channel (re)creation with the freshly-bumped epoch; returns the broker-assigned consumer tag the source
        // stores so it owns cancellation. CLEARED by StopReceivingAsync (terminal stop) so a late recovery
        // re-registers nothing — mirroring the production source.
        private Func<IChannel, long, CancellationToken, Task<string>> _registerConsumer;

        // The latest consumer tag the registration delegate returned, mirroring the production source's _consumerTag.
        // StopReceivingAsync cancels this tag on the current receive channel.
        private string _consumerTag;

        public InMemoryRabbitMqConnectionSource()
        {
            ReceiveChannel = new RecordingChannel(_sequencer);
        }

        // The CURRENT serialized receive channel handed to RunOnReceiveChannelAsync. Records ack/nack and
        // captures the consumer registered by the receiver. Replaced by SimulateRecoveryAsync to model a recycle.
        public RecordingChannel ReceiveChannel { get; private set; }

        // Every receive channel created across the source's life, in creation order, so a recovery test can assert
        // that the pre-recovery channel was disposed and the post-recovery one carries the re-registered consumer.
        public List<RecordingChannel> ReceiveChannels { get; } = new List<RecordingChannel> { };

        // The publish channels handed out by AcquirePublishChannelAsync, in acquisition order, so a sender or
        // republish test can assert the publish recordings.
        public List<RecordingChannel> PublishChannels { get; } = new List<RecordingChannel>();

        // Optional callback invoked with each newly-created publish channel immediately after it is recorded
        // but before the rental is returned. Tests use this to inject a PublishFault (or other state) onto
        // the channel before Dispatch awaits BasicPublishAsync.
        public Action<RecordingChannel> OnPublishChannelCreated { get; set; }

        public int RunOnReceiveChannelCount { get; private set; }
        public int AcquirePublishChannelCount { get; private set; }

        public long CurrentReceiveChannelEpoch => Interlocked.Read(ref _currentReceiveChannelEpoch);

        // Forces the receive-channel epoch forward WITHOUT recreating the channel or re-registering the consumer,
        // modelling the moment a channel has been recycled but an ALREADY-BUFFERED delivery still carries the prior
        // epoch — so the receiver's settle guard makes that delivery's ack a no-op. This is the bare false-ack-guard
        // probe; use SimulateRecoveryAsync to model a full recovery that also re-stamps NEW deliveries.
        public void AdvanceEpoch()
            => Interlocked.Increment(ref _currentReceiveChannelEpoch);

        // Models a real automatic recovery exactly as the production source does: under one atomic step, swap in a
        // fresh recording channel, bump the epoch, and re-run the stored consume-registration delegate with the NEW
        // epoch. A delivery pushed AFTER this is stamped with the new epoch (settles); a delivery buffered BEFORE it
        // keeps the old epoch (no-ops at settle). Disposes the prior channel, mirroring the source's recreate.
        public async Task SimulateRecoveryAsync()
        {
            // Mirror the production RecreateReceiveChannelAsync: dispose any existing receive channel first.
            if (ReceiveChannel is not null)
            {
                await ReceiveChannel.DisposeAsync().ConfigureAwait(false);
                ReceiveChannel = null;
            }

            // NO CONSUMERLESS COMMITTED CHANNEL (mirrors production): when no delegate is stored — a recovery that
            // raced ahead of the first StartReceivingAsync, or a late recovery after a terminal StopReceivingAsync
            // cleared the delegate — do NOT create or commit a fresh receive channel. The production
            // RecreateReceiveChannelAsync returns early in this case so a later StartReceivingAsync forces a full
            // recreate-and-register rather than taking an IsOpen fast path onto a consumerless channel.
            if (_registerConsumer is null)
            {
                return;
            }

            ReceiveChannel = new RecordingChannel(_sequencer);
            ReceiveChannels.Add(ReceiveChannel);
            Interlocked.Increment(ref _currentReceiveChannelEpoch);

            _consumerTag = await _registerConsumer(ReceiveChannel, Interlocked.Read(ref _currentReceiveChannelEpoch), CancellationToken.None).ConfigureAwait(false);
        }

        public async Task StartReceivingAsync(Func<IChannel, long, CancellationToken, Task<string>> registerConsumer,
                                              CancellationToken cancellationToken)
        {
            if (registerConsumer is null)
            {
                throw new ArgumentNullException(nameof(registerConsumer));
            }

            // Store the delegate first, then run it once against the current channel with the current epoch — the
            // production source's StartReceivingAsync -> EnsureReceiveChannelAsync ordering. ReceiveChannels tracks
            // this initial channel as the first session.
            _registerConsumer = registerConsumer;

            // Mirror production EnsureReceiveChannelAsync -> RecreateReceiveChannelAsync: when no receive channel is
            // committed (e.g. a recovery fired before this first StartReceivingAsync and — per the no-consumerless-
            // committed-channel rule — committed nothing), create and commit a fresh channel here, bumping the epoch
            // exactly as a real recreate would. When the cold channel already exists (epoch 0) it is reused in place.
            if (ReceiveChannel is null)
            {
                ReceiveChannel = new RecordingChannel(_sequencer);
                Interlocked.Increment(ref _currentReceiveChannelEpoch);
            }

            if (ReceiveChannels.Count == 0 || ReceiveChannels[ReceiveChannels.Count - 1] != ReceiveChannel)
            {
                ReceiveChannels.Add(ReceiveChannel);
            }

            _consumerTag = await registerConsumer(ReceiveChannel, Interlocked.Read(ref _currentReceiveChannelEpoch), cancellationToken).ConfigureAwait(false);
        }

        // Mirrors the production StopReceivingAsync: cancels the registered consumer on the current receive channel,
        // tears down the receive channel, and CLEARS the stored delegate so a late recovery re-registers nothing —
        // WITHOUT touching the fake connection or the publish pool (AcquirePublishChannelAsync keeps working, so a
        // sender still publishes after the receiver stops). Idempotent: a double-stop finds a null channel + null
        // delegate and no-ops.
        public async Task StopReceivingAsync(CancellationToken cancellationToken)
        {
            if (ReceiveChannel is not null)
            {
                if (!string.IsNullOrEmpty(_consumerTag))
                {
                    await ReceiveChannel.BasicCancelAsync(_consumerTag, noWait: false, cancellationToken: cancellationToken).ConfigureAwait(false);
                }

                await ReceiveChannel.DisposeAsync().ConfigureAwait(false);
                ReceiveChannel = null;
            }

            _registerConsumer = null;
            _consumerTag = null;
            ReceivingStopped = true;
        }

        // True once StopReceivingAsync has terminally stopped receiving, so a test can assert the stop happened
        // without reaching into the cleared delegate.
        public bool ReceivingStopped { get; private set; }

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
            var channel = new RecordingChannel(_sequencer);
            PublishChannels.Add(channel);
            OnPublishChannelCreated?.Invoke(channel);
            return Task.FromResult(_rentalFactory.Create(channel));
        }

        // Pushes a delivery through the captured consumer, exactly as the broker's push consumer would, so the
        // receiver's BufferDeliveryAsync enqueues a ReceivedMessage carrying the registration-time epoch.
        // By default a string header value is presented as a UTF-8 byte[] (the AMQP longstr coercion a REAL
        // broker performs on the wire), STRING-ONLY — numeric and byte[] values are passed verbatim so the
        // delivery-count / epoch tests are unaffected. A test that must observe a header at its CLR type pre-wire
        // (e.g. a numeric stamp that should not be longstr-coerced anyway) can pass coerceStringHeadersToBytes:
        // false to push the dictionary verbatim.
        public async Task PushDeliveryAsync(ulong deliveryTag,
                                            byte[] body,
                                            string exchange = "",
                                            string routingKey = "queue",
                                            IDictionary<string, object> headers = null,
                                            bool redelivered = false,
                                            string messageId = null,
                                            bool coerceStringHeadersToBytes = true,
                                            string expiration = null,
                                            byte? priority = null,
                                            AmqpTimestamp? timestamp = null,
                                            string type = null,
                                            string appId = null,
                                            string contentEncoding = null,
                                            string contentType = null,
                                            string correlationId = null)
        {
            // After a terminal StopReceivingAsync the receive channel is torn down and the consumer cancelled, so a
            // delivery the broker might still try to push is safely DROPPED here (a real cancelled consumer receives
            // no further deliveries) rather than being forced into a completed buffer writer.
            if (ReceiveChannel is null)
            {
                return;
            }

            if (ReceiveChannel.RegisteredConsumer is null)
            {
                throw new InvalidOperationException("No consumer registered; call the receiver's InitializeAsync first.");
            }

            // Set only the native properties the caller supplied so the delivery's Is*Present() guards model a
            // real broker delivery: an unsupplied property stays absent (Is*Present() false), exactly as the
            // receiver's BufferDeliveryAsync capture expects (absent => null).
            var properties = new BasicProperties
            {
                Headers = coerceStringHeadersToBytes ? CoerceStringHeadersToLongstr(headers) : headers,
                MessageId = messageId
            };
            if (expiration != null)
            {
                properties.Expiration = expiration;
            }
            if (priority.HasValue)
            {
                properties.Priority = priority.Value;
            }
            if (timestamp.HasValue)
            {
                properties.Timestamp = timestamp.Value;
            }
            if (type != null)
            {
                properties.Type = type;
            }
            if (appId != null)
            {
                properties.AppId = appId;
            }
            if (contentEncoding != null)
            {
                properties.ContentEncoding = contentEncoding;
            }
            if (contentType != null)
            {
                properties.ContentType = contentType;
            }
            if (correlationId != null)
            {
                properties.CorrelationId = correlationId;
            }

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

        // Models the broker's on-the-wire AMQP longstr coercion: a string header value is delivered as a UTF-8
        // byte[]. STRING-ONLY — every other value type (numeric, byte[], bool) is passed verbatim so the
        // delivery-count / epoch tests still see the CLR types they push. Returns null for a null dictionary.
        private static IDictionary<string, object> CoerceStringHeadersToLongstr(IDictionary<string, object> headers)
        {
            if (headers is null)
            {
                return null;
            }

            var coerced = new Dictionary<string, object>(headers.Count);
            foreach (var entry in headers)
            {
                coerced[entry.Key] = entry.Value is string asString
                    ? System.Text.Encoding.UTF8.GetBytes(asString)
                    : entry.Value;
            }

            return coerced;
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
