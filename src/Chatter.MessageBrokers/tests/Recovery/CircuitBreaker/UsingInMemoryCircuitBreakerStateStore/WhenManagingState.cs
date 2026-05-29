using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using Chatter.Testing.Core.Creators.Common;
using Chatter.Testing.Core.Creators.MessageBrokers.Recovery;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Recovery.CircuitBreaker.UsingInMemoryCircuitBreakerStateStore
{
    public class WhenManagingState : Testing.Core.Context
    {
        private readonly LoggerCreator<InMemoryCircuitBreakerStateStore> _logger;
        private readonly InMemoryCircuitBreakerStateStore _sut;

        public WhenManagingState()
        {
            _logger = New.Common().Logger<InMemoryCircuitBreakerStateStore>();
            _sut = new InMemoryCircuitBreakerStateStore(_logger.Creation);
        }

        [Fact]
        public void MustThrowArgumentNullExceptionWhenLoggerIsNull()
            => FluentActions.Invoking(() => new InMemoryCircuitBreakerStateStore(null))
                .Should().Throw<ArgumentNullException>();

        [Fact]
        public void MustDefaultToClosedState()
            => _sut.State.Should().Be(CircuitBreakerState.Closed);

        [Fact]
        public void MustReportIsClosedWhenInitialState()
            => _sut.IsClosed.Should().BeTrue();

        [Fact]
        public void MustStartWithZeroFailureCount()
            => _sut.FailureCount.Should().Be(0);

        [Fact]
        public void MustStartWithZeroSuccessCount()
            => _sut.SuccessCount.Should().Be(0);

        [Fact]
        public void MustStartWithNullLastException()
            => _sut.LastException.Should().BeNull();

        [Fact]
        public async Task MustSetOpenStateOnOpenAsync()
        {
            await _sut.OpenAsync(new FakeRecoverableException());
            _sut.State.Should().Be(CircuitBreakerState.Open);
        }

        [Fact]
        public async Task MustNotBeClosedAfterOpenAsync()
        {
            await _sut.OpenAsync(new FakeRecoverableException());
            _sut.IsClosed.Should().BeFalse();
        }

        [Fact]
        public async Task MustStoreLastExceptionOnOpenAsync()
        {
            var ex = new FakeRecoverableException("boom");
            await _sut.OpenAsync(ex);
            _sut.LastException.Should().BeSameAs(ex);
        }

        [Fact]
        public async Task MustUpdateLastStateChangedDateOnOpenAsync()
        {
            await _sut.OpenAsync(new FakeRecoverableException());
            _sut.LastStateChangedDateUtc.Should().BeAfter(DateTime.MinValue);
        }

        [Fact]
        public async Task MustLogOpenTransition()
        {
            await _sut.OpenAsync(new FakeRecoverableException());
            _logger.VerifyWasCalled(LogLevel.Information, "Circuit Breaker is now in the OPEN state.", Times.Once());
        }

        [Fact]
        public async Task MustSetClosedStateOnCloseAsync()
        {
            await _sut.OpenAsync(new FakeRecoverableException());
            await _sut.CloseAsync();
            _sut.State.Should().Be(CircuitBreakerState.Closed);
        }

        [Fact]
        public async Task MustResetFailureCountOnCloseAsync()
        {
            await _sut.IncrementFailureCounterAsync(new FakeRecoverableException());
            await _sut.CloseAsync();
            _sut.FailureCount.Should().Be(0);
        }

        [Fact]
        public async Task MustLogClosedTransition()
        {
            await _sut.CloseAsync();
            _logger.VerifyWasCalled(LogLevel.Information, "Circuit Breaker is now in the CLOSED state.", Times.Once());
        }

        [Fact]
        public async Task MustSetHalfOpenStateOnHalfOpenAsync()
        {
            await _sut.OpenAsync(new FakeRecoverableException());
            await _sut.HalfOpenAsync();
            _sut.State.Should().Be(CircuitBreakerState.HalfOpen);
        }

        [Fact]
        public async Task MustResetSuccessCountOnHalfOpenAsync()
        {
            await _sut.OpenAsync(new FakeRecoverableException());
            await _sut.IncrementSuccessCounterAsync();
            await _sut.HalfOpenAsync();
            _sut.SuccessCount.Should().Be(0);
        }

        [Fact]
        public async Task MustLogHalfOpenTransition()
        {
            await _sut.OpenAsync(new FakeRecoverableException());
            await _sut.HalfOpenAsync();
            _logger.VerifyWasCalled(LogLevel.Information, "Circuit Breaker is now in the HALF-OPEN state.", Times.Once());
        }

        [Fact]
        public async Task MustNoOpWhenAlreadyHalfOpen()
        {
            await _sut.OpenAsync(new FakeRecoverableException());
            await _sut.HalfOpenAsync();
            await _sut.HalfOpenAsync();
            // Second call returns early before re-logging the transition.
            _logger.VerifyWasCalled(LogLevel.Information, "Circuit Breaker is now in the HALF-OPEN state.", Times.Once());
        }

        [Fact]
        public async Task MustIncrementAndReturnNewSuccessCount()
        {
            (await _sut.IncrementSuccessCounterAsync()).Should().Be(1);
            (await _sut.IncrementSuccessCounterAsync()).Should().Be(2);
            _sut.SuccessCount.Should().Be(2);
        }

        [Fact]
        public async Task MustIncrementAndReturnNewFailureCount()
        {
            (await _sut.IncrementFailureCounterAsync(new FakeRecoverableException())).Should().Be(1);
            (await _sut.IncrementFailureCounterAsync(new FakeRecoverableException())).Should().Be(2);
            _sut.FailureCount.Should().Be(2);
        }

        [Fact]
        public async Task MustStoreLastExceptionOnIncrementFailure()
        {
            var ex = new FakeRecoverableException("failure");
            await _sut.IncrementFailureCounterAsync(ex);
            _sut.LastException.Should().BeSameAs(ex);
        }
    }
}
