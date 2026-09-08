using Chatter.MessageBrokers.Recovery.CircuitBreaker;
using Chatter.Testing.Core.Creators.Common;
using Chatter.Testing.Core.Creators.MessageBrokers.Recovery;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
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
        public async Task MustNotBeClosedAfterAFailureReportOpensTheCircuit()
        {
            await DriveToOpenAsync();
            _sut.IsClosed.Should().BeFalse();
        }

        [Fact]
        public async Task MustStoreTheLastExceptionWhenAFailureReportOpensTheCircuit()
        {
            var ex = new FakeRecoverableException("boom");
            await DriveToOpenAsync(ex);
            _sut.LastException.Should().BeSameAs(ex);
        }

        [Fact]
        public async Task MustUpdateLastStateChangedDateWhenAFailureReportOpensTheCircuit()
        {
            await DriveToOpenAsync();
            _sut.LastStateChangedDateUtc.Should().BeAfter(DateTime.MinValue);
        }

        // INVARIANT: the published wall-clock stamp keeps moving at EVERY transition even though no decision
        // reads it any more. Clearing it first is what gives these guards teeth: they fail if a transition site
        // ever stamps the monotonic measure alone and leaves the diagnostic behind.
        [Fact]
        public async Task MustUpdateLastStateChangedDateWhenAnAdmissionEntersHalfOpen()
        {
            await DriveToOpenAsync();
            ClearWallClockStamp();

            await _sut.AdmitAsync(TimeSpan.Zero, CancellationToken.None);

            _sut.LastStateChangedDateUtc.Should().BeAfter(DateTime.MinValue);
        }

        [Fact]
        public async Task MustUpdateLastStateChangedDateWhenASuccessReportClosesTheCircuit()
        {
            var trial = await DriveToTrialAsync();
            ClearWallClockStamp();

            await _sut.RecordSuccessAsync(trial, successesToClose: 1, CancellationToken.None);

            _sut.LastStateChangedDateUtc.Should().BeAfter(DateTime.MinValue);
        }

        [Fact]
        public async Task MustLogOpenTransition()
        {
            await DriveToOpenAsync();
            _logger.VerifyWasCalled(LogLevel.Information, "Circuit Breaker is now in the OPEN state.", Times.Once());
        }

        [Fact]
        public async Task MustResetTheFailureCountWhenASuccessReportClosesTheCircuit()
        {
            var trial = await DriveToTrialAsync();
            _sut.FailureCount.Should().Be(1);

            await _sut.RecordSuccessAsync(trial, successesToClose: 1, CancellationToken.None);

            _sut.FailureCount.Should().Be(0);
        }

        [Fact]
        public async Task MustLogClosedTransition()
        {
            var trial = await DriveToTrialAsync();
            await _sut.RecordSuccessAsync(trial, successesToClose: 1, CancellationToken.None);
            _logger.VerifyWasCalled(LogLevel.Information, "Circuit Breaker is now in the CLOSED state.", Times.Once());
        }

        [Fact]
        public async Task MustAdmitExecutionAndMutateNothingWhenTheCircuitIsClosed()
        {
            var first = await _sut.AdmitAsync(TimeSpan.Zero, CancellationToken.None);
            await _sut.RecordFailureAsync(first, new FakeRecoverableException(), failuresToOpen: 2, CancellationToken.None);
            var lastStateChanged = _sut.LastStateChangedDateUtc;

            var admission = await _sut.AdmitAsync(TimeSpan.Zero, CancellationToken.None);

            admission.Verdict.Should().Be(CircuitBreakerVerdict.Execute);
            admission.State.Should().Be(CircuitBreakerState.Closed);
            _sut.FailureCount.Should().Be(1);
            _sut.LastStateChangedDateUtc.Should().Be(lastStateChanged);
        }

        [Fact]
        public async Task MustRefuseAdmissionWhileTheOpenCircuitIsStillCooling()
        {
            await DriveToOpenAsync();
            var lastStateChanged = _sut.LastStateChangedDateUtc;

            var admission = await _sut.AdmitAsync(TimeSpan.FromMinutes(5), CancellationToken.None);

            admission.Verdict.Should().Be(CircuitBreakerVerdict.Refused);
            _sut.State.Should().Be(CircuitBreakerState.Open);
            _sut.LastStateChangedDateUtc.Should().Be(lastStateChanged);
        }

        [Fact]
        public async Task MustCarryTheLastExceptionOnARefusedAdmission()
        {
            var ex = new FakeRecoverableException("boom");
            await DriveToOpenAsync(ex);

            var admission = await _sut.AdmitAsync(TimeSpan.FromMinutes(5), CancellationToken.None);

            admission.LastException.Should().BeSameAs(ex);
        }

        // INVARIANT: the cooling period is measured from a MONOTONIC source, so a wall-clock correction — an
        // NTP step, a VM migration, an operator moving the clock — cannot change when an open circuit is
        // admitted to a trial. A BACKWARD correction is the dangerous direction under a wall-clock measure: it
        // makes an interval that has not passed look as though it has, and admits a trial early.
        [Fact]
        public async Task MustNotAdmitATrialEarlyWhenTheWallClockStampMovesBackward()
        {
            await DriveToOpenAsync();
            MoveWallClockStamp(TimeSpan.FromHours(-1));

            var admission = await _sut.AdmitAsync(TimeSpan.FromMinutes(30), CancellationToken.None);

            admission.Verdict.Should().Be(CircuitBreakerVerdict.Refused);
            _sut.State.Should().Be(CircuitBreakerState.Open);
        }

        // The other direction of the same defect: a FORWARD correction makes an elapsed interval look as though
        // it lies in the future, holding the circuit open past its configured wait — potentially far past it.
        [Fact]
        public async Task MustStillAdmitATrialWhenTheWallClockStampMovesForward()
        {
            await DriveToOpenAsync();
            MoveWallClockStamp(TimeSpan.FromHours(1));

            var admission = await _sut.AdmitAsync(TimeSpan.Zero, CancellationToken.None);

            admission.Verdict.Should().Be(CircuitBreakerVerdict.Trial);
            _sut.State.Should().Be(CircuitBreakerState.HalfOpen);
        }

        [Fact]
        public async Task MustAdmitATrialAndEnterHalfOpenWhenTheOpenCircuitHasFinishedCooling()
        {
            // The first episode carries a success, so the reset below is a real reset rather than an
            // already-zero count that would pass whatever the store did.
            var firstTrial = await DriveToTrialAsync();
            await _sut.RecordSuccessAsync(firstTrial, successesToClose: 2, CancellationToken.None);
            await _sut.RecordFailureAsync(firstTrial, new FakeRecoverableException(), failuresToOpen: 1, CancellationToken.None);
            var whileCooling = await _sut.AdmitAsync(TimeSpan.FromMinutes(5), CancellationToken.None);

            var admission = await _sut.AdmitAsync(TimeSpan.Zero, CancellationToken.None);

            admission.Verdict.Should().Be(CircuitBreakerVerdict.Trial);
            admission.State.Should().Be(CircuitBreakerState.HalfOpen);
            admission.Episode.Should().NotBe(whileCooling.Episode);
            _sut.State.Should().Be(CircuitBreakerState.HalfOpen);
            _sut.SuccessCount.Should().Be(0);
        }

        [Fact]
        public async Task MustAdmitATrialAndLeaveTheSuccessCountWhenTheCircuitIsAlreadyHalfOpen()
        {
            var entered = await DriveToTrialAsync();
            await _sut.RecordSuccessAsync(entered, successesToClose: 2, CancellationToken.None);

            var admission = await _sut.AdmitAsync(TimeSpan.Zero, CancellationToken.None);

            admission.Verdict.Should().Be(CircuitBreakerVerdict.Trial);
            admission.Episode.Should().Be(entered.Episode);
            _sut.SuccessCount.Should().Be(1);
        }

        [Fact]
        public async Task MustAnnounceTheHalfOpenTransitionOnceForTheAdmissionThatEntersIt()
        {
            await DriveToOpenAsync();

            await _sut.AdmitAsync(TimeSpan.Zero, CancellationToken.None);
            await _sut.AdmitAsync(TimeSpan.Zero, CancellationToken.None);

            _logger.VerifyWasCalled(LogLevel.Information, "Circuit Breaker is now in the HALF-OPEN state.", Times.Once());
        }

        [Fact]
        public async Task MustIssueANewEpisodeForEveryHalfOpenAdmission()
        {
            var first = await DriveToTrialAsync();
            await _sut.RecordFailureAsync(first, new FakeRecoverableException(), failuresToOpen: 1, CancellationToken.None);

            var second = await _sut.AdmitAsync(TimeSpan.Zero, CancellationToken.None);

            second.Episode.Should().NotBe(first.Episode);
        }

        // INVARIANT: every member of the contract carries the ambient cancellation of the call it adjudicates.
        // This store's bodies are synchronous under one lock, so it honours the token by refusing BEFORE taking
        // that lock — never inside it, and never by awaiting under it. The parameter is on the contract so an
        // external store doing real I/O can carry the caller's cancellation into that I/O.
        [Fact]
        public async Task MustRefuseToAdmitWithoutTouchingStateWhenTheTokenIsAlreadyCancelled()
        {
            await DriveToOpenAsync();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await FluentActions.Invoking(async () => await _sut.AdmitAsync(TimeSpan.Zero, cts.Token))
                .Should().ThrowAsync<OperationCanceledException>();

            _sut.State.Should().Be(CircuitBreakerState.Open);
        }

        [Fact]
        public async Task MustRecordNothingWhenASuccessIsReportedWithAnAlreadyCancelledToken()
        {
            var trial = await DriveToTrialAsync();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await FluentActions
                .Invoking(async () => await _sut.RecordSuccessAsync(trial, successesToClose: 1, cts.Token))
                .Should().ThrowAsync<OperationCanceledException>();

            _sut.State.Should().Be(CircuitBreakerState.HalfOpen);
            _sut.SuccessCount.Should().Be(0);
        }

        [Fact]
        public async Task MustRecordNothingWhenAFailureIsReportedWithAnAlreadyCancelledToken()
        {
            var execute = await _sut.AdmitAsync(TimeSpan.Zero, CancellationToken.None);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await FluentActions
                .Invoking(async () => await _sut.RecordFailureAsync(
                    execute, new FakeRecoverableException(), failuresToOpen: 1, cts.Token))
                .Should().ThrowAsync<OperationCanceledException>();

            _sut.State.Should().Be(CircuitBreakerState.Closed);
            _sut.FailureCount.Should().Be(0);
            _sut.LastException.Should().BeNull();
        }

        [Fact]
        public void MustNotExposeAnUnconditionalHalfOpenCommand()
        {
            typeof(ICircuitBreakerStateStore).GetMethod("HalfOpenAsync").Should().BeNull();
            typeof(InMemoryCircuitBreakerStateStore).GetMethod("HalfOpenAsync").Should().BeNull();
        }

        // INVARIANT: the store adjudicates every transition, so there is no transition COMMAND for a caller to
        // issue against it. Removing the capability is what closes the stale-mutation class — an added episode
        // check at each call site would only enumerate the cases someone remembered.
        // GetMethods().Where is used rather than GetMethod(name), which throws AmbiguousMatchException the
        // moment a name is overloaded and so would fail for the wrong reason.
        [Fact]
        public void MustNotExposeATransitionCommand()
        {
            var commands = new[]
            {
                "OpenAsync", "CloseAsync", "IncrementFailureCounterAsync", "IncrementSuccessCounterAsync"
            };

            foreach (var command in commands)
            {
                typeof(ICircuitBreakerStateStore).GetMethods().Where(m => m.Name == command)
                    .Should().BeEmpty(because: $"'{command}' must not be declared on the contract");
                typeof(InMemoryCircuitBreakerStateStore).GetMethods().Where(m => m.Name == command)
                    .Should().BeEmpty(because: $"'{command}' must not be declared on the store");
            }
        }

        [Fact]
        public async Task MustDiscardASuccessWhoseEpisodeHasEnded()
        {
            var trial = await DriveToTrialAsync();
            await _sut.RecordSuccessAsync(trial, successesToClose: 2, CancellationToken.None);
            await _sut.RecordFailureAsync(trial, new FakeRecoverableException(), failuresToOpen: 1, CancellationToken.None);

            (await _sut.RecordSuccessAsync(trial, successesToClose: 2, CancellationToken.None)).Should().BeFalse();

            _sut.SuccessCount.Should().Be(1);
        }

        [Fact]
        public async Task MustStoreTheLastExceptionForAFailureThatDoesNotOpenTheCircuit()
        {
            var ex = new FakeRecoverableException("failure");
            var execute = await _sut.AdmitAsync(TimeSpan.Zero, CancellationToken.None);

            await _sut.RecordFailureAsync(execute, ex, failuresToOpen: 2, CancellationToken.None);

            _sut.State.Should().Be(CircuitBreakerState.Closed);
            _sut.LastException.Should().BeSameAs(ex);
        }

        // Arranges an open circuit through the store's OWN adjudication rather than a transition command,
        // because there is no command to arrange with: a failure reported under the admission the store just
        // issued is the only way an open circuit comes about. The admission returned is the one the report
        // ENDED, so a test can report against an episode the store has already moved past.
        private async Task<CircuitBreakerAdmission> DriveToOpenAsync(Exception ex = null)
        {
            var admission = await _sut.AdmitAsync(TimeSpan.Zero, CancellationToken.None);
            await _sut.RecordFailureAsync(admission, ex ?? new FakeRecoverableException(), failuresToOpen: 1, CancellationToken.None);
            return admission;
        }

        private async Task<CircuitBreakerAdmission> DriveToTrialAsync(Exception ex = null)
        {
            await DriveToOpenAsync(ex);
            return await _sut.AdmitAsync(TimeSpan.Zero, CancellationToken.None);
        }

        // The wall-clock stamp the store publishes has no setter — it is a diagnostic, never an input — so a
        // clock correction is simulated by moving the field behind it. Nothing the store DECIDES may move with
        // it; that is exactly what the two tests above pin.
        private static FieldInfo WallClockStampField()
        {
            var field = typeof(InMemoryCircuitBreakerStateStore)
                .GetField("_lastStateChangedDateUtc", BindingFlags.Instance | BindingFlags.NonPublic);

            field.Should().NotBeNull(because: "the store keeps the published wall-clock stamp in this field");
            return field;
        }

        private void MoveWallClockStamp(TimeSpan by)
        {
            var field = WallClockStampField();
            field.SetValue(_sut, ((DateTime)field.GetValue(_sut)).Add(by));
        }

        private void ClearWallClockStamp() => WallClockStampField().SetValue(_sut, DateTime.MinValue);

        [Fact]
        public async Task MustCountEverySuccessReportedUnderTheTrialAdmission()
        {
            var trial = await DriveToTrialAsync();

            await _sut.RecordSuccessAsync(trial, successesToClose: 3, CancellationToken.None);
            await _sut.RecordSuccessAsync(trial, successesToClose: 3, CancellationToken.None);

            _sut.SuccessCount.Should().Be(2);
        }

        [Fact]
        public async Task MustCloseTheCircuitWhenTheHalfOpenSuccessThresholdIsReached()
        {
            var trial = await DriveToTrialAsync();

            (await _sut.RecordSuccessAsync(trial, successesToClose: 1, CancellationToken.None)).Should().BeTrue();

            _sut.State.Should().Be(CircuitBreakerState.Closed);
        }

        [Fact]
        public async Task MustNotCloseTheCircuitWhenTheHalfOpenSuccessCountIsBelowTheThreshold()
        {
            var trial = await DriveToTrialAsync();

            (await _sut.RecordSuccessAsync(trial, successesToClose: 2, CancellationToken.None)).Should().BeFalse();

            _sut.State.Should().Be(CircuitBreakerState.HalfOpen);
        }

        // INVARIANT (finding 1): validating the episode and closing the circuit are ONE decision taken under one
        // lock. A success whose episode ended while it was in flight can never close the circuit another caller
        // has just tripped.
        [Fact]
        public async Task MustNotCloseTheCircuitForASuccessReportedAgainstAnEndedEpisode()
        {
            var trial = await DriveToTrialAsync();
            await _sut.RecordFailureAsync(trial, new FakeRecoverableException(), failuresToOpen: 1, CancellationToken.None);

            (await _sut.RecordSuccessAsync(trial, successesToClose: 1, CancellationToken.None)).Should().BeFalse();

            _sut.State.Should().Be(CircuitBreakerState.Open);
        }

        // INVARIANT: only a TRIAL admission may report a success. An admission that was never issued carries
        // Episode 0, which a fresh store also carries, so the episode alone would let it mutate the store.
        [Fact]
        public async Task MustChangeNothingForAnOutcomeReportedAgainstAnAdmissionThatWasNeverIssued()
        {
            var neverIssued = default(CircuitBreakerAdmission);

            (await _sut.RecordSuccessAsync(neverIssued, successesToClose: 1, CancellationToken.None)).Should().BeFalse();
            (await _sut.RecordFailureAsync(neverIssued, new FakeRecoverableException(), failuresToOpen: 1, CancellationToken.None))
                .Should().BeFalse();

            _sut.State.Should().Be(CircuitBreakerState.Closed);
            _sut.SuccessCount.Should().Be(0);
            _sut.FailureCount.Should().Be(0);
            _sut.LastException.Should().BeNull();
        }

        [Fact]
        public async Task MustOpenTheCircuitWhenTheFailureThresholdIsReached()
        {
            var execute = await _sut.AdmitAsync(TimeSpan.Zero, CancellationToken.None);

            (await _sut.RecordFailureAsync(execute, new FakeRecoverableException(), failuresToOpen: 1, CancellationToken.None))
                .Should().BeTrue();

            _sut.State.Should().Be(CircuitBreakerState.Open);
        }

        [Fact]
        public async Task MustNotOpenTheCircuitWhenTheFailureCountIsBelowTheThreshold()
        {
            var execute = await _sut.AdmitAsync(TimeSpan.Zero, CancellationToken.None);

            (await _sut.RecordFailureAsync(execute, new FakeRecoverableException(), failuresToOpen: 2, CancellationToken.None))
                .Should().BeFalse();

            _sut.State.Should().Be(CircuitBreakerState.Closed);
        }

        [Fact]
        public async Task MustCountEveryFailureReportedUnderTheExecuteAdmission()
        {
            var execute = await _sut.AdmitAsync(TimeSpan.Zero, CancellationToken.None);

            await _sut.RecordFailureAsync(execute, new FakeRecoverableException(), failuresToOpen: 3, CancellationToken.None);
            await _sut.RecordFailureAsync(execute, new FakeRecoverableException(), failuresToOpen: 3, CancellationToken.None);

            _sut.FailureCount.Should().Be(2);
        }

        // INVARIANT: the returned bool answers "did THIS report transition the circuit", which is the whole
        // signal a caller needs — a success can only ever CLOSE and a failure can only ever OPEN.
        [Fact]
        public async Task MustReportOnlyTheCallThatTransitionedTheCircuit()
        {
            var execute = await _sut.AdmitAsync(TimeSpan.Zero, CancellationToken.None);
            (await _sut.RecordFailureAsync(execute, new FakeRecoverableException(), failuresToOpen: 2, CancellationToken.None))
                .Should().BeFalse();
            (await _sut.RecordFailureAsync(execute, new FakeRecoverableException(), failuresToOpen: 2, CancellationToken.None))
                .Should().BeTrue();

            var trial = await _sut.AdmitAsync(TimeSpan.Zero, CancellationToken.None);
            (await _sut.RecordSuccessAsync(trial, successesToClose: 2, CancellationToken.None)).Should().BeFalse();
            (await _sut.RecordSuccessAsync(trial, successesToClose: 2, CancellationToken.None)).Should().BeTrue();
        }

        // INVARIANT (finding 2): a trial overtaken on the breaker's half-open semaphore reports its failure
        // against an episode the store has already closed. That report can never re-open a circuit a later
        // episode healed.
        [Fact]
        public async Task MustNotOpenTheCircuitForAFailureReportedAgainstAnEndedEpisode()
        {
            var trial = await DriveToTrialAsync();
            await _sut.RecordSuccessAsync(trial, successesToClose: 1, CancellationToken.None);

            (await _sut.RecordFailureAsync(trial, new FakeRecoverableException(), failuresToOpen: 1, CancellationToken.None))
                .Should().BeFalse();

            _sut.State.Should().Be(CircuitBreakerState.Closed);
        }

        [Fact]
        public async Task MustNotRecordTheLastExceptionForAFailureReportedAgainstAnEndedEpisode()
        {
            var opening = new FakeRecoverableException("opened the circuit");
            var trial = await DriveToTrialAsync(opening);
            await _sut.RecordSuccessAsync(trial, successesToClose: 1, CancellationToken.None);

            await _sut.RecordFailureAsync(trial, new FakeRecoverableException("belongs to no episode"), failuresToOpen: 1, CancellationToken.None);

            _sut.LastException.Should().BeSameAs(opening);
        }

        // INVARIANT: a failed TRIAL re-opens the circuit immediately and never counts toward failuresToOpen.
        // The trial IS the probe, so one failure is the whole evidence.
        [Fact]
        public async Task MustReopenTheCircuitOnASingleFailedTrialRegardlessOfTheFailureThreshold()
        {
            var trial = await DriveToTrialAsync();
            var failuresBeforeTheTrial = _sut.FailureCount;

            (await _sut.RecordFailureAsync(trial, new FakeRecoverableException(), failuresToOpen: int.MaxValue, CancellationToken.None))
                .Should().BeTrue();

            _sut.State.Should().Be(CircuitBreakerState.Open);
            _sut.FailureCount.Should().Be(failuresBeforeTheTrial);
        }
    }
}
