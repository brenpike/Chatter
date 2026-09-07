using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Recovery.Retry;
using FluentAssertions;
using System.Threading;
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

        [Fact]
        public void MustCompleteWithoutObservingCancellationForDeliveryCountOverload()
        {
            // INVARIANT: there is no delay in flight to abort, so a requested cancellation leaves
            // the strategy already completed rather than cancelled.
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            _sut.ExecuteAsync(50, cancellation.Token).IsCompletedSuccessfully.Should().BeTrue();
        }

        [Fact]
        public void MustCompleteWithoutObservingCancellationForFailureContextOverload()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            _sut.ExecuteAsync(new FailureContext(null, null, "failed", null, 1, null), cancellation.Token)
                .IsCompletedSuccessfully.Should().BeTrue();
        }
    }
}
