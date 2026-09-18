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
    /// <remarks>
    /// INVARIANT: this registry holds no renewal cleanup obligation of its own. Each renewal's cancellation,
    /// disposal and fault report belong to the <see cref="RenewalLifetime"/> that runs it, so no step here can
    /// skip a cleanup by raising. What is left is bookkeeping: ONE collection whose membership means EXACTLY
    /// "this renewal has not ended", written by exactly two events — an insert in <see cref="Start"/>, and the
    /// owning renewal removing itself as it ends.
    /// </remarks>
    internal sealed class MessageLockRenewalRegistry
    {
        private static readonly Func<TimeSpan, CancellationToken, Task> _systemDelay = (delay, cancellationToken) => Task.Delay(delay, cancellationToken);

        private readonly object _syncLock = new object();

        // Keyed by REFERENCE, for the same reason SessionReceiverMultiplexer._slotsByDeliveredMessage is: settlement
        // is by received message object, and two distinct deliveries may carry equal field values, so value equality
        // would conflate two in-flight deliveries onto one renewal.
        private readonly Dictionary<ServiceBusReceivedMessage, RenewalLifetime> _renewalsByDeliveredMessage
            = new Dictionary<ServiceBusReceivedMessage, RenewalLifetime>(ReferenceEqualityComparer.Instance);

        private readonly TimeSpan _maxRenewalDuration;
        private readonly string _receiverPath;
        private readonly ILogger _logger;
        private readonly TimeProvider _timeProvider;
        private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

        // The ONE completion every caller of CloseAsync observes, and this registry's closed flag in the SAME
        // field, so a registry cannot be closed to new renewals yet missing the completion that ends the ones it
        // has. It is an async method's task, which the language guarantees reaches a terminal state, so a close
        // that is published but never completed is unrepresentable.
        private Task _closeCompletion;

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
        /// <remarks>
        /// A projection over the one collection rather than a second collection of its own: a stopped renewal is
        /// held until it has actually ended, and a delivery whose renewal was stopped is no longer in flight.
        /// </remarks>
        internal int ActiveRenewalCount
        {
            get
            {
                lock (_syncLock)
                {
                    var inFlight = 0;
                    foreach (var renewal in _renewalsByDeliveredMessage.Values)
                    {
                        if (!renewal.Stopped)
                        {
                            inFlight++;
                        }
                    }

                    return inFlight;
                }
            }
        }

        /// <summary>
        /// Begins renewing <paramref name="message"/>'s lock on a renewal of its OWN. A no-op when renewal is
        /// disabled, when this registry has closed, or when that message reference is already renewing.
        /// </summary>
        internal void Start(ServiceBusReceivedMessage message, Func<CancellationToken, Task> renewAsync)
        {
            if (!LockRenewalLoop.IsEnabled(_maxRenewalDuration))
            {
                return;
            }

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
                if (_closeCompletion != null || _renewalsByDeliveredMessage.ContainsKey(message))
                {
                    return;
                }

                var renewal = new RenewalLifetime(description, _logger);

                // INVARIANT: the renewal is TRACKED BEFORE it begins. A loop that ends without ever awaiting ends
                // inside Begin and leaves its tracking from there, so beginning first would remove an entry not yet
                // added and leave a dead one behind forever. The lock is reentrant, so that synchronous exit
                // re-entering it here is safe. Nothing is ever awaited while _syncLock is held.
                _renewalsByDeliveredMessage.Add(message, renewal);
                renewal.Begin(loop.RunAsync, () => EndTracking(message));
            }
        }

        /// <summary>
        /// Ends every delivery's renewal and AWAITS each one — including a renewal already STOPPED but still
        /// ending — so no renewal outlives the receiver that owns it. Idempotent, and a closed registry starts
        /// nothing further.
        /// </summary>
        /// <remarks>
        /// INVARIANT: every caller observes the SAME completion. Repeated and overlapping closes alike await one
        /// task, so no caller can be told teardown is done while another close is still awaiting a renewal — which
        /// is the whole point of awaiting at all, since <see cref="AzureSdkMessageReceiverAdapter"/> closes the SDK
        /// receiver the moment its close of this registry returns.
        /// </remarks>
        internal Task CloseAsync()
        {
            List<RenewalLifetime> renewalsToEnd;
            Task closeCompletion;

            lock (_syncLock)
            {
                if (_closeCompletion != null)
                {
                    return _closeCompletion;
                }

                renewalsToEnd = StopEveryRenewal();

                // Published as ONE expression: what a caller awaits IS the flow that ends these renewals, so there
                // is no moment at which a close is visible and nothing is going to complete it.
                closeCompletion = _closeCompletion = AwaitEveryRenewalAsync(renewalsToEnd);
            }

            // AFTER publication and OUTSIDE the lock, so no renewal is ended while _syncLock is held, and every
            // renewal this close owns is already ending by the time its caller holds a task to await. Ending a
            // renewal never raises, so no renewal in this snapshot is skipped because an earlier one failed.
            foreach (var renewal in renewalsToEnd)
            {
                renewal.End();
            }

            return closeCompletion;
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
            RenewalLifetime renewal;
            lock (_syncLock)
            {
                if (message == null || !_renewalsByDeliveredMessage.TryGetValue(message, out renewal))
                {
                    return;
                }

                // The entry is RETAINED, because a stopped renewal may still be awaiting the broker inside the renew
                // call and membership means it has not ENDED. Stopped is what tells a teardown it must still await
                // this renewal while telling ActiveRenewalCount the delivery is no longer in flight.
                renewal.Stopped = true;
            }

            renewal.End();
        }

        // Marks every held renewal stopped in the SAME lock that snapshots them, because the adapter closes the SDK
        // receiver as soon as this close returns: no delivery is in flight past that point, however long its renewal
        // takes to end.
        private List<RenewalLifetime> StopEveryRenewal()
        {
            var renewalsToEnd = new List<RenewalLifetime>(_renewalsByDeliveredMessage.Values);
            foreach (var renewal in renewalsToEnd)
            {
                renewal.Stopped = true;
            }

            return renewalsToEnd;
        }

        // A renewal reports its own failure as it ends, so a close waits only for each one to have ended.
        private static async Task AwaitEveryRenewalAsync(List<RenewalLifetime> renewalsToEnd)
        {
            foreach (var renewal in renewalsToEnd)
            {
                await renewal.Completion.ConfigureAwait(false);
            }
        }

        private void EndTracking(ServiceBusReceivedMessage message)
        {
            lock (_syncLock)
            {
                _renewalsByDeliveredMessage.Remove(message);
            }
        }

        private string DescribeDelivery(ServiceBusReceivedMessage message)
            => $"message '{message.MessageId}' on '{_receiverPath}'";
    }
}
