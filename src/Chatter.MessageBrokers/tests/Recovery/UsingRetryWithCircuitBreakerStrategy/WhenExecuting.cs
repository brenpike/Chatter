using Chatter.MessageBrokers.Recovery;
using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using Chatter.MessageBrokers.Recovery.Retry;
using Chatter.Testing.Core.Creators.MessageBrokers;
using Chatter.Testing.Core.Creators.MessageBrokers.Recovery;
using FluentAssertions;
using Moq;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Recovery.UsingRetryWithCircuitBreakerStrategy
{
    public class WhenExecuting : Testing.Core.Context
    {
        private readonly Mock<ICircuitBreaker> _circuitBreaker = new Mock<ICircuitBreaker>();
        private readonly Mock<IRetryStrategy> _retry = new Mock<IRetryStrategy>();
        private readonly RetryWithCircuitBreakerStrategy _sut;

        public WhenExecuting()
        {
            // Both collaborators forward to the delegate they are handed so the composition can be observed.
            _retry.Setup(r => r.ExecuteAsync(It.IsAny<Func<Task<int>>>(), It.IsAny<CancellationToken>()))
                  .Returns<Func<Task<int>>, CancellationToken>((action, _) => action());
            _circuitBreaker.Setup(c => c.ExecuteAsync(It.IsAny<Func<CircuitBreakerState, Task<int>>>(), It.IsAny<CancellationToken>()))
                  .Returns<Func<CircuitBreakerState, Task<int>>, CancellationToken>((action, _) => action(CircuitBreakerState.Closed));

            var options = New.MessageBrokers().Recovery().RecoveryOptions().Creation;
            _sut = new RetryWithCircuitBreakerStrategy(options, _circuitBreaker.Object, _retry.Object);
        }

        [Fact]
        public async Task MustInvokeActionAndReturnResultThroughComposition()
        {
            var result = await _sut.ExecuteAsync(() => Task.FromResult(7), CancellationToken.None);
            result.Should().Be(7);
        }

        [Fact]
        public async Task MustDelegateToRetryStrategyOnce()
        {
            await _sut.ExecuteAsync(() => Task.FromResult(1), CancellationToken.None);
            _retry.Verify(r => r.ExecuteAsync(It.IsAny<Func<Task<int>>>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task MustRouteActionThroughCircuitBreaker()
        {
            await _sut.ExecuteAsync(() => Task.FromResult(1), CancellationToken.None);
            _circuitBreaker.Verify(c => c.ExecuteAsync(It.IsAny<Func<CircuitBreakerState, Task<int>>>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task MustPropagateCancellationTokenToRetryStrategy()
        {
            using var cts = new CancellationTokenSource();
            await _sut.ExecuteAsync(() => Task.FromResult(1), cts.Token);
            _retry.Verify(r => r.ExecuteAsync(It.IsAny<Func<Task<int>>>(), cts.Token), Times.Once);
        }

        [Fact]
        public void MustNotThrowWhenConstructedWithNullOptions()
            => FluentActions.Invoking(() => new RetryWithCircuitBreakerStrategy(null, _circuitBreaker.Object, _retry.Object))
                .Should().NotThrow();
    }
}
