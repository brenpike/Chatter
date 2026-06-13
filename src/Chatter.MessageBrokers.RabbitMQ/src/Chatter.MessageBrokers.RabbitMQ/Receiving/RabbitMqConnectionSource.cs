using Chatter.MessageBrokers.RabbitMQ.Configuration;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Chatter.MessageBrokers.RabbitMQ.Receiving
{
    /// <summary>
    /// Production <see cref="IRabbitMqConnectionSource"/>. A process singleton that owns exactly one
    /// <see cref="IConnection"/> (lazily, thread-safely initialized) materialized from
    /// <see cref="RabbitMqOptions"/>, one serialized receive <see cref="IChannel"/> guarded by an async
    /// gate, and a separate pool of publish channels with publisher confirms enabled. This is the sole
    /// place the configured connection settings become a live connection.
    /// </summary>
    /// <remarks>
    /// INVARIANT: the receive channel is only ever touched while the gate is held; AMQP channels are not
    /// thread-safe. INVARIANT: the receive-channel epoch is incremented whenever the receive channel is
    /// (re)created, and it is read under the same gate that hands out the channel, so callers observe the
    /// epoch and the channel atomically.
    /// INVARIANT (closed-by-construction epoch lifecycle): the source OWNS the receive channel and consumer
    /// lifecycle. Connection-level automatic recovery stays ENABLED but TOPOLOGY (consumer) recovery is
    /// DISABLED, so the client never silently re-binds the old consumer. On every receive-channel
    /// (re)creation — cold start, lazy recreate, and automatic recovery — the source, UNDER THE RECEIVE GATE
    /// and as ONE atomic event, disposes any old channel, creates a fresh one, increments the epoch, and
    /// re-runs the stored consume-registration delegate against the new channel with the freshly-bumped
    /// epoch. Because the bump and the re-registration are the SAME gated event, a delivery's stamped epoch
    /// always equals the epoch of the session that delivered it. A pre-recovery in-flight delivery carries
    /// the old epoch (correctly no-ops at settle); a post-recovery delivery is stamped by the freshly
    /// re-registered consumer (correctly settles). This eliminates both the recovery-stale-epoch false-ack
    /// and the topology-recovery stale-closure no-op-settle classes by construction, race-free.
    /// </remarks>
    public sealed class RabbitMqConnectionSource : IRabbitMqConnectionSource
    {
        // INVARIANT: prefetch must be >= MaxConcurrentCalls so the broker keeps enough unacknowledged
        // deliveries in flight to saturate the core's workers. MaxConcurrentCalls is not available at
        // this layer; STEP-004/STEP-006 finalize QoS wiring if a larger floor is required. Until then
        // the configured Prefetch (default 1) is applied as-is.
        private const int _defaultPublishChannelPoolCapacity = 8;

        private readonly RabbitMqOptions _options;
        private readonly int _publishChannelPoolCapacity;
        private readonly SemaphoreSlim _connectionInitGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _receiveChannelGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _publishPoolGate;
        private readonly ConcurrentBag<IChannel> _publishChannels = new ConcurrentBag<IChannel>();

        private IConnection _connection;
        private IChannel _receiveChannel;
        private long _receiveChannelEpoch;
        private bool _disposed;

        // The receiver-supplied consume-registration delegate. Stored by StartReceivingAsync and re-run by the
        // source on every receive-channel (re)creation (cold start, lazy recreate, recovery) under the receive
        // gate, AFTER the epoch bump, so the re-registered consumer always closes over the current epoch.
        private Func<IChannel, long, CancellationToken, Task> _registerConsumer;

        public RabbitMqConnectionSource(RabbitMqOptions options)
            : this(options, _defaultPublishChannelPoolCapacity)
        {
        }

        public RabbitMqConnectionSource(RabbitMqOptions options, int publishChannelPoolCapacity)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (publishChannelPoolCapacity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(publishChannelPoolCapacity),
                    publishChannelPoolCapacity, "The publish channel pool capacity must be at least 1.");
            }

            _publishChannelPoolCapacity = publishChannelPoolCapacity;
            _publishPoolGate = new SemaphoreSlim(publishChannelPoolCapacity, publishChannelPoolCapacity);
        }

        public long CurrentReceiveChannelEpoch => Interlocked.Read(ref _receiveChannelEpoch);

        public async Task<TResult> RunOnReceiveChannelAsync<TResult>(Func<IChannel, long, Task<TResult>> operation,
                                                                     CancellationToken cancellationToken)
        {
            if (operation is null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            ThrowIfDisposed();

            await _receiveChannelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var channel = await EnsureReceiveChannelAsync(cancellationToken).ConfigureAwait(false);
                return await operation(channel, Interlocked.Read(ref _receiveChannelEpoch)).ConfigureAwait(false);
            }
            finally
            {
                _receiveChannelGate.Release();
            }
        }

        public async Task StartReceivingAsync(Func<IChannel, long, CancellationToken, Task> registerConsumer,
                                              CancellationToken cancellationToken)
        {
            if (registerConsumer is null)
            {
                throw new ArgumentNullException(nameof(registerConsumer));
            }

            ThrowIfDisposed();

            await _receiveChannelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Store the delegate FIRST so EnsureReceiveChannelAsync re-runs it on this and every later
                // (re)creation. EnsureReceiveChannelAsync bumps the epoch and invokes the stored delegate against
                // the fresh channel with the bumped epoch, so the initial registration is itself the first atomic
                // bump+register event.
                _registerConsumer = registerConsumer;
                await EnsureReceiveChannelAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _receiveChannelGate.Release();
            }
        }

        public async Task<RabbitMqPublishChannelRental> AcquirePublishChannelAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();

            await _publishPoolGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_publishChannels.TryTake(out var channel) || !channel.IsOpen)
                {
                    channel?.Dispose();
                    channel = await CreatePublishChannelAsync(cancellationToken).ConfigureAwait(false);
                }

                return new RabbitMqPublishChannelRental(this, channel);
            }
            catch
            {
                _publishPoolGate.Release();
                throw;
            }
        }

        // INVARIANT: only ever called while the receive gate is held (from RunOnReceiveChannelAsync,
        // StartReceivingAsync, or RecreateReceiveChannelAsync). When it (re)creates the channel it ALSO bumps the
        // epoch and re-runs the stored consume-registration delegate against the new channel with the bumped
        // epoch, as one atomic gated event, so the re-registered consumer always closes over the current epoch.
        private async Task<IChannel> EnsureReceiveChannelAsync(CancellationToken cancellationToken)
        {
            if (_receiveChannel is { IsOpen: true })
            {
                return _receiveChannel;
            }

            await RecreateReceiveChannelAsync(cancellationToken).ConfigureAwait(false);
            return _receiveChannel;
        }

        // INVARIANT: only ever called while the receive gate is held. Disposes any existing receive channel,
        // creates a fresh one, bumps the epoch, then re-runs the stored consume-registration delegate (when set)
        // against the new channel with the bumped epoch. The bump and the re-registration are this single gated
        // event, so a delivery the new consumer stamps always carries the epoch of the channel that delivered it.
        private async Task RecreateReceiveChannelAsync(CancellationToken cancellationToken)
        {
            if (_receiveChannel is not null)
            {
                await _receiveChannel.DisposeAsync().ConfigureAwait(false);
                _receiveChannel = null;
            }

            var connection = await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);
            var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            await channel.BasicQosAsync(prefetchSize: 0,
                                        prefetchCount: (ushort)Math.Max(1, _options.Prefetch),
                                        global: false,
                                        cancellationToken: cancellationToken).ConfigureAwait(false);

            _receiveChannel = channel;
            Interlocked.Increment(ref _receiveChannelEpoch);

            // Re-register the consumer on the fresh channel with the freshly-bumped epoch. Null only before
            // StartReceivingAsync stores the delegate (cold start has nothing to re-register yet).
            if (_registerConsumer is not null)
            {
                await _registerConsumer(_receiveChannel, Interlocked.Read(ref _receiveChannelEpoch), cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<IChannel> CreatePublishChannelAsync(CancellationToken cancellationToken)
        {
            var connection = await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);
            var options = new CreateChannelOptions(publisherConfirmationsEnabled: true,
                                                   publisherConfirmationTrackingEnabled: true);
            return await connection.CreateChannelAsync(options, cancellationToken).ConfigureAwait(false);
        }

        private async Task<IConnection> EnsureConnectionAsync(CancellationToken cancellationToken)
        {
            var connection = _connection;
            if (connection is { IsOpen: true })
            {
                return connection;
            }

            await _connectionInitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_connection is { IsOpen: true })
                {
                    return _connection;
                }

                if (_connection is not null)
                {
                    _connection.RecoverySucceededAsync -= OnRecoverySucceededAsync;
                    await _connection.DisposeAsync().ConfigureAwait(false);
                    _connection = null;
                }

                var factory = CreateConnectionFactory();
                _connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);

                // AutomaticRecoveryEnabled is true (connection/channel transport recovers) but TopologyRecoveryEnabled
                // is false, so the client does NOT re-bind the old consumer. Subscribe so each successful recovery
                // recreates the receive channel, bumps the epoch, and re-registers the consumer under the gate — the
                // source OWNS consumer lifecycle. This makes both the stale-epoch false-ack AND the stale-closure
                // no-op-settle impossible by construction (see the type remarks).
                _connection.RecoverySucceededAsync += OnRecoverySucceededAsync;
                return _connection;
            }
            finally
            {
                _connectionInitGate.Release();
            }
        }

        // INVARIANT: recreates the receive channel, bumps the epoch, and re-registers the consumer under the SAME
        // gate RunOnReceiveChannelAsync holds, all as one atomic event. The bump is atomic against an in-flight
        // settlement (a delivery tag captured under a pre-recovery epoch can never equal the post-recovery epoch,
        // so the stale settle no-ops) AND the re-registration runs only after the bump, so the new consumer stamps
        // the new epoch — closing both the false-ack and the stale-closure no-op-settle windows. Forces a recreate
        // (RecreateReceiveChannelAsync, not EnsureReceiveChannelAsync) because with topology recovery off the
        // transport-recovered channel may report IsOpen but carries NO consumer; an early-return would leave it
        // consumer-less. Guards against firing after disposal — the gate is disposed in DisposeAsync, and a recovery
        // callback can still be queued at that point — so a late recovery is dropped rather than throwing
        // ObjectDisposedException out of the library's event dispatch.
        private async Task OnRecoverySucceededAsync(object sender, AsyncEventArgs eventArgs)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                await _receiveChannelGate.WaitAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The source was disposed between the _disposed check and the wait; nothing left to recreate.
                return;
            }

            try
            {
                // Re-check under the gate: DisposeAsync may have run between the wait acquiring and here, in which
                // case the channel is gone and recreating it would resurrect resources past disposal.
                if (_disposed)
                {
                    return;
                }

                await RecreateReceiveChannelAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _receiveChannelGate.Release();
            }
        }

        private ConnectionFactory CreateConnectionFactory()
        {
            var factory = new ConnectionFactory
            {
                // Connection/channel transport recovery stays on, but TOPOLOGY recovery is off: the source owns
                // consumer re-registration on recovery (RecreateReceiveChannelAsync), so the client must NOT
                // silently re-bind the old consumer under the stale pre-recovery epoch. This is the closed-by-
                // construction guarantee that a delivery's stamped epoch equals its delivering session's epoch.
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = false
            };

            if (!string.IsNullOrWhiteSpace(_options.Uri))
            {
                factory.Uri = new Uri(_options.Uri);
            }

            if (!string.IsNullOrWhiteSpace(_options.HostName))
            {
                factory.HostName = _options.HostName;
            }

            if (!string.IsNullOrWhiteSpace(_options.UserName))
            {
                factory.UserName = _options.UserName;
            }

            if (!string.IsNullOrWhiteSpace(_options.Password))
            {
                factory.Password = _options.Password;
            }

            return factory;
        }

        // INVARIANT: invoked only by RabbitMqPublishChannelRental.DisposeAsync to return a rented channel.
        // A rental can outlive the source (the source is disposed while a publish is still in flight): in that
        // case _publishPoolGate has already been disposed by DisposeAsync, so releasing it would throw
        // ObjectDisposedException out of the rental's DisposeAsync. Dispose the orphaned channel and return
        // WITHOUT touching the disposed semaphore — the pool is gone, so there is nothing to release back into.
        internal void ReturnPublishChannel(IChannel channel)
        {
            if (_disposed)
            {
                channel?.Dispose();
                return;
            }

            if (channel is { IsOpen: true })
            {
                _publishChannels.Add(channel);
            }
            else
            {
                channel?.Dispose();
            }

            _publishPoolGate.Release();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_receiveChannel is not null)
            {
                await _receiveChannel.DisposeAsync().ConfigureAwait(false);
                _receiveChannel = null;
            }

            while (_publishChannels.TryTake(out var publishChannel))
            {
                await publishChannel.DisposeAsync().ConfigureAwait(false);
            }

            if (_connection is not null)
            {
                // Unsubscribe BEFORE disposing so no recovery callback can be dispatched into a half-torn-down
                // source; combined with the _disposed flag (set above) and the ObjectDisposedException guard in
                // the handler, this guarantees no handler touches the gate after it is disposed below.
                _connection.RecoverySucceededAsync -= OnRecoverySucceededAsync;
                await _connection.DisposeAsync().ConfigureAwait(false);
                _connection = null;
            }

            _connectionInitGate.Dispose();
            _receiveChannelGate.Dispose();
            _publishPoolGate.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(RabbitMqConnectionSource));
            }
        }
    }

    /// <summary>
    /// A rented publish channel from the <see cref="RabbitMqConnectionSource"/> pool. Disposing the
    /// rental returns the underlying channel to the pool; the channel must not be used after disposal.
    /// </summary>
    public sealed class RabbitMqPublishChannelRental : IAsyncDisposable
    {
        private readonly RabbitMqConnectionSource _source;
        private bool _returned;

        internal RabbitMqPublishChannelRental(RabbitMqConnectionSource source, IChannel channel)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            Channel = channel ?? throw new ArgumentNullException(nameof(channel));
        }

        /// <summary>The rented publish channel, with publisher confirms enabled.</summary>
        public IChannel Channel { get; }

        public ValueTask DisposeAsync()
        {
            if (_returned)
            {
                return default;
            }

            _returned = true;
            _source.ReturnPublishChannel(Channel);
            return default;
        }
    }
}
