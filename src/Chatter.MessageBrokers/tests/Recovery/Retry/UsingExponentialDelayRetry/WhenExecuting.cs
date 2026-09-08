using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Recovery.Retry;
using FluentAssertions;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Recovery.Retry.UsingExponentialDelayRetry
{
    public class WhenExecuting : Testing.Core.Context
    {
        private const int _attemptsYieldingADelayLongerThanTheTest = 15;
        private static readonly TimeSpan _cancellationObservationWindow = TimeSpan.FromSeconds(5);

        private static FailureContext FailureContextWithDeliveryCount(int deliveryCount)
            => new FailureContext(null, null, "failed", null, deliveryCount, null);

        [Fact]
        public async Task MustCompleteInstantlyWhenComputedDelayIsZero()
            // Attempt 1 computes 0ms; constructing with cap 0 keeps the awaited delay at zero.
            => await new ExponentialDelayRetry(0).ExecuteAsync(1);

        [Fact]
        public async Task MustClampToMaxDelayWhenComputedDelayExceedsMax()
            // Constructed with maxRetryAttempts 0 -> cap 0, so a larger computed delay is clamped to 0 (instant).
            => await new ExponentialDelayRetry(0).ExecuteAsync(5);

        [Fact]
        public async Task MustCompleteWhenConstructedWithAnOverflowingMaxRetryAttempts()
            // Cap computed from 30 attempts used to wrap negative, so the first delay was rejected outright.
            => await new ExponentialDelayRetry(30).ExecuteAsync(1);

        [Fact]
        public async Task MustCompleteWhenTheDeliveryCountOverflowsTheComputedDelay()
            // Cap 0 with an attempt whose computed delay used to wrap negative; the negative computed value won.
            => await new ExponentialDelayRetry(0).ExecuteAsync(23);

        [Fact]
        public async Task MustCompleteForFailureContextWhenTheDeliveryCountOverflowsTheComputedDelay()
            => await new ExponentialDelayRetry(0).ExecuteAsync(FailureContextWithDeliveryCount(23));

        [Fact]
        public async Task MustThrowArgumentNullExceptionWhenFailureContextIsNull()
            => await FluentActions
                .Invoking(async () => await new ExponentialDelayRetry(0).ExecuteAsync((FailureContext)null))
                .Should().ThrowAsync<ArgumentNullException>();

        [Fact]
        public async Task MustAbortInFlightDelayWhenCancellationIsRequested()
        {
            using var cancellation = new CancellationTokenSource();
            var delaying = new ExponentialDelayRetry(_attemptsYieldingADelayLongerThanTheTest)
                .ExecuteAsync(_attemptsYieldingADelayLongerThanTheTest, cancellation.Token);

            cancellation.Cancel();

            await FluentActions.Invoking(() => delaying.WaitAsync(_cancellationObservationWindow))
                .Should().ThrowAsync<TaskCanceledException>();
        }

        [Fact]
        public async Task MustAbortInFlightDelayForFailureContextWhenCancellationIsRequested()
        {
            using var cancellation = new CancellationTokenSource();
            var delaying = new ExponentialDelayRetry(_attemptsYieldingADelayLongerThanTheTest)
                .ExecuteAsync(FailureContextWithDeliveryCount(_attemptsYieldingADelayLongerThanTheTest), cancellation.Token);

            cancellation.Cancel();

            await FluentActions.Invoking(() => delaying.WaitAsync(_cancellationObservationWindow))
                .Should().ThrowAsync<TaskCanceledException>();
        }
    }
}
