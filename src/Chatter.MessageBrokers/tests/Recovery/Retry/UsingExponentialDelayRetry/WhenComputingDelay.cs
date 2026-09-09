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

        // This theory exists so the per-attempt table documented on ExponentialDelayRetry and on
        // RecoveryOptionsBuilder.UseExponentialDelayRecovery cannot drift from the computation again.
        // Every row below is transcribed from that table; a documented row that stops matching what
        // ExponentialDelayRetry computes fails here.
        [Theory]
        [InlineData(1, 0)]
        [InlineData(2, 1)]
        [InlineData(3, 3)]
        [InlineData(4, 7)]
        [InlineData(5, 15)]
        [InlineData(6, 31)]
        [InlineData(7, 63)]
        [InlineData(8, 127)]
        [InlineData(9, 255)]
        [InlineData(10, 511)]
        [InlineData(11, 1023)]
        [InlineData(12, 2047)]
        [InlineData(13, 4095)]
        [InlineData(14, 8191)]
        [InlineData(15, 16383)]
        public void MustComputeTheDelayTheDocumentedTableStatesForEveryDocumentedAttempt(int attempts, int documentedSeconds)
            => ComputedDelayFor(_sut, attempts).Should().Be(documentedSeconds * 1000);

        [Fact]
        public void MustSetMaxDelayFromConstructorRetryAttempts()
            // The constructor overwrites the 1024 default with the computed delay for maxRetryAttempts (5 -> 15000ms).
            => MaxDelayOf(_sut).Should().Be(15000);

        [Fact]
        public void MustComputeTheLargestInDomainDelayForAttemptTwentyTwo()
            // 0.5 * (2^22 - 1) truncates to 2097151s -> 2097151000ms, the last product below int.MaxValue.
            => ComputedDelayFor(_sut, 22).Should().Be(2097151000);

        [Fact]
        public void MustSaturateAtTaskDelayCeilingForAttemptThirtyOne()
            // Inverted from MustOverflowToNegativeForLargeAttempt, which pinned the Int32 wrap as-is.
            => ComputedDelayFor(_sut, 31).Should().Be(int.MaxValue);

        [Theory]
        [InlineData(23)]
        [InlineData(24)]
        [InlineData(25)]
        [InlineData(26)]
        [InlineData(27)]
        [InlineData(28)]
        [InlineData(29)]
        [InlineData(30)]
        [InlineData(32)]
        [InlineData(1024)]
        [InlineData(2048)]
        [InlineData(int.MaxValue)]
        public void MustSaturateAtTaskDelayCeilingForEveryOverflowingAttempt(int attempts)
            // Attempt 23 is the first whose product exceeds int.MaxValue; every larger attempt does too,
            // including 28 (which wrapped positive to a 12.43 day delay) and 1024 (where 2^n is +Infinity).
            => ComputedDelayFor(_sut, attempts).Should().Be(int.MaxValue);

        [Theory]
        [InlineData(-1)]
        [InlineData(int.MinValue)]
        public void MustComputeZeroMillisecondsForANegativeAttemptCount(int attempts)
            => ComputedDelayFor(_sut, attempts).Should().Be(0);

        [Theory]
        [InlineData(0)]
        [InlineData(22)]
        [InlineData(23)]
        [InlineData(28)]
        [InlineData(31)]
        [InlineData(1024)]
        [InlineData(int.MaxValue)]
        [InlineData(int.MinValue)]
        public void MustComputeADelayWithinTaskDelaysAcceptedDomain(int attempts)
            => ComputedDelayFor(_sut, attempts).Should().BeInRange(0, int.MaxValue);
    }
}
