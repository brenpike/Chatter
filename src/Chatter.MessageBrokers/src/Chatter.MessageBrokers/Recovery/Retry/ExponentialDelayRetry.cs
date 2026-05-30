using Chatter.MessageBrokers.Context;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery.Retry
{
    class ExponentialDelayRetry : IRetryDelayStrategy
    {
        private const int _milliSecondsInASecond = 1000;
        private readonly int _maxDelayInMilliseconds = 1024;

        public ExponentialDelayRetry(int maxRetryAttempts)
            => _maxDelayInMilliseconds = GetDelayTimeInMillisecondsFromRetryAttempts(maxRetryAttempts);

        int GetDelayTimeInMillisecondsFromRetryAttempts(int retryAttempts)
            => (int)(1d / 2d * (Math.Pow(2d, retryAttempts) - 1d)) * _milliSecondsInASecond;

        /// <summary>
        /// Calculates the exponential delay that will occur between operations based on the number of previous attempts
        /// </summary>
        /// <returns>The time in seconds to delay. Truncated to the nearest second.</returns>
        /// <remarks>
        /// Exponential delay per attempt:
        ///<br>Attempt #1  - 0s</br>
        ///<br>Attempt #2  - 2s</br>
        ///<br>Attempt #3  - 4s</br>
        ///<br>Attempt #4  - 8s</br>
        ///<br>Attempt #5  - 16s</br>
        ///<br>Attempt #6  - 32s</br>
        ///<br>Attempt #7  - 1m 4s</br>
        ///<br>Attempt #8  - 2m 8s</br>
        ///<br>Attempt #9  - 4m 16s</br>
        ///<br>Attempt #10 - 8m 32s</br>
        ///<br>Attempt #11 - 17m 4s</br>
        ///<br>Attempt #12 - 34m 8s</br>
        ///<br>Attempt #13 - 1h 8m 16s</br>
        ///<br>Attempt #14 - 2h 16m 32s</br>
        ///<br>Attempt #15 - 4h 33m 4s</br>
        /// </remarks>
        public Task ExecuteAsync(FailureContext failureContext)
        {
            _ = failureContext ?? throw new ArgumentNullException(nameof(failureContext));
            return ExecuteAsync(failureContext.DeliveryCount);
        }

        public Task ExecuteAsync(int deliveryCount)
        {
            var delayInMilliseconds = GetDelayTimeInMillisecondsFromRetryAttempts(deliveryCount);

            return Task.Delay(_maxDelayInMilliseconds < delayInMilliseconds
                ? _maxDelayInMilliseconds
                : delayInMilliseconds);
        }
    }
}
