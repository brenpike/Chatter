using Chatter.MessageBrokers.Recovery.Retry;
using FluentAssertions;
using System.Reflection;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Recovery.Retry.UsingExponentialDelayRetry
{
    public class WhenComputingDelay : Testing.Core.Context
    {
        // The escalation formula lives in a private method; reflect it to pin the exact computed
        // milliseconds per attempt without performing any wall-clock delay.
        private static int ComputedDelayFor(ExponentialDelayRetry sut, int attempts)
            => (int)typeof(ExponentialDelayRetry)
                .GetMethod("GetDelayTimeInMillisecondsFromRetryAttempts", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(sut, new object[] { attempts });

        private static int MaxDelayOf(ExponentialDelayRetry sut)
            => (int)typeof(ExponentialDelayRetry)
                .GetField("_maxDelayInMilliseconds", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(sut);

        private readonly ExponentialDelayRetry _sut = new ExponentialDelayRetry(5);

        [Fact]
        public void MustComputeZeroMillisecondsForAttemptZero()
            => ComputedDelayFor(_sut, 0).Should().Be(0);

        [Fact]
        public void MustComputeZeroMillisecondsForAttemptOneDueToTruncation()
            // (int)(0.5 * (2^1 - 1)) == (int)0.5 == 0, so attempt 1 yields no delay.
            => ComputedDelayFor(_sut, 1).Should().Be(0);

        [Fact]
        public void MustComputeOneSecondForAttemptTwo()
            // (int)(0.5 * (2^2 - 1)) == (int)1.5 == 1 -> 1000ms.
            => ComputedDelayFor(_sut, 2).Should().Be(1000);

        [Fact]
        public void MustComputeThreeSecondsForAttemptThree()
            => ComputedDelayFor(_sut, 3).Should().Be(3000);

        [Fact]
        public void MustComputeSevenSecondsForAttemptFour()
            => ComputedDelayFor(_sut, 4).Should().Be(7000);

        [Fact]
        public void MustComputeFifteenSecondsForAttemptFive()
            => ComputedDelayFor(_sut, 5).Should().Be(15000);

        [Fact]
        public void MustComputeFiveHundredElevenSecondsForAttemptTen()
            => ComputedDelayFor(_sut, 10).Should().Be(511000);

        [Fact]
        public void MustSetMaxDelayFromConstructorRetryAttempts()
            // The constructor overwrites the 1024 default with the computed delay for maxRetryAttempts (5 -> 15000ms).
            => MaxDelayOf(_sut).Should().Be(15000);

        [Fact]
        public void MustOverflowToNegativeForLargeAttempt()
        {
            // (int)(0.5 * (2^31 - 1)) * 1000 overflows Int32 and wraps negative; pinned as-is.
            ComputedDelayFor(_sut, 31).Should().BeLessThan(0);
        }
    }
}
