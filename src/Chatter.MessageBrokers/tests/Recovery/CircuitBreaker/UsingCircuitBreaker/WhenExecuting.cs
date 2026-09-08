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

        private const long Episode = 7;

        private void Closed()
        {
            _store.SetupGet(s => s.IsClosed).Returns(true);
            Admit(CircuitBreakerVerdict.Execute, CircuitBreakerState.Closed, null);
        }

        // A test names the branch it wants ADMITTED, not the state it wants observed. There is deliberately no
        // State setup: the breaker must never read State to select a branch.
        private void Open(CircuitBreakerState state = CircuitBreakerState.Open, Exception lastException = null)
        {
            _store.SetupGet(s => s.IsClosed).Returns(false);
            Admit(state == CircuitBreakerState.HalfOpen ? CircuitBreakerVerdict.Trial : CircuitBreakerVerdict.Refused,
                  state,
                  lastException);
        }

        private void Admit(CircuitBreakerVerdict verdict, CircuitBreakerState state, Exception lastException)
            => _store.Setup(s => s.AdmitAsync(It.IsAny<TimeSpan>()))
                     .ReturnsAsync(new CircuitBreakerAdmission(verdict, state, Episode, lastException));

        // The store answers whether THIS report transitioned the circuit. An un-stubbed report answers false —
        // "no transition" — so a test that wants the reported success to close the circuit must say so.
        private void CloseOnSuccess()
            => _store.Setup(s => s.RecordSuccessAsync(It.IsAny<CircuitBreakerAdmission>(), It.IsAny<int>()))
                     .ReturnsAsync(true);

        // Arranges an open REAL store through the store's own adjudication: a failure reported under the
        // admission it just issued is the only way an open circuit comes about, there being no OpenAsync
        // command to arrange with.
        private static async Task DriveToOpenAsync(InMemoryCircuitBreakerStateStore store)
        {
            var admission = await store.AdmitAsync(TimeSpan.Zero);
            await store.RecordFailureAsync(admission, new FakeRecoverableException(), failuresToOpen: 1);
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
        public async Task MustSelectItsBranchFromTheIssuedAdmissionRatherThanFromAStateRead()
        {
            Open(CircuitBreakerState.HalfOpen);
            CloseOnSuccess();
            var admittedTo = CircuitBreakerState.Closed;

            (await CreateSut().ExecuteAsync(state =>
            {
                admittedTo = state;
                return Task.FromResult(11);
            })).Should().Be(11);

            admittedTo.Should().Be(CircuitBreakerState.HalfOpen);
            _store.VerifyGet(s => s.State, Times.Never);
            _store.VerifyGet(s => s.IsClosed, Times.Never);
        }

        // INVARIANT: an outcome is reported against the admission the store ISSUED — that exact verdict and
        // episode — together with the configured threshold. The store adjudicates from the admission, so a
        // breaker that substituted a re-read for it would hand the store an outcome belonging to no episode.
        [Fact]
        public async Task MustReportEveryOutcomeAgainstTheAdmissionItWasIssued()
        {
            Open(CircuitBreakerState.HalfOpen);
            _evaluator.Setup(e => e.ShouldTrip(It.IsAny<Exception>())).Returns(true);
            var sut = CreateSut();

            await sut.ExecuteAsync(_ => Task.FromResult(1));
            await FluentActions
                .Invoking(async () => await sut.ExecuteAsync<int>(_ => throw new FakeRecoverableException()))
                .Should().ThrowAsync<FakeRecoverableException>();

            _store.Verify(s => s.RecordSuccessAsync(
                It.Is<CircuitBreakerAdmission>(a => a.Verdict == CircuitBreakerVerdict.Trial && a.Episode == Episode),
                _options.NumberOfHalfOpenSuccessesToClose), Times.Once);
            _store.Verify(s => s.RecordFailureAsync(
                It.Is<CircuitBreakerAdmission>(a => a.Verdict == CircuitBreakerVerdict.Trial && a.Episode == Episode),
                It.IsAny<Exception>(),
                _options.NumberOfFailuresBeforeOpen), Times.Once);
        }

        [Fact]
        public async Task MustRethrowAndSkipTripWhenExceptionNotConfigured()
        {
            Closed();
            _evaluator.Setup(e => e.ShouldTrip(It.IsAny<Exception>())).Returns(false);

            await FluentActions
                .Invoking(async () => await CreateSut().ExecuteAsync<int>(_ => throw new FakeRecoverableException()))
                .Should().ThrowAsync<FakeRecoverableException>();

            _store.Verify(s => s.RecordFailureAsync(
                It.IsAny<CircuitBreakerAdmission>(), It.IsAny<Exception>(), It.IsAny<int>()), Times.Never);
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

        // Whether the reported failure OPENS the circuit is the store's decision, adjudicated against the
        // admission and the threshold this report carries — see
        // Recovery/CircuitBreaker/UsingInMemoryCircuitBreakerStateStore/WhenManagingState.cs::MustOpenTheCircuitWhenTheFailureThresholdIsReached,
        // MustNotOpenTheCircuitWhenTheFailureCountIsBelowTheThreshold and
        // MustCountEveryFailureReportedUnderTheExecuteAdmission. What is the BREAKER's is that a tripping
        // exception is reported at all.
        [Fact]
        public async Task MustReportTheFailureWhenTheExceptionTripsTheCircuit()
        {
            Closed();
            _evaluator.Setup(e => e.ShouldTrip(It.IsAny<Exception>())).Returns(true);

            await FluentActions
                .Invoking(async () => await CreateSut().ExecuteAsync<int>(_ => throw new FakeRecoverableException()))
                .Should().ThrowAsync<FakeRecoverableException>();

            _store.Verify(s => s.RecordFailureAsync(
                It.IsAny<CircuitBreakerAdmission>(), It.IsAny<Exception>(), It.IsAny<int>()), Times.Once);
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
            _store.Verify(s => s.AdmitAsync(It.IsAny<TimeSpan>()), Times.Once);
        }

        // INVARIANT: Refused is the DEFAULT CircuitBreakerVerdict, so an admission that was never issued denies
        // rather than admits. This test deliberately sets up NO AdmitAsync — the un-stubbed Moq
        // Task<CircuitBreakerAdmission> completes with default(CircuitBreakerAdmission), which is exactly the
        // "never issued" admission — and pins that the breaker refuses on it instead of silently taking a branch.
        [Fact]
        public async Task MustRefuseOnAnAdmissionThatWasNeverIssued()
        {
            default(CircuitBreakerAdmission).Verdict.Should().Be(CircuitBreakerVerdict.Refused);
            var wasExecuted = false;

            await FluentActions
                .Invoking(async () => await CreateSut().ExecuteAsync<int>(_ =>
                {
                    wasExecuted = true;
                    return Task.FromResult(11);
                }))
                .Should().ThrowAsync<CircuitBreakerOpenException>();

            wasExecuted.Should().BeFalse();
        }

        // INVARIANT: the branch dispatch is a POSITIVE ALLOWLIST — only Execute and Trial run the action, so a
        // verdict added later, or one an external store invents, refuses instead of executing. The store's two
        // report sites already allowlist the verdicts they accept, so a fail-OPEN dispatch would run an action
        // whose failure the store would then discard, hiding the fault from the circuit entirely.
        [Fact]
        public async Task MustRefuseAnAdmissionCarryingAVerdictOutsideTheContract()
        {
            Admit((CircuitBreakerVerdict)int.MaxValue, CircuitBreakerState.Closed, null);
            var wasExecuted = false;

            await FluentActions
                .Invoking(async () => await CreateSut().ExecuteAsync<int>(_ =>
                {
                    wasExecuted = true;
                    return Task.FromResult(11);
                }))
                .Should().ThrowAsync<CircuitBreakerOpenException>();

            wasExecuted.Should().BeFalse();
        }

        [Fact]
        public async Task MustCarryLastExceptionOnTheRefusalWhenOpen()
        {
            var lastException = new FakeRecoverableException();
            Open(lastException: lastException);

            var refusal = await FluentActions
                .Invoking(async () => await CreateSut().ExecuteAsync(_ => Task.FromResult(11)))
                .Should().ThrowAsync<CircuitBreakerOpenException>();

            refusal.Which.InnerException.Should().BeSameAs(lastException);
            _store.VerifyGet(s => s.LastException, Times.Never);
        }

        [Fact]
        public async Task MustNotAnnounceATrialWhenTheStoreRefusesAdmission()
        {
            Open();

            await FluentActions
                .Invoking(async () => await CreateSut().ExecuteAsync(_ => Task.FromResult(11)))
                .Should().ThrowAsync<CircuitBreakerOpenException>();

            _logger.VerifyWasCalled(LogLevel.Information,
                "Circuit Breaker admitted a HALF-OPEN trial.",
                Times.Never());
        }

        [Fact]
        public async Task MustAnnounceTheTrialWhenTheStoreAdmitsOne()
        {
            // INVERTED for #432: an admitted trial RUNS. The refusal that used to follow the store's own grant
            // was the defect, not the contract.
            Open(CircuitBreakerState.HalfOpen);
            CloseOnSuccess();

            (await CreateSut().ExecuteAsync(_ => Task.FromResult(11))).Should().Be(11);

            _logger.VerifyWasCalled(LogLevel.Information,
                "Circuit Breaker admitted a HALF-OPEN trial.",
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

            // The store adjudicates BEFORE the wait paces the refusal, so the admission is taken exactly once
            // and the cancelled wait is what surfaces.
            _store.Verify(s => s.AdmitAsync(It.IsAny<TimeSpan>()), Times.Once);
        }

        [Fact]
        public async Task MustTrialTheOpenCircuitOnTheCallThatFindsItsCoolingPeriodElapsed()
        {
            // INVERTED for #432: OpenToHalfOpenWaitTimeInSeconds is 0 here, so the store half-opens the circuit
            // and admits the trial as ONE decision instead of refusing a circuit it has just half-opened.
            var store = new InMemoryCircuitBreakerStateStore(
                New.Common().Logger<InMemoryCircuitBreakerStateStore>().Creation);
            await DriveToOpenAsync(store);
            var sut = new CircuitBreakerSut(store, _options, _logger.Creation, _evaluator.Object);

            (await sut.ExecuteAsync(_ => Task.FromResult(11))).Should().Be(11);

            store.IsClosed.Should().BeTrue();
        }

        [Fact]
        public async Task MustNotAdmitAnythingWhileTheOpenCircuitIsStillCooling()
        {
            var store = new InMemoryCircuitBreakerStateStore(
                New.Common().Logger<InMemoryCircuitBreakerStateStore>().Creation);
            await DriveToOpenAsync(store);
            _options.OpenToHalfOpenWaitTimeInSeconds = 30;
            var sut = new CircuitBreakerSut(store, _options, _logger.Creation, _evaluator.Object);
            var wasExecuted = false;
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

            // The refusal is paced by the cooling wait, so cancelling that wait is how the refusal is observed
            // without spending thirty seconds of wall clock. What is pinned is that nothing was admitted and the
            // circuit was NOT half-opened on the way.
            await FluentActions
                .Invoking(async () => await sut.ExecuteAsync(_ =>
                {
                    wasExecuted = true;
                    return Task.FromResult(11);
                }, cts.Token))
                .Should().ThrowAsync<OperationCanceledException>();

            wasExecuted.Should().BeFalse();
            store.State.Should().Be(CircuitBreakerState.Open);
        }

        [Fact]
        public async Task MustNotRegressTheCircuitWhenItRecoversDuringTheCoolingWait()
        {
            var inner = new InMemoryCircuitBreakerStateStore(
                New.Common().Logger<InMemoryCircuitBreakerStateStore>().Creation);
            await DriveToOpenAsync(inner);
            var sut = new CircuitBreakerSut(
                new RecoverOnAdmissionStateStore(inner, _options.NumberOfHalfOpenSuccessesToClose),
                _options, _logger.Creation, _evaluator.Object);

            // INVERTED for #432: the caller acts on the admission the store ISSUED, so a circuit that recovered
            // across that admission is executed against rather than refused. The two no-regression assertions
            // below are #433's and are unchanged.
            (await sut.ExecuteAsync(_ => Task.FromResult(11))).Should().Be(11);

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
            await DriveToOpenAsync(inner);
            await inner.AdmitAsync(TimeSpan.Zero);
            var sut = new CircuitBreakerSut(
                new RecoverOnAdmissionStateStore(inner, options.NumberOfHalfOpenSuccessesToClose),
                options, _logger.Creation, _evaluator.Object);

            (await sut.ExecuteAsync(_ => Task.FromResult(11))).Should().Be(11);

            inner.IsClosed.Should().BeTrue();
        }

        // Whether the reported success CLOSES the circuit is the store's decision — see
        // Recovery/CircuitBreaker/UsingInMemoryCircuitBreakerStateStore/WhenManagingState.cs::MustCloseTheCircuitWhenTheHalfOpenSuccessThresholdIsReached,
        // MustNotCloseTheCircuitWhenTheHalfOpenSuccessCountIsBelowTheThreshold and
        // MustNotCloseTheCircuitForASuccessReportedAgainstAnEndedEpisode. What stays observable at the BREAKER
        // is that the trial's own result reaches its caller even when the store discards the report.
        [Fact]
        public async Task MustReturnTheTrialsResultWhenTheStoreDiscardsItsSuccess()
        {
            Open(CircuitBreakerState.HalfOpen);
            // An un-stubbed report answers false: the store recorded no transition for this success.

            (await CreateSut().ExecuteAsync(_ => Task.FromResult(11))).Should().Be(11);

            _store.Verify(s => s.RecordSuccessAsync(
                It.Is<CircuitBreakerAdmission>(a => a.Episode == Episode), It.IsAny<int>()), Times.Once);
        }

        // The REOPEN half of this behaviour is the store's, adjudicated from the admission's Trial verdict —
        // see Recovery/CircuitBreaker/UsingInMemoryCircuitBreakerStateStore/WhenManagingState.cs::MustReopenTheCircuitOnASingleFailedTrialRegardlessOfTheFailureThreshold.
        // The RETHROW half is the breaker's and stays here.
        [Fact]
        public async Task MustReportTheFailureAndRethrowWhenTheHalfOpenActionFails()
        {
            Open(CircuitBreakerState.HalfOpen);
            _evaluator.Setup(e => e.ShouldTrip(It.IsAny<Exception>())).Returns(true);

            await FluentActions
                .Invoking(async () => await CreateSut().ExecuteAsync<int>(_ => throw new FakeRecoverableException()))
                .Should().ThrowAsync<FakeRecoverableException>();

            _store.Verify(s => s.RecordFailureAsync(
                It.IsAny<CircuitBreakerAdmission>(), It.IsAny<Exception>(), It.IsAny<int>()), Times.Once);
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

            _store.Verify(s => s.RecordFailureAsync(
                It.IsAny<CircuitBreakerAdmission>(), It.IsAny<Exception>(), It.IsAny<int>()), Times.Never);

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

            _store.Verify(s => s.RecordFailureAsync(
                It.IsAny<CircuitBreakerAdmission>(), It.IsAny<Exception>(), It.IsAny<int>()), Times.Never);
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

            _store.Verify(s => s.RecordFailureAsync(
                It.IsAny<CircuitBreakerAdmission>(), It.IsAny<Exception>(), It.IsAny<int>()), Times.Never);
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
            _options.OpenToHalfOpenWaitTimeInSeconds = 30;
            Open(CircuitBreakerState.HalfOpen);
            CloseOnSuccess();
            // A cooling wait on the trial path would outlive this watchdog by twenty-five seconds.
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            await CreateSut().ExecuteAsync(_ => Task.FromResult(1), watchdog.Token);

            _store.Verify(s => s.AdmitAsync(It.IsAny<TimeSpan>()), Times.Once);
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

        // INVARIANT: ISSUING AN ADMISSION is the ONLY trigger. AdmitAsync returns the admission the inner store
        // issued and THEN reports one success against that same admission, exactly once, so the caller holds an
        // admission whose episode the store may already have moved past. That is a deterministic stand-in for
        // another caller recovering the circuit across the breaker's unbounded await, with no thread race to
        // lose. Every other member delegates plainly: the overtaken admission alone is the trigger.
        //
        // successesToClose is a CONSTRUCTOR parameter because it selects WHICH overtaking a test arranges: at 1
        // the forced success closes the circuit and ENDS the episode, so the caller's own later report is
        // discarded; above 1 the forced success is progress WITHIN the episode, which the caller's report then
        // completes.
        private sealed class RecoverOnAdmissionStateStore : ICircuitBreakerStateStore
        {
            private readonly InMemoryCircuitBreakerStateStore _inner;
            private readonly int _successesToClose;
            private bool _hasRecovered;

            public RecoverOnAdmissionStateStore(InMemoryCircuitBreakerStateStore inner, int successesToClose)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                _successesToClose = successesToClose;
            }

            public async Task<CircuitBreakerAdmission> AdmitAsync(TimeSpan openToHalfOpenWaitTime)
            {
                var admission = await _inner.AdmitAsync(openToHalfOpenWaitTime);
                RecoverInnerOnce(admission);
                return admission;
            }

            public CircuitBreakerState State => _inner.State;
            public Exception LastException => _inner.LastException;
            public DateTime LastStateChangedDateUtc => _inner.LastStateChangedDateUtc;
            public bool IsClosed => _inner.IsClosed;
            public int FailureCount => _inner.FailureCount;
            public int SuccessCount => _inner.SuccessCount;

            public Task<bool> RecordSuccessAsync(CircuitBreakerAdmission admission, int successesToClose)
                => _inner.RecordSuccessAsync(admission, successesToClose);
            public Task<bool> RecordFailureAsync(CircuitBreakerAdmission admission, Exception ex, int failuresToOpen)
                => _inner.RecordFailureAsync(admission, ex, failuresToOpen);

            private void RecoverInnerOnce(CircuitBreakerAdmission admission)
            {
                if (_hasRecovered)
                {
                    return;
                }

                _hasRecovered = true;
                _inner.RecordSuccessAsync(admission, _successesToClose).GetAwaiter().GetResult();
            }
        }
    }
}
