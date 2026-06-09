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

        // INVARIANT: completed exactly once, at the IsReceiving = true seam in StartReceiverImpl. Backs the
        // ReceivingStarted startup-completion signal so callers gate on go-live without polling IsReceiving.
        // RunContinuationsAsynchronously keeps the awaiter's continuation off the receive-loop start path.
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
        public bool IsReceiving { get; private set; } = false;

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
            options.Description ??= options.MessageReceiverPath;
            _infrastructureReceiver = _infrastructureProvider.GetReceiver(options.InfrastructureType);
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
            // cross-entity guard rather than as an obscure semaphore error.
            if (_maxConcurrentCalls < 1)
            {
                throw new InvalidOperationException(
                    $"ReceiverOptions.MaxConcurrentCalls must be at least 1 for receiver '{options.MessageReceiverPath}'; found {_maxConcurrentCalls}. Configure a value >= 1.");
            }

            _logger.LogTrace("Initializing messaging infrastructure");
            await _infrastructureReceiver.InitializeAsync(_options, receiverTerminationToken);
            _logger.LogTrace("Successfully initialized messaging infrastructure");

            _logger.LogDebug("Receiver options: Infrastructure type: '{infrastructureType}', Transaction Mode: '{transactionMode}', Message receiver: '{messageReceiverPath}', Deadletter queue: '{deadLetterQueuePath}', Error queue: '{errorQueuePath}', Max receive attempts: '{maxReceiveAttempts}', Message sent from: '{sendingPath}', Max Concurrent Receives: '{maxConcurrentCalls}'",
                _options.InfrastructureType, options.TransactionMode, options.MessageReceiverPath, options.DeadLetterQueuePath, options.ErrorQueuePath, options.MaxReceiveAttempts, options.SendingPath, _maxConcurrentCalls);

            _concurrentMessagesSemaphore = new SemaphoreSlim(_maxConcurrentCalls, _maxConcurrentCalls);
            _messageReceiverLoopTokenSource = CancellationTokenSource.CreateLinkedTokenSource(receiverTerminationToken);

            _messageReceiverLoop = MessageReceiverLoopAsync();
            this.IsReceiving = true;
            _receivingStartedSource.TrySetResult(true);
            _logger.LogInformation("'{executingFunction}' has started receiving messages of type '{receiverMessageType}'.", nameof(BrokeredMessageReceiver<TMessage>), typeof(TMessage).Name);
            await _messageReceiverLoop;
            _logger.LogInformation("'{executingFunction}' for messages of type '{receiverMessageType}' is shutting down.", nameof(BrokeredMessageReceiver<TMessage>), typeof(TMessage).Name);
        }

        public async Task StopReceiver()
        {
            _messageReceiverLoopTokenSource?.Cancel();

            if (_messageReceiverLoop != null && !_messageReceiverLoop.IsFaulted)
            {
                await _messageReceiverLoop;
            }

            // INVARIANT: drain any worker tasks still in flight before disposing the semaphore / token source so no
            // worker touches a disposed SemaphoreSlim. When the loop completed normally its own finally already
            // drained; this is the belt-and-suspenders path for a faulted loop (the await above is skipped) and is
            // a no-op once the set is empty.
            await DrainInFlightWorkersAsync();

            await _infrastructureReceiver.StopReceiver();

            _concurrentMessagesSemaphore?.Dispose();
            _messageReceiverLoopTokenSource?.Dispose();
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
                _logger.LogCritical(e, "Receiver is unable continue due to critical error");
                await _criticalFailureNotifier.Notify(new FailureContext(null, this.ErrorQueueName, "Critical error occurred", e, -1, null));
            }
            finally
            {
                // Drain any still-running workers before the loop task completes so callers that await the loop
                // (StartReceiverImpl / StopReceiver) observe a fully-quiesced receiver.
                await DrainInFlightWorkersAsync();
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
                Interlocked.CompareExchange(ref _workerCriticalFault, e, null);
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
            // INVARIANT: signal cancellation and drain in-flight workers BEFORE the semaphore / token source are
            // disposed so no worker touches a disposed SemaphoreSlim. Dispose(disposing: false) below does not
            // dispose the managed semaphore/token source, so this is the async path's authoritative drain.
            _messageReceiverLoopTokenSource?.Cancel();
            await DrainInFlightWorkersAsync();

            await _infrastructureReceiver.DisposeAsync();

            _concurrentMessagesSemaphore?.Dispose();
            _messageReceiverLoopTokenSource?.Dispose();

            Dispose(disposing: false);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    _messageReceiverLoopTokenSource?.Cancel();
                    _infrastructureReceiver?.Dispose();
                    _concurrentMessagesSemaphore?.Dispose();
                    _messageReceiverLoopTokenSource?.Dispose();
                }

                _infrastructureReceiver = null;
                _disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
