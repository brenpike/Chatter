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
    /// INVARIANT: the receive channel is only ever touched while the receive gate is held; AMQP channels are not
    /// thread-safe. INVARIANT: the receive-channel epoch is incremented whenever the receive channel is
    /// (re)created, and it is read under the same gate that hands out the channel, so callers observe the
    /// epoch and the channel atomically.
    /// INVARIANT (closed-by-construction epoch lifecycle — ADR 0002, PRESERVED): the source OWNS the receive channel
    /// and consumer lifecycle. Connection-level automatic recovery stays ENABLED but TOPOLOGY (consumer) recovery is
    /// DISABLED, so the client never silently re-binds the old consumer. On every receive-channel
    /// (re)creation — cold start, lazy recreate, and automatic recovery — the source, UNDER THE RECEIVE GATE
    /// and as ONE atomic event, disposes any old channel, creates a fresh one, increments the epoch, and
    /// re-runs the stored consume-registration delegate against the new channel with the freshly-bumped
    /// epoch. Because the bump and the re-registration are the SAME gated event, a delivery's stamped epoch
    /// always equals the epoch of the session that delivered it. A pre-recovery in-flight delivery carries
    /// the old epoch (correctly no-ops at settle); a post-recovery delivery is stamped by the freshly
    /// re-registered consumer (correctly settles). This eliminates both the recovery-stale-epoch false-ack
    /// and the topology-recovery stale-closure no-op-settle classes by construction, race-free. The epoch
    /// lifecycle below is UNCHANGED by the lifecycle-authority collapse — the connection-create/dispose unification
    /// only narrows WHEN the connection materializes, never HOW the channel epoch is bumped/re-registered.
    ///
    /// INVARIANT (single monotonic lifecycle authority — ADR 0003): the source's liveness is ONE monotonic integer
    /// (<see cref="_lifecycle"/>: Live -&gt; Disposing -&gt; Disposed) advanced only by Interlocked.CompareExchange,
    /// adapting the BrokeredMessageReceiver lifecycle-state-machine precedent. There is NO standalone _disposed bool
    /// read independently by separate mutual-exclusion domains. The connection is CREATED and DISPOSED under the SAME
    /// _receiveChannelGate, so create and dispose are mutually exclusive BY CONSTRUCTION (the prior split — create under
    /// a dedicated init gate, dispose under the receive gate — could never let dispose exclude create). PUBLISH-OR-
    /// SURRENDER HANDOFF: an operation suspended mid-resource-creation across a completing DisposeAsync re-checks the
    /// lifecycle UNDER the gate before publishing the resource (assigning _connection / subscribing recovery / returning
    /// a rental); if not Live it SURRENDERS — disposes the just-created resource and throws ObjectDisposedException —
    /// rather than resurrecting a connection/channel past disposal. So a resource reachable past teardown is
    /// UNREPRESENTABLE rather than guarded at each site.
    /// </remarks>
    public sealed class RabbitMqConnectionSource : IRabbitMqConnectionSource
    {
        // INVARIANT: prefetch must be >= MaxConcurrentCalls so the broker keeps enough unacknowledged
        // deliveries in flight to saturate the core's workers. MaxConcurrentCalls is not available at
        // this layer; STEP-004/STEP-006 finalize QoS wiring if a larger floor is required. Until then
        // the configured Prefetch (default 1) is applied as-is.
        private const int _defaultPublishChannelPoolCapacity = 8;

        // INVARIANT (single monotonic lifecycle authority): the SOLE liveness state, advanced ONLY via
        // Interlocked.CompareExchange and totally ordered as written. Replaces the former `bool _disposed` that each
        // mutual-exclusion domain read independently — a model where disposal could never exclude connection-creation
        // because creation and disposal lived under different locks and read _disposed at different, un-composed points.
        //   Live      - the source is serving: connections/channels may be created, deliveries settled, publishes rented.
        //   Disposing - DisposeAsync has been admitted (CAS Live->Disposing is the SINGLE admission gate) and is
        //               quiescing under the receive gate. Already past the point of no return; no new resource may be
        //               published. Reads observe not-Live, so ThrowIfNotLive throws and the publish-or-surrender handoff
        //               surrenders any in-flight resource.
        //   Disposed  - quiesce complete; written MONOTONICALLY under the receive gate after teardown.
        // ThrowIfNotLive throws ObjectDisposedException for Disposing and Disposed alike, so external callers see the
        // same observable contract the prior `_disposed` bool produced.
        private const int LifecycleLive = 0;
        private const int LifecycleDisposing = 1;
        private const int LifecycleDisposed = 2;

        private readonly RabbitMqOptions _options;
        private readonly int _publishChannelPoolCapacity;
        // INVARIANT (one gate owns the connection lifecycle): _receiveChannelGate now serializes BOTH the receive
        // channel AND the connection CREATE/DISPOSE. The former _connectionInitGate is GONE — folding connection
        // creation under this same gate makes "create the connection" and "dispose the connection" mutually exclusive
        // by construction, closing the cross-lock whack-a-mole the prior split produced.
        private readonly SemaphoreSlim _receiveChannelGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _publishPoolGate;
        private readonly ConcurrentBag<IChannel> _publishChannels = new ConcurrentBag<IChannel>();

        private IConnection _connection;
        private IChannel _receiveChannel;
        private long _receiveChannelEpoch;
        private int _lifecycle = LifecycleLive;

        // TEST SEAM (InternalsVisibleTo-only, default null): when set, REPLACES the real factory.CreateConnectionAsync
        // call inside EnsureConnectionAsync — it is the overridable connection-create STEP. A test can both await its
        // own suspend-gate (deterministically pinning an op mid-connection-creation while a DisposeAsync completes) and
        // return a fake IConnection, all broker-free. The lifecycle surrender re-check runs AFTER this step resumes, on
        // the connection it returns, exactly as for the production factory call. No public/DI surface; production
        // leaves it null and the real factory path runs unchanged.
        internal Func<CancellationToken, Task<IConnection>> _createConnectionForTest;

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

        // INVARIANT (lifecycle authority): the source is torn (no longer Live) once DisposeAsync has been admitted.
        // Volatile.Read so a thread observing the CAS publication of Disposing/Disposed sees it without further fences.
        private bool IsTorn => Volatile.Read(ref _lifecycle) != LifecycleLive;

        // INVARIANT: throws ObjectDisposedException (the SAME observable type the prior `_disposed` bool produced) when
        // the source is not Live, so existing callers/tests are unaffected by the bool -> tri-state migration.
        private void ThrowIfNotLive()
        {
            if (Volatile.Read(ref _lifecycle) != LifecycleLive)
            {
                throw new ObjectDisposedException(nameof(RabbitMqConnectionSource));
            }
        }

        // INVARIANT (lifecycle observed on both sides): EVERY receive-gated entrypoint routes its gate acquisition
        // through this single coordination primitive, so no raw _receiveChannelGate.WaitAsync survives outside it.
        // It checks the lifecycle BEFORE the wait (fail-fast for an already-torn source) AND re-checks UNDER the gate
        // AFTER the wait (a caller queued behind DisposeAsync observes not-Live once teardown releases the gate and
        // throws rather than resurrecting a connection/channel or overwriting _registerConsumer on a torn singleton).
        // The gate is ALWAYS released. Gated bodies run the existing logic verbatim; this helper wraps gate acquisition
        // only and never touches the epoch-lifecycle / recovery recreate logic.
        private async Task<TResult> RunReceiveGatedAsync<TResult>(Func<CancellationToken, Task<TResult>> body,
                                                                  CancellationToken cancellationToken)
        {
            ThrowIfNotLive();

            await _receiveChannelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (IsTorn)
                {
                    throw new ObjectDisposedException(nameof(RabbitMqConnectionSource));
                }

                return await body(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _receiveChannelGate.Release();
            }
        }

        private Task RunReceiveGatedAsync(Func<CancellationToken, Task> body, CancellationToken cancellationToken)
            => RunReceiveGatedAsync<object>(async ct =>
            {
                await body(ct).ConfigureAwait(false);
                return null;
            }, cancellationToken);

        public Task<TResult> RunOnReceiveChannelAsync<TResult>(Func<IChannel, long, Task<TResult>> operation,
                                                               CancellationToken cancellationToken)
        {
            if (operation is null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            return RunReceiveGatedAsync(async ct =>
            {
                var channel = await EnsureReceiveChannelAsync(ct).ConfigureAwait(false);
                return await operation(channel, Interlocked.Read(ref _receiveChannelEpoch)).ConfigureAwait(false);
            }, cancellationToken);
        }

        public Task StartReceivingAsync(Func<IChannel, long, CancellationToken, Task> registerConsumer,
                                        CancellationToken cancellationToken)
        {
            if (registerConsumer is null)
            {
                throw new ArgumentNullException(nameof(registerConsumer));
            }

            return RunReceiveGatedAsync(async ct =>
            {
                // Store the delegate INSIDE the gated body so a caller queued behind DisposeAsync — which the helper's
                // post-wait lifecycle re-check rejects — cannot overwrite _registerConsumer on a torn singleton.
                // EnsureReceiveChannelAsync re-runs it on this and every later (re)creation: it bumps the epoch and
                // invokes the stored delegate against the fresh channel with the bumped epoch, so the initial
                // registration is itself the first atomic bump+register event.
                _registerConsumer = registerConsumer;
                await EnsureReceiveChannelAsync(ct).ConfigureAwait(false);
            }, cancellationToken);
        }

        // INVARIANT (publish-permit lifecycle-observed-on-both-sides + exactly-one-release): checks the lifecycle
        // BEFORE the wait (fail-fast) AND re-checks UNDER the permit AFTER the wait. A waiter stranded behind a
        // saturated pool when DisposeAsync runs is woken by the unconditional Release in ReturnPublishChannel / the
        // drain, then observes not-Live here, RELEASES the permit it just acquired, and throws — so no publish waiter
        // hangs across teardown. EXACTLY-ONE-RELEASE per acquired permit on every exit path: the not-Live recheck
        // (pre- and post-create), a create failure, and the surrender recheck each release here; a successful acquire
        // transfers the permit to the rental, which releases it via ReturnPublishChannel. No path double-releases
        // (SemaphoreFullException) nor under-releases.
        //
        // PUBLISH-PATH LOCK ORDERING (ADR 0003): the permit is taken FIRST, then the connection is read-or-created
        // through AcquireConnectionUnderReceiveGateAsync, which acquires the receive gate ONLY long enough to
        // read-or-create the _connection object (lifecycle-checked under the gate) and RELEASES it before returning.
        // The publish CHANNEL creation (CreateChannelAsync w/ confirms) then runs OUTSIDE the receive gate on the
        // returned connection. Blocking publish I/O therefore NEVER runs while the receive gate is held — publishing
        // never contends with the receive/ack gate, and there is no nested-gate deadlock (the receive gate is always
        // acquired-then-released before the permit-held channel I/O).
        //
        // PUBLISH-OR-SURRENDER HANDOFF: after the publish channel is created, the lifecycle is re-checked. If a
        // DisposeAsync completed while this op was suspended creating the channel, the op SURRENDERS — disposes the
        // just-created channel, releases the permit, and throws ObjectDisposedException — rather than returning a live
        // rental on a torn-down source.
        public async Task<RabbitMqPublishChannelRental> AcquirePublishChannelAsync(CancellationToken cancellationToken)
        {
            ThrowIfNotLive();

            await _publishPoolGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            if (IsTorn)
            {
                _publishPoolGate.Release();
                throw new ObjectDisposedException(nameof(RabbitMqConnectionSource));
            }

            IChannel channel = null;
            try
            {
                if (!_publishChannels.TryTake(out channel) || !channel.IsOpen)
                {
                    channel?.Dispose();
                    channel = await CreatePublishChannelAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                _publishPoolGate.Release();
                throw;
            }

            // PUBLISH-OR-SURRENDER: a DisposeAsync that completed while the channel was being created leaves the source
            // torn. Surrender the just-created/taken channel rather than returning a rental on a torn-down source.
            if (IsTorn)
            {
                channel.Dispose();
                _publishPoolGate.Release();
                throw new ObjectDisposedException(nameof(RabbitMqConnectionSource));
            }

            return new RabbitMqPublishChannelRental(this, channel);
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

            // EnsureConnectionAsync runs UNDER the receive gate (this method is only ever called gated), so connection
            // create is serialized against connection dispose by the same gate — closed by construction.
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
            // Read-or-create the connection under the receive gate, then RELEASE the gate before the publish channel
            // I/O (see AcquirePublishChannelAsync PUBLISH-PATH LOCK ORDERING) so blocking publish I/O never runs while
            // the receive gate is held.
            var connection = await AcquireConnectionUnderReceiveGateAsync(cancellationToken).ConfigureAwait(false);
            var options = new CreateChannelOptions(publisherConfirmationsEnabled: true,
                                                   publisherConfirmationTrackingEnabled: true);
            return await connection.CreateChannelAsync(options, cancellationToken).ConfigureAwait(false);
        }

        // INVARIANT (publish-path connection access): acquires the receive gate ONLY to read-or-create the _connection
        // object (lifecycle-checked under the gate via EnsureConnectionAsync, which serializes against connection
        // dispose) and RELEASES the gate before returning. The caller (CreatePublishChannelAsync) then creates the
        // publish channel OUTSIDE the gate. This keeps connection create/dispose mutually exclusive while ensuring no
        // blocking publish channel I/O is held under the receive gate.
        private async Task<IConnection> AcquireConnectionUnderReceiveGateAsync(CancellationToken cancellationToken)
        {
            ThrowIfNotLive();

            await _receiveChannelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (IsTorn)
                {
                    throw new ObjectDisposedException(nameof(RabbitMqConnectionSource));
                }

                return await EnsureConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _receiveChannelGate.Release();
            }
        }

        // INVARIANT: only ever called while the receive gate is held (from RecreateReceiveChannelAsync on the receive
        // path, or AcquireConnectionUnderReceiveGateAsync on the publish path). Connection CREATE is thus serialized
        // under the SAME gate that owns connection DISPOSE (DisposeAsync), so create and dispose are mutually exclusive
        // BY CONSTRUCTION — the former dedicated _connectionInitGate is gone.
        //
        // PUBLISH-OR-SURRENDER HANDOFF: factory.CreateConnectionAsync is the one blocking await inside the gate. After
        // it resumes, the lifecycle is RE-CHECKED before assigning _connection or subscribing RecoverySucceededAsync.
        // (Because the gate is held throughout, the only way to observe not-Live here is a DisposeAsync that ran to
        // completion BEFORE this op acquired the gate yet whose admission CAS the op missed — defensively closed: the
        // op disposes the just-created connection, does NOT subscribe, does NOT assign _connection, and throws. So a
        // connection created across a completing disposal SURRENDERS rather than resurrecting.)
        private async Task<IConnection> EnsureConnectionAsync(CancellationToken cancellationToken)
        {
            var connection = _connection;
            if (connection is { IsOpen: true })
            {
                return connection;
            }

            if (_connection is not null)
            {
                _connection.RecoverySucceededAsync -= OnRecoverySucceededAsync;
                await _connection.DisposeAsync().ConfigureAwait(false);
                _connection = null;
            }

            // The connection-create STEP: the test seam (when set) replaces the real factory call, so a test can
            // suspend mid-creation and inject a fake connection broker-free; otherwise the production factory runs.
            IConnection created;
            if (_createConnectionForTest is not null)
            {
                created = await _createConnectionForTest(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var factory = CreateConnectionFactory();
                created = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            }

            // PUBLISH-OR-SURRENDER: if disposal completed while the connection-create step was in flight,
            // surrender the just-created connection rather than assigning/subscribing it onto a torn-down source.
            if (IsTorn)
            {
                await created.DisposeAsync().ConfigureAwait(false);
                throw new ObjectDisposedException(nameof(RabbitMqConnectionSource));
            }

            _connection = created;

            // AutomaticRecoveryEnabled is true (connection/channel transport recovers) but TopologyRecoveryEnabled
            // is false, so the client does NOT re-bind the old consumer. Subscribe so each successful recovery
            // recreates the receive channel, bumps the epoch, and re-registers the consumer under the gate — the
            // source OWNS consumer lifecycle. This makes both the stale-epoch false-ack AND the stale-closure
            // no-op-settle impossible by construction (see the type remarks; ADR 0002 preserved).
            _connection.RecoverySucceededAsync += OnRecoverySucceededAsync;
            return _connection;
        }

        // INVARIANT: recreates the receive channel, bumps the epoch, and re-registers the consumer under the SAME
        // gate RunOnReceiveChannelAsync holds, all as one atomic event. The bump is atomic against an in-flight
        // settlement (a delivery tag captured under a pre-recovery epoch can never equal the post-recovery epoch,
        // so the stale settle no-ops) AND the re-registration runs only after the bump, so the new consumer stamps
        // the new epoch — closing both the false-ack and the stale-closure no-op-settle windows. Forces a recreate
        // (RecreateReceiveChannelAsync, not EnsureReceiveChannelAsync) because with topology recovery off the
        // transport-recovered channel may report IsOpen but carries NO consumer; an early-return would leave it
        // consumer-less.
        //
        // MUST stay a NO-OP-ON-DISPOSED: this runs inside the client's async event dispatch and MUST NOT throw out of
        // it (pinned by WhenRecreatingReceiveChannelOnRecovery.MustNotThrowWhenRecoveryFiresAfterDisposal). It routes
        // its gate acquisition through RunReceiveGatedAsync like every other gated entrypoint, but SWALLOWS the
        // ObjectDisposedException the helper throws when the post-wait re-check observes a torn source — that
        // disposed-after-the-fact case is a clean no-op for recovery, not an error. The pre-wait not-Live fast path
        // and the catch (kept as defense-in-depth) drop a late recovery rather than recreating resources past disposal.
        private async Task OnRecoverySucceededAsync(object sender, AsyncEventArgs eventArgs)
        {
            if (IsTorn)
            {
                return;
            }

            try
            {
                await RunReceiveGatedAsync(
                    ct => RecreateReceiveChannelAsync(ct),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The source was disposed before or during the gated recreate; nothing left to recreate. A clean
                // no-op so recovery never throws out of the client's async event dispatch.
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

        // INVARIANT (permit conservation): invoked only by RabbitMqPublishChannelRental.DisposeAsync to return a
        // rented channel. The permit acquired in AcquirePublishChannelAsync is ALWAYS released here — exactly once
        // per acquired permit — REGARDLESS of lifecycle state, so a publish waiter stranded behind a saturated pool at
        // teardown is woken (it then observes not-Live in AcquirePublishChannelAsync, releases, and throws) and no
        // permit is leaked. A rental can outlive the source (the source is disposed while a publish is still in
        // flight): in that case the returning channel is orphaned — it is disposed and NOT re-pooled (the pool is
        // being drained by DisposeAsync) — but the permit is still released. _publishPoolGate is NEVER disposed (see
        // DisposeAsync GATE LIFETIME), so releasing into the never-disposed semaphore is always safe.
        internal void ReturnPublishChannel(IChannel channel)
        {
            if (IsTorn || channel is not { IsOpen: true })
            {
                // Torn source: the pool is gone, so dispose the orphaned channel instead of re-pooling. Closed
                // channel: it cannot serve a future rental, so dispose rather than re-pool. Either way, fall through
                // to the unconditional Release below so the permit count is conserved.
                channel?.Dispose();
            }
            else
            {
                _publishChannels.Add(channel);
            }

            _publishPoolGate.Release();
        }

        // INVARIANT: teardown of the receive channel and connection is SERIALIZED through _receiveChannelGate — the
        // SAME gate that serializes RunOnReceiveChannelAsync / StartReceivingAsync / OnRecoverySucceededAsync AND the
        // connection CREATE (EnsureConnectionAsync). So connection create and connection dispose are mutually exclusive
        // by construction. Under the gate this writes _lifecycle = Disposed, unsubscribes the recovery handler, and
        // disposes the receive channel and the connection. A concurrent gated op (settle, cold start, recovery
        // recreate, publish-path connection read) either runs to completion BEFORE teardown acquires the gate, or
        // observes not-Live UNDER THE SAME GATE after teardown released it and short-circuits — there is no torn-down-
        // resource window for a gated op. Every internal await uses ConfigureAwait(false), so teardown cannot deadlock
        // on a captured context.
        //
        // SINGLE ADMISSION GATE: _lifecycle Live->Disposing is advanced via Interlocked.CompareExchange. A CAS loser
        // (observing not-Live) returns — DisposeAsync is idempotent. The winner proceeds to quiesce, then writes
        // Disposed monotonically under the gate.
        //
        // LIFECYCLE OBSERVED ON BOTH SIDES BY CONSTRUCTION: every receive-gated entrypoint (RunOnReceiveChannelAsync,
        // StartReceivingAsync, OnRecoverySucceededAsync, AcquireConnectionUnderReceiveGateAsync) acquires the receive
        // gate ONLY through a helper that checks the lifecycle before AND under the gate; AcquirePublishChannelAsync
        // re-checks under the acquired permit AND after channel creation (publish-or-surrender). So a caller queued
        // behind this DisposeAsync observes not-Live once teardown releases the gate/permit and throws
        // ObjectDisposedException rather than resurrecting a channel/connection, overwriting _registerConsumer, or
        // proceeding to publish past teardown. The publish permit is ALWAYS released, so a publish waiter stranded
        // behind a saturated pool at teardown is woken and throws instead of hanging.
        //
        // GATE LIFETIME: the surviving two SemaphoreSlims (_receiveChannelGate, _publishPoolGate) are NEVER disposed —
        // left for GC, mirroring the core BrokeredMessageReceiver._teardownGate ("GATE LIFETIME" comment in
        // src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Receiving/BrokeredMessageReceiver.cs). Disposing a gate
        // a concurrent waiter (a queued recovery callback, an in-flight AcquirePublishChannelAsync, or a rental
        // returning via ReturnPublishChannel) may still touch is the hazard; the lifecycle checks above narrow the
        // window but disposing the gate would still race that waiter, so the gates are left for GC. A SemaphoreSlim
        // used only via async WaitAsync (no timeout, no AvailableWaitHandle) allocates no native handle, so leaving it
        // for GC leaks nothing requiring deterministic release.
        public async ValueTask DisposeAsync()
        {
            // SINGLE ADMISSION GATE: only the thread that advances Live->Disposing proceeds; a loser is a clean no-op
            // (idempotent dispose).
            if (Interlocked.CompareExchange(ref _lifecycle, LifecycleDisposing, LifecycleLive) != LifecycleLive)
            {
                return;
            }

            // Serialize receive-channel + connection teardown against the gated ops. Writing _lifecycle = Disposed
            // UNDER the gate means a concurrent gated op that acquired the gate first completes before teardown
            // proceeds, and one that acquires it after teardown observes not-Live and short-circuits.
            await _receiveChannelGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_receiveChannel is not null)
                {
                    await _receiveChannel.DisposeAsync().ConfigureAwait(false);
                    _receiveChannel = null;
                }

                if (_connection is not null)
                {
                    // Unsubscribe BEFORE disposing so no recovery callback can be dispatched into a half-torn-down
                    // source; combined with the lifecycle state (Disposing, set above the gate via CAS; Disposed,
                    // written below under this gate) this guarantees a queued recovery callback either ran before this
                    // teardown or observes not-Live and short-circuits.
                    _connection.RecoverySucceededAsync -= OnRecoverySucceededAsync;
                    await _connection.DisposeAsync().ConfigureAwait(false);
                    _connection = null;
                }

                // MONOTONIC terminal write under the gate, after quiesce.
                Volatile.Write(ref _lifecycle, LifecycleDisposed);
            }
            finally
            {
                _receiveChannelGate.Release();
            }

            // Drain the publish pool OUTSIDE the receive gate (the publish pool is governed by _publishPoolGate, not
            // the receive gate). A rental still in flight returns via ReturnPublishChannel, which observes not-Live
            // (set above) and disposes its orphaned channel WITHOUT touching the bag's re-pool path, so the drain and a
            // late rental return cannot both add to / take from the bag in a way that loses a channel.
            while (_publishChannels.TryTake(out var publishChannel))
            {
                await publishChannel.DisposeAsync().ConfigureAwait(false);
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
