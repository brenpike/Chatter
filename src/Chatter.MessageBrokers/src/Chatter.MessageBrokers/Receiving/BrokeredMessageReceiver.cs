using Chatter.CQRS;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Recovery;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Receiving
{
    /// <summary>
    /// An infrastructure agnostic receiver of brokered messages of type <typeparamref name="TMessage"/>
    /// </summary>
    /// <typeparam name="TMessage">The type of messages the brokered message receiver accepts</typeparam>
    public class BrokeredMessageReceiver<TMessage> : IBrokeredMessageReceiver<TMessage>, IReceiverStartupSignal where TMessage : class, IMessage
    {
        private IMessagingInfrastructureReceiver _infrastructureReceiver;
        private readonly IMessagingInfrastructureProvider _infrastructureProvider;
        protected readonly ILogger<BrokeredMessageReceiver<TMessage>> _logger;
        protected ReceiverOptions _options;
        private bool _disposedValue;
        private SemaphoreSlim _concurrentMessagesSemaphore;
        CancellationTokenSource _messageReceiverLoopTokenSource;
        private Task _messageReceiverLoop;
        private int _maxConcurrentCalls = 1;

        // INVARIANT: explicit lifecycle state machine, advanced ONLY by Interlocked.CompareExchange. This is the single
        // admission/drop gate for teardown; it replaces the former bool IsReceiving + int _teardownState pair, which
        // could not represent the in-startup window (NotStarted vs Live was a single boolean, so a teardown landing
        // after _infrastructureReceiver was assigned but before go-live had no distinct state to observe). The four
        // states are totally ordered as written:
        //   NotStarted - StartReceiverImpl has not yet entered its startup body. A teardown here has nothing to quiesce
        //                (no infra receiver, no loop, no workers); it is a structural no-op that MUST leave admission
        //                untouched for the genuine post-startup teardown (preserves b91b751: a DI singleton Dispose()d
        //                during graph resolution must not latch out the host's later await-using DisposeAsync).
        //   Starting   - StartReceiverImpl has begun startup. OWNERSHIP/HANDOFF MODEL: the infrastructure receiver is
        //                resolved and InitializeAsync'd into a STARTUP-OWNED LOCAL, never published to the shared
        //                _infrastructureReceiver field during this window — so the field is still null and NO infra
        //                object, partial or complete, is reachable by teardown until the single atomic handoff at the
        //                go-live seam. A teardown observed in Starting therefore quiesces a null infra (a structural
        //                no-op), latches TornDown, and records its strength; StartReceiverImpl observes that TornDown at
        //                the handoff and SURRENDERS its local at the recorded strength. This dissolves the "teardown
        //                reaches infra during the Starting window" race class by construction rather than by guarding
        //                each partial-state access.
        //   Live       - the receive loop is running and ReceivingStarted has been published. Steady state.
        //   TornDown   - a QuiesceCoreAsync ran to completion UNDER THE GATE and latched the receiver terminally; further
        //                teardowns observe TornDown under the gate and only escalate infra disposal if a stronger
        //                strength was requested. NotStarted/Starting/Live advance via Interlocked.CompareExchange;
        //                TornDown is written ONLY under _teardownGate after a successful quiesce.
        // Only NotStarted is a no-op. Starting and Live both admit a real quiesce.
        private const int LifecycleNotStarted = 0;
        private const int LifecycleStarting = 1;
        private const int LifecycleLive = 2;
        private const int LifecycleTornDown = 3;
        private int _lifecycle = LifecycleNotStarted;

        // INVARIANT: a single SemaphoreSlim(1,1) serializes the teardown critical section AND the startup publish-or-
        // surrender handoff, so they never interleave. Every teardown entrypoint (StopReceiver/DisposeAsync/synchronous
        // Dispose) acquires this gate before running QuiesceCoreAsync; StartReceiverImpl acquires it ONLY for the atomic
        // handoff of its startup-owned infra local (field writes, primitive construction, CAS — NO blocking I/O await).
        //
        // OWNERSHIP/HANDOFF MODEL: StartReceiverImpl resolves and InitializeAsync's the infra receiver into a LOCAL with
        // the gate NOT held, and never publishes it to _infrastructureReceiver before that gated handoff. So during the
        // Starting window the shared field is null and NOTHING reachable by teardown exists — teardown cannot reach a
        // not-yet-published, startup-owned infra object. Because both sides take the SAME gate, a teardown racing startup
        // either runs entirely BEFORE the handoff (it sees a null field, quiesces nothing, latches TornDown + records its
        // strength, and the startup handoff then SURRENDERS its local at that strength) or entirely AFTER go-live (it sees
        // the FULL published set) — there is no window where it observes a half-built set. Holding NO blocking await under
        // the gate also means a hung InitializeAsync can never block a concurrent teardown. The race classes (stale
        // single-flight loser, startup/teardown rendezvous-orphan, sync-dispose latch, AND the "teardown reaches infra
        // during the Starting window") are closed by CONSTRUCTION here rather than by patching each path.
        //
        // FAULT-RESETTABLE: a QuiesceCoreAsync that THROWS under the gate PROPAGATES without latching _lifecycle to
        // TornDown — the gate is released in the finally and the terminal field is never set, so the NEXT caller to
        // acquire the gate re-runs the quiesce cleanly. There is no completion source to strand. Only a SUCCESSFUL
        // QuiesceCoreAsync writes _lifecycle = TornDown (the monotonic terminal field, written ONLY under the gate).
        //
        // DEADLOCK-SAFETY: SemaphoreSlim.WaitAsync()/Wait() and the synchronous Dispose path's GetAwaiter().GetResult()
        // capture no SynchronizationContext; the loop and workers run on Task.Run (default scheduler) and every internal
        // await uses ConfigureAwait(false), so an async teardown holding the gate never needs the blocked synchronous
        // thread to make progress. The synchronous wait is therefore bounded by teardown completion and cannot deadlock.
        //
        // GATE LIFETIME: NEVER disposed — left for GC. Disposing it would risk a concurrent teardown calling WaitAsync on
        // a disposed gate under interleaved Dispose/DisposeAsync (the _disposedValue fast path narrows but cannot fully
        // close that race). A SemaphoreSlim used only via async WaitAsync (no timeout, no AvailableWaitHandle) allocates
        // no native handle, so leaving it for GC leaks nothing requiring deterministic release.
        private readonly SemaphoreSlim _teardownGate = new SemaphoreSlim(1, 1);

        // INVARIANT: EPOCH-SCOPED teardown strength (NOT process-monotonic). Stop (weakest) < DisposeAsync/DisposeSync
        // (strongest). Recorded by RaiseRequestedStrength AFTER the NotStarted no-op fast-path in QuiesceAsync, so a
        // premature teardown of a still-NotStarted receiver (the DI-singleton Dispose during graph resolution) records
        // NOTHING and leaves this field at None — the receiver stays restartable in a fresh epoch and a later genuine
        // teardown re-derives strength from scratch. WITHIN one epoch the recording is still monotonic (Interlocked.Max
        // by CAS), so a Stop-then-Dispose racing in ONE teardown still escalates to Dispose: both record after the no-op
        // gate, both before the completion reset. The admitted quiesce records the strongest strength requested by ANY
        // participating entrypoint; when a Dispose-strength entrypoint participates in a race a Stop winner ran, the
        // Dispose observes the Stop's completion and THEN escalates so the infra is actually Disposed, never left merely
        // Stopped (dissolves the disposal-strength-loss defect). RESET to None when an epoch's teardown COMPLETES against
        // a REAL (non-null) infra receiver — under the SAME single _teardownGate acquisition, after QuiesceCoreAsync +
        // EscalateInfrastructureDisposalIfRequiredAsync — so the strength does not leak across epochs (an earlier Stop
        // epoch's recording cannot later escalate a genuine StopReceiver into a Dispose). The reset is conditioned on a
        // non-null infra so it NEVER pre-empts the Starting-window surrender consumer: a teardown that raced the Starting
        // window quiesced a null field (disposed nothing real) and must leave the strength intact for StartReceiverImpl's
        // separate-gate surrender to read. A throwing QuiesceCoreAsync/escalation leaves the strength UNRESET so the retry
        // re-derives it.
        private const int TeardownStrengthNone = 0;
        private const int TeardownStrengthStop = 1;
        private const int TeardownStrengthDispose = 2;
        private int _requestedTeardownStrength = TeardownStrengthNone;

        // INVARIANT: latched 0->1 exactly once when the infrastructure receiver has actually been DISPOSED (not merely
        // Stopped). SINGLE WRITER: the flag is written 0->1 by exactly ONE primitive, TryClaimAndDisposeInfrastructureAsync.
        // Both Dispose-strength teardown sites — EscalateInfrastructureDisposalIfRequiredAsync (strength-escalation path)
        // and the Dispose branch of TearDownInfrastructureAsync (strength-aware teardown inside QuiesceCoreAsync) — delegate
        // their claim+dispose to that one primitive instead of running their own inline CAS, so the infra is disposed at
        // most once across a Stop-then-Dispose escalation and the construct exists in one place. The synchronous
        // Dispose(bool) field-null is the only OTHER site and merely READS this flag (== 1) — it never writes it 0->1.
        // GATE-SERIALIZED CLAIM: the primitive is called ONLY under _teardownGate (the escalation is folded into
        // QuiesceAsync's gated try, and TearDownInfrastructureAsync runs from QuiesceCoreAsync which is itself gated). The
        // claim->dispose->reset sequence inside the primitive is therefore atomic under the gate: a concurrent teardown
        // blocked on the gate can NEVER observe an in-flight 0->1 claim as a completed disposal, so a faulting in-flight
        // DisposeAsync that resets the claim 1->0 cannot leave a loser having already latched _disposedValue / nulled the
        // infra over the in-flight claim. The synchronous Dispose(bool) field-null keys off this flag == 1, set ONLY by a
        // dispose that actually completed under the gate (never by a still-in-flight claim, and never by a null infra —
        // the primitive captures-then-null-checks BEFORE claiming).
        private int _infrastructureDisposed;

        // INVARIANT: every per-message worker task admitted by the concurrency semaphore is tracked here for the
        // lifetime of its processing. The loop prunes completed tasks each turn (bounded accumulation) and the
        // shutdown path (StopReceiver/Dispose) drains the live set before disposing the semaphore or token source,
        // so no in-flight worker can touch a disposed SemaphoreSlim / CancellationTokenSource. Guarded by its own
        // lock because the loop thread adds/prunes while worker continuations remove on completion.
        private readonly HashSet<Task> _inFlightTasks = new HashSet<Task>();
        private readonly object _inFlightTasksLock = new object();

        // INVARIANT: holds the FIRST CriticalReceiverException observed inside a worker task. Workers run
        // fire-and-forget relative to the receive loop, so a critical fault cannot be surfaced by awaiting them
        // inline; instead the faulting worker publishes it here via Interlocked.CompareExchange (first writer wins)
        // and the loop observes it each turn, rethrowing on the loop thread so the existing outer
        // catch (CriticalReceiverException) fires the _criticalFailureNotifier.Notify path exactly as before.
        private CriticalReceiverException _workerCriticalFault;
        private readonly MessageBrokerOptions _messageBrokerOptions;
        private readonly IRecoveryStrategy _recoveryStrategy;
        private readonly IReceivedMessageDispatcher _receivedMessageDispatcher;
        private readonly IMaxReceivesExceededAction _failedRecoveryAction;
        private readonly ICriticalFailureNotifier _criticalFailureNotifier;

        // INVARIANT: completed exactly once, at the go-live seam in StartReceiverImpl (coincident with the
        // Starting -> Live lifecycle advance). Backs the ReceivingStarted startup-completion signal so callers gate on
        // go-live without polling IsReceiving. RunContinuationsAsynchronously keeps the awaiter's continuation off the
        // receive-loop start path.
        private readonly TaskCompletionSource<bool> _receivingStartedSource =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Creates a brokered message receiver that receives messages of <typeparamref name="TMessage"/>
        /// </summary>
        /// <param name="infrastructureProvider">The message broker infrastructure</param>
        /// <param name="serviceFactory">The service scope factory used to create a new scope when a message is received from the messaging infrastructure.</param>
        /// <param name="logger">Provides logging capability</param>
        public BrokeredMessageReceiver(IMessagingInfrastructureProvider infrastructureProvider,
                                       MessageBrokerOptions messageBrokerOptions,
                                       ILogger<BrokeredMessageReceiver<TMessage>> logger,
                                       IMaxReceivesExceededAction recoveryAction,
                                       ICriticalFailureNotifier criticalFailureNotifier,
                                       IRecoveryStrategy recoveryStrategy,
                                       IReceivedMessageDispatcher receivedMessageDispatcher)
        {
            if (infrastructureProvider is null)
            {
                throw new ArgumentNullException(nameof(infrastructureProvider));
            }

            _infrastructureProvider = infrastructureProvider;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _failedRecoveryAction = recoveryAction;
            _criticalFailureNotifier = criticalFailureNotifier ?? throw new ArgumentNullException(nameof(criticalFailureNotifier));
            _messageBrokerOptions = messageBrokerOptions ?? throw new ArgumentNullException(nameof(messageBrokerOptions));
            _recoveryStrategy = recoveryStrategy ?? throw new ArgumentNullException(nameof(recoveryStrategy));
            _receivedMessageDispatcher = receivedMessageDispatcher ?? throw new ArgumentNullException(nameof(receivedMessageDispatcher));
        }

        /// <summary>
        /// Indicates if the <see cref="BrokeredMessageReceiverBackgroundService{TMessage}"/> is currently receiving messages
        /// </summary>
        // INVARIANT: derived read of the lifecycle state machine — true exactly while the receiver is Live. The setter
        // was removed; go-live and teardown advance _lifecycle via Interlocked.CompareExchange, and IsReceiving merely
        // projects that state so the public surface (and the public-API regression guard) is preserved unchanged.
        public bool IsReceiving => Volatile.Read(ref _lifecycle) == LifecycleLive;

        // EXPLICIT interface implementation: the go-live signal is reachable ONLY through the internal
        // IReceiverStartupSignal seam, never as a member of the public BrokeredMessageReceiver<TMessage> surface.
        // This keeps the relocated signal fully off the public contract (the public-API regression guard asserts
        // its absence from IReceiveMessages, IBrokeredMessageReceiver<>, and this concrete type).
        Task IReceiverStartupSignal.ReceivingStarted => _receivingStartedSource.Task;

        public string SendingPath { get; private set; }
        public string MessageReceiverPath { get; private set; }
        public string ErrorQueueName { get; private set; }
        public string DeadLetterQueueName { get; private set; }

        public Task<IAsyncDisposable> StartReceiver(ReceiverOptions options)
            => StartReceiver(options, CancellationToken.None);

        ///<inheritdoc/>
        public async Task<IAsyncDisposable> StartReceiver(ReceiverOptions options, CancellationToken receiverTerminationToken)
        {
            try
            {
                await StartReceiverImpl(options, receiverTerminationToken);
            }
            catch (Exception e) when (this.IsReceiving)
            {
                // INVARIANT: this.IsReceiving only becomes true once StartReceiverImpl has finished the startup
                // phase (infrastructure resolve + InitializeAsync) and the steady-state receive loop is live.
                // Exceptions caught here therefore escaped the running loop, NOT the startup phase. The receive
                // loop owns its own transient/retry/circuit-breaker handling, so anything that reaches here is a
                // post-startup runtime fault: log it critically and let the host keep running (existing behavior).
                _logger.LogCritical(e, "Critical unhandled error occured during {executingFunction}", nameof(MessageReceiverLoopAsync));
            }
            // INVARIANT: startup-fatal exceptions (e.g. the Azure Service Bus cross-entity-transactions guard's
            // InvalidOperationException, or any infrastructure/configuration failure surfaced before the receive
            // loop goes live) are intentionally NOT caught here. They propagate to the caller so that, when this
            // runs under IHostedService.StartAsync, .NET aborts host startup loudly instead of leaving a silently
            // stopped receiver in a still-running host.

            return this;
        }

        async Task StartReceiverImpl(ReceiverOptions options, CancellationToken receiverTerminationToken)
        {
            _logger.LogInformation("Initializing '{executingFunction}' of type '{receiverMessageType}'.", nameof(BrokeredMessageReceiver<TMessage>), typeof(TMessage).Name);

            // INVARIANT: enter the Starting state BEFORE resolving or initializing the infrastructure receiver, so a
            // teardown that lands anywhere from here through go-live observes Starting (not NotStarted), serializes on the
            // teardown gate, and latches TornDown rather than no-op'ing. The CAS only advances NotStarted -> Starting; a
            // teardown cannot set TornDown while the lifecycle is still NotStarted (QuiesceAsync returns early on
            // NotStarted without touching the gate), so the only way this CAS fails is a duplicate StartReceiver call over
            // an already-started receiver — abandon that. NotStarted is the only legal predecessor of Starting.
            if (Interlocked.CompareExchange(ref _lifecycle, LifecycleStarting, LifecycleNotStarted) != LifecycleNotStarted)
            {
                _logger.LogInformation("'{executingFunction}' of type '{receiverMessageType}' was torn down before startup could begin; abandoning startup.", nameof(BrokeredMessageReceiver<TMessage>), typeof(TMessage).Name);
                return;
            }

            // OWNERSHIP/HANDOFF MODEL: resolve and InitializeAsync the infrastructure receiver into a STARTUP-OWNED LOCAL
            // — NEVER into the shared _infrastructureReceiver field — with _teardownGate NOT held. This dissolves the
            // "teardown reaches infra during the Starting window" race class by CONSTRUCTION: a teardown racing startup
            // sees only a null _infrastructureReceiver field, so it cannot reach this not-yet-published, startup-owned
            // infra object. Nothing reachable by teardown exists during Starting until the single atomic handoff below.
            // Because the blocking InitializeAsync I/O runs here with the gate NOT held, a hung init can never block a
            // concurrent teardown — teardown latches TornDown against the null field and returns; startup observes
            // TornDown at the handoff and surrenders this local. (No partial of THIS local is observable to teardown.)
            options.Description ??= options.MessageReceiverPath;
            var infra = _infrastructureProvider.GetReceiver(options.InfrastructureType);
            options.MessageReceiverPath = _infrastructureProvider.GetInfrastructure(options.InfrastructureType).PathBuilder.GetMessageReceivingPath(options.SendingPath, options.MessageReceiverPath);

            this.SendingPath = options.SendingPath;
            this.MessageReceiverPath = options.MessageReceiverPath;
            this.ErrorQueueName = options.ErrorQueuePath;
            this.DeadLetterQueueName = options.DeadLetterQueuePath;

            options.TransactionMode ??= _messageBrokerOptions.TransactionMode;
            _options = options;
            _maxConcurrentCalls = _options.MaxConcurrentCalls;

            // Floor-at-the-sink: MaxConcurrentCalls is the single convergence point for every ingress path
            // (the ASB WithMaxConcurrentCalls fluent setter, config binding, and the ASB MaxConcurrentCalls
            // stamp onto retained ReceiverOptions), all of which accept an unvalidated int. A value < 1 would
            // reach 'new SemaphoreSlim(count, count)' below and throw an opaque ArgumentOutOfRangeException at
            // receiver startup. Validate here so a misconfigured value fails fast with a message that names the
            // bad value and its source, surfaced through the same startup-fatal propagation path as the
            // cross-entity guard rather than as an obscure semaphore error. Runs BEFORE the InitializeAsync I/O and
            // before any publish, so a startup-fatal value leaves nothing published and nothing to surrender.
            if (_maxConcurrentCalls < 1)
            {
                throw new InvalidOperationException(
                    $"ReceiverOptions.MaxConcurrentCalls must be at least 1 for receiver '{options.MessageReceiverPath}'; found {_maxConcurrentCalls}. Configure a value >= 1.");
            }

            _logger.LogTrace("Initializing messaging infrastructure");
            // INVARIANT: blocking infrastructure I/O on the startup-owned LOCAL, gate NOT held. If this throws
            // (startup-fatal), nothing was ever published to _infrastructureReceiver, there is nothing to surrender, the
            // local is GC'd, IsReceiving never becomes true, and the exception propagates to the caller exactly as before.
            await infra.InitializeAsync(_options, receiverTerminationToken);
            _logger.LogTrace("Successfully initialized messaging infrastructure");

            _logger.LogDebug("Receiver options: Infrastructure type: '{infrastructureType}', Transaction Mode: '{transactionMode}', Message receiver: '{messageReceiverPath}', Deadletter queue: '{deadLetterQueuePath}', Error queue: '{errorQueuePath}', Max receive attempts: '{maxReceiveAttempts}', Message sent from: '{sendingPath}', Max Concurrent Receives: '{maxConcurrentCalls}'",
                _options.InfrastructureType, options.TransactionMode, options.MessageReceiverPath, options.DeadLetterQueuePath, options.ErrorQueuePath, options.MaxReceiveAttempts, options.SendingPath, _maxConcurrentCalls);

            // ATOMIC PUBLISH-OR-SURRENDER HANDOFF: a SINGLE non-blocking _teardownGate critical section — containing NO
            // blocking I/O await, only field writes, primitive construction, and CAS — performs the handoff of the
            // startup-owned infra local. Reading _requestedTeardownStrength and the lifecycle UNDER the gate guarantees we
            // observe any racing teardown's recorded strength / TornDown latch.
            //   - lifecycle != TornDown: no teardown raced (or one tried, saw the null field, and a teardown does not
            //     advance Starting -> TornDown except via the gated quiesce we are holding the gate against). PUBLISH the
            //     local to _infrastructureReceiver, construct the loop primitives, CAS Starting -> Live, signal start.
            //   - lifecycle == TornDown: a teardown arrived during init, saw a null _infrastructureReceiver, quiesced the
            //     partial (a no-op — nothing of THIS local was reachable), latched TornDown, and recorded its strength.
            //     SURRENDER: PUBLISH the local to _infrastructureReceiver FIRST (so TryClaimAndDisposeInfrastructureAsync
            //     remains the SOLE 0->1 writer of _infrastructureDisposed and no new local-taking overload is needed),
            //     THEN dispose it at the strongest recorded strength via the shared strength-aware seam
            //     TearDownInfrastructureAsync (Stop-strength -> infra.StopReceiver(); Dispose-strength ->
            //     TryClaimAndDisposeInfrastructureAsync). Do NOT go live.
            // Either way a racing teardown can never observe a half-built set: it ran entirely against the null field
            // (latching TornDown) or this handoff runs entirely before it can next acquire the gate.
            var wentLive = false;
            await _teardownGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _lifecycle) == LifecycleTornDown)
                {
                    // A teardown ran to completion while we were initializing the local. The receiver is terminal; do not
                    // publish for go-live or construct the loop/semaphore primitives. Publish the startup-owned local to
                    // the field FIRST so the indivisible claim primitive (TryClaimAndDisposeInfrastructureAsync) stays the
                    // sole 0->1 writer of _infrastructureDisposed, THEN surrender it at the strongest recorded strength via
                    // the single strength-aware seam. The teardown that latched TornDown observed a null field and disposed
                    // nothing (its claim is unclaimed), so this is the path that actually quiesces the real infra.
                    // TearDownInfrastructureAsync is idempotent (CAS on _infrastructureDisposed) and runs under this same
                    // gate acquisition, so the claim->dispose->reset sequence stays gate-serialized and atomic.
                    _logger.LogInformation("'{executingFunction}' of type '{receiverMessageType}' was torn down during startup; surrendering the startup-owned infrastructure receiver at the recorded teardown strength and abandoning go-live without constructing the receive loop.", nameof(BrokeredMessageReceiver<TMessage>), typeof(TMessage).Name);
                    _infrastructureReceiver = infra;
                    await TearDownInfrastructureAsync().ConfigureAwait(false);
                    return;
                }

                // No teardown raced: hand the startup-owned local off to the shared field and go live. This is the SINGLE
                // point at which the infra object becomes reachable by teardown — only after it is fully initialized.
                _infrastructureReceiver = infra;
                _concurrentMessagesSemaphore = new SemaphoreSlim(_maxConcurrentCalls, _maxConcurrentCalls);
                _messageReceiverLoopTokenSource = CancellationTokenSource.CreateLinkedTokenSource(receiverTerminationToken);
                _messageReceiverLoop = Task.Run(MessageReceiverLoopAsync);

                // Go-live seam: advance Starting -> Live. This CAS runs under the gate, so no teardown can have latched
                // TornDown between the check above and here. ReceivingStarted publishes the live signal at this seam.
                Interlocked.CompareExchange(ref _lifecycle, LifecycleLive, LifecycleStarting);
                _receivingStartedSource.TrySetResult(true);
                wentLive = true;
            }
            finally
            {
                _teardownGate.Release();
            }

            if (!wentLive)
            {
                return;
            }

            _logger.LogInformation("'{executingFunction}' has started receiving messages of type '{receiverMessageType}'.", nameof(BrokeredMessageReceiver<TMessage>), typeof(TMessage).Name);
            await _messageReceiverLoop;
            _logger.LogInformation("'{executingFunction}' for messages of type '{receiverMessageType}' is shutting down.", nameof(BrokeredMessageReceiver<TMessage>), typeof(TMessage).Name);
        }

        // INVARIANT: the per-entrypoint infrastructure teardown call differs (StopReceiver stops the receiver,
        // DisposeAsync disposes it asynchronously, the synchronous Dispose path disposes it synchronously) while the
        // surrounding cancel/await-loop/drain/dispose-primitives skeleton is shared. The shared quiesce routine takes
        // this enum and dispatches the correct infrastructure call at the single seam where the paths diverge, so the
        // concurrency-sensitive skeleton lives in exactly one place.
        private enum InfrastructureTeardown
        {
            Stop,
            DisposeAsync,
            DisposeSync
        }

        public Task StopReceiver()
            => QuiesceAsync(InfrastructureTeardown.Stop);

        // INVARIANT: cancel the loop token to wake a loop parked in WaitAsync OR a receive that does not honor the
        // token, so a worker-published critical fault is observed promptly instead of after the next message. Called
        // only by the FIRST fault publisher. Routes through GuardedCancel so a token source already disposed by a
        // concurrent teardown (a detached worker can outlive Dispose) is a no-op the shutdown path already covers.
        void SignalLoopCriticalFault() => GuardedCancel();

        // INVARIANT: every teardown entrypoint cancels the loop token through here. A prior teardown's body disposes
        // the token source while LEAVING the field non-null (the Interlocked.Exchange-to-null happens in the same body
        // AFTER the dispose, but a concurrent caller can still observe the field before that exchange), so an unguarded
        // Cancel() on a repeated OR concurrent teardown — and on a detached worker's SignalLoopCriticalFault that
        // outlives Dispose — would throw ObjectDisposedException before quiesce ran. Swallow that one benign race.
        void GuardedCancel()
        {
            try
            {
                _messageReceiverLoopTokenSource?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        // INVARIANT: maps a per-entrypoint InfrastructureTeardown to its monotonic strength. Stop is weakest; both
        // dispose flavors are Dispose-strength. The admitted quiesce tears infra down at the STRONGEST strength any
        // participating entrypoint requested, so a Dispose racing (or following) a Stop still disposes the infra.
        private static int StrengthOf(InfrastructureTeardown infrastructureTeardown)
            => infrastructureTeardown == InfrastructureTeardown.Stop ? TeardownStrengthStop : TeardownStrengthDispose;

        // INVARIANT: monotonically raise the recorded requested strength to at least the caller's, WITHIN the current
        // epoch. Lock-free CAS loop (Interlocked.Max). Called from QuiesceAsync ONLY AFTER the NotStarted no-op fast-path,
        // so a premature teardown of a still-NotStarted receiver never records here (the field stays None and the receiver
        // stays restartable). The strongest value is read back from _requestedTeardownStrength at the infra-teardown seam
        // (TearDownInfrastructureAsync / EscalateInfrastructureDisposalIfRequiredAsync) and the field is reset to None when
        // an epoch's teardown completes against a real infra (see QuiesceAsync), so the raise is epoch-scoped, not
        // process-monotonic.
        void RaiseRequestedStrength(int strength)
        {
            int current;
            do
            {
                current = Volatile.Read(ref _requestedTeardownStrength);
                if (current >= strength)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref _requestedTeardownStrength, strength, current) != current);
        }

        // INVARIANT: single shared quiesce-before-dispose contract for ALL teardown entrypoints, serialized on
        // _teardownGate, with EPOCH-SCOPED teardown strength. A teardown of a still-NotStarted receiver records NO strength
        // and is a structural no-op (the RaiseRequestedStrength call sits AFTER the NotStarted fast-path), so a DI-singleton
        // premature Dispose leaves the field at None and the receiver RESTARTABLE in a fresh epoch. Teardowns that actually
        // run (Starting/Live) record their requested strength (Stop < Dispose) after that fast-path, then serialize on the
        // gate: the holder runs the cancel/await-loop/drain/infra-teardown skeleton ONCE (QuiesceCoreAsync) and, on success,
        // writes _lifecycle = TornDown; a later caller that acquires the gate and observes TornDown skips the body. STILL
        // UNDER THE GATE — on BOTH the body-ran path and the TornDown-loser path — every caller escalates infra disposal if
        // its strongest-recorded strength exceeds what was actually disposed, so a Dispose that followed a Stop still leaves
        // the infra Disposed, never merely Stopped (dissolves the disposal-strength-loss defect). Folding the escalation
        // INSIDE the gate makes the claim->dispose->reset sequence atomic under the gate: a loser can never observe an
        // in-flight _infrastructureDisposed claim as a completed disposal, so a concurrent in-flight DisposeAsync that
        // FAULTS resets the claim with no loser having latched _disposedValue or nulled the infra over the in-flight claim —
        // the infra is never leaked unretryably. EPOCH RESET: after the gated teardown completes against a REAL (non-null)
        // infra receiver, reset _requestedTeardownStrength to None so the strength does not leak across epochs. The reset is
        // conditioned on a non-null infra so it never pre-empts the Starting-window surrender consumer (StartReceiverImpl
        // reads the recorded strength under a SEPARATE gate acquisition AFTER publishing the surrendered local; the teardown
        // that fed it quiesced a null field, so the reset is skipped and the strength survives for the surrender). A NotStarted
        // receiver is a structural no-op that never touches the gate (preserves b91b751: a DI-singleton premature Dispose
        // during graph resolution must not consume admission and lock out the host's later await-using DisposeAsync). A
        // throwing QuiesceCoreAsync (or a throwing gated escalation) PROPAGATES with TornDown unset where applicable AND with
        // the strength UNRESET, so the next gate acquirer re-runs cleanly and re-derives the strength (fault-resettable retry
        // is inherent to the gate + terminal field — there is no completion source to strand).
        async Task QuiesceAsync(InfrastructureTeardown infrastructureTeardown)
        {
            // INVARIANT: do NOT touch the gate OR record strength for a receiver that never started. The receiver is
            // commonly registered as a singleton; a host/DI graph can synchronously Dispose() the instance during
            // resolution — BEFORE StartReceiverImpl entered its startup body (lifecycle still NotStarted) — and that
            // premature teardown has nothing to quiesce and must record NO strength, leaving the receiver restartable in a
            // fresh epoch. Only NotStarted is a no-op; Starting and Live both admit a real quiesce, so a teardown landing in
            // the startup window serializes on the gate against go-live and tears the PARTIAL infra down rather than
            // no-op'ing. RaiseRequestedStrength therefore runs AFTER this fast-path, never before it.
            if (Volatile.Read(ref _lifecycle) == LifecycleNotStarted)
            {
                return;
            }

            RaiseRequestedStrength(StrengthOf(infrastructureTeardown));

            // Fully-disposed fast path: once a synchronous Dispose (or the Dispose(false) reached from a completed
            // DisposeAsync) has latched _disposedValue, the receiver is terminal and its infra is already disposed (the
            // strength-aware teardown ran before _disposedValue latched). Short-circuit before the gate — there is
            // nothing left to quiesce. (The gate itself is left for GC, never disposed, so this is a fast path, not a
            // safety against a disposed gate.)
            if (Volatile.Read(ref _disposedValue))
            {
                return;
            }

            await _teardownGate.WaitAsync().ConfigureAwait(false);
            try
            {
                // The terminal-state check under the gate is the single stop latch: only the FIRST caller to acquire the
                // gate against a not-yet-torn-down receiver runs the quiesce body; later callers observe TornDown and skip
                // it. A throw from QuiesceCoreAsync propagates WITHOUT writing TornDown, so the gate releases in the
                // finally and the next acquirer re-runs the body cleanly (fault-resettable retry).
                if (Volatile.Read(ref _lifecycle) != LifecycleTornDown)
                {
                    await QuiesceCoreAsync().ConfigureAwait(false);
                    Volatile.Write(ref _lifecycle, LifecycleTornDown);
                }

                // STILL UNDER THE GATE: escalate infra disposal if this (or any) caller requested a strength stronger than
                // what the quiesce body actually disposed (e.g. a Dispose following a Stop). Runs unconditionally after the
                // if-block so it fires on BOTH the body-ran path AND the TornDown-loser path (a loser that skipped the
                // quiesce body must STILL run the gated escalation). Because it runs under the same single gate acquisition,
                // the claim->dispose->reset sequence is atomic: a concurrent teardown cannot observe an in-flight
                // _infrastructureDisposed claim as a completed disposal. Single acquisition only — no nested WaitAsync.
                await EscalateInfrastructureDisposalIfRequiredAsync().ConfigureAwait(false);

                // EPOCH RESET (still under the gate, after the quiesce body + escalation both consumed the recorded
                // strength): reset _requestedTeardownStrength to None so a completed epoch's strength does not leak into a
                // later one. Conditioned on a REAL (non-null) infra receiver so the reset NEVER pre-empts the Starting-window
                // surrender consumer: a teardown that raced the Starting window quiesced a null _infrastructureReceiver
                // (StartReceiverImpl had not yet published the startup-owned local), disposed nothing real, and must leave
                // the strength intact for StartReceiverImpl's SEPARATE-gate surrender (which publishes the local THEN reads
                // the strength via TearDownInfrastructureAsync). On the genuine Live/published path the field is non-null at
                // this point — TearDownInfrastructureAsync stopped/disposed it and Dispose(bool) nulls it only LATER — so the
                // reset fires and the consumers above have already read the value. A throwing body/escalation never reaches
                // here, so the strength stays UNRESET for the fault-resettable retry. No new gate, no await: a plain field
                // write under the single acquisition already held.
                if (_infrastructureReceiver != null)
                {
                    Volatile.Write(ref _requestedTeardownStrength, TeardownStrengthNone);
                }
            }
            finally
            {
                _teardownGate.Release();
            }
        }

        // INVARIANT: the SOLE writer of _infrastructureDisposed 0->1. Both Dispose-strength teardown sites
        // (EscalateInfrastructureDisposalIfRequiredAsync and the Dispose branch of TearDownInfrastructureAsync) delegate
        // here so the claim->dispose->reset construct lives in exactly ONE place. The primitive does claim+dispose ONLY —
        // it NEVER re-checks teardown strength; strength gating stays at the CALL SITES. Called ONLY under _teardownGate
        // (both call sites run gated: the escalation is folded into QuiesceAsync's gated try, and TearDownInfrastructureAsync
        // runs from the gated QuiesceCoreAsync), so the claim->dispose->reset sequence is gate-serialized and atomic — a
        // concurrent teardown blocked on the gate can never observe the in-flight claim as a completed disposal.
        // ORDER IS LOAD-BEARING: capture _infrastructureReceiver into a local FIRST; if it is null, return immediately with
        // NO claim (a null infra was never disposed, so latching the flag would falsely record a disposal — this is the
        // former escalation-path claim-before-null-check defect). Only on a non-null local does it run the single-flight CAS
        // and DisposeAsync. On a faulting DisposeAsync it resets the claim 1->0 and rethrows so a later teardown retries
        // (fault-resettable). Capturing into a local first also means a concurrent Dispose(bool) that nulls the field
        // cannot NRE this path.
        async Task TryClaimAndDisposeInfrastructureAsync()
        {
            var infrastructureReceiver = _infrastructureReceiver;
            if (infrastructureReceiver == null)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _infrastructureDisposed, 1, 0) != 0)
            {
                return;
            }

            try
            {
                await infrastructureReceiver.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Fault-resettable: reset the claim so a later teardown can retry the disposal.
                Volatile.Write(ref _infrastructureDisposed, 0);
                throw;
            }
        }

        // INVARIANT: escalate infra disposal when the strongest requested strength is Dispose but only a weaker teardown
        // (Stop) actually disposed the infra. Called ONLY from inside the _teardownGate (folded into QuiesceAsync's gated
        // try). Keeps the strength gate at this CALL SITE (the < TeardownStrengthDispose early return) and then delegates
        // the claim+dispose to TryClaimAndDisposeInfrastructureAsync — the single sole-writer of _infrastructureDisposed
        // 0->1 — so the primitive captures-then-null-checks BEFORE claiming and never falsely latches the flag on a null
        // infra. Single-flight is enforced by the primitive's CAS: the first gated caller against an as-yet-undisposed
        // infra runs DisposeAsync exactly once; a later gated caller observes _infrastructureDisposed == 1 and short-circuits.
        // Safe to call from winner and losers alike and any number of times — a no-op unless a Dispose strength was requested
        // AND the infra has not yet been disposed.
        async Task EscalateInfrastructureDisposalIfRequiredAsync()
        {
            if (Volatile.Read(ref _requestedTeardownStrength) < TeardownStrengthDispose)
            {
                return;
            }

            await TryClaimAndDisposeInfrastructureAsync().ConfigureAwait(false);
        }

        // INVARIANT: the shared quiesce body, run exactly once by the admitted teardown caller. Cancel (guarded) ->
        // await the loop OBSERVING-AND-SWALLOWING all residual exceptions (no !IsFaulted check: an already-faulted loop
        // is awaited inside the broadened try so its rethrow is swallowed; a non-OCE residual is logged at Trace,
        // mirroring StopReceiver's prior disposition) -> drain in-flight workers -> strength-aware infrastructure
        // teardown -> Interlocked.Exchange the shared primitives to null and Dispose the captured locals so a concurrent
        // caller can never see a half-disposed SemaphoreSlim / CancellationTokenSource. ConfigureAwait(false) on every
        // await keeps teardown context-free so the synchronous Dispose wait (GetAwaiter().GetResult()) cannot deadlock.
        async Task QuiesceCoreAsync()
        {
            GuardedCancel();

            try
            {
                // The loop treats loop-token cancellation as a NORMAL completion (it swallows the cancellation
                // OperationCanceledException/ObjectDisposedException), so under the parallelized design — where the loop
                // commonly parks in WaitAsync with workers in flight — awaiting it completes rather than throwing.
                // INVARIANT: any residual exception from awaiting the loop — benign shutdown cancellation OR a non-OCE
                // loop fault (e.g. the loop's notify epilogue throwing) — is observed-and-swallowed here and must NOT
                // abort teardown. No `!IsFaulted` guard: that was a TOCTOU race (a fault surfacing between the check and
                // the await escaped); awaiting an already-faulted loop inside this broadened try swallows its rethrow.
                if (_messageReceiverLoop != null)
                {
                    await _messageReceiverLoop.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Loop-shutdown cancellation surfaced from the await: benign, teardown still proceeds below.
            }
            catch (Exception ex)
            {
                // A non-OCE loop fault surfaced from the await. The loop epilogue already LogCritical'd it, so log only
                // at Trace here (loud re-logging would double-report); observe-and-swallow so teardown completes.
                _logger.LogTrace(ex, "Receive loop faulted during shutdown; observed and swallowed so teardown completes.");
            }

            // INVARIANT: drain any worker tasks still in flight before disposing the semaphore / token source so no
            // worker touches a disposed SemaphoreSlim. When the loop completed normally its own finally already drained;
            // this is the belt-and-suspenders path for a faulted loop (the await above swallowed) and is a no-op once
            // the set is empty.
            await DrainInFlightWorkersAsync().ConfigureAwait(false);

            await TearDownInfrastructureAsync().ConfigureAwait(false);

            // INVARIANT: exchange the shared primitives to null and dispose the CAPTURED locals so a concurrent caller
            // (e.g. a detached worker's GuardedCancel/ReleaseConcurrencySlot) can never observe a half-disposed
            // primitive through the field. The field is nulled BEFORE Dispose runs on the local.
            var semaphore = Interlocked.Exchange(ref _concurrentMessagesSemaphore, null);
            var loopTokenSource = Interlocked.Exchange(ref _messageReceiverLoopTokenSource, null);
            semaphore?.Dispose();
            loopTokenSource?.Dispose();
        }

        // INVARIANT: the ONE seam where teardown entrypoints diverge — driven now by the STRONGEST recorded teardown
        // strength rather than the admitted caller's own entrypoint, so a Dispose racing a Stop winner still disposes the
        // infra (strongest-wins). The strength gate stays at THIS CALL SITE: the Dispose-strength branch delegates the
        // claim+dispose to TryClaimAndDisposeInfrastructureAsync — the single sole-writer of _infrastructureDisposed 0->1 —
        // so the later escalation path (EscalateInfrastructureDisposalIfRequiredAsync) and the synchronous Dispose(bool)
        // field-null both observe a flag set ONLY by a dispose that actually completed. The Stop-strength else branch
        // stops the receiver and claims nothing. The top-of-method null-guard keeps a concurrent Dispose(bool) field-null
        // from reaching either branch on a null infra; the primitive re-captures into its own local for the same reason.
        // Called ONLY from the gated QuiesceCoreAsync, so the delegated claim->dispose->reset is gate-serialized.
        async Task TearDownInfrastructureAsync()
        {
            var infrastructureReceiver = _infrastructureReceiver;
            if (infrastructureReceiver == null)
            {
                return;
            }

            if (Volatile.Read(ref _requestedTeardownStrength) >= TeardownStrengthDispose)
            {
                await TryClaimAndDisposeInfrastructureAsync().ConfigureAwait(false);
            }
            else
            {
                await infrastructureReceiver.StopReceiver().ConfigureAwait(false);
            }
        }

        async Task MessageReceiverLoopAsync()
        {
            try
            {
                while (!_messageReceiverLoopTokenSource.IsCancellationRequested)
                {
                    // INVARIANT: a CriticalReceiverException observed in a worker stops the loop. Observe it BEFORE
                    // admitting another slot so a critical fault halts pulling promptly and routes through the outer
                    // catch (preserving the _criticalFailureNotifier.Notify path), rather than being lost in the
                    // fire-and-forget worker.
                    ThrowIfWorkerCriticalFault();

                    await _concurrentMessagesSemaphore.WaitAsync(_messageReceiverLoopTokenSource.Token);

                    // INVARIANT: re-observe the worker critical fault IMMEDIATELY after the semaphore is acquired and
                    // BEFORE any receive. When all slots are full the loop parks inside WaitAsync above; a worker can
                    // publish a CriticalReceiverException while we are parked, and the slot it releases is what wakes
                    // this WaitAsync. Without this second check the loop would proceed straight into ReceiveMessageAsync
                    // and admit one more receive after a critical failure (and, on infrastructures whose receive does
                    // not honor the loop token, stall the notifier path until another message arrives). Release the slot
                    // we just took before rethrowing so the fault routes through the outer catch / notifier path with no
                    // leaked slot. The pre-WaitAsync check above still covers the all-slots-free fast path.
                    try
                    {
                        ThrowIfWorkerCriticalFault();
                    }
                    catch (CriticalReceiverException)
                    {
                        ReleaseConcurrencySlot();
                        throw; //stop receiver loop
                    }

                    // INVARIANT: the per-message TransactionContext is constructed PER-TURN and its ownership transfers
                    // to the spawned worker, so concurrent workers never share a TransactionContext and cannot
                    // cross-enlist. The receive call itself stays on the loop thread (pull cadence stays serialized);
                    // only the processing error ladder and the semaphore release fan out into the worker task.
                    var transactionContext = new TransactionContext(this.MessageReceiverPath, _options.TransactionMode.Value);
                    MessageBrokerContext messageContext = null;
                    var slotAcquired = true;

                    try
                    {
                        _messageReceiverLoopTokenSource.Token.ThrowIfCancellationRequested();

                        messageContext = await _recoveryStrategy.ExecuteAsync(() => _infrastructureReceiver.ReceiveMessageAsync(transactionContext, _messageReceiverLoopTokenSource.Token), _messageReceiverLoopTokenSource.Token);
                    }
                    catch (CriticalReceiverException)
                    {
                        ReleaseConcurrencySlot();
                        throw; //stop receiver loop
                    }
                    catch (OperationCanceledException) when (_messageReceiverLoopTokenSource.IsCancellationRequested)
                    {
                        ReleaseConcurrencySlot();
                        slotAcquired = false;
                    }
                    catch (ObjectDisposedException) when (_messageReceiverLoopTokenSource.IsCancellationRequested)
                    {
                        ReleaseConcurrencySlot();
                        slotAcquired = false;
                    }
                    catch (Exception e)
                    {
                        // A failure to RECEIVE (no messageContext) is handled inline on the loop thread, mirroring the
                        // original error ladder's messageContext == null branch, then the slot is released.
                        _logger.LogError(e, "Error receiving brokered message");
                        ReleaseConcurrencySlot();
                        slotAcquired = false;
                    }

                    if (!slotAcquired)
                    {
                        continue;
                    }

                    if (messageContext == null)
                    {
                        // Nothing received this turn (empty receive or swallowed cancellation): release the slot and
                        // continue pulling. Only a non-null messageContext spawns a worker.
                        ReleaseConcurrencySlot();
                        continue;
                    }

                    _logger.LogTrace("Message received successfully");
                    SpawnProcessingWorker(messageContext, transactionContext);

                    // Prune completed worker references so the tracking set does not accumulate unboundedly across
                    // the loop's lifetime.
                    PruneCompletedWorkers();
                }
            }
            catch (CriticalReceiverException e)
            {
                // The loop thread itself observed the fault (pre/post-WaitAsync recheck or an inline receive). Record
                // it as the authoritative fault so the notify-exactly-once epilogue fires for it and a worker-published
                // fault is not ALSO re-notified.
                Interlocked.CompareExchange(ref _workerCriticalFault, e, null);
            }
            catch (OperationCanceledException) when (_messageReceiverLoopTokenSource.IsCancellationRequested)
            {
                // Cancellation of the loop token is a NORMAL shutdown signal (StopReceiver/Dispose cancel it, and a
                // worker critical fault now cancels it too via SignalLoopCriticalFault). The loop commonly parks in
                // WaitAsync once all slots are full, so cancellation most often surfaces THERE — outside the inner
                // receive try/catch. Swallow it here so the loop task completes NORMALLY rather than propagating an
                // OperationCanceledException out of MessageReceiverLoopAsync, which would otherwise abort StopReceiver
                // before it stops/disposes the infrastructure and shared primitives. Any published critical fault is
                // still surfaced by the notify-once epilogue below.
            }
            catch (ObjectDisposedException) when (_messageReceiverLoopTokenSource.IsCancellationRequested)
            {
                // Same shutdown rationale as the cancellation catch: a primitive observed disposed mid-teardown while
                // cancellation is in progress is a benign race, not a loop fault to propagate.
            }
            finally
            {
                // INVARIANT: notify EXACTLY ONCE on a critical fault, regardless of HOW the loop exited. A worker
                // fault now also cancels the loop token (SignalLoopCriticalFault), so the loop can exit either by
                // rethrowing the CriticalReceiverException (caught above) OR via OperationCanceledException from the
                // cancelled token while a worker fault sits in _workerCriticalFault. Both routes converge here: if a
                // fault was published by ANY path, fire the single notifier. The first-writer-wins field guarantees
                // one fault value and the catch above never double-notifies (it only records into the same field).
                //
                // INVARIANT (fail-fast notify-BEFORE-drain): surface the critical fault to the host BEFORE awaiting
                // the worker drain. With MaxConcurrentCalls > 1, a sibling worker that is blocked or that ignores the
                // cancellation token can make DrainInFlightWorkersAsync's Task.WhenAll wait indefinitely. Notifying
                // first guarantees the host learns of a critical receiver failure promptly (the fail-fast path) instead
                // of being stalled behind an unbounded drain. The drain still runs afterward so the quiesce-before-
                // dispose guarantee is preserved for callers that await the loop (StartReceiverImpl / StopReceiver).
                var criticalFault = Interlocked.CompareExchange(ref _workerCriticalFault, null, null);
                if (criticalFault != null)
                {
                    _logger.LogCritical(criticalFault, "Receiver is unable continue due to critical error");
                    await _criticalFailureNotifier.Notify(new FailureContext(null, this.ErrorQueueName, "Critical error occurred", criticalFault, -1, null)).ConfigureAwait(false);
                }

                // Drain any still-running workers before the loop task completes so callers that await the loop
                // (StartReceiverImpl / StopReceiver) observe a fully-quiesced receiver. ConfigureAwait(false): the
                // loop already runs on the pool, but keep teardown context-free as defense-in-depth so the
                // synchronous Dispose wait can never deadlock on a captured context. This runs AFTER the critical-fault
                // notify above so an unbounded drain can never starve the fail-fast notification path.
                await DrainInFlightWorkersAsync().ConfigureAwait(false);
            }
        }

        // INVARIANT: runs the per-message error ladder + semaphore release for a SINGLE received message in a tracked
        // background task that owns its OWN messageContext + transactionContext. Up to MaxConcurrentCalls of these run
        // concurrently. A CriticalReceiverException raised here is published to _workerCriticalFault (observed by the
        // loop) rather than lost; every other fault is handled by the ladder, and the slot is always released in the
        // per-task finally so a fault never leaks a semaphore slot.
        void SpawnProcessingWorker(MessageBrokerContext messageContext, TransactionContext transactionContext)
        {
            // INVARIANT: snapshot the CancellationToken on the loop thread (where the token source is guaranteed live)
            // and hand the VALUE to the worker. A CancellationToken struct keeps reporting IsCancellationRequested
            // after its source is disposed, whereas re-reading _messageReceiverLoopTokenSource.Token from a detached
            // worker could hit a disposed source during shutdown. This keeps the worker's cancellation-swallow filters
            // valid through teardown.
            var workerToken = _messageReceiverLoopTokenSource.Token;
            var worker = Task.Run(() => ProcessReceivedMessageWorkerAsync(messageContext, transactionContext, workerToken));

            lock (_inFlightTasksLock)
            {
                _inFlightTasks.Add(worker);
            }

            // INVARIANT: the worker body itself never rethrows (its finally swallows nothing critical past publishing
            // to _workerCriticalFault), so this continuation only prunes the completed reference. It must not be the
            // sole observer of faults — fault propagation is via _workerCriticalFault, observed on the loop thread.
            worker.ContinueWith(
                completed =>
                {
                    lock (_inFlightTasksLock)
                    {
                        _inFlightTasks.Remove(completed);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        async Task ProcessReceivedMessageWorkerAsync(MessageBrokerContext messageContext, TransactionContext transactionContext, CancellationToken workerToken)
        {
            try
            {
                await ProcessMessageAsync(messageContext, transactionContext, workerToken);
                _logger.LogTrace("Message processed successfully");
            }
            catch (CriticalReceiverException e)
            {
                // First writer wins: publish to the loop-observed fault field so the loop stops and the existing
                // outer-handler _criticalFailureNotifier.Notify path fires. Never lost in fire-and-forget.
                if (Interlocked.CompareExchange(ref _workerCriticalFault, e, null) == null)
                {
                    // Only the FIRST publisher wakes the loop. Cancel the loop token so a loop parked in WaitAsync OR
                    // inside a receive that does not otherwise honor the token unblocks promptly: the loop's existing
                    // OperationCanceledException-when-cancelled branch releases its slot and falls through to the
                    // shutdown path, where the post-loop ThrowIfWorkerCriticalFault / outer handler still routes the
                    // fault to _criticalFailureNotifier.Notify. Without this, a last-worker fault could sit unobserved
                    // while the receiver idles waiting for the next message. Guarded against a disposed source during
                    // teardown (the worker may outlive a Dispose that already disposed the source).
                    SignalLoopCriticalFault();
                }
            }
            catch (OperationCanceledException) when (workerToken.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (workerToken.IsCancellationRequested)
            {
            }
            catch (PoisonedMessageException e)
            {
                _logger.LogError(e, "Poisoned message received. Deadlettering.");
                await TryDeadletterWithRecoveryAsync(messageContext, transactionContext, e, workerToken);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error processing brokered message");

                // INVARIANT: the generic processing-error recovery ladder must not let an UNEXPECTED fault escape the
                // worker. TryAck/TryNack/TryDeadletter/TryExecuteFailedRecoveryAction each catch internally, but the
                // delivery-count probe (MessageDeliveryCountAsync) is awaited OUTSIDE any protective try here. If it
                // throws (e.g. recovery-strategy retries exhausted), the worker task would fault AFTER the slot is
                // released, the loop never observes it (workers are fire-and-forget; only CriticalReceiverException is
                // published to _workerCriticalFault), the in-flight continuation removes it, and DrainInFlightWorkersAsync
                // swallows worker faults — the message is left unsettled while the receiver still reports healthy. In the
                // pre-parallelization serial loop this exception escaped to the loop and was logged critically. Restore
                // that VISIBILITY by catching an unexpected fault in this branch and logging it critically rather than
                // letting it vanish. A CriticalReceiverException is NOT caught here (it never originates from this ladder
                // and would be handled by its own catch above), so circuit-breaker/critical-stop semantics are unchanged.
                try
                {
                    var deliveryCount = await _recoveryStrategy.ExecuteAsync(() => _infrastructureReceiver.MessageDeliveryCountAsync(messageContext, workerToken), workerToken);
                    if (deliveryCount >= _options.MaxReceiveAttempts)
                    {
                        if (await TryDeadletterWithRecoveryAsync(messageContext, transactionContext, e, workerToken))
                        {
                            await TryExecuteFailedRecoveryAction(messageContext, "Max message receive attempts exceeded", e, deliveryCount, transactionContext);
                        }
                    }
                    else
                    {
                        await TryNackWithRecoveryAsync(messageContext, transactionContext, workerToken);
                    }
                }
                catch (OperationCanceledException) when (workerToken.IsCancellationRequested)
                {
                    // Shutdown cancellation during recovery: benign, swallowed like the worker's other cancellation paths.
                }
                catch (ObjectDisposedException) when (workerToken.IsCancellationRequested)
                {
                }
                catch (Exception recoveryError)
                {
                    // The settlement/recovery probe itself failed unexpectedly (the message could not be settled). Surface
                    // it critically — matching the serial loop's visibility — instead of letting the worker fault silently
                    // and the unsettled message look healthy. The loop is unaffected; this single message's settlement
                    // failure is now observable in logs rather than swallowed by the worker drain.
                    _logger.LogCritical(recoveryError, "Unrecoverable failure settling brokered message after a processing error; message may remain unsettled");
                }
            }
            finally
            {
                ReleaseConcurrencySlot();
            }
        }

        // INVARIANT: idempotent-safe release guarded against the disposed-semaphore race during shutdown, preserving
        // the original finally's ObjectDisposedException swallow.
        void ReleaseConcurrencySlot()
        {
            try
            {
                _concurrentMessagesSemaphore?.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        void ThrowIfWorkerCriticalFault()
        {
            var fault = Interlocked.CompareExchange(ref _workerCriticalFault, null, null);
            if (fault != null)
            {
                throw fault;
            }
        }

        void PruneCompletedWorkers()
        {
            lock (_inFlightTasksLock)
            {
                _inFlightTasks.RemoveWhere(t => t.IsCompleted);
            }
        }

        async Task DrainInFlightWorkersAsync()
        {
            Task[] pending;
            lock (_inFlightTasksLock)
            {
                pending = new Task[_inFlightTasks.Count];
                _inFlightTasks.CopyTo(pending);
            }

            if (pending.Length == 0)
            {
                return;
            }

            try
            {
                await Task.WhenAll(pending);
            }
            catch
            {
                // Worker faults are already handled inside ProcessReceivedMessageWorkerAsync (error ladder +
                // _workerCriticalFault publication). Draining only needs to ensure every worker has QUIESCED before
                // the semaphore / token source are disposed; individual fault outcomes are not re-surfaced here.
            }
        }

        public virtual async Task DispatchReceivedMessageAsync(TMessage payload, MessageBrokerContext messageContext, CancellationToken receiverTokenSource)
        {
            receiverTokenSource.ThrowIfCancellationRequested();

            try
            {
                await _receivedMessageDispatcher.DispatchAsync(payload, messageContext, receiverTokenSource);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error dispatching brokered message to handler(s)");
                throw;
            }
        }

        async Task ProcessMessageAsync(MessageBrokerContext messageContext, TransactionContext transactionContext, CancellationToken receiverTokenSource)
        {
            receiverTokenSource.ThrowIfCancellationRequested();

            TMessage brokeredMessagePayload = null;

            var inboundMessage = messageContext.BrokeredMessage;

            if (transactionContext is null)
            {
                transactionContext = new TransactionContext(_options.MessageReceiverPath, _options.TransactionMode.Value);
            }

            messageContext.Container.GetOrAdd(() => transactionContext);

            try
            {
                brokeredMessagePayload = inboundMessage.GetMessageFromBody<TMessage>();
            }
            catch (Exception e)
            {
                throw new PoisonedMessageException($"Unable deserialize {typeof(TMessage).Name} from message body", e);
            }

            using var localTransaction = _infrastructureReceiver.CreateLocalTransaction(transactionContext);
            await _recoveryStrategy.ExecuteAsync(async () =>
                {
                    await DispatchReceivedMessageAsync(brokeredMessagePayload, messageContext, receiverTokenSource);
                    return true;
                }, receiverTokenSource);

            if (!receiverTokenSource.IsCancellationRequested)
            {
                if (await TryAckWithRecoveryAsync(messageContext, transactionContext, receiverTokenSource))
                {
                    localTransaction?.Complete();
                }
            }
            else
            {
                await TryNackWithRecoveryAsync(messageContext, transactionContext, receiverTokenSource);
            }
        }

        private async Task<bool> TryAckWithRecoveryAsync(MessageBrokerContext messageContext, TransactionContext transactionContext, CancellationToken receiverTokenSource)
        {
            try
            {
                return await _recoveryStrategy.ExecuteAsync(() => _infrastructureReceiver.AckMessageAsync(messageContext, transactionContext, receiverTokenSource), receiverTokenSource);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Unable to send acknowledgment");
                return false;
            }
        }

        private async Task<bool> TryNackWithRecoveryAsync(MessageBrokerContext messageContext, TransactionContext transactionContext, CancellationToken receiverTokenSource)
        {
            try
            {
                return await _recoveryStrategy.ExecuteAsync(() => _infrastructureReceiver.NackMessageAsync(messageContext, transactionContext, receiverTokenSource), receiverTokenSource);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Unable to send negative acknowledgment");
                return false;
            }
        }

        private async Task<bool> TryDeadletterWithRecoveryAsync(MessageBrokerContext messageContext, TransactionContext transactionContext, Exception e, CancellationToken receiverTokenSource)
        {
            try
            {
                return await _recoveryStrategy.ExecuteAsync(() => _infrastructureReceiver.DeadletterMessageAsync(messageContext, transactionContext, "Poisoned message received", e.ToString(), receiverTokenSource), receiverTokenSource);
            }
            catch (Exception inner)
            {
                _logger.LogError(inner, "Unable to deadletter message");
                return false;
            }
        }

        private async Task<bool> TryExecuteFailedRecoveryAction(MessageBrokerContext messageContext, string failureDescription, Exception exception, int deliveryCount, TransactionContext transactionContext)
        {
            try
            {
                var failureContext = new FailureContext(messageContext.BrokeredMessage, this.ErrorQueueName, failureDescription, exception, deliveryCount, transactionContext);
                return await _recoveryStrategy.ExecuteAsync(async () =>
                {
                    await _failedRecoveryAction.ExecuteAsync(failureContext);
                    return true;
                }, messageContext.CancellationToken);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Unable to execute recovery action");
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            // INVARIANT: route through the single shared quiesce-before-dispose contract so DisposeAsync, StopReceiver,
            // and the synchronous Dispose path all converge on ONE idempotent disposition. QuiesceAsync cancels (guarded
            // against an already-disposed token source), awaits the LOOP to completion first — draining only the
            // in-flight worker SNAPSHOT is not enough, since the loop may still be inside WaitAsync / ReceiveMessageAsync
            // or in the gap after receiving but before SpawnProcessingWorker added the worker to _inFlightTasks, where
            // the snapshot is empty or stale — then drains residual workers, then disposes the infrastructure receiver
            // asynchronously, then exchanges-and-disposes the shared primitives. A repeated or concurrent DisposeAsync
            // observes the first caller's quiesce completion instead of re-running it. ConfigureAwait(false) inside the
            // contract keeps teardown context-free.
            await QuiesceAsync(InfrastructureTeardown.DisposeAsync).ConfigureAwait(false);

            Dispose(disposing: false);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!Volatile.Read(ref _disposedValue))
            {
                if (disposing)
                {
                    // INVARIANT: the synchronous Dispose path must QUIESCE the receiver before disposing any
                    // worker-touched primitive, exactly like the async StopReceiver/DisposeAsync paths — otherwise a
                    // worker already past its token check could still be inside DispatchReceivedMessageAsync or an
                    // Ack/Nack/Deadletter and would race a disposed infrastructure receiver / semaphore / token source
                    // (the ReleaseConcurrencySlot ObjectDisposedException swallow only hides ONE symptom; it does not
                    // protect the infrastructure receiver or the in-flight message's settlement). Route through the same
                    // shared QuiesceAsync contract the async paths use, blocked synchronously: it serializes on
                    // _teardownGate, cancels (guarded), awaits the loop (which treats loop-token cancellation as a clean
                    // completion, so the wait completes rather than throwing), drains detached workers, disposes the
                    // infrastructure receiver at Dispose-strength (the strength-aware teardown seam uses the infra's async
                    // DisposeAsync under ConfigureAwait(false)), and exchanges-and-disposes the shared primitives.
                    // DEADLOCK-SAFETY: the loop is started via Task.Run (default scheduler) and every await inside the
                    // contract uses ConfigureAwait(false), and workers are started via Task.Run, so NEITHER the loop nor
                    // the drain captures a caller SynchronizationContext; SemaphoreSlim.WaitAsync/GetAwaiter().GetResult()
                    // capture none either — so an async teardown holding the gate never needs this blocked thread and
                    // GetAwaiter().GetResult() cannot deadlock even from a single-threaded context.
                    //
                    // FAULT-RESETTABLE LATCH: if the quiesce THROWS (e.g. the infra teardown faulted), do NOT latch
                    // _disposedValue and do NOT null the infra receiver — leave the synchronous Dispose retryable so a
                    // SECOND Dispose() re-runs the gate body and actually disposes the infra. Mirrors the async
                    // fault-resettable contract (a thrown QuiesceCoreAsync leaves TornDown unset). The throw is swallowed
                    // here (Dispose must not throw); the unset _disposedValue is what makes the retry happen.
                    try
                    {
                        QuiesceAsync(InfrastructureTeardown.DisposeSync).GetAwaiter().GetResult();
                    }
                    catch
                    {
                        return;
                    }
                }

                // INVARIANT: null the infrastructure receiver ONLY after a quiesce that actually DISPOSED it. A
                // synchronous Dispose() of a NotStarted receiver (the DI-singleton premature-Dispose) leaves the field
                // untouched so a later genuine startup + teardown still has an infra receiver to construct over and
                // dispose; _infrastructureDisposed gates the field-null so DisposeSync of a never-started receiver does
                // not erase a not-yet-constructed receiver, and a Dispose(disposing:false) reached from DisposeAsync only
                // nulls the field once the async quiesce above disposed the infra. _disposedValue still makes repeated
                // synchronous Dispose a no-op regardless.
                if (Volatile.Read(ref _infrastructureDisposed) == 1)
                {
                    _infrastructureReceiver = null;
                }

                // INVARIANT: latch _disposedValue ONLY once the receiver is terminal (a quiesce ran to completion and set
                // TornDown). A NotStarted premature synchronous Dispose (the DI-singleton case) quiesced nothing — its
                // QuiesceAsync no-op'd on NotStarted and left the lifecycle NotStarted — so it must NOT latch, or the
                // _disposedValue fast path in QuiesceAsync would lock the host's later genuine teardown out (b91b751). The
                // sync-fault path already returned above without reaching here, so a faulting quiesce never latches
                // either. A successful sync Dispose and the Dispose(false) reached from a completed DisposeAsync both
                // observe TornDown and latch, making repeated synchronous Dispose a no-op. The gate itself is left for GC
                // (never disposed), so latching here does not strand a concurrent WaitAsync.
                if (Volatile.Read(ref _lifecycle) == LifecycleTornDown)
                {
                    Volatile.Write(ref _disposedValue, true);
                }
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
