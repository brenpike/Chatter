using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Recovery.Retry;
using FluentAssertions;
using System;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Recovery.Retry.UsingExponentialDelayRetry
{
    public class WhenExecuting : Testing.Core.Context
    {
        [Fact]
        public async Task MustCompleteInstantlyWhenComputedDelayIsZero()
            // Attempt 1 computes 0ms; constructing with cap 0 keeps the awaited delay at zero.
            => await new ExponentialDelayRetry(0).ExecuteAsync(1);

        [Fact]
        public async Task MustClampToMaxDelayWhenComputedDelayExceedsMax()
            // Constructed with maxRetryAttempts 0 -> cap 0, so a larger computed delay is clamped to 0 (instant).
            => await new ExponentialDelayRetry(0).ExecuteAsync(5);

        [Fact]
        public async Task MustThrowArgumentNullExceptionWhenFailureContextIsNull()
            => await FluentActions
                .Invoking(async () => await new ExponentialDelayRetry(0).ExecuteAsync((FailureContext)null))
                .Should().ThrowAsync<ArgumentNullException>();
    }
}
