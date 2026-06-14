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
    internal class AzureSdkSessionMessageReceiverAdapter : IServiceBusMessageReceiver
    {
        readonly object _syncLock = new object();
        private readonly ServiceBusClient _client;
        private readonly string _messageReceiverPath;
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
                                                     string messageReceiverPath,
                                                     ServiceBusReceiveMode receiveMode,
                                                     int prefetchCount,
                                                     TimeSpan sessionIdleTimeout,
                                                     TimeSpan maxSessionLockRenewalDuration,
                                                     ILogger logger)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _messageReceiverPath = messageReceiverPath;
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
        internal ServiceBusSessionReceiver HeldSessionReceiver
        {
            get
            {
                lock (_syncLock)
                {
                    return _sessionReceiver;
                }
            }
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
                _logger.LogTrace($"No Azure Service Bus session available for '{_messageReceiverPath}'; re-polling");
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
                _logger.LogWarning(sbe, $"Azure Service Bus session lock lost for '{_messageReceiverPath}'; releasing session and re-polling");
                await ReleaseSessionAsync().ConfigureAwait(false);
                return null;
            }
        }

        public Task CompleteAsync(ServiceBusReceivedMessage message)
        {
            if (_receiveMode != ServiceBusReceiveMode.PeekLock)
            {
                return Task.CompletedTask;
            }

            var session = HeldSessionReceiver;
            if (session == null)
            {
                return Task.CompletedTask;
            }

            return session.CompleteMessageAsync(message);
        }

        public Task AbandonAsync(ServiceBusReceivedMessage message, IDictionary<string, object> propertiesToModify)
        {
            if (_receiveMode != ServiceBusReceiveMode.PeekLock)
            {
                return Task.CompletedTask;
            }

            var session = HeldSessionReceiver;
            if (session == null)
            {
                return Task.CompletedTask;
            }

            return session.AbandonMessageAsync(message, propertiesToModify);
        }

        public Task DeadLetterAsync(ServiceBusReceivedMessage message, string deadLetterReason, string deadLetterErrorDescription)
        {
            if (_receiveMode != ServiceBusReceiveMode.PeekLock)
            {
                return Task.CompletedTask;
            }

            var session = HeldSessionReceiver;
            if (session == null)
            {
                return Task.CompletedTask;
            }

            return session.DeadLetterMessageAsync(message, deadLetterReason, deadLetterErrorDescription);
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

            var accepted = await _client.AcceptNextSessionAsync(_messageReceiverPath, new ServiceBusSessionReceiverOptions
            {
                ReceiveMode = _receiveMode,
                PrefetchCount = _prefetchCount,
            }, cancellationToken).ConfigureAwait(false);

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

            _renewalTask = RenewSessionLockLoopAsync(accepted, renewalCts.Token);
            return accepted;
        }

        // Bounded session-lock renewal loop: renews the held session's lock on a cadence derived from the
        // session lock duration, bounded by MaxSessionLockRenewalDuration. Once the ceiling is reached,
        // renewal stops and the session is allowed to expire/roll naturally rather than being held forever.
        private async Task RenewSessionLockLoopAsync(ServiceBusSessionReceiver session, CancellationToken renewalToken)
        {
            var ceiling = DateTimeOffset.UtcNow + _maxSessionLockRenewalDuration;

            try
            {
                while (!renewalToken.IsCancellationRequested && DateTimeOffset.UtcNow < ceiling)
                {
                    var lockedUntil = session.SessionLockedUntil;
                    var delay = ComputeRenewalDelay(lockedUntil);

                    await Task.Delay(delay, renewalToken).ConfigureAwait(false);

                    if (renewalToken.IsCancellationRequested || DateTimeOffset.UtcNow >= ceiling)
                    {
                        break;
                    }

                    await session.RenewSessionLockAsync(renewalToken).ConfigureAwait(false);
                    _logger.LogTrace($"Renewed Azure Service Bus session lock for session '{session.SessionId}' on '{_messageReceiverPath}'");
                }
            }
            catch (OperationCanceledException) when (renewalToken.IsCancellationRequested)
            {
                // Expected: the session is being released/closed. The release path cancels this CTS before
                // closing the receiver, so a cancelled renewal is normal teardown, not a fault.
            }
            catch (ServiceBusException sbe) when (sbe.Reason == ServiceBusFailureReason.SessionLockLost)
            {
                // The lock was lost out from under the renewal loop. ReceiveAsync observes the same loss on
                // its next receive and releases the session there; the loop simply stops renewing.
                _logger.LogTrace(sbe, $"Azure Service Bus session lock lost during renewal for '{_messageReceiverPath}'; stopping renewal");
            }
            catch (ObjectDisposedException)
            {
                // The session receiver was closed concurrently with renewal. Stop renewing; the release path
                // owns teardown.
            }
        }

        // Renews at the halfway point between now and the lock expiry, clamped to a small positive floor so a
        // near-expired or already-expired lock renews promptly rather than spinning or waiting a negative span.
        private static TimeSpan ComputeRenewalDelay(DateTimeOffset lockedUntil)
        {
            var remaining = lockedUntil - DateTimeOffset.UtcNow;
            var half = TimeSpan.FromTicks(remaining.Ticks / 2);
            var floor = TimeSpan.FromSeconds(1);
            return half < floor ? floor : half;
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
