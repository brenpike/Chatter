using Chatter.MessageBrokers.Recovery.Retry;
using FluentAssertions;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Recovery.Retry.UsingNoDelayRetry
{
    public class WhenExecuting : Testing.Core.Context
    {
        private readonly NoDelayRetry _sut = new NoDelayRetry();

        [Fact]
        public void MustReturnAlreadyCompletedTaskForDeliveryCountOverload()
            => ((System.Threading.Tasks.Task)_sut.ExecuteAsync(0)).IsCompletedSuccessfully.Should().BeTrue();

        [Fact]
        public void MustReturnAlreadyCompletedTaskRegardlessOfDeliveryCount()
            => ((System.Threading.Tasks.Task)_sut.ExecuteAsync(100)).IsCompletedSuccessfully.Should().BeTrue();

        [Fact]
        public async Task MustCompleteInstantlyForDeliveryCountOverload()
            => await _sut.ExecuteAsync(50);
    }
}
