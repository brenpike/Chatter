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
        public async Task MustRefuseWithoutExecutingWhenOpen()
        {
            Open();
            var wasExecuted = false;

            await FluentActions
                .Invoking(async () => await CreateSut().ExecuteAsync<int>(_ =>
                {
                    wasExecuted = true;
                    return Task.FromResult(11);
                }))
                .Should().ThrowAsync<CircuitBreakerOpenException>();

            wasExecuted.Should().BeFalse();
            _store.Verify(s => s.TryHalfOpenAsync(), Times.Once);
        }

        [Fact]
        public async Task MustCarryLastExceptionOnTheRefusalWhenOpen()
        {
            Open();
            var lastException = new FakeRecoverableException();
            _store.SetupGet(s => s.LastException).Returns(lastException);

            var refusal = await FluentActions
                .Invoking(async () => await CreateSut().ExecuteAsync(_ => Task.FromResult(11)))
                .Should().ThrowAsync<CircuitBreakerOpenException>();

            refusal.Which.InnerException.Should().BeSameAs(lastException);
        }

        [Fact]
        public async Task MustNotAnnounceHalfOpenWhenTheStoreRefusesTheTransition()
        {
            Open();
            // Another caller recovered the circuit across the cooling wait, so the store refuses.
            _store.Setup(s => s.TryHalfOpenAsync()).ReturnsAsync(false);

            await FluentActions
                .Invoking(async () => await CreateSut().ExecuteAsync(_ => Task.FromResult(11)))
                .Should().ThrowAsync<CircuitBreakerOpenException>();

            _logger.VerifyWasCalled(LogLevel.Information,
                "Circuit Breaker half-open timer expired. Entering HALF-OPEN state.",
                Times.Never());
        }

        [Fact]
        public async Task MustAnnounceHalfOpenWhenTheStoreGrantsTheTransition()
        {
            Open();
            _store.Setup(s => s.TryHalfOpenAsync()).ReturnsAsync(true);

            await FluentActions
                .Invoking(async () => await CreateSut().ExecuteAsync(_ => Task.FromResult(11)))
                .Should().ThrowAsync<CircuitBreakerOpenException>();

            _logger.VerifyWasCalled(LogLevel.Information,
                "Circuit Breaker half-open timer expired. Entering HALF-OPEN state.",
                Times.Once());
        }

        [Fact]
        public async Task MustThrowOperationCanceledWhenTokenCancelledDuringTheOpenWait()
        {
            Open();
            _options.OpenToHalfOpenWaitTimeInSeconds = 5;
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(50));

            await FluentActions
                .Invoking(async () => await CreateSut().ExecuteAsync(_ => Task.FromResult(11), cts.Token))
                .Should().ThrowAsync<OperationCanceledException>();

            _store.Verify(s => s.TryHalfOpenAsync(), Times.Never);
        }

        [Fact]
        public async Task MustTrialOnTheNextCallAfterRefusingWhenOpen()
        {
            var store = new InMemoryCircuitBreakerStateStore(
                New.Common().Logger<InMemoryCircuitBreakerStateStore>().Creation);
            await store.OpenAsync(new FakeRecoverableException());
            var sut = new CircuitBreakerSut(store, _options, _logger.Creation, _evaluator.Object);

            await FluentActions
                .Invoking(async () => await sut.ExecuteAsync(_ => Task.FromResult(11)))
                .Should().ThrowAsync<CircuitBreakerOpenException>();

            store.State.Should().Be(CircuitBreakerState.HalfOpen);
            (await sut.ExecuteAsync(_ => Task.FromResult(11))).Should().Be(11);
        }

        [Fact]
        public async Task MustNotRegressTheCircuitWhenItRecoversDuringTheCoolingWait()
        {
            var inner = new InMemoryCircuitBreakerStateStore(
                New.Common().Logger<InMemoryCircuitBreakerStateStore>().Creation);
            await inner.OpenAsync(new FakeRecoverableException());
            var sut = new CircuitBreakerSut(
                new RecoverOnStateReadStateStore(inner), _options, _logger.Creation, _evaluator.Object);

            await FluentActions
                .Invoking(async () => await sut.ExecuteAsync(_ => Task.FromResult(11)))
                .Should().ThrowAsync<CircuitBreakerOpenException>();

            inner.State.Should().Be(CircuitBreakerState.Closed);
            inner.SuccessCount.Should().Be(1);
        }

        [Fact]
        public async Task MustNotDiscardHalfOpenProgressWhenTheCircuitRecoversBeforeAdmission()
        {
            CircuitBreakerOptions options = New.MessageBrokers().Recovery().CircuitBreakerOptions()
                .WithFailuresBeforeOpen(1)
                .WithHalfOpenSuccessesToClose(2);
            var inner = new InMemoryCircuitBreakerStateStore(
                New.Common().Logger<InMemoryCircuitBreakerStateStore>().Creation);
            await inner.OpenAsync(new FakeRecoverableException());
            await inner.HalfOpenAsync();
            var sut = new CircuitBreakerSut(
                new RecoverOnStateReadStateStore(inner), options, _logger.Creation, _evaluator.Object);

            (await sut.ExecuteAsync(_ => Task.FromResult(11))).Should().Be(11);

            inner.IsClosed.Should().BeTrue();
        }

        [Fact]
        public async Task MustCloseStoreWhenHalfOpenSuccessThresholdReached()
        {
            Open(CircuitBreakerState.HalfOpen);
            _store.Setup(s => s.IncrementSuccessCounterAsync()).ReturnsAsync(1);

            await CreateSut().ExecuteAsync(_ => Task.FromResult(1));

            _store.Verify(s => s.CloseAsync(), Times.Once);
        }

        [Fact]
        public async Task MustNotCloseStoreWhenHalfOpenSuccessBelowThreshold()
        {
            Open(CircuitBreakerState.HalfOpen);
            _store.Setup(s => s.IncrementSuccessCounterAsync()).ReturnsAsync(0);

            await CreateSut().ExecuteAsync(_ => Task.FromResult(1));

            _store.Verify(s => s.CloseAsync(), Times.Never);
        }

        [Fact]
        public async Task MustReopenStoreAndRethrowWhenHalfOpenActionFails()
        {
            Open(CircuitBreakerState.HalfOpen);
            _evaluator.Setup(e => e.ShouldTrip(It.IsAny<Exception>())).Returns(true);

            await FluentActions
                .Invoking(async () => await CreateSut().ExecuteAsync<int>(_ => throw new FakeRecoverableException()))
                .Should().ThrowAsync<FakeRecoverableException>();

            _store.Verify(s => s.OpenAsync(It.IsAny<Exception>()), Times.Once);
        }

        [Fact]
        public async Task MustNotReopenStoreWhenHalfOpenTrialThrowsNonTrippingException()
        {
            Open(CircuitBreakerState.HalfOpen);
            _evaluator.Setup(e => e.ShouldTrip(It.IsAny<Exception>())).Returns(false);
            var sut = CreateSut();

            await FluentActions
                .Invoking(async () => await sut.ExecuteAsync<int>(_ => throw new FakeRecoverableException()))
                .Should().ThrowAsync<FakeRecoverableException>();

            _store.Verify(s => s.OpenAsync(It.IsAny<Exception>()), Times.Never);

            // The circuit stays HALF-OPEN and keeps executing while IsOpen still reports true: a non-tripping
            // exception neither reopens the circuit nor counts a half-open success. This mirrors the closed
            // path's non-tripping behaviour and is a defended property, not an oversight.
            sut.IsOpen.Should().BeTrue();
            (await sut.ExecuteAsync(_ => Task.FromResult(11))).Should().Be(11);
        }

        [Fact]
        public async Task MustNotReopenStoreWhenHalfOpenTrialIsCancelledByCaller()
        {
            Open(CircuitBreakerState.HalfOpen);
            using var cts = new CancellationTokenSource();

            await FluentActions
                .Invoking(async () => await CreateSut().ExecuteAsync<int>(_ =>
                {
                    cts.Cancel();
                    throw new OperationCanceledException(cts.Token);
                }, cts.Token))
                .Should().ThrowAsync<OperationCanceledException>();

            _store.Verify(s => s.OpenAsync(It.IsAny<Exception>()), Times.Never);
            _evaluator.Verify(e => e.ShouldTrip(It.IsAny<Exception>()), Times.Never);
        }

        [Fact]
        public async Task MustNotCountFailureWhenClosedActionIsCancelledByCaller()
        {
            Closed();
            using var cts = new CancellationTokenSource();

            await FluentActions
                .Invoking(async () => await CreateSut().ExecuteAsync<int>(_ =>
                {
                    cts.Cancel();
                    throw new OperationCanceledException(cts.Token);
                }, cts.Token))
                .Should().ThrowAsync<OperationCanceledException>();

            _store.Verify(s => s.IncrementFailureCounterAsync(It.IsAny<Exception>()), Times.Never);
            _evaluator.Verify(e => e.ShouldTrip(It.IsAny<Exception>()), Times.Never);
        }

        [Fact]
        public async Task MustReleaseTheHalfOpenSlotWhenTheTrialThrows()
        {
            Open(CircuitBreakerState.HalfOpen);
            _options.ConcurrentHalfOpenAttempts.Should().Be(1);
            _evaluator.Setup(e => e.ShouldTrip(It.IsAny<Exception>())).Returns(true);
            var sut = CreateSut();

            await FluentActions
                .Invoking(async () => await sut.ExecuteAsync<int>(_ => throw new FakeRecoverableException()))
                .Should().ThrowAsync<FakeRecoverableException>();

            // The single half-open slot must be back, so the next trial is admitted rather than waiting forever.
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            (await sut.ExecuteAsync(_ => Task.FromResult(11), watchdog.Token)).Should().Be(11);
        }

        [Fact]
        public async Task MustNotDelayWhenAlreadyHalfOpen()
        {
            Open(CircuitBreakerState.HalfOpen);
            _store.Setup(s => s.IncrementSuccessCounterAsync()).ReturnsAsync(1);

            await CreateSut().ExecuteAsync(_ => Task.FromResult(1));

            _store.Verify(s => s.TryHalfOpenAsync(), Times.Once);
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

        // INVARIANT: reading State is the ONLY trigger. The getter captures the inner value, drives the
        // inner store through one full recovery (HalfOpen -> success -> Closed) exactly once, then returns
        // the CAPTURED — now stale — value. That is a deterministic stand-in for another caller recovering
        // the circuit across the breaker's unbounded await, with no thread race to lose. IsClosed must
        // delegate plainly: the stale read alone is the trigger.
        private sealed class RecoverOnStateReadStateStore : ICircuitBreakerStateStore
        {
            private readonly InMemoryCircuitBreakerStateStore _inner;
            private bool _hasRecovered;

            public RecoverOnStateReadStateStore(InMemoryCircuitBreakerStateStore inner)
                => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

            public CircuitBreakerState State
            {
                get
                {
                    var captured = _inner.State;
                    RecoverInnerOnce();
                    return captured;
                }
            }

            public Exception LastException => _inner.LastException;
            public DateTime LastStateChangedDateUtc => _inner.LastStateChangedDateUtc;
            public bool IsClosed => _inner.IsClosed;
            public int FailureCount => _inner.FailureCount;
            public int SuccessCount => _inner.SuccessCount;

            public Task OpenAsync(Exception ex) => _inner.OpenAsync(ex);
            public Task<int> IncrementFailureCounterAsync(Exception ex) => _inner.IncrementFailureCounterAsync(ex);
            public Task<int> IncrementSuccessCounterAsync() => _inner.IncrementSuccessCounterAsync();
            public Task CloseAsync() => _inner.CloseAsync();
            public Task HalfOpenAsync() => _inner.HalfOpenAsync();
            public Task<bool> TryHalfOpenAsync() => _inner.TryHalfOpenAsync();

            private void RecoverInnerOnce()
            {
                if (_hasRecovered)
                {
                    return;
                }

                _hasRecovered = true;
                _inner.HalfOpenAsync().GetAwaiter().GetResult();
                _inner.IncrementSuccessCounterAsync().GetAwaiter().GetResult();
                _inner.CloseAsync().GetAwaiter().GetResult();
            }
        }
    }
}
