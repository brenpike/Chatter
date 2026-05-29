using Chatter.MessageBrokers.Recovery.CircuitBreaker;
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
using CircuitBreakerSut = Chatter.MessageBrokers.Recovery.CircuitBreaker.CircuitBreaker;

namespace Chatter.MessageBrokers.Tests.Recovery.CircuitBreaker.UsingCircuitBreaker
{
    public class WhenExecuting : Testing.Core.Context
    {
        private readonly Mock<ICircuitBreakerStateStore> _store = new Mock<ICircuitBreakerStateStore>();
        private readonly LoggerCreator<CircuitBreakerSut> _logger;
        private readonly Mock<ICircuitBreakerExceptionEvaluator> _evaluator = new Mock<ICircuitBreakerExceptionEvaluator>();
        private readonly CircuitBreakerOptions _options;

        public WhenExecuting()
        {
            _logger = New.Common().Logger<CircuitBreakerSut>();
            _options = New.MessageBrokers().Recovery().CircuitBreakerOptions()
                .WithFailuresBeforeOpen(1)
                .WithHalfOpenSuccessesToClose(1);
        }

        private CircuitBreakerSut CreateSut()
            => new CircuitBreakerSut(_store.Object, _options, _logger.Creation, _evaluator.Object);

        private void Closed() => _store.SetupGet(s => s.IsClosed).Returns(true);
        private void Open(CircuitBreakerState state = CircuitBreakerState.Open)
        {
            _store.SetupGet(s => s.IsClosed).Returns(false);
            _store.SetupGet(s => s.State).Returns(state);
        }

        [Fact]
        public void MustReportIsClosedFromStateStore()
        {
            Closed();
            CreateSut().IsClosed.Should().BeTrue();
        }

        [Fact]
        public void MustReportIsOpenAsInverseOfStateStoreClosed()
        {
            Open();
            CreateSut().IsOpen.Should().BeTrue();
        }

        [Fact]
        public async Task MustExecuteActionAndReturnResultWhenClosed()
        {
            Closed();
            var result = await CreateSut().ExecuteAsync(_ => Task.FromResult(5));
            result.Should().Be(5);
        }

        [Fact]
        public async Task MustRethrowAndSkipTripWhenExceptionNotConfigured()
        {
            Closed();
            _evaluator.Setup(e => e.ShouldTrip(It.IsAny<Exception>())).Returns(false);

            await FluentActions
                .Invoking(async () => await CreateSut().ExecuteAsync<int>(_ => throw new FakeRecoverableException()))
                .Should().ThrowAsync<FakeRecoverableException>();

            _store.Verify(s => s.IncrementFailureCounterAsync(It.IsAny<Exception>()), Times.Never);
        }

        [Fact]
        public async Task MustLogTraceWhenExceptionNotConfiguredForTrip()
        {
            Closed();
            _evaluator.Setup(e => e.ShouldTrip(It.IsAny<Exception>())).Returns(false);

            await FluentActions
                .Invoking(async () => await CreateSut().ExecuteAsync<int>(_ => throw new FakeRecoverableException()))
                .Should().ThrowAsync<FakeRecoverableException>();

            _logger.VerifyWasCalled(LogLevel.Trace,
                $"Circuit break not configured for exception type '{typeof(FakeRecoverableException).FullName}'. Skipping.",
                Times.Once());
        }

        [Fact]
        public async Task MustIncrementFailureCounterWhenExceptionTripsCircuit()
        {
            Closed();
            _evaluator.Setup(e => e.ShouldTrip(It.IsAny<Exception>())).Returns(true);
            _store.Setup(s => s.IncrementFailureCounterAsync(It.IsAny<Exception>())).ReturnsAsync(1);

            await FluentActions
                .Invoking(async () => await CreateSut().ExecuteAsync<int>(_ => throw new FakeRecoverableException()))
                .Should().ThrowAsync<FakeRecoverableException>();

            _store.Verify(s => s.IncrementFailureCounterAsync(It.IsAny<Exception>()), Times.Once);
        }

