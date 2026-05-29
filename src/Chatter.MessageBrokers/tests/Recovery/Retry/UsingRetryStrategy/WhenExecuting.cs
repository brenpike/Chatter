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
            _delay.Setup(d => d.ExecuteAsync(It.IsAny<int>())).Returns(Task.CompletedTask);
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
            _delay.Verify(d => d.ExecuteAsync(It.IsAny<int>()), Times.Never);
        }

        [Fact]
        public async Task MustRethrowOriginalExceptionWhenNotConfiguredForRetry()
        {
            _evaluator.Setup(e => e.ShouldRetry(It.IsAny<Exception>())).Returns(false);

            await FluentActions
                .Invoking(async () => await _sut.ExecuteAsync<int>(() => throw new FakeRecoverableException()))
                .Should().ThrowAsync<FakeRecoverableException>();

            _delay.Verify(d => d.ExecuteAsync(It.IsAny<int>()), Times.Never);
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
            _delay.Verify(d => d.ExecuteAsync(It.IsAny<int>()), Times.Exactly(2));
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
            _delay.Verify(d => d.ExecuteAsync(It.IsAny<int>()), Times.Never);
        }

        [Fact]
        public async Task MustSwallowCircuitBreakerOpenExceptionAndRetryWithoutDelay()
        {
            // The CircuitBreakerOpenException catch block is empty: it neither evaluates, delays,
            // nor counts the attempt. The loop simply re-runs the action on the next iteration.
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
            _delay.Verify(d => d.ExecuteAsync(It.IsAny<int>()), Times.Never);
            _evaluator.Verify(e => e.ShouldRetry(It.IsAny<Exception>()), Times.Never);
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
