using Chatter.MessageBrokers.Context;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery.Retry
{
    class ExponentialDelayRetry : IRetryDelayStrategy
    {
        private const int _milliSecondsInASecond = 1000;
        private readonly int _maxDelayInMilliseconds = 1024;

        public ExponentialDelayRetry(int maxRetryAttempts)
            => _maxDelayInMilliseconds = GetDelayTimeInMillisecondsFromRetryAttempts(maxRetryAttempts);

        // INVARIANT: the whole computation stays in double and is clamped to Task.Delay's accepted
        // [0, int.MaxValue] domain before the integral cast, so no attempt count can wrap or hit an
        // implementation-defined float-to-int conversion. Truncation stays ahead of the multiply to
        // keep the per-attempt schedule byte-identical.
        int GetDelayTimeInMillisecondsFromRetryAttempts(int retryAttempts)
            => (int)Math.Clamp(Math.Truncate(1d / 2d * (Math.Pow(2d, retryAttempts) - 1d)) * _milliSecondsInASecond, 0d, int.MaxValue);

        /// <summary>
        /// Calculates the exponential delay that will occur between operations based on the number of previous attempts
        /// </summary>
        /// <returns>The time in seconds to delay. Truncated to the nearest second.</returns>
        /// <remarks>
        /// Exponential delay per attempt:
        ///<br>Attempt #1  - 0s</br>
        ///<br>Attempt #2  - 1s</br>
        ///<br>Attempt #3  - 3s</br>
        ///<br>Attempt #4  - 7s</br>
        ///<br>Attempt #5  - 15s</br>
        ///<br>Attempt #6  - 31s</br>
        ///<br>Attempt #7  - 1m 3s</br>
        ///<br>Attempt #8  - 2m 7s</br>
        ///<br>Attempt #9  - 4m 15s</br>
        ///<br>Attempt #10 - 8m 31s</br>
        ///<br>Attempt #11 - 17m 3s</br>
        ///<br>Attempt #12 - 34m 7s</br>
        ///<br>Attempt #13 - 1h 8m 15s</br>
        ///<br>Attempt #14 - 2h 16m 31s</br>
        ///<br>Attempt #15 - 4h 33m 3s</br>
        /// </remarks>
        public Task ExecuteAsync(FailureContext failureContext, CancellationToken cancellationToken)
        {
            _ = failureContext ?? throw new ArgumentNullException(nameof(failureContext));
            return ExecuteAsync(failureContext.DeliveryCount, cancellationToken);
        }

        public Task ExecuteAsync(FailureContext failureContext) => ExecuteAsync(failureContext, CancellationToken.None);

        public Task ExecuteAsync(int deliveryCount, CancellationToken cancellationToken)
        {
            var delayInMilliseconds = GetDelayTimeInMillisecondsFromRetryAttempts(deliveryCount);

            return Task.Delay(_maxDelayInMilliseconds < delayInMilliseconds
                ? _maxDelayInMilliseconds
                : delayInMilliseconds, cancellationToken);
        }

        public Task ExecuteAsync(int deliveryCount) => ExecuteAsync(deliveryCount, CancellationToken.None);
    }
}
