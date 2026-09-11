using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.AzureServiceBus.Receiving
{
    /// <summary>
    /// Session-mode <see cref="IServiceBusSessionMessageReceiver"/> that holds up to N Azure Service Bus sessions at
    /// once by owning N single-session children (<see cref="IServiceBusSessionChildReceiver"/>). Each child accepts
    /// its own session, serves that session's messages FIFO, and settles on the session receiver that delivered the
    /// message, so the per-session guarantee is preserved by construction: only the number of sessions held at once
    /// changes (ADR-0014).
    /// </summary>
    /// <remarks>
    /// INVARIANT: a child that is serving a delivery is never asked for another message. A child is re-armed only
    /// after <see cref="DeliveryReleased(ServiceBusReceivedMessage)"/> reports the worker is finished with the
    /// delivery it yielded, or after it yields no message at all.
    /// INVARIANT: the child-freed signal is in EVERY await set, not only when all children are busy — otherwise a
    /// child freed while the loop waits on a sibling's pending receive sits idle until that sibling happens to yield.
    /// INVARIANT: all mutable state is read and written under <c>_syncLock</c>, and nothing is awaited while the lock
    /// is held. Arming calls the child's task-returning receive under the lock — a call, not an await.
    /// </remarks>
    internal sealed class SessionReceiverMultiplexer : IServiceBusSessionMessageReceiver
    {
        private readonly object _syncLock = new object();
        private readonly Func<IServiceBusSessionChildReceiver> _sessionChildFactory;
        private readonly string _receiverPath;
        private readonly ILogger _logger;
        private readonly SessionSlot[] _slots;

        // Keyed by REFERENCE: settlement is by received message object (IServiceBusMessageReceiver), and two
        // distinct deliveries may carry equal field values.
        private readonly Dictionary<ServiceBusReceivedMessage, SessionSlot> _slotsByDeliveredMessage
            = new Dictionary<ServiceBusReceivedMessage, SessionSlot>(ReferenceEqualityComparer.Instance);

        // Cancels the receives armed on the children. Deliberately NOT disposed: arming reads this token under the
        // lock while CloseAsync cancels outside it, so disposing would race an in-flight arm for no benefit.
        private readonly CancellationTokenSource _armedReceiveSource = new CancellationTokenSource();

        // INVARIANT: RunContinuationsAsynchronously. DeliveryReleased completes this source from the worker's
        // finally, before the concurrency slot is returned; a synchronous continuation would re-enter the receive
        // loop on that thread and, because C# locks are re-entrant, could re-enter multiplexer state mid-mutation.
        private TaskCompletionSource<bool> _childFreedSource
            = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        private bool _closed;

        public SessionReceiverMultiplexer(int maxConcurrentSessions,
                                          Func<IServiceBusSessionChildReceiver> sessionChildFactory,
                                          string receiverPath,
                                          ILogger logger)
        {
            if (maxConcurrentSessions < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxConcurrentSessions), maxConcurrentSessions,
                    "A session receiver multiplexer must hold at least one session.");
            }

            _sessionChildFactory = sessionChildFactory ?? throw new ArgumentNullException(nameof(sessionChildFactory));
            _receiverPath = receiverPath;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _slots = new SessionSlot[maxConcurrentSessions];
            for (var slotIndex = 0; slotIndex < _slots.Length; slotIndex++)
            {
                _slots[slotIndex] = new SessionSlot(_sessionChildFactory());
            }
        }

        /// <summary>
        /// True once this multiplexer has been closed. Deliberately NOT derived from the children: reporting closed
        /// because one child closed would discard N - 1 healthy children, and reporting closed only when all children
        /// are closed would let a single disposed child be rebuilt forever.
        /// </summary>
        public bool IsClosedOrClosing
        {
            get
            {
                lock (_syncLock)
                {
                    return _closed;
                }
            }
        }

        /// <summary>
        /// Arms every child that is neither serving a delivery nor already awaiting one, then waits for the first of:
        /// a child yielding, a child being freed, or cancellation. Returns the message a child yielded, or null when
        /// a child yielded nothing, was replaced after disposal, or the multiplexer is closed.
        /// </summary>
        public async Task<ServiceBusReceivedMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            // INVARIANT: the park must be cancellable by the caller's token, otherwise receiver teardown awaits a
            // loop that never returns. The registration is disposed when this call returns so a long-lived token
            // does not accumulate one registration per delivery.
            var cancelledSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => cancelledSource.TrySetResult(true)))
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var awaitSet = new List<Task>(_slots.Length + 2);
                    List<(string sessionId, TimeSpan heldFor, DateTimeOffset lockedUntil)> staleSessionHolds;
                    var armedReceiveToken = _armedReceiveSource.Token;
                    lock (_syncLock)
                    {
                        if (_closed)
                        {
                            return null;
                        }

                        ArmIdleChildren(armedReceiveToken);
                        staleSessionHolds = CollectNewlyStaleSessionHolds();

                        foreach (var slot in _slots)
                        {
                            if (slot.PendingReceive != null)
                            {
                                awaitSet.Add(slot.PendingReceive);
                            }
                        }

                        // INVARIANT: captured under the SAME lock as the arming pass and replaced under the lock when
                        // signalled, so a release landing between the capture and the await completes THIS task
                        // rather than being lost.
                        awaitSet.Add(_childFreedSource.Task);
                    }

                    LogStaleSessionHolds(staleSessionHolds);

                    awaitSet.Add(cancelledSource.Task);

                    var completed = await Task.WhenAny(awaitSet).ConfigureAwait(false);

                    SessionSlot yieldingSlot;
                    lock (_syncLock)
                    {
                        yieldingSlot = FindSlotAwaiting(completed);
                        if (yieldingSlot != null)
                        {
                            yieldingSlot.PendingReceive = null;
                        }
                    }

                    if (yieldingSlot == null)
                    {
                        // The freed signal or cancellation woke the park; re-evaluate from the top.
                        continue;
                    }

                    ServiceBusReceivedMessage message;
                    try
                    {
                        message = await ((Task<ServiceBusReceivedMessage>)completed).ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException disposed) when (yieldingSlot.Child.IsClosedOrClosing)
                    {
                        await ReplaceDisposedChildAsync(yieldingSlot, disposed).ConfigureAwait(false);
                        return null;
                    }
                    catch (OperationCanceledException) when (IsClosedOrClosing)
                    {
                        // The armed receive was cancelled by teardown, not by a fault.
                        return null;
                    }

                    if (message == null)
                    {
                        // Drain, idle roll or accept timeout: the slot is idle again and is re-armed on the next pass.
                        continue;
                    }

                    lock (_syncLock)
                    {
                        if (_closed)
                        {
                            return null;
                        }

                        yieldingSlot.BusyMessage = message;
                        yieldingSlot.BusySince = DateTimeOffset.UtcNow;
                        yieldingSlot.StaleSessionLockWarned = false;
                        _slotsByDeliveredMessage[message] = yieldingSlot;
                    }

                    return message;
                }
            }
        }

        /// <summary>
        /// Answers which held session receiver delivered <paramref name="message"/>, or null when no child currently
        /// holds that delivery.
        /// </summary>
        public ServiceBusSessionReceiver SessionReceiverFor(ServiceBusReceivedMessage message)
        {
            lock (_syncLock)
            {
                return TryFindSlotFor(message, out var slot) ? slot.Child.HeldSessionReceiver : null;
            }
        }

        /// <summary>
        /// Frees the session slot <paramref name="message"/>'s delivery occupied and wakes a parked receive so the
        /// freed child is re-armed immediately.
        /// </summary>
        /// <remarks>
        /// A message this multiplexer does not hold is a SILENT no-op, not a fault: a cancelled worker still runs the
        /// finally that raises this signal, so throwing here would report an error on every shutdown. Contrast with
        /// settlement, where an unroutable message MUST throw.
        /// </remarks>
        public void DeliveryReleased(ServiceBusReceivedMessage message)
        {
            TaskCompletionSource<bool> freedSource;
            lock (_syncLock)
            {
                if (!TryFindSlotFor(message, out var slot))
                {
                    return;
                }

                _slotsByDeliveredMessage.Remove(message);
                slot.BusyMessage = null;
                slot.StaleSessionLockWarned = false;
                freedSource = _childFreedSource;
                _childFreedSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            freedSource.TrySetResult(true);
        }

        public Task CompleteAsync(ServiceBusReceivedMessage message)
            => DeliveringChildFor(message).CompleteAsync(message);

        public Task AbandonAsync(ServiceBusReceivedMessage message, IDictionary<string, object> propertiesToModify)
            => DeliveringChildFor(message).AbandonAsync(message, propertiesToModify);

        public Task DeadLetterAsync(ServiceBusReceivedMessage message, string deadLetterReason, string deadLetterErrorDescription)
            => DeliveringChildFor(message).DeadLetterAsync(message, deadLetterReason, deadLetterErrorDescription);

        /// <summary>
        /// Closes every child, cancels the receives armed on them, observes their pending receives and wakes a parked
        /// receive loop. Idempotent: a second call closes nothing a second time.
        /// </summary>
        public async Task CloseAsync()
        {
            bool alreadyClosed;
            var children = new List<IServiceBusSessionChildReceiver>(_slots.Length);
            var pendingReceives = new List<Task<ServiceBusReceivedMessage>>(_slots.Length);
            TaskCompletionSource<bool> freedSource;
            lock (_syncLock)
            {
                alreadyClosed = _closed;
                _closed = true;
                _slotsByDeliveredMessage.Clear();

                foreach (var slot in _slots)
                {
                    children.Add(slot.Child);
                    if (slot.PendingReceive != null)
                    {
                        pendingReceives.Add(slot.PendingReceive);
                        slot.PendingReceive = null;
                    }

                    slot.BusyMessage = null;
                }

                freedSource = _childFreedSource;
                _childFreedSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            if (alreadyClosed)
            {
                return;
            }

            _armedReceiveSource.Cancel();
            freedSource.TrySetResult(true);

            foreach (var child in children)
            {
                await CloseChildAsync(child).ConfigureAwait(false);
            }

            foreach (var pendingReceive in pendingReceives)
            {
                await ObservePendingReceiveAsync(pendingReceive).ConfigureAwait(false);
            }
        }

        // INVARIANT: called under _syncLock. The child's receive is INVOKED here, not awaited — the returned task is
        // parked on outside the lock.
        private void ArmIdleChildren(CancellationToken armedReceiveToken)
        {
            foreach (var slot in _slots)
            {
                if (slot.PendingReceive == null && slot.BusyMessage == null)
                {
                    slot.PendingReceive = slot.Child.ReceiveAsync(armedReceiveToken);
                }
            }
        }

        // INVARIANT: called under _syncLock. Collects rather than logs so the log call happens outside the lock, and
        // marks each hold so ONE warning is raised per busy episode. LOG-ONLY by decision (ADR-0014): a slot whose
        // session lock lapsed while its worker is still running is NOT reclaimed, because reclaiming it would race
        // the live worker and make that worker's settlement unroutable.
        private List<(string sessionId, TimeSpan heldFor, DateTimeOffset lockedUntil)> CollectNewlyStaleSessionHolds()
        {
            List<(string, TimeSpan, DateTimeOffset)> staleSessionHolds = null;
            var now = DateTimeOffset.UtcNow;

            foreach (var slot in _slots)
            {
                if (slot.BusyMessage == null || slot.StaleSessionLockWarned)
                {
                    continue;
                }

                var lockedUntil = slot.Child.HeldSessionLockedUntil;
                if (lockedUntil == null || lockedUntil.Value > now)
                {
                    continue;
                }

                slot.StaleSessionLockWarned = true;
                if (staleSessionHolds == null)
                {
                    staleSessionHolds = new List<(string, TimeSpan, DateTimeOffset)>();
                }

                staleSessionHolds.Add((slot.Child.HeldSessionId, now - slot.BusySince, lockedUntil.Value));
            }

            return staleSessionHolds;
        }

        private void LogStaleSessionHolds(List<(string sessionId, TimeSpan heldFor, DateTimeOffset lockedUntil)> staleSessionHolds)
        {
            if (staleSessionHolds == null)
            {
                return;
            }

            foreach (var staleSessionHold in staleSessionHolds)
            {
                _logger.LogWarning($"Azure Service Bus session '{staleSessionHold.sessionId}' on '{_receiverPath}' has held its slot for {staleSessionHold.heldFor} but its session lock expired at {staleSessionHold.lockedUntil:O}; the slot stays occupied until the worker releases the delivery");
            }
        }

        // INVARIANT: called under _syncLock.
        private SessionSlot FindSlotAwaiting(Task completed)
        {
            foreach (var slot in _slots)
            {
                if (ReferenceEquals(slot.PendingReceive, completed))
                {
                    return slot;
                }
            }

            return null;
        }

        // INVARIANT: called under _syncLock.
        private bool TryFindSlotFor(ServiceBusReceivedMessage message, out SessionSlot slot)
        {
            if (message == null)
            {
                slot = null;
                return false;
            }

            return _slotsByDeliveredMessage.TryGetValue(message, out slot);
        }

        /// <remarks>
        /// A message no child holds THROWS rather than returning a completed task. The settlement members return a
        /// bare <see cref="Task"/>, so a completed task means SUCCESS — quietly returning one would have the receiver
        /// record an acknowledgement that never happened and commit the local transaction for a delivery the broker
        /// will redeliver. <see cref="InvalidOperationException"/> is deliberate: the module's retry and circuit
        /// breaker predicates match only <see cref="ServiceBusException"/>, so this is neither retried nor counted
        /// against the breaker, and the receiver's settlement recovery reports it as a failed settlement.
        /// </remarks>
        private IServiceBusSessionChildReceiver DeliveringChildFor(ServiceBusReceivedMessage message)
        {
            lock (_syncLock)
            {
                if (TryFindSlotFor(message, out var slot))
                {
                    return slot.Child;
                }
            }

            throw new InvalidOperationException($"No held Azure Service Bus session on '{_receiverPath}' delivered message '{message?.MessageId}' (session '{message?.SessionId}'), so the delivery cannot be settled");
        }

        private async Task ReplaceDisposedChildAsync(SessionSlot slot, ObjectDisposedException disposed)
        {
            await CloseChildAsync(slot.Child).ConfigureAwait(false);

            var replacement = _sessionChildFactory();
            lock (_syncLock)
            {
                slot.Child = replacement;
                slot.PendingReceive = null;
            }

            _logger.LogWarning(disposed, $"Azure Service Bus session child receiver for '{_receiverPath}' was disposed; it has been closed and replaced");
        }

        private async Task CloseChildAsync(IServiceBusSessionChildReceiver child)
        {
            try
            {
                await child.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogTrace(e, $"Failure closing a session child receiver for '{_receiverPath}'");
            }
        }

        // Every armed receive must be observed so a faulted one never surfaces as an unobserved task exception.
        private async Task ObservePendingReceiveAsync(Task<ServiceBusReceivedMessage> pendingReceive)
        {
            try
            {
                await pendingReceive.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: closing cancels the armed receives.
            }
            catch (ObjectDisposedException)
            {
                // Expected: the child's receiver was disposed as it closed.
            }
            catch (Exception e)
            {
                _logger.LogTrace(e, $"A session child receive for '{_receiverPath}' faulted while the multiplexer was closing");
            }
        }

        // One session slot: the child that holds the session, the receive armed on it, and the delivery it is
        // currently serving. Mutated only under the multiplexer's lock.
        private sealed class SessionSlot
        {
            public SessionSlot(IServiceBusSessionChildReceiver child) => Child = child;

            public IServiceBusSessionChildReceiver Child { get; set; }
            public Task<ServiceBusReceivedMessage> PendingReceive { get; set; }
            public ServiceBusReceivedMessage BusyMessage { get; set; }
            public DateTimeOffset BusySince { get; set; }
            public bool StaleSessionLockWarned { get; set; }
        }
    }
}
