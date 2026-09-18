using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.AzureServiceBus.Receiving
{
    /// <summary>
    /// Session-mode <see cref="IServiceBusMessageReceiver"/> adapter. It holds ONE
    /// <see cref="ServiceBusSessionReceiver"/> at a time (accepted via
    /// <see cref="ServiceBusClient.AcceptNextSessionAsync(string, ServiceBusSessionReceiverOptions, CancellationToken)"/>),
    /// serves that session's messages FIFO through the same single-message
    /// <see cref="IServiceBusMessageReceiver.ReceiveAsync(CancellationToken)"/> contract the non-session
    /// <see cref="AzureSdkMessageReceiverAdapter"/> satisfies, settles on the held session receiver, and
    /// rolls to the next session on drain, idle, or lock loss by releasing the session and returning null
    /// so the pump re-polls.
    /// </summary>
    /// <remarks>
    /// INVARIANT: a held session owns exactly ONE <see cref="RenewalLifetime"/>, RECORDED in the same lock
    /// acquisition that records the session it renews and BEFORE that renewal begins, and released under that same
    /// lock. A release therefore cannot reach a held session whose renewal it cannot see, and on every release path
    /// (drain, idle, lock loss, teardown) it ends that renewal and AWAITS it BEFORE closing the held session
    /// receiver — so no renewal call races a closing receiver, and no way a renewal ends can skip that close.
    /// </remarks>
    internal class AzureSdkSessionMessageReceiverAdapter : IServiceBusSessionMessageReceiver, IServiceBusSessionChildReceiver
    {
        private static readonly Func<TimeSpan, CancellationToken, Task> _systemDelay = (delay, cancellationToken) => Task.Delay(delay, cancellationToken);

        readonly object _syncLock = new object();
        private readonly ServiceBusSessionEntityPath _entityPath;
        private readonly ServiceBusReceiveMode _receiveMode;
        private readonly TimeSpan _sessionIdleTimeout;
        private readonly TimeSpan _maxSessionLockRenewalDuration;
        private readonly ILogger _logger;
        private readonly Func<CancellationToken, Task<IServiceBusHeldSession>> _acceptNextSessionAsync;
        private readonly TimeProvider _timeProvider;
        private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

        private IServiceBusHeldSession _heldSession;
        private RenewalLifetime _renewal;
        private bool _closed;

        /// <summary>
        /// Builds an adapter that accepts its sessions from the shared <paramref name="client"/> on the system
        /// clock, which is what a receive path uses.
        /// </summary>
        public AzureSdkSessionMessageReceiverAdapter(ServiceBusClient client,
                                                     ServiceBusSessionEntityPath entityPath,
                                                     ServiceBusReceiveMode receiveMode,
                                                     int prefetchCount,
                                                     TimeSpan sessionIdleTimeout,
                                                     TimeSpan maxSessionLockRenewalDuration,
                                                     ILogger logger)
            : this(entityPath,
                   receiveMode,
                   sessionIdleTimeout,
                   maxSessionLockRenewalDuration,
                   logger,
                   CreateSdkSessionAcceptor(client, entityPath, receiveMode, prefetchCount),
                   TimeProvider.System,
                   _systemDelay)
        {
        }

        /// <summary>
        /// Builds an adapter whose session acceptance, clock and delay are supplied, the seam a test drives the
        /// acquire and release paths through without a live Azure Service Bus namespace or a wall-clock wait.
        /// </summary>
        internal AzureSdkSessionMessageReceiverAdapter(ServiceBusSessionEntityPath entityPath,
                                                      ServiceBusReceiveMode receiveMode,
                                                      TimeSpan sessionIdleTimeout,
                                                      TimeSpan maxSessionLockRenewalDuration,
                                                      ILogger logger,
                                                      Func<CancellationToken, Task<IServiceBusHeldSession>> acceptNextSessionAsync,
                                                      TimeProvider timeProvider,
                                                      Func<TimeSpan, CancellationToken, Task> delayAsync)
        {
            _entityPath = entityPath;
            _receiveMode = receiveMode;
            _sessionIdleTimeout = sessionIdleTimeout;
            _maxSessionLockRenewalDuration = maxSessionLockRenewalDuration;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _acceptNextSessionAsync = acceptNextSessionAsync ?? throw new ArgumentNullException(nameof(acceptNextSessionAsync));
            _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
            _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
        }

        /// <summary>
        /// The currently held SDK session receiver, or null when no session is held. The
        /// <see cref="ServiceBusReceiver"/> includes this in the transaction <c>Container</c> and the public
        /// session-state extension resolves it for Get/Set/Clear.
        /// </summary>
        /// <remarks>
        /// INVARIANT: it answers the CONCRETE <see cref="ServiceBusSessionReceiver"/>, never
        /// <see cref="IServiceBusHeldSession"/>. The container keys by the STATIC type of what it is handed, and
        /// the session-state extension looks the answer up by that concrete type, so widening this answer would
        /// leave every session-state call resolving nothing.
        /// </remarks>
        public ServiceBusSessionReceiver HeldSessionReceiver => HeldSession?.SdkSessionReceiver;

        public string HeldSessionId => HeldSession?.SessionId;

        public DateTimeOffset? HeldSessionLockedUntil => HeldSession?.SessionLockedUntil;

        // The held session itself, which is what everything inside this adapter works through.
        private IServiceBusHeldSession HeldSession
        {
            get
            {
                lock (_syncLock)
                {
                    return _heldSession;
                }
            }
        }

        /// <summary>
        /// Answers the held session receiver, ignoring <paramref name="message"/>: this adapter holds exactly one
        /// session, so every message it delivered came from that session.
        /// </summary>
        public ServiceBusSessionReceiver SessionReceiverFor(ServiceBusReceivedMessage message) => HeldSessionReceiver;

        /// <summary>
        /// No-op: holding exactly one session there is no session slot to free when the worker finishes with a
        /// delivery. The session rolls on drain, idle, or lock loss instead.
        /// </summary>
        public void DeliveryReleased(ServiceBusReceivedMessage message)
        {
        }

        public bool IsClosedOrClosing
        {
            get
            {
                lock (_syncLock)
                {
                    return _closed || (_heldSession != null && _heldSession.IsClosed);
                }
            }
        }

        public async Task<ServiceBusReceivedMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            IServiceBusHeldSession session;
            try
            {
                session = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ServiceBusException sbe) when (sbe.Reason == ServiceBusFailureReason.ServiceTimeout
                                                  || sbe.Reason == ServiceBusFailureReason.SessionCannotBeLocked)
            {
                // No session available right now. Non-fatal: return null so the pump re-polls and the
                // adapter attempts to accept the next session on the following turn.
                _logger.LogTrace($"No Azure Service Bus session available for '{_entityPath}'; re-polling");
                return null;
            }

            if (session == null)
            {
                return null;
            }

            try
            {
                // Idle rollover: a held session that yields no message within SessionIdleTimeout is released
                // and the adapter rolls to the next session (return null, re-poll).
                var message = await session.ReceiveMessageAsync(_sessionIdleTimeout, cancellationToken).ConfigureAwait(false);

                if (message == null)
                {
                    // Drain or idle: the held session yielded nothing this turn. Release it and roll.
                    await ReleaseSessionAsync().ConfigureAwait(false);
                    return null;
                }

                return message;
            }
            catch (ServiceBusException sbe) when (sbe.Reason == ServiceBusFailureReason.SessionLockLost)
            {
                // Losing a session lock is an expected operational event, NOT a receiver-stopping fault.
                // Release the session and return null so the pump re-polls — deliberately NOT raised as
                // CriticalReceiverException (unlike the cross-entity-transaction rejection).
                _logger.LogWarning(sbe, $"Azure Service Bus session lock lost for '{_entityPath}'; releasing session and re-polling");
                await ReleaseSessionAsync().ConfigureAwait(false);
                return null;
            }
        }

        public async Task<ServiceBusSettlementOutcome> CompleteAsync(ServiceBusReceivedMessage message)
        {
            if (!TrySettlingSession(out var session, out var shortCircuit))
            {
                return shortCircuit;
            }

            await session.CompleteMessageAsync(message).ConfigureAwait(false);
            return ServiceBusSettlementOutcome.Settled;
        }

        public async Task<ServiceBusSettlementOutcome> AbandonAsync(ServiceBusReceivedMessage message, IDictionary<string, object> propertiesToModify)
        {
            if (!TrySettlingSession(out var session, out var shortCircuit))
            {
                return shortCircuit;
            }

            await session.AbandonMessageAsync(message, propertiesToModify).ConfigureAwait(false);
            return ServiceBusSettlementOutcome.Settled;
        }

        public async Task<ServiceBusSettlementOutcome> DeadLetterAsync(ServiceBusReceivedMessage message, string deadLetterReason, string deadLetterErrorDescription)
        {
            if (!TrySettlingSession(out var session, out var shortCircuit))
            {
                return shortCircuit;
            }

            await session.DeadLetterMessageAsync(message, deadLetterReason, deadLetterErrorDescription).ConfigureAwait(false);
            return ServiceBusSettlementOutcome.Settled;
        }

        // Answers the session a settlement must run on, or the outcome to report instead of running one.
        // INVARIANT: a released session yields DeliveryUnreachable, NEVER a silent success — the broker still
        // holds the delivery, so reporting it settled would have the delivery processed twice (and, for
        // deadletter, leave a poison message circulating while the pipeline believes it was contained).
        private bool TrySettlingSession(out IServiceBusHeldSession session, out ServiceBusSettlementOutcome shortCircuit)
        {
            if (_receiveMode != ServiceBusReceiveMode.PeekLock)
            {
                session = null;
                shortCircuit = ServiceBusSettlementOutcome.NotOwed;
                return false;
            }

            session = HeldSession;
            if (session == null)
            {
                shortCircuit = ServiceBusSettlementOutcome.DeliveryUnreachable;
                return false;
            }

            shortCircuit = ServiceBusSettlementOutcome.Settled;
            return true;
        }

        public async Task CloseAsync()
        {
            lock (_syncLock)
            {
                _closed = true;
            }

            await ReleaseSessionAsync().ConfigureAwait(false);
        }

        // Returns the held session if one is already held, otherwise accepts the next available session
        // and starts its bounded lock-renewal loop. Lets SessionCannotBeLocked / ServiceTimeout propagate
        // to the caller's non-fatal guard.
        private async Task<IServiceBusHeldSession> AcquireSessionAsync(CancellationToken cancellationToken)
        {
            var existing = HeldSession;
            if (existing != null)
            {
                return existing;
            }

            var accepted = await _acceptNextSessionAsync(cancellationToken).ConfigureAwait(false);

            // Built OUTSIDE the lock, because recording and beginning the renewal are the only things that belong
            // inside it. A non-positive ceiling admits no renewal window at all, so no loop is built; the held
            // session's lock is allowed to expire naturally, exactly as a loop that stopped at its ceiling would
            // leave it.
            LockRenewalLoop renewalLoop = null;
            string renewalDescription = null;
            if (LockRenewalLoop.IsEnabled(_maxSessionLockRenewalDuration))
            {
                renewalDescription = $"session '{accepted.SessionId}' on '{_entityPath}'";
                renewalLoop = new LockRenewalLoop(() => accepted.SessionLockedUntil,
                                                  renewalToken => accepted.RenewSessionLockAsync(renewalToken),
                                                  _maxSessionLockRenewalDuration,
                                                  ServiceBusFailureReason.SessionLockLost,
                                                  renewalDescription,
                                                  _logger,
                                                  _timeProvider,
                                                  _delayAsync);
            }

            // Its OWN answer, and deliberately not "this session has no renewal": renewal is OFF for a non-positive
            // ceiling, so reading an absent renewal as a race would close every session a non-renewing receiver
            // accepts.
            bool racedTheClose;
            lock (_syncLock)
            {
                racedTheClose = _closed;
                if (!racedTheClose)
                {
                    _heldSession = accepted;
                    if (renewalLoop != null)
                    {
                        // INVARIANT: the renewal is RECORDED BEFORE it begins, in the SAME lock acquisition that
                        // records the session it renews. A loop that ends without ever awaiting ends inside Begin,
                        // and a release reaching this adapter between the two would hold a session whose renewal it
                        // cannot see — which is what leaves a renewal running past the receiver it renews against.
                        // The field IS the membership, written by exactly two events, so an ended renewal has
                        // nothing to unrecord. Nothing is ever awaited while _syncLock is held.
                        var renewal = new RenewalLifetime(renewalDescription, _logger);
                        _renewal = renewal;
                        renewal.Begin(renewalLoop.RunAsync, () => { });
                    }
                }
            }

            if (racedTheClose)
            {
                // Raced with CloseAsync — do not hold the just-accepted session.
                await accepted.CloseAsync().ConfigureAwait(false);
                return null;
            }

            return accepted;
        }

        // Ends the held session's renewal and AWAITS it BEFORE closing the held session receiver, so no renewal call
        // races a closing/closed session receiver. Idempotent across repeated release paths.
        // INVARIANT: everything BEFORE the close is TOTAL. Ending a renewal never throws and its completion never
        // faults — the RenewalLifetime discharges and reports everything it owns — so there is no way for a renewal
        // to end that could skip the close the session's lock and its AMQP link depend on. The close ITSELF carries
        // no such guarantee and is deliberately unguarded: it is the LAST statement, so a fault there jumps over
        // nothing. A close that fails forgets its session rather than re-serving a dead one — an inherited residual
        // recorded on the record in ADR-0021 ("a close that fails is not retried"), not a claim this path makes.
        private async Task ReleaseSessionAsync()
        {
            IServiceBusHeldSession toClose;
            RenewalLifetime renewal;
            lock (_syncLock)
            {
                toClose = _heldSession;
                renewal = _renewal;
                _heldSession = null;
                _renewal = null;
            }

            if (renewal != null)
            {
                renewal.End();
                await renewal.Completion.ConfigureAwait(false);
            }

            if (toClose != null)
            {
                await toClose.CloseAsync().ConfigureAwait(false);
            }
        }

        // The production session acceptance: accept the next available session from the shared client and hand it
        // back behind the held-session port. Bound ONCE at construction, because the adapter never accepts a
        // session any other way.
        private static Func<CancellationToken, Task<IServiceBusHeldSession>> CreateSdkSessionAcceptor(ServiceBusClient client,
                                                                                                      ServiceBusSessionEntityPath entityPath,
                                                                                                      ServiceBusReceiveMode receiveMode,
                                                                                                      int prefetchCount)
        {
            if (client == null)
            {
                throw new ArgumentNullException(nameof(client));
            }

            return cancellationToken => AcceptNextSdkSessionAsync(client, entityPath, receiveMode, prefetchCount, cancellationToken);
        }

        private static async Task<IServiceBusHeldSession> AcceptNextSdkSessionAsync(ServiceBusClient client,
                                                                                    ServiceBusSessionEntityPath entityPath,
                                                                                    ServiceBusReceiveMode receiveMode,
                                                                                    int prefetchCount,
                                                                                    CancellationToken cancellationToken)
        {
            var sessionOptions = new ServiceBusSessionReceiverOptions
            {
                ReceiveMode = receiveMode,
                PrefetchCount = prefetchCount,
            };

            // INVARIANT: address the entity through the structured identity so the correct AcceptNextSessionAsync
            // overload is chosen — the (topic, subscription) overload for a subscription, the (queue) overload for a
            // queue — rather than feeding a composite "<topic>/Subscriptions/<sub>" string to the queue-only overload.
            var accepted = entityPath.IsSubscription
                ? await client.AcceptNextSessionAsync(entityPath.TopicName, entityPath.SubscriptionName, sessionOptions, cancellationToken).ConfigureAwait(false)
                : await client.AcceptNextSessionAsync(entityPath.QueueName, sessionOptions, cancellationToken).ConfigureAwait(false);

            return new AzureSdkHeldSession(accepted);
        }
    }
}
