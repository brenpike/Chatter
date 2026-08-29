using Chatter.CQRS;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Diagnostics;
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
    public partial class BrokeredMessageReceiver<TMessage> : IBrokeredMessageReceiver<TMessage>, IReceiverStartupSignal where TMessage : class, IMessage
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
        // SWALLOW-AND-FINALIZE (single-shot teardown): infra teardown is non-throwing at the boundary — the strength-aware
        // seam (TearDownInfrastructureAsync / TryClaimAndDisposeInfrastructureAsync) CATCHES, LogError's, and swallows a
        // throwing infra StopReceiver()/DisposeAsync() rather than propagating it. So QuiesceCoreAsync runs to completion
        // even when infra teardown faults, and the admitted caller ALWAYS writes _lifecycle = TornDown (the monotonic
        // terminal field, written ONLY under the gate). Teardown is therefore single-shot, not retryable: a SECOND
        // teardown observes TornDown (and _infrastructureDisposed where applicable) and no-ops, matching .NET dispose
        // guidance that Dispose must not throw and must be safe to call multiple times.
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

        // INVARIANT: GATE-OWNED, MONOTONIC, NEVER-RESET teardown disposition. None (no teardown recorded) < Stop (weakest)
        // < DisposeAsync/DisposeSync (strongest). This field is read, written, and observed ONLY under _teardownGate — there
        // is NO lock-free outside-gate writer and NO per-epoch reset. Because StopReceiver() is ONE-WAY terminal (there is no
        // TornDown -> Live transition and no restart-after-stop), the disposition only ever needs to climb, never unwind:
        // once a teardown is admitted the receiver never returns to service, so a recorded disposition can never become
        // stale and never needs clearing. Collapsing the old raise/escalate/reset triad into this single gate-owned field
        // dissolves the recurring TOCTOU class where the disposition straddled the gate boundary — a Dispose-strength raise
        // landing between a Stop holder's in-gate read and its in-gate reset could be ERASED, ending the infra Stopped not
        // Disposed. With this model that class holds by CONSTRUCTION: every record is a plain (gate-owned) field write of
        // max(current, StrengthOf(entrypoint)) taken AFTER acquiring the gate, and nothing ever resets it, so a Dispose
        // request can never be lost to a concurrent reset and a Stop holder can never observe a stale/cleared disposition.
        // A teardown of a still-NotStarted receiver records NOTHING (the record sits AFTER the NotStarted no-op fast-path,
        // which never touches the gate), so the DI-singleton premature Dispose during graph resolution leaves this field at
        // None and the receiver stays restartable — exactly the property the old epoch reset existed to provide, now obtained
        // for free because the no-op path never records and so never needs to be undone.
        private const int TeardownStrengthNone = 0;
        private const int TeardownStrengthStop = 1;
        private const int TeardownStrengthDispose = 2;
        private int _teardownDisposition = TeardownStrengthNone;

        // INVARIANT: latched 0->1 exactly once when the infrastructure receiver has actually been DISPOSED (not merely
        // Stopped). SINGLE WRITER: the flag is written 0->1 by exactly ONE primitive, TryClaimAndDisposeInfrastructureAsync.
        // Both Dispose-strength teardown sites — the folded post-body dispose-if-disposition-is-Dispose step in QuiesceAsync
        // and the Dispose branch of TearDownInfrastructureAsync (strength-aware teardown inside QuiesceCoreAsync) — delegate
        // their claim+dispose to that one primitive instead of running their own inline CAS, so the infra is disposed at
        // most once across a Stop-then-Dispose escalation and the construct exists in one place. The synchronous
        // Dispose(bool) field-null is the only OTHER site and merely READS this flag (== 1) — it never writes it 0->1.
        // GATE-SERIALIZED CLAIM: the primitive is called ONLY under _teardownGate (the post-body dispose-if-Dispose step is
        // folded into QuiesceAsync's gated try, and TearDownInfrastructureAsync runs from QuiesceCoreAsync which is itself gated). The
        // claim+dispose sequence inside the primitive is therefore atomic under the gate: a concurrent teardown blocked on
        // the gate can NEVER observe an in-flight 0->1 claim as a completed disposal. SWALLOW-AND-FINALIZE makes the claim
        // SINGLE-SHOT: a faulting infra DisposeAsync is caught, LogError'd, and swallowed with the claim LEFT LATCHED at 1
        // (no 1->0 reset), so a SECOND teardown observes _infrastructureDisposed == 1 and no-ops rather than retrying. The
        // synchronous Dispose(bool) field-null keys off this flag == 1, set ONLY by a dispose attempt that ran under the
        // gate (never by a still-in-flight claim, and never by a null infra —
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
            // SINGLE-SHOT INIT-FAILURE: the startup-owned local was never published to _infrastructureReceiver, so teardown
            // could never reach it and it cannot leak via the gated surrender path. Dispose THIS local best-effort here so a
            // partially-initialized infra receiver does not leak when InitializeAsync throws, then re-throw the ORIGINAL
            // InitializeAsync exception so startup stays startup-fatal and propagates exactly as before. A secondary fault
            // from the compensating DisposeAsync is logged at Warning and swallowed so it cannot mask the root cause. This is
            // single-shot: the lifecycle is NOT reset and no restart-after-failed-init logic is added.
            try
            {
                await infra.InitializeAsync(_options, receiverTerminationToken);
            }
            catch
            {
                try
                {
                    await infra.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception disposalError)
                {
                    _logger.LogWarning(disposalError, "Failed to dispose the startup-owned infrastructure receiver after initialization faulted; surfacing the original initialization failure.");
                }

                throw;
            }
            _logger.LogTrace("Successfully initialized messaging infrastructure");

            _logger.LogDebug("Receiver options: Infrastructure type: '{infrastructureType}', Transaction Mode: '{transactionMode}', Message receiver: '{messageReceiverPath}', Deadletter queue: '{deadLetterQueuePath}', Error queue: '{errorQueuePath}', Max receive attempts: '{maxReceiveAttempts}', Message sent from: '{sendingPath}', Max Concurrent Receives: '{maxConcurrentCalls}'",
                _options.InfrastructureType, options.TransactionMode, options.MessageReceiverPath, options.DeadLetterQueuePath, options.ErrorQueuePath, options.MaxReceiveAttempts, options.SendingPath, _maxConcurrentCalls);

            // ATOMIC PUBLISH-OR-SURRENDER HANDOFF: a SINGLE non-blocking _teardownGate critical section — containing NO
            // blocking I/O await, only field writes, primitive construction, and CAS — performs the handoff of the
            // startup-owned infra local. Reading _teardownDisposition and the lifecycle UNDER the gate guarantees we
            // observe any racing teardown's recorded disposition / TornDown latch. Because the disposition is gate-owned and
            // NEVER reset, the surrender consumer below (TearDownInfrastructureAsync) reads exactly the disposition the racing
            // Starting-window teardown recorded under this same gate.
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
                    // gate acquisition, so the claim+dispose sequence stays gate-serialized and atomic.
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
        // dispose flavors are Dispose-strength. The admitted quiesce tears infra down at the STRONGEST disposition any
        // participating entrypoint recorded under the gate, so a Dispose racing (or following) a Stop still disposes the infra.
        private static int StrengthOf(InfrastructureTeardown infrastructureTeardown)
            => infrastructureTeardown == InfrastructureTeardown.Stop ? TeardownStrengthStop : TeardownStrengthDispose;

        // INVARIANT: single shared quiesce-before-dispose contract for ALL teardown entrypoints, serialized on
        // _teardownGate, with a GATE-OWNED, MONOTONIC, NEVER-RESET teardown disposition. A teardown of a still-NotStarted
        // receiver records NOTHING and is a structural no-op (the disposition record sits INSIDE the gate, which the
        // NotStarted fast-path returns before ever acquiring), so a DI-singleton premature Dispose leaves _teardownDisposition
        // at None and the receiver RESTARTABLE (preserves b91b751: a DI-singleton premature Dispose during graph resolution
        // must not consume admission and lock out the host's later await-using DisposeAsync). Teardowns that actually run
        // (Starting/Live) acquire the gate and THEN monotonically raise _teardownDisposition = max(current, StrengthOf(entry))
        // with a PLAIN gate-owned field write (no Interlocked — the field is only ever touched under the gate). The holder
        // runs the cancel/await-loop/drain/infra-teardown skeleton ONCE (QuiesceCoreAsync) and, on success, writes
        // _lifecycle = TornDown; a later caller that acquires the gate and observes TornDown skips the body. STILL UNDER THE
        // GATE — on BOTH the body-ran path AND the TornDown-loser path — every caller disposes the infra if the recorded
        // disposition is Dispose and the infra has not yet been disposed, so a Dispose that followed (or raced) a Stop still
        // leaves the infra Disposed, never merely Stopped (dissolves the disposal-strength-loss defect). Running that step
        // INSIDE the gate makes the claim+dispose sequence atomic: a loser can never observe an in-flight _infrastructureDisposed
        // claim as a completed disposal. SWALLOW-AND-FINALIZE: a concurrent in-flight DisposeAsync that FAULTS is caught,
        // logged, and swallowed at the infra seam with the claim LEFT LATCHED (single-shot), so the disposal is never retried
        // and a loser observing _infrastructureDisposed == 1 short-circuits.
        //
        // NO RESET, NO OUTSIDE-GATE WRITE: because StopReceiver() is ONE-WAY terminal (no TornDown -> Live, no restart-after-
        // stop), the disposition only ever climbs and never needs clearing — so there is NO per-epoch reset and NO lock-free
        // outside-gate writer. This collapses the former raise/escalate/reset triad that straddled the gate boundary and kept
        // spawning ordering windows (a Dispose raise landing between a Stop holder's in-gate read and its in-gate reset could
        // be ERASED). With a single gate-owned never-reset disposition, that whole TOCTOU class holds by construction: a Dispose
        // request can never be lost to a concurrent reset, and a Stop holder can never observe a stale/cleared disposition. The
        // Starting-window surrender consumer (StartReceiverImpl) reads this same never-reset disposition under its own gate
        // acquisition AFTER publishing the surrendered local, and so reads exactly what the racing teardown recorded.
        // Infra teardown is non-throwing at the boundary (swallow-and-finalize at TearDownInfrastructureAsync /
        // TryClaimAndDisposeInfrastructureAsync): a throwing infra StopReceiver()/DisposeAsync() is caught, LogError'd, and
        // swallowed, so QuiesceCoreAsync + the gated dispose-if-Dispose step always run to completion and TornDown is ALWAYS
        // latched. Teardown is single-shot, not retryable — a SECOND teardown observes the terminal latch and no-ops (per
        // .NET dispose guidance: Dispose must not throw and must be idempotent).
        async Task QuiesceAsync(InfrastructureTeardown infrastructureTeardown)
        {
            // INVARIANT: do NOT touch the gate OR record a disposition for a receiver that never started. The receiver is
            // commonly registered as a singleton; a host/DI graph can synchronously Dispose() the instance during
            // resolution — BEFORE StartReceiverImpl entered its startup body (lifecycle still NotStarted) — and that
            // premature teardown has nothing to quiesce and must record NOTHING, leaving the receiver restartable. Only
            // NotStarted is a no-op; Starting and Live both admit a real quiesce, so a teardown landing in the startup
            // window serializes on the gate against go-live and tears the PARTIAL infra down rather than no-op'ing. The
            // disposition is recorded INSIDE the gate below, which this fast-path returns before ever acquiring — so a
            // NotStarted teardown never records, which is exactly why no reset is needed to keep the receiver restartable.
            if (Volatile.Read(ref _lifecycle) == LifecycleNotStarted)
            {
                return;
            }

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
                // GATE-OWNED DISPOSITION RECORD: monotonically raise the disposition to at least this entrypoint's strength.
                // Plain field write (no Interlocked) — _teardownDisposition is read and written ONLY under this gate, so no
                // atomic is needed and the value can never be clobbered by an outside-gate writer (there is none). The
                // disposition is NEVER reset, so a Dispose recorded here can never be erased by a concurrent reset and a Stop
                // holder can never observe a stale/cleared disposition (the TOCTOU class this collapse dissolves).
                var entryStrength = StrengthOf(infrastructureTeardown);
                if (entryStrength > _teardownDisposition)
                {
                    _teardownDisposition = entryStrength;
                }

                // The terminal-state check under the gate is the single stop latch: only the FIRST caller to acquire the
                // gate against a not-yet-torn-down receiver runs the quiesce body; later callers observe TornDown and skip
                // it. QuiesceCoreAsync is non-throwing for infra teardown (swallow-and-finalize at the infra seam), so the
                // admitted caller ALWAYS reaches the TornDown write — teardown is single-shot, and the SECOND caller
                // observes TornDown and no-ops.
                if (Volatile.Read(ref _lifecycle) != LifecycleTornDown)
                {
                    await QuiesceCoreAsync().ConfigureAwait(false);
                    Volatile.Write(ref _lifecycle, LifecycleTornDown);
                }

                // STILL UNDER THE GATE: dispose the infra if the recorded disposition is Dispose and it has not yet been
                // disposed (e.g. a Dispose following or racing a Stop that only stopped it). Runs UNCONDITIONALLY after the
                // if-block so it fires on BOTH the body-ran path AND the TornDown-loser path (a loser that skipped the
                // quiesce body must STILL run this so a Dispose that follows a Stop winner actually escalates to Disposed —
                // gating it inside the if-block would regress strongest-wins). The disposition gate stays at this CALL SITE
                // (< TeardownStrengthDispose early return); the claim+dispose is delegated to TryClaimAndDisposeInfrastructureAsync,
                // the sole 0->1 writer of _infrastructureDisposed, which captures-then-null-checks before claiming. Because it
                // runs under the same single gate acquisition, the claim+dispose sequence is atomic: a concurrent teardown
                // cannot observe an in-flight _infrastructureDisposed claim as a completed disposal. Single acquisition only —
                // no nested WaitAsync.
                if (_teardownDisposition >= TeardownStrengthDispose)
                {
                    await TryClaimAndDisposeInfrastructureAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _teardownGate.Release();
            }
        }

        // INVARIANT: the SOLE writer of _infrastructureDisposed 0->1. Both Dispose-strength teardown sites
        // (the folded post-body dispose-if-Dispose step in QuiesceAsync and the Dispose branch of TearDownInfrastructureAsync)
        // delegate here so the claim+dispose construct lives in exactly ONE place. The primitive does claim+dispose ONLY —
        // it NEVER re-checks the teardown disposition; disposition gating stays at the CALL SITES. Called ONLY under _teardownGate
        // (both call sites run gated: the post-body dispose step is folded into QuiesceAsync's gated try, and TearDownInfrastructureAsync
        // runs from the gated QuiesceCoreAsync), so the claim+dispose sequence is gate-serialized and atomic — a
        // concurrent teardown blocked on the gate can never observe the in-flight claim as a completed disposal.
        // ORDER IS LOAD-BEARING: capture _infrastructureReceiver into a local FIRST; if it is null, return immediately with
        // NO claim (a null infra was never disposed, so latching the flag would falsely record a disposal — this is the
        // former escalation-path claim-before-null-check defect). Only on a non-null local does it run the single-flight CAS
        // and DisposeAsync. Capturing into a local first also means a concurrent Dispose(bool) that nulls the field cannot
        // NRE this path.
        // SWALLOW-AND-FINALIZE: a faulting infra DisposeAsync is CAUGHT, LogError'd, and NOT propagated — and the claim STAYS
        // LATCHED (no 1->0 reset). Teardown is single-shot per .NET dispose guidance (Dispose must not throw and is not
        // retryable): the infra was claimed and a dispose was attempted, so the receiver is treated as terminally disposed
        // even if the infra's own DisposeAsync threw. A SECOND teardown observes _infrastructureDisposed == 1 and no-ops.
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
            catch (Exception infrastructureDisposalError)
            {
                // Swallow-and-finalize: a throwing infra DisposeAsync must NOT propagate out of teardown and the claim stays
                // latched so the disposal is single-shot. Log the infra fault so it is observable, then complete teardown.
                _logger.LogError(infrastructureDisposalError, "Infrastructure receiver disposal faulted during teardown; swallowed so teardown completes terminally and is not retried.");
            }
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

        // INVARIANT: the ONE seam where teardown entrypoints diverge — driven now by the gate-owned recorded teardown
        // DISPOSITION rather than the admitted caller's own entrypoint, so a Dispose racing a Stop winner still disposes the
        // infra (strongest-wins). The disposition gate stays at THIS CALL SITE: the Dispose-strength branch delegates the
        // claim+dispose to TryClaimAndDisposeInfrastructureAsync — the single sole-writer of _infrastructureDisposed 0->1 —
        // so the later folded post-body dispose step in QuiesceAsync and the synchronous Dispose(bool)
        // field-null both observe a flag set ONLY by a dispose that actually completed. The Stop-strength else branch
        // stops the receiver and claims nothing. The top-of-method null-guard keeps a concurrent Dispose(bool) field-null
        // from reaching either branch on a null infra; the primitive re-captures into its own local for the same reason.
        // Called ONLY from the gated QuiesceCoreAsync, so the delegated claim+dispose is gate-serialized.
        // SWALLOW-AND-FINALIZE: the Dispose-strength branch's primitive already swallows-and-logs a throwing infra
        // DisposeAsync; mirror that here for the Stop-strength branch so a throwing infra.StopReceiver() is CAUGHT,
        // LogError'd, and NOT propagated. Teardown must complete deterministically — the caller (QuiesceCoreAsync) then
        // latches TornDown regardless, so a throwing infra teardown never aborts the terminal transition.
        async Task TearDownInfrastructureAsync()
        {
            var infrastructureReceiver = _infrastructureReceiver;
            if (infrastructureReceiver == null)
            {
                return;
            }

            if (_teardownDisposition >= TeardownStrengthDispose)
            {
                await TryClaimAndDisposeInfrastructureAsync().ConfigureAwait(false);
            }
            else
            {
                try
                {
                    await infrastructureReceiver.StopReceiver().ConfigureAwait(false);
                }
                catch (Exception infrastructureStopError)
                {
                    // Swallow-and-finalize: a throwing infra StopReceiver() must NOT propagate out of teardown. Log it so it
                    // is observable, then complete teardown so the terminal transition still latches.
                    _logger.LogError(infrastructureStopError, "Infrastructure receiver StopReceiver faulted during teardown; swallowed so teardown completes terminally.");
                }
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
            var worker = Task.Run(() => RunProcessingWorkerAsync(messageContext, transactionContext, workerToken));

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
            // INVARIANT: THE single choke point at which the diagnostics half observes a delivery fault. A C#
            // exception FILTER runs during the FIRST PASS of exception handling — before the stack unwinds and
            // before ANY catch body below executes — so every fault that leaves the try above is observed here
            // regardless of which ladder branch settles it, including branches added later. RetainReceiveFailure
            // ALWAYS returns false, so this clause never admits an exception: clause ordering, settlement choice,
            // swallow semantics and the concurrency invariants of the ladder below are byte-for-byte unchanged.
            catch (Exception deliveryFault) when (RetainReceiveFailure(deliveryFault, workerToken))
            {
                throw; // unreachable: the filter above never admits.
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
                RecordReceiveSettlement(BrokerDiagnostics.Settlements.Deadletter);
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
                        RecordReceiveSettlement(BrokerDiagnostics.Settlements.Deadletter);
                        if (await TryDeadletterWithRecoveryAsync(messageContext, transactionContext, e, workerToken))
                        {
                            await TryExecuteFailedRecoveryAction(messageContext, "Max message receive attempts exceeded", e, deliveryCount, transactionContext);
                        }
                    }
                    else
                    {
                        RecordReceiveSettlement(BrokerDiagnostics.Settlements.Nack);
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
                    CountReceiveAttempt();
                    await DispatchReceivedMessageAsync(brokeredMessagePayload, messageContext, receiverTokenSource);
                    return true;
                }, receiverTokenSource);

            if (!receiverTokenSource.IsCancellationRequested)
            {
                RecordReceiveSettlement(BrokerDiagnostics.Settlements.Ack);
                if (await TryAckWithRecoveryAsync(messageContext, transactionContext, receiverTokenSource))
                {
                    localTransaction?.Complete();
                }
            }
            else
            {
                RecordReceiveSettlement(BrokerDiagnostics.Settlements.Nack);
                await TryNackWithRecoveryAsync(messageContext, transactionContext, receiverTokenSource);
            }
        }

        // INVARIANT: THE single place a settle-path fault is SWALLOWED INTO A BOOL. Every "try to settle, report
        // success as a bool, never rethrow" path routes through here, so the swallow — and the diagnostics retention
        // that must accompany it — exists once rather than once per settle path. This is deliberately the twin of the
        // exception FILTER on the worker's processing try block: the filter observes every fault that LEAVES that
        // block, and a fault converted to `false` here never does, so this is the only other point at which a
        // delivery's fault can be lost. A settle path added later cannot forget the retention, because it cannot
        // perform the swallow itself (ADR-0010 D11).
        // Control flow is byte-for-byte what each per-settlement method did before: the same broad catch, the same
        // LogError, the same `false`. Only the retention is new.
        private async Task<bool> TrySettleWithRecoveryAsync(Func<Task<bool>> settle, string failureDescription, CancellationToken settlementToken)
        {
            try
            {
                return await _recoveryStrategy.ExecuteAsync(settle, settlementToken);
            }
            catch (Exception settlementFault)
            {
                _logger.LogError(settlementFault, failureDescription);
                RetainSettlementFailure(settlementFault, settlementToken);
                return false;
            }
        }

        private Task<bool> TryAckWithRecoveryAsync(MessageBrokerContext messageContext, TransactionContext transactionContext, CancellationToken receiverTokenSource)
            => TrySettleWithRecoveryAsync(
                () => _infrastructureReceiver.AckMessageAsync(messageContext, transactionContext, receiverTokenSource),
                "Unable to send acknowledgment",
                receiverTokenSource);

        private Task<bool> TryNackWithRecoveryAsync(MessageBrokerContext messageContext, TransactionContext transactionContext, CancellationToken receiverTokenSource)
            => TrySettleWithRecoveryAsync(
                () => _infrastructureReceiver.NackMessageAsync(messageContext, transactionContext, receiverTokenSource),
                "Unable to send negative acknowledgment",
                receiverTokenSource);

        private Task<bool> TryDeadletterWithRecoveryAsync(MessageBrokerContext messageContext, TransactionContext transactionContext, Exception e, CancellationToken receiverTokenSource)
            => TrySettleWithRecoveryAsync(
                () => _infrastructureReceiver.DeadletterMessageAsync(messageContext, transactionContext, "Poisoned message received", e.ToString(), receiverTokenSource),
                "Unable to deadletter message",
                receiverTokenSource);

        private Task<bool> TryExecuteFailedRecoveryAction(MessageBrokerContext messageContext, string failureDescription, Exception exception, int deliveryCount, TransactionContext transactionContext)
        {
            // Built ONCE, on the first Recovery attempt, and reused by every retry — the shape this method always had.
            // It is built INSIDE the settle delegate rather than before the call so that its own argument validation
            // stays behind the same swallow it was behind before this method was routed through the shared choke
            // point; hoisting it out would have turned an ArgumentException into an escape from the error ladder.
            FailureContext failureContext = null;

            return TrySettleWithRecoveryAsync(
                async () =>
                {
                    failureContext ??= new FailureContext(messageContext.BrokeredMessage, this.ErrorQueueName, failureDescription, exception, deliveryCount, transactionContext);
                    await _failedRecoveryAction.ExecuteAsync(failureContext);
                    return true;
                },
                "Unable to execute recovery action",
                messageContext.CancellationToken);
        }

        // CANONICAL ASYNC DISPOSE SHAPE: DisposeAsync delegates the async cleanup to DisposeAsyncCore, then runs the
        // synchronous unmanaged/finalizer-suppression tail (Dispose(false); GC.SuppressFinalize(this)) exactly as the .NET
        // async-dispose pattern prescribes. ConfigureAwait(false) keeps teardown context-free.
        public async ValueTask DisposeAsync()
        {
            await DisposeAsyncCore().ConfigureAwait(false);

            Dispose(disposing: false);
            GC.SuppressFinalize(this);
        }

        // INVARIANT: the async cleanup half of the canonical async-dispose pattern. Routes through the single shared
        // quiesce-before-dispose contract so DisposeAsync, StopReceiver, and the synchronous Dispose path all converge on
        // ONE idempotent disposition. QuiesceAsync cancels (guarded against an already-disposed token source), awaits the
        // LOOP to completion first — draining only the in-flight worker SNAPSHOT is not enough, since the loop may still be
        // inside WaitAsync / ReceiveMessageAsync or in the gap after receiving but before SpawnProcessingWorker added the
        // worker to _inFlightTasks, where the snapshot is empty or stale — then drains residual workers, then disposes the
        // infrastructure receiver asynchronously, then exchanges-and-disposes the shared primitives. A repeated or
        // concurrent DisposeAsync observes the first caller's quiesce completion instead of re-running it. CONFIRMED:
        // requests DisposeAsync-strength (NOT Stop-strength), so the canonical extraction does not weaken what DisposeAsync
        // requests — the infra is still disposed, never merely stopped. ConfigureAwait(false) keeps teardown context-free.
        protected virtual async ValueTask DisposeAsyncCore()
        {
            await QuiesceAsync(InfrastructureTeardown.DisposeAsync).ConfigureAwait(false);
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
                    // SWALLOW-AND-FINALIZE: infra teardown is non-throwing at the boundary (the strength-aware seam catches,
                    // logs, and swallows a throwing infra StopReceiver()/DisposeAsync()), so the quiesce body runs to
                    // completion and latches TornDown even when infra teardown faults. Should the synchronous wait observe
                    // any OTHER unexpected fault, swallow-and-log it here (Dispose must NOT throw per .NET guidance) and then
                    // FALL THROUGH to the terminal latching below rather than returning early — teardown is single-shot, not
                    // retryable, so a SECOND Dispose() must observe a terminal receiver and no-op.
                    try
                    {
                        QuiesceAsync(InfrastructureTeardown.DisposeSync).GetAwaiter().GetResult();
                    }
                    catch (Exception quiesceError)
                    {
                        _logger.LogError(quiesceError, "Synchronous receiver teardown faulted; swallowed so Dispose does not throw and the receiver still completes terminally.");
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
                // _disposedValue fast path in QuiesceAsync would lock the host's later genuine teardown out (b91b751).
                // SWALLOW-AND-FINALIZE: a faulting infra teardown is swallowed inside the quiesce body, which still latches
                // TornDown, and the sync wait's catch above also falls through to here — so a Started receiver always
                // reaches this latch and a swallowed teardown fault still makes repeated synchronous Dispose a no-op. A
                // successful sync Dispose and the Dispose(false) reached from a completed DisposeAsync both observe TornDown
                // and latch. The gate itself is left for GC (never disposed), so latching here does not strand a concurrent
                // WaitAsync.
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
