using System;

namespace Chatter.MessageBrokers.Receiving
{
    public static class BrokeredMessageReceiverRetry
    {
        public const int MillisecondsInASecond = 1000;
        /// <summary>
        /// Calculates the exponential delay that will occur between operations based on the number of previous attempts
        /// </summary>
        /// <param name="numberOfAttempts">The number of attempts that have been attempted</param>
        /// <param name="maxDelayInSeconds">The maximum seconds that an operation will be delayed</param>
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
        public static int ExponentialDelay(int numberOfAttempts,
                                           int maxDelayInSeconds = 1024)
        {
            var delayInSeconds = (int)((1d / 2d) * (Math.Pow(2d, numberOfAttempts) - 1d));

            return maxDelayInSeconds < delayInSeconds
                ? maxDelayInSeconds
                : delayInSeconds;
        }
    }
}
