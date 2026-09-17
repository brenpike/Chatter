using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.AzureServiceBus.Receiving
{
    /// <summary>
    /// Owns message-lock renewal lifetime PER DELIVERY for the non-session receive path.
    /// </summary>
    internal sealed class MessageLockRenewalRegistry
    {
        private static readonly Func<TimeSpan, CancellationToken, Task> _systemDelay = (delay, cancellationToken) => Task.Delay(delay, cancellationToken);

        private readonly object _syncLock = new object();

        // Keyed by REFERENCE, for the same reason SessionReceiverMultiplexer._slotsByDeliveredMessage is: settlement
        // is by received message object, and two distinct deliveries may carry equal field values, so value equality
        // would conflate two in-flight deliveries onto one renewal.
        private readonly Dictionary<ServiceBusReceivedMessage, Registration> _renewalsByDeliveredMessage
            = new Dictionary<ServiceBusReceivedMessage, Registration>(ReferenceEqualityComparer.Instance);

        // A stopped delivery's loop is cancelled but NOT yet ended: it may still be awaiting the broker inside the
        // renew call. It stays tracked HERE until it actually ends, because leaving the keyed collection is not the
        // same event as the loop finishing, and a close that could not see it would report teardown complete while
        // that renewal was still live against the receiver about to be closed.
        private readonly HashSet<Registration> _stoppedRenewalsStillEnding = new HashSet<Registration>();

        private readonly TimeSpan _maxRenewalDuration;
        private readonly string _receiverPath;
        private readonly ILogger _logger;
        private readonly TimeProvider _timeProvider;
        private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

        private bool _closed;

        // The ONE completion every caller of CloseAsync observes. A close that answered per-caller would let a
        // second caller proceed to close the SDK receiver while the first was still awaiting a renewal.
        private TaskCompletionSource<bool> _closeGate;

        internal MessageLockRenewalRegistry(TimeSpan maxRenewalDuration, string receiverPath, ILogger logger)
            : this(maxRenewalDuration, receiverPath, logger, TimeProvider.System, _systemDelay)
        {
        }

        internal MessageLockRenewalRegistry(TimeSpan maxRenewalDuration,
                                            string receiverPath,
                                            ILogger logger,
                                            TimeProvider timeProvider,
                                            Func<TimeSpan, CancellationToken, Task> delayAsync)
        {
            _maxRenewalDuration = maxRenewalDuration;
            _receiverPath = receiverPath;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
            _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
        }

        /// <summary>The number of in-flight deliveries whose locks this registry is currently renewing.</summary>
        internal int ActiveRenewalCount
        {
            get
            {
                lock (_syncLock)
                {
                    return _renewalsByDeliveredMessage.Count;
                }
            }
        }

        /// <summary>
        /// Begins renewing <paramref name="message"/>'s lock on a renewal loop of its OWN. A no-op when renewal is
        /// disabled, when this registry has closed, or when that message reference is already renewing.
        /// </summary>
        internal void Start(ServiceBusReceivedMessage message, Func<CancellationToken, Task> renewAsync)
        {
            if (!LockRenewalLoop.IsEnabled(_maxRenewalDuration))
            {
                return;
            }

            var renewalSource = new CancellationTokenSource();
            var description = DescribeDelivery(message);
            var loop = new LockRenewalLoop(() => message.LockedUntil,
                                           renewAsync,
                                           _maxRenewalDuration,
                                           ServiceBusFailureReason.MessageLockLost,
                                           description,
                                           _logger,
                                           _timeProvider,
                                           _delayAsync);

            lock (_syncLock)
            {
                if (_closed || _renewalsByDeliveredMessage.ContainsKey(message))
                {
                    renewalSource.Dispose();
                    return;
                }

                // INVARIANT: the loop is INVOKED here, not awaited — a task-returning call under the lock, exactly
                // as SessionReceiverMultiplexer arms a child's receive under its own lock. Nothing is ever awaited
                // while _syncLock is held.
                _renewalsByDeliveredMessage.Add(message, new Registration(renewalSource, loop.RunAsync(renewalSource.Token), description));
            }
        }

        /// <summary>
        /// Ends every delivery's renewal and AWAITS each loop — including a loop already STOPPED but still ending —
        /// so no renewal outlives the receiver that owns it. Idempotent, and a closed registry starts nothing
        /// further.
        /// </summary>
        /// <remarks>
        /// INVARIANT: every caller observes the SAME completion. Repeated and overlapping closes alike await one
        /// gate, so no caller can be told teardown is done while another close is still awaiting a renewal — which
        /// is the whole point of awaiting at all, since <see cref="AzureSdkMessageReceiverAdapter"/> closes the SDK
        /// receiver the moment its close of this registry returns.
        /// </remarks>
        internal Task CloseAsync()
        {
            List<Registration> renewalsToEnd;
            List<Registration> renewalsAlreadyEnding;
            TaskCompletionSource<bool> closeGate;

            lock (_syncLock)
            {
                if (_closeGate != null)
                {
                    return _closeGate.Task;
                }

                _closed = true;
                _closeGate = closeGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                // Taken in ONE lock: a concurrent Stop either still holds its registration here or has already moved
                // it to the stopped-but-ending set, so every live loop lands in exactly one of these two lists.
                renewalsToEnd = new List<Registration>(_renewalsByDeliveredMessage.Values);
                _renewalsByDeliveredMessage.Clear();
                renewalsAlreadyEnding = new List<Registration>(_stoppedRenewalsStillEnding);
            }

            // Cancelled OUTSIDE the lock, and BEFORE this returns, so every renewal this close owns is already
            // ending by the time the caller holds a task to await. Nothing is ever awaited while _syncLock is held.
            foreach (var registration in renewalsToEnd)
            {
                registration.RenewalSource.Cancel();
            }

            return AwaitEveryRenewalAsync(closeGate, renewalsToEnd, renewalsAlreadyEnding);
        }

        private async Task AwaitEveryRenewalAsync(TaskCompletionSource<bool> closeGate,
                                                  List<Registration> renewalsToEnd,
                                                  List<Registration> renewalsAlreadyEnding)
        {
            try
            {
                foreach (var registration in renewalsToEnd)
                {
                    await ObserveRenewalLoopAsync(registration).ConfigureAwait(false);
                    registration.RenewalSource.Dispose();
                }

                foreach (var registration in renewalsAlreadyEnding)
                {
                    await AwaitStoppedRenewalLoopAsync(registration).ConfigureAwait(false);
                }
            }
            finally
            {
                closeGate.TrySetResult(true);
            }
        }

        // Close must end EVERY delivery's renewal, so one loop faulting on an unexpected broker failure cannot leave
        // the rest running.
        private async Task ObserveRenewalLoopAsync(Registration registration)
        {
            try
            {
                await registration.Loop.ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, $"Failure renewing the Azure Service Bus lock for {registration.Description}");
            }
        }

        // Waits for a loop the release path already STOPPED. Close only needs it to have ENDED: the Stop that
        // cancelled it owns its renewal source's disposal and already reports a faulted loop, so close neither
        // disposes nor logs here — doing either would give one renewal source two owners.
        private static async Task AwaitStoppedRenewalLoopAsync(Registration registration)
        {
            try
            {
                await registration.Loop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Reported by EndRenewal, which owns this loop.
            }
        }

        /// <summary>
        /// Ends <paramref name="message"/>'s renewal and nothing else's. Idempotent, never throws, and a SILENT
        /// no-op for a delivery this registry does not hold.
        /// </summary>
        /// <remarks>
        /// A delivery this registry never saw is not a fault: <see cref="ServiceBusReceiver"/> swaps its inner
        /// receiver and closes the old one after an <see cref="ObjectDisposedException"/>, so a delivery still in
        /// flight against the DISCARDED adapter has its release routed to the NEW adapter, which has never seen that
        /// message reference. Throwing there would turn a normal recovery into a crash.
        /// </remarks>
        internal void Stop(ServiceBusReceivedMessage message)
        {
            Registration registration;
            lock (_syncLock)
            {
                if (message == null || !_renewalsByDeliveredMessage.TryGetValue(message, out registration))
                {
                    return;
                }

                _renewalsByDeliveredMessage.Remove(message);

                // Leaving the keyed collection is NOT the loop having ended — it may still be awaiting the broker
                // inside the renew call. Tracked here, in the SAME lock that removed it, so a close cannot snapshot
                // a registry that has forgotten a renewal which is still running.
                _stoppedRenewalsStillEnding.Add(registration);
            }

            // INVARIANT: exactly ONE caller ever owns a given renewal source's disposal. Removing under the lock
            // before cancelling guarantees it: no other caller can still observe this registration. A close that
            // sees the stopped registration only AWAITS its loop; it never cancels or disposes it again.
            registration.RenewalSource.Cancel();
            DisposeRenewalSourceWhenLoopEnds(registration);
        }

        // Disposes the delivery's renewal source once its loop has ended, WITHOUT awaiting that loop on the release
        // path. Mirrors ServiceBusReceiver.CloseDiscardedReceiver: the release must not block on a renewal that is
        // still awaiting the broker, and reading the loop's exception here also observes a faulted renewal.
        private void DisposeRenewalSourceWhenLoopEnds(Registration registration)
        {
            _ = registration.Loop.ContinueWith(EndRenewal,
                                               registration,
                                               CancellationToken.None,
                                               TaskContinuationOptions.ExecuteSynchronously,
                                               TaskScheduler.Default);
        }

        private void EndRenewal(Task renewal, object endedRegistration)
        {
            var registration = (Registration)endedRegistration;

            lock (_syncLock)
            {
                _stoppedRenewalsStillEnding.Remove(registration);
            }

            registration.RenewalSource.Dispose();

            if (renewal.Exception != null)
            {
                _logger.LogWarning(renewal.Exception, $"Failure renewing the Azure Service Bus lock for {registration.Description}");
            }
        }

        private string DescribeDelivery(ServiceBusReceivedMessage message)
            => $"message '{message.MessageId}' on '{_receiverPath}'";

        /// <summary>One delivery's renewal: the cancellation source that ends it, and the loop task to await.</summary>
        private sealed class Registration
        {
            public Registration(CancellationTokenSource renewalSource, Task loop, string description)
            {
                RenewalSource = renewalSource;
                Loop = loop;
                Description = description;
            }

            public CancellationTokenSource RenewalSource { get; }

            public Task Loop { get; }

            public string Description { get; }
        }
    }
}
