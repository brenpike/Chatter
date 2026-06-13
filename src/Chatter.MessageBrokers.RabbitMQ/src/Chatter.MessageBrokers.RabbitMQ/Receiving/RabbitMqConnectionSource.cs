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
    /// epoch and the channel atomically. INVARIANT: the epoch is ALSO incremented on every successful
    /// automatic recovery (RabbitMQ.Client transparently recovers the SAME <see cref="IChannel"/>, so the
    /// (re)create path above does not run and would otherwise leave the epoch stale), closing the
    /// post-recovery false-ack window where an in-flight pre-recovery delivery tag — invalid on the recovered
    /// session — would otherwise pass the stale-epoch settle guard.
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

        // INVARIANT: only ever called from RunOnReceiveChannelAsync while the receive gate is held.
        private async Task<IChannel> EnsureReceiveChannelAsync(CancellationToken cancellationToken)
        {
            if (_receiveChannel is { IsOpen: true })
            {
                return _receiveChannel;
            }

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
            return _receiveChannel;
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

                // AutomaticRecoveryEnabled is true, so RabbitMQ.Client transparently recovers the SAME receive
                // IChannel on reconnect: EnsureReceiveChannelAsync then early-returns without recreating it, so the
                // epoch would stay stale. Subscribe so each successful recovery advances the epoch, invalidating any
                // delivery tag captured under the pre-recovery epoch (the false-ack guard the epoch exists for).
                _connection.RecoverySucceededAsync += OnRecoverySucceededAsync;
                return _connection;
            }
            finally
            {
                _connectionInitGate.Release();
            }
        }

        // INVARIANT: advances the receive-channel epoch under the SAME gate RunOnReceiveChannelAsync holds, so the
        // bump is atomic against an in-flight settlement: a delivery tag captured under a pre-recovery epoch can
        // never equal the post-recovery epoch, so the stale settle no-ops. Guards against firing after disposal —
        // the gate is disposed in DisposeAsync, and a recovery callback can still be queued at that point — so a
        // late recovery is dropped rather than throwing ObjectDisposedException out of the library's event dispatch.
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
                // The source was disposed between the _disposed check and the wait; nothing left to advance.
                return;
            }

            try
            {
                Interlocked.Increment(ref _receiveChannelEpoch);
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
                AutomaticRecoveryEnabled = true
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