        [Fact]
        public async Task MustOpenStoreWhenFailureThresholdReached()
        {
            Closed();
            _evaluator.Setup(e => e.ShouldTrip(It.IsAny<Exception>())).Returns(true);
            _store.Setup(s => s.IncrementFailureCounterAsync(It.IsAny<Exception>())).ReturnsAsync(1);

            await FluentActions
                .Invoking(async () => await CreateSut().ExecuteAsync<int>(_ => throw new FakeRecoverableException()))
                .Should().ThrowAsync<FakeRecoverableException>();

            _store.Verify(s => s.OpenAsync(It.IsAny<Exception>()), Times.Once);
        }

        [Fact]
        public async Task MustNotOpenStoreWhenFailureCountBelowThreshold()
        {
            Closed();
            _options.NumberOfFailuresBeforeOpen.Should().Be(1);
            _evaluator.Setup(e => e.ShouldTrip(It.IsAny<Exception>())).Returns(true);
            _store.Setup(s => s.IncrementFailureCounterAsync(It.IsAny<Exception>())).ReturnsAsync(0);

            await FluentActions
                .Invoking(async () => await CreateSut().ExecuteAsync<int>(_ => throw new FakeRecoverableException()))
                .Should().ThrowAsync<FakeRecoverableException>();

            _store.Verify(s => s.OpenAsync(It.IsAny<Exception>()), Times.Never);
        }

        [Fact]
        public async Task MustTransitionToHalfOpenAndExecuteWhenOpen()
        {
            Open();
            _store.Setup(s => s.IncrementSuccessCounterAsync()).ReturnsAsync(1);

            var result = await CreateSut().ExecuteAsync(_ => Task.FromResult(11));

            result.Should().Be(11);
            _store.Verify(s => s.HalfOpenAsync(), Times.Once);
        }

        [Fact]
        public async Task MustCloseStoreWhenHalfOpenSuccessThresholdReached()
        {
            Open();
            _store.Setup(s => s.IncrementSuccessCounterAsync()).ReturnsAsync(1);

            await CreateSut().ExecuteAsync(_ => Task.FromResult(1));

            _store.Verify(s => s.CloseAsync(), Times.Once);
        }

        [Fact]
        public async Task MustNotCloseStoreWhenHalfOpenSuccessBelowThreshold()
        {
            Open();
            _store.Setup(s => s.IncrementSuccessCounterAsync()).ReturnsAsync(0);

            await CreateSut().ExecuteAsync(_ => Task.FromResult(1));

            _store.Verify(s => s.CloseAsync(), Times.Never);
        }

        [Fact]
        public async Task MustReopenStoreAndRethrowWhenHalfOpenActionFails()
        {
            Open();

            await FluentActions
                .Invoking(async () => await CreateSut().ExecuteAsync<int>(_ => throw new FakeRecoverableException()))
                .Should().ThrowAsync<FakeRecoverableException>();

            _store.Verify(s => s.OpenAsync(It.IsAny<Exception>()), Times.Once);
        }

        [Fact]
        public async Task MustNotDelayWhenAlreadyHalfOpen()
        {
            Open(CircuitBreakerState.HalfOpen);
            _store.Setup(s => s.IncrementSuccessCounterAsync()).ReturnsAsync(1);

            await CreateSut().ExecuteAsync(_ => Task.FromResult(1));

            _store.Verify(s => s.HalfOpenAsync(), Times.Once);
        }

        [Fact]
        public async Task MustThrowOperationCanceledWhenTokenAlreadyCancelled()
        {
            Closed();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await FluentActions
                .Invoking(async () => await CreateSut().ExecuteAsync(_ => Task.FromResult(1), cts.Token))
                .Should().ThrowAsync<OperationCanceledException>();
        }
    }
}
