using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using Chatter.MessageBrokers.Recovery.Options;
using Chatter.MessageBrokers.Recovery.Retry;
using Chatter.Testing.Core.Creators.Common;
using Chatter.Testing.Core.Creators.MessageBrokers;
using Chatter.Testing.Core.Creators.MessageBrokers.Recovery;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Recovery.Retry.UsingRetryStrategy
{
    public class WhenExecuting : Testing.Core.Context
    {
        private readonly RecoveryOptions _options;
        private readonly RecordingLoggerCreator<RetryStrategy> _logger;
        private readonly Mock<IRetryDelayStrategy> _delay = new Mock<IRetryDelayStrategy>();
        private readonly Mock<IRetryExceptionEvaluator> _evaluator = new Mock<IRetryExceptionEvaluator>();
        private readonly RetryStrategy _sut;

        public WhenExecuting()
        {
            _options = New.MessageBrokers().Recovery().RecoveryOptions().WithMaxRetryAttempts(3);
            _logger = New.Common().RecordingLogger<RetryStrategy>();
            _delay.Setup(d => d.ExecuteAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            _sut = new RetryStrategy(_options, _logger.Creation, _delay.Object, _evaluator.Object);
        }

        [Fact]
        public async Task MustReturnResultAndInvokeActionOnceOnSuccess()
        {
            var callCount = 0;
            var result = await _sut.ExecuteAsync(() =>
            {
                callCount++;
                return Task.FromResult(42);
            });

            result.Should().Be(42);
            callCount.Should().Be(1);
            _delay.Verify(d => d.ExecuteAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task MustRethrowOriginalExceptionWhenNotConfiguredForRetry()
        {
            _evaluator.Setup(e => e.ShouldRetry(It.IsAny<Exception>())).Returns(false);

            await FluentActions
                .Invoking(async () => await _sut.ExecuteAsync<int>(() => throw new FakeRecoverableException()))
                .Should().ThrowAsync<FakeRecoverableException>();

            _delay.Verify(d => d.ExecuteAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task MustLogTraceWhenRetryAbortedForNonRetryableException()
        {
            _evaluator.Setup(e => e.ShouldRetry(It.IsAny<Exception>())).Returns(false);

            await FluentActions
                .Invoking(async () => await _sut.ExecuteAsync<int>(() => throw new FakeRecoverableException()))
                .Should().ThrowAsync<FakeRecoverableException>();

            _logger.VerifyWasCalled(LogLevel.Trace,
                $"Retry aborted. Exception type '{typeof(FakeRecoverableException).FullName}' not configured for retry.",
                1);
        }

        [Fact]
        public async Task MustThrowMaxRetryAttemptsExceededAfterExhaustingRetries()
        {
            _evaluator.Setup(e => e.ShouldRetry(It.IsAny<Exception>())).Returns(true);

            var ex = await FluentActions
                .Invoking(async () => await _sut.ExecuteAsync<int>(() => throw new FakeRecoverableException()))
                .Should().ThrowAsync<MaxRetryAttemptsExceededException>();

            ex.Which.Attempts.Should().Be(3);
        }

        [Fact]
        public async Task MustInvokeActionOncePerAttemptUntilMaxReached()
        {
            _evaluator.Setup(e => e.ShouldRetry(It.IsAny<Exception>())).Returns(true);
            var callCount = 0;

            await FluentActions
                .Invoking(async () => await _sut.ExecuteAsync<int>(() =>
                {
                    callCount++;
                    throw new FakeRecoverableException();
                }))
                .Should().ThrowAsync<MaxRetryAttemptsExceededException>();

            callCount.Should().Be(3);
        }

        [Fact]
        public async Task MustConsultDelayStrategyBetweenAttempts()
        {
            _evaluator.Setup(e => e.ShouldRetry(It.IsAny<Exception>())).Returns(true);

            await FluentActions
                .Invoking(async () => await _sut.ExecuteAsync<int>(() => throw new FakeRecoverableException()))
                .Should().ThrowAsync<MaxRetryAttemptsExceededException>();

            // 3 max attempts -> delay consulted between attempts 1->2 and 2->3 only.
            _delay.Verify(d => d.ExecuteAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        }

        [Fact]
        public async Task MustThrowMaxRetryImmediatelyWhenMaxRetryAttemptsIsOne()
        {
            var options = New.MessageBrokers().Recovery().RecoveryOptions().WithMaxRetryAttempts(1);
            var sut = new RetryStrategy(options, _logger.Creation, _delay.Object, _evaluator.Object);
            _evaluator.Setup(e => e.ShouldRetry(It.IsAny<Exception>())).Returns(true);
            var callCount = 0;

            var ex = await FluentActions
                .Invoking(async () => await sut.ExecuteAsync<int>(() =>
                {
                    callCount++;
                    throw new FakeRecoverableException();
                }))
                .Should().ThrowAsync<MaxRetryAttemptsExceededException>();

            ex.Which.Attempts.Should().Be(1);
            callCount.Should().Be(1);
            _delay.Verify(d => d.ExecuteAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task MustDelayAndCountAttemptWhenCircuitBreakerRefusesThenSucceeds()
        {
            // A refusal from an open circuit consumes retry budget exactly like an action failure:
            // it is delayed and counted. It is NOT put to the retry exception evaluator, because a
            // refusal is the circuit declining to run the action rather than a fault the action raised.
            var callCount = 0;
            var result = await _sut.ExecuteAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    throw new CircuitBreakerOpenException(new FakeRecoverableException());
                }
                return Task.FromResult(99);
            });

            result.Should().Be(99);
            callCount.Should().Be(2);
            _delay.Verify(d => d.ExecuteAsync(1, It.IsAny<CancellationToken>()), Times.Once);
            _evaluator.Verify(e => e.ShouldRetry(It.IsAny<Exception>()), Times.Never);
        }

        [Fact]
        public async Task MustThrowMaxRetryAttemptsExceededWhenCircuitBreakerRefusesEveryAttempt()
        {
            // A circuit that stays open must exhaust the retry budget and terminate rather than spin.
            // The watchdog bounds the wait so a regression that stops counting the refused attempt
            // fails this test instead of hanging the run.
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var callCount = 0;

            var ex = await FluentActions
                .Invoking(async () => await _sut.ExecuteAsync<int>(() =>
                {
                    callCount++;
                    throw new CircuitBreakerOpenException(new FakeRecoverableException());
                }, watchdog.Token))
                .Should().ThrowAsync<MaxRetryAttemptsExceededException>();

            ex.Which.Attempts.Should().Be(3);
            ex.Which.InnerException.Should().BeOfType<CircuitBreakerOpenException>();
            callCount.Should().Be(3);
            _delay.Verify(d => d.ExecuteAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        }

        [Fact]
        public async Task MustPassCallersCancellationTokenToDelayStrategy()
        {
            _evaluator.Setup(e => e.ShouldRetry(It.IsAny<Exception>())).Returns(true);
            using var cts = new CancellationTokenSource();

            await FluentActions
                .Invoking(async () => await _sut.ExecuteAsync<int>(() => throw new FakeRecoverableException(), cts.Token))
                .Should().ThrowAsync<MaxRetryAttemptsExceededException>();

            _delay.Verify(d => d.ExecuteAsync(It.IsAny<int>(), cts.Token), Times.Exactly(2));
        }

        [Fact]
        public async Task MustThrowOperationCanceledWhenTokenAlreadyCancelled()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await FluentActions
                .Invoking(async () => await _sut.ExecuteAsync(() => Task.FromResult(1), cts.Token))
                .Should().ThrowAsync<OperationCanceledException>();
        }
    }
}
