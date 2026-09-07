using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Recovery.Retry;
using FluentAssertions;
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Recovery.Retry.UsingConstantDelayRetry
{
    public class WhenExecuting : Testing.Core.Context
    {
        private const int _delayLongerThanTheTestInMilliseconds = 60_000;
        private static readonly TimeSpan _cancellationObservationWindow = TimeSpan.FromSeconds(5);

        private static FailureContext AnyFailureContext()
            => new FailureContext(null, null, "failed", null, 1, null);

        private static int ConfiguredDelayOf(ConstantDelayRetry sut)
            => (int)typeof(ConstantDelayRetry)
                .GetField("_constantDelayInMilliseconds", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(sut);

        [Fact]
        public void MustStoreConfiguredConstantDelay()
            => ConfiguredDelayOf(new ConstantDelayRetry(2500)).Should().Be(2500);

        [Fact]
        public void MustIgnoreDeliveryCountAndAlwaysUseConfiguredConstant()
        {
            // INVARIANT: the configured constant is fixed regardless of delivery count; the int
            // overload delays by the configured value, not by the passed-in count.
            ConfiguredDelayOf(new ConstantDelayRetry(750)).Should().Be(750);
        }

        [Fact]
        public async Task MustCompleteInstantlyWhenConfiguredWithZeroDelay()
            => await new ConstantDelayRetry(0).ExecuteAsync(99);

        [Fact]
        public async Task MustAbortInFlightDelayWhenCancellationIsRequested()
        {
            using var cancellation = new CancellationTokenSource();
            var delaying = new ConstantDelayRetry(_delayLongerThanTheTestInMilliseconds).ExecuteAsync(1, cancellation.Token);

            cancellation.Cancel();

            await FluentActions.Invoking(() => delaying.WaitAsync(_cancellationObservationWindow))
                .Should().ThrowAsync<TaskCanceledException>();
        }

        [Fact]
        public async Task MustAbortInFlightDelayForFailureContextWhenCancellationIsRequested()
        {
            using var cancellation = new CancellationTokenSource();
            var delaying = new ConstantDelayRetry(_delayLongerThanTheTestInMilliseconds).ExecuteAsync(AnyFailureContext(), cancellation.Token);

            cancellation.Cancel();

            await FluentActions.Invoking(() => delaying.WaitAsync(_cancellationObservationWindow))
                .Should().ThrowAsync<TaskCanceledException>();
        }
    }
}
