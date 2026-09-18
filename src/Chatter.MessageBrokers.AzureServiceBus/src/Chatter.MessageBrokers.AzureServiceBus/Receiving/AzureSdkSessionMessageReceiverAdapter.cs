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
    /// INVARIANT: a held session owns exactly ONE renewal <see cref="CancellationTokenSource"/> and ONE
    /// renewal <see cref="Task"/>; the renewal CTS is cancelled BEFORE the held session receiver is closed
    /// on every release path (drain, idle, lock loss, teardown) so no renewal call races a closing receiver.
    /// </remarks>
    internal class AzureSdkSessionMessageReceiverAdapter : IServiceBusSessionMessageReceiver, IServiceBusSessionChildReceiver
    {
        readonly object _syncLock = new object();
        private readonly ServiceBusClient _client;
        private readonly ServiceBusSessionEntityPath _entityPath;
        private readonly ServiceBusReceiveMode _receiveMode;
        private readonly int _prefetchCount;
        private readonly TimeSpan _sessionIdleTimeout;
        private readonly TimeSpan _maxSessionLockRenewalDuration;
        private readonly ILogger _logger;

        private ServiceBusSessionReceiver _sessionReceiver;
        private CancellationTokenSource _renewalCts;
        private Task _renewalTask;
        private bool _closed;

        public AzureSdkSessionMessageReceiverAdapter(ServiceBusClient client,
                                                     ServiceBusSessionEntityPath entityPath,
                                                     ServiceBusReceiveMode receiveMode,
                                                     int prefetchCount,
                                                     TimeSpan sessionIdleTimeout,
                                                     TimeSpan maxSessionLockRenewalDuration,
                                                     ILogger logger)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _entityPath = entityPath;
            _receiveMode = receiveMode;
            _prefetchCount = prefetchCount;
            _sessionIdleTimeout = sessionIdleTimeout;
            _maxSessionLockRenewalDuration = maxSessionLockRenewalDuration;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// The currently held SDK session receiver, or null when no session is held. Later steps include
        /// this in the transaction <c>Container</c> and resolve it for session-state Get/Set/Clear.
        /// </summary>
        public ServiceBusSessionReceiver HeldSessionReceiver
        {
            get
            {
                lock (_syncLock)
                {
                    return _sessionReceiver;
                }
            }
        }

        public string HeldSessionId
        {
            get
            {
                lock (_syncLock)
                {
                    return _sessionReceiver?.SessionId;
                }
            }
        }

        public DateTimeOffset? HeldSessionLockedUntil
        {
            get
            {
                lock (_syncLock)
                {
                    return _sessionReceiver?.SessionLockedUntil;
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
                    return _closed || (_sessionReceiver != null && _sessionReceiver.IsClosed);
                }
            }
        }

        public async Task<ServiceBusReceivedMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            ServiceBusSessionReceiver session;
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
                var message = await session.ReceiveMessageAsync(maxWaitTime: _sessionIdleTimeout, cancellationToken).ConfigureAwait(false);

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
        private bool TrySettlingSession(out ServiceBusSessionReceiver session, out ServiceBusSettlementOutcome shortCircuit)
        {
            if (_receiveMode != ServiceBusReceiveMode.PeekLock)
            {
                session = null;
                shortCircuit = ServiceBusSettlementOutcome.NotOwed;
                return false;
            }

            session = HeldSessionReceiver;
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
        private async Task<ServiceBusSessionReceiver> AcquireSessionAsync(CancellationToken cancellationToken)
        {
            var existing = HeldSessionReceiver;
            if (existing != null)
            {
                return existing;
            }

            var sessionOptions = new ServiceBusSessionReceiverOptions
            {
                ReceiveMode = _receiveMode,
                PrefetchCount = _prefetchCount,
            };

            // INVARIANT: address the entity through the structured identity so the correct AcceptNextSessionAsync
            // overload is chosen — the (topic, subscription) overload for a subscription, the (queue) overload for a
            // queue — rather than feeding a composite "<topic>/Subscriptions/<sub>" string to the queue-only overload.
            var accepted = _entityPath.IsSubscription
                ? await _client.AcceptNextSessionAsync(_entityPath.TopicName, _entityPath.SubscriptionName, sessionOptions, cancellationToken).ConfigureAwait(false)
                : await _client.AcceptNextSessionAsync(_entityPath.QueueName, sessionOptions, cancellationToken).ConfigureAwait(false);

            CancellationTokenSource renewalCts;
            lock (_syncLock)
            {
                if (_closed)
                {
                    // Raced with CloseAsync — do not hold the just-accepted session.
                    renewalCts = null;
                }
                else
                {
                    _sessionReceiver = accepted;
                    _renewalCts = renewalCts = new CancellationTokenSource();
                }
            }

            if (renewalCts == null)
            {
                await accepted.CloseAsync().ConfigureAwait(false);
                return null;
            }

            // A non-positive ceiling admits no renewal window at all, so no loop is started; the held session's
            // lock is allowed to expire naturally, exactly as a loop that stopped at its ceiling would leave it.
            if (LockRenewalLoop.IsEnabled(_maxSessionLockRenewalDuration))
            {
                var renewalLoop = new LockRenewalLoop(() => accepted.SessionLockedUntil,
                                                      renewalToken => accepted.RenewSessionLockAsync(renewalToken),
                                                      _maxSessionLockRenewalDuration,
                                                      ServiceBusFailureReason.SessionLockLost,
                                                      $"session '{accepted.SessionId}' on '{_entityPath}'",
                                                      _logger);

                _renewalTask = renewalLoop.RunAsync(renewalCts.Token);
            }

            return accepted;
        }

        // Cancels the renewal CTS BEFORE closing the held session receiver, then awaits the renewal task so
        // no renewal call races a closing/closed session receiver. Idempotent across repeated release paths.
        private async Task ReleaseSessionAsync()
        {
            ServiceBusSessionReceiver toClose;
            CancellationTokenSource renewalCts;
            Task renewalTask;
            lock (_syncLock)
            {
                toClose = _sessionReceiver;
                renewalCts = _renewalCts;
                renewalTask = _renewalTask;
                _sessionReceiver = null;
                _renewalCts = null;
                _renewalTask = null;
            }

            if (renewalCts != null)
            {
                renewalCts.Cancel();
            }

            if (renewalTask != null)
            {
                try
                {
                    await renewalTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Renewal task observed its cancellation — expected on the release path.
                }
            }

            if (renewalCts != null)
            {
                renewalCts.Dispose();
            }

            if (toClose != null)
            {
                await toClose.CloseAsync().ConfigureAwait(false);
            }
        }
    }
}
