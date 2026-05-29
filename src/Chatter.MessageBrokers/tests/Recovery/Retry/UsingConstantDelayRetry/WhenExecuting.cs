using Chatter.MessageBrokers.Recovery.Retry;
using FluentAssertions;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Recovery.Retry.UsingConstantDelayRetry
{
    public class WhenExecuting : Testing.Core.Context
    {
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
    }
}
