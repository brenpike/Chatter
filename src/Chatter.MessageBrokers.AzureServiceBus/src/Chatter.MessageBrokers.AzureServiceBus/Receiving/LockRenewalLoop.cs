using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.AzureServiceBus.Receiving
{
    /// <summary>
    /// The bounded lock-renewal policy shared by the receive paths: renew a held lock at the halfway point between
    /// now and its expiry — floored at one second so a near-expired or already-expired lock renews promptly rather
    /// than spinning or waiting a negative span — until a ceiling of <c>now + maxRenewalDuration</c>, computed ONCE
    /// at loop start. Past the ceiling renewal stops and the lock is allowed to expire naturally rather than being
    /// held forever.
    /// </summary>
    /// <remarks>
    /// It takes NO Azure SDK receiver: the held lock's expiry is read through a delegate (it advances after each
    /// successful renewal, so it is re-read every iteration) and the renewal itself is a delegate. That is what makes
    /// the policy testable on an injected clock with no live namespace.
    /// INVARIANT: <see cref="RunAsync(CancellationToken)"/> completes WITHOUT throwing for the three expected
    /// renewal outcomes — cancellation of its own token, a <see cref="ServiceBusException"/> carrying the caller's
    /// lock-lost reason, and a concurrently disposed receiver. Any OTHER failure faults the returned task rather than
    /// being swallowed, because a loop that quietly stopped renewing on an unrecognised fault would look identical to
    /// one that ran to its ceiling.
    /// </remarks>
    internal sealed class LockRenewalLoop
    {
        private static readonly TimeSpan _renewalDelayFloor = TimeSpan.FromSeconds(1);
        private static readonly Func<TimeSpan, CancellationToken, Task> _systemDelay = (delay, cancellationToken) => Task.Delay(delay, cancellationToken);

        private readonly Func<DateTimeOffset> _lockedUntil;
        private readonly Func<CancellationToken, Task> _renewAsync;
        private readonly TimeSpan _maxRenewalDuration;
        private readonly ServiceBusFailureReason _lockLostReason;
        private readonly string _description;
        private readonly ILogger _logger;
        private readonly TimeProvider _timeProvider;
        private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

        /// <summary>
        /// Builds a renewal loop on the system clock and <see cref="Task.Delay(TimeSpan, CancellationToken)"/>, which
        /// is what a receive path uses.
        /// </summary>
        internal LockRenewalLoop(Func<DateTimeOffset> lockedUntil,
                                 Func<CancellationToken, Task> renewAsync,
                                 TimeSpan maxRenewalDuration,
                                 ServiceBusFailureReason lockLostReason,
                                 string description,
                                 ILogger logger)
            : this(lockedUntil, renewAsync, maxRenewalDuration, lockLostReason, description, logger, TimeProvider.System, _systemDelay)
        {
        }

        /// <summary>
        /// Builds a renewal loop on a supplied <paramref name="timeProvider"/> and <paramref name="delayAsync"/>, the
        /// seam a test drives the cadence and the ceiling through without a wall-clock wait.
        /// </summary>
        internal LockRenewalLoop(Func<DateTimeOffset> lockedUntil,
                                 Func<CancellationToken, Task> renewAsync,
                                 TimeSpan maxRenewalDuration,
                                 ServiceBusFailureReason lockLostReason,
                                 string description,
                                 ILogger logger,
                                 TimeProvider timeProvider,
                                 Func<TimeSpan, CancellationToken, Task> delayAsync)
        {
            _lockedUntil = lockedUntil ?? throw new ArgumentNullException(nameof(lockedUntil));
            _renewAsync = renewAsync ?? throw new ArgumentNullException(nameof(renewAsync));
            _maxRenewalDuration = maxRenewalDuration;
            _lockLostReason = lockLostReason;
            _description = description;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
            _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
        }

        /// <summary>
        /// Answers whether a <paramref name="maxRenewalDuration"/> admits renewal at all. This is the SINGLE
        /// definition of renewal being off: a receive path that reads it agrees with every other path by construction.
        /// </summary>
        internal static bool IsEnabled(TimeSpan maxRenewalDuration) => maxRenewalDuration > TimeSpan.Zero;

        internal async Task RunAsync(CancellationToken renewalToken)
        {
            var ceiling = _timeProvider.GetUtcNow() + _maxRenewalDuration;

            try
            {
                while (!renewalToken.IsCancellationRequested && _timeProvider.GetUtcNow() < ceiling)
                {
                    var delay = ComputeRenewalDelay(_lockedUntil(), _timeProvider.GetUtcNow());

                    await _delayAsync(delay, renewalToken).ConfigureAwait(false);

                    if (renewalToken.IsCancellationRequested || _timeProvider.GetUtcNow() >= ceiling)
                    {
                        break;
                    }

                    await _renewAsync(renewalToken).ConfigureAwait(false);
                    _logger.LogTrace($"Renewed Azure Service Bus lock for {_description}");
                }
            }
            catch (OperationCanceledException) when (renewalToken.IsCancellationRequested)
            {
                // Expected: the lock is being released/closed. The release path cancels this token before closing
                // the receiver, so a cancelled renewal is normal teardown, not a fault.
            }
            catch (ServiceBusException sbe) when (sbe.Reason == _lockLostReason)
            {
                // The lock was lost out from under the renewal loop. The receive path observes the same loss on its
                // next receive and releases there; the loop simply stops renewing.
                _logger.LogTrace(sbe, $"Azure Service Bus lock lost during renewal for {_description}; stopping renewal");
            }
            catch (ObjectDisposedException ode)
            {
                // The receiver was closed concurrently with renewal. Stop renewing; the release path owns teardown.
                _logger.LogTrace(ode, $"Azure Service Bus receiver disposed during renewal for {_description}; stopping renewal");
            }
        }

        private static TimeSpan ComputeRenewalDelay(DateTimeOffset lockedUntil, DateTimeOffset now)
        {
            var remaining = lockedUntil - now;
            var half = TimeSpan.FromTicks(remaining.Ticks / 2);
            return half < _renewalDelayFloor ? _renewalDelayFloor : half;
        }
    }
}
