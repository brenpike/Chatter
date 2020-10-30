using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Recovery.Options;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Recovery
{
    class ExponentialDelayRecovery : IDelayedRecovery
    {
        private const int _milliSecondsInASecond = 1000;
        private readonly int _maxDelayInMilliseconds = 1024;

        public ExponentialDelayRecovery(RecoveryOptions options) 
            => _maxDelayInMilliseconds = GetDelayTimeInMillisecondsFromRetryAttempts(options.MaxRetryAttempts);

        int GetDelayTimeInMillisecondsFromRetryAttempts(int retryAttempts) 
            => (int)(1d / 2d * (Math.Pow(2d, retryAttempts) - 1d)) * _milliSecondsInASecond;

        /// <summary>
        /// Calculates the exponential delay that will occur between operations based on the number of previous attempts
        /// </summary>
        /// <returns>The time in seconds to delay. Truncated to the nearest second.</returns>
        /// <remarks>
        ///Attempt 1     0s     0s
        ///Attempt 2     2s     2s
        ///Attempt 3     4s     4s
        ///Attempt 4     8s     8s
        ///Attempt 5     16s    16s
        ///Attempt 6     32s    32s
        ///Attempt 7     64s     1m 4s
        ///Attempt 8     128s    2m 8s
        ///Attempt 9     256s    4m 16s
        ///Attempt 10    512     8m 32s
        ///Attempt 11    1024    17m 4s
        ///Attempt 12    2048    34m 8s
        ///Attempt 13    4096    1h 8m 16s
        ///Attempt 14    8192    2h 16m 32s
        ///Attempt 15    16384   4h 33m 4s
        /// </remarks>
        public Task Delay(FailureContext failureContext)
        {
            var delayInMilliseconds = GetDelayTimeInMillisecondsFromRetryAttempts(failureContext.DeliveryCount);

            return Task.Delay(_maxDelayInMilliseconds < delayInMilliseconds
                ? _maxDelayInMilliseconds
                : delayInMilliseconds);
        }
    }
}
