using Chatter.MessageBrokers.Reliability.Cosmos.Diagnostics;
using Chatter.MessageBrokers.Reliability.Cosmos.Tests.Diagnostics;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingOutboxDrainGate
{
    /// <summary>
    /// The Drain Suspension the Outbox Relay raises when one Lease Token's confirmations keep failing (#416): the
    /// publish succeeds, the delivered stamp does not, the batch is never checkpointed, and the relay republishes the
    /// same document forever at real broker, receiver and request-unit cost. The gate is what makes the relay decline
    /// to publish that lease again for a bounded window.
    /// </summary>
    [Collection(DiagnosticsCollection.Name)]
    public class WhenConfirmationsFailRepeatedly
    {
        /// <summary>
        /// The Change-Feed Source Identity the gate under test belongs to — the processor name a host constructs it
        /// with, and the identity its suspensions are reported under.
        /// </summary>
        private const string SourceIdentity = "chatter-cosmos-outbox-relay:source-under-test";

        private static readonly Exception _confirmationFault = new InvalidOperationException("the delivered stamp failed");

        /// <summary>A lease the gate has never seen drains, which is every lease on a healthy host.</summary>
        [Fact]
        public void MustPermitDrainingALeaseItHasNeverSeen()
        {
            var gate = new OutboxDrainGate(SourceIdentity, new GuardedRelayLog(logger: null), new AdvanceableTimeProvider());

            gate.PermitDrain("lease-7").Should().BeTrue();
        }

        /// <summary>
        /// Confirmation Failures below the threshold keep draining. A transient confirmation fault is the ordinary
        /// case the at-least-once relay already handles by re-surfacing the batch; suspending on the first one would
        /// stop delivery the relay was about to complete.
        /// </summary>
        [Fact]
        public void MustPermitDrainingBelowTheThreshold()
        {
            var gate = new OutboxDrainGate(SourceIdentity, new GuardedRelayLog(logger: null), new AdvanceableTimeProvider());

            FailConfirmations(gate, "lease-7", OutboxDrainGate.Threshold - 1);

            gate.PermitDrain("lease-7").Should().BeTrue();
        }

        /// <summary>
        /// The threshold Confirmation Failure raises the Drain Suspension: the relay declines to publish that lease
        /// again. It halts nothing — no processor and no hosted service is stopped — because the give-up decision
        /// cannot be recorded in a store that is not accepting the very write that keeps failing.
        /// </summary>
        [Fact]
        public void MustRefuseDrainingOnceTheThresholdIsReached()
        {
            var gate = new OutboxDrainGate(SourceIdentity, new GuardedRelayLog(logger: null), new AdvanceableTimeProvider());

            FailConfirmations(gate, "lease-7", OutboxDrainGate.Threshold);

            gate.PermitDrain("lease-7").Should().BeFalse();
        }

        /// <summary>
        /// The suspension is reported against the Lease Token it was raised for, which is what keeps a suspended
        /// lease distinguishable from an idle one that simply has nothing pending.
        /// </summary>
        [Fact]
        public void MustCountTheDrainSuspensionAgainstItsLease()
        {
            var gate = new OutboxDrainGate(SourceIdentity, new GuardedRelayLog(logger: null), new AdvanceableTimeProvider());

            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                FailConfirmations(gate, "lease-7", OutboxDrainGate.Threshold);

                var measurement = meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainSuspensionsInstrumentName).Should().ContainSingle().Subject;

                measurement.Value.Should().Be(1);
                measurement.TryGetTag(CosmosReliabilityDiagnostics.LeaseToken, out var leaseToken).Should().BeTrue();
                leaseToken.Should().Be("lease-7");
            }
        }

        /// <summary>
        /// The suspension is reported at <see cref="LogLevel.Error"/> ALWAYS-ON, carrying the confirmation fault that
        /// raised it. A meter-less application has no other channel, and a relay that quietly stopped publishing a
        /// lease would be indistinguishable from one with nothing to publish.
        /// </summary>
        [Fact]
        public void MustReportTheDrainSuspensionAtErrorWithTheConfirmationFault()
        {
            var logger = new RecordingLogger();
            var gate = new OutboxDrainGate(SourceIdentity, new GuardedRelayLog(logger), new AdvanceableTimeProvider());

            FailConfirmations(gate, "lease-7", OutboxDrainGate.Threshold);

            var entry = logger.Entries.Should().ContainSingle().Subject;
            entry.Level.Should().Be(LogLevel.Error);
            entry.Message.Should().Contain("lease-7").And.Contain(OutboxDrainGate.Threshold.ToString());
            entry.Exception.Should().BeSameAs(_confirmationFault);
        }

        /// <summary>
        /// Once the window elapses EXACTLY ONE batch is let through, as the probe that discovers whether the
        /// confirmation path came back. A second batch is refused, because nothing has yet confirmed that it did.
        /// </summary>
        [Fact]
        public void MustPermitExactlyOneProbeBatchOnceTheWindowElapses()
        {
            var timeProvider = new AdvanceableTimeProvider();
            var gate = new OutboxDrainGate(SourceIdentity, new GuardedRelayLog(logger: null), timeProvider);

            FailConfirmations(gate, "lease-7", OutboxDrainGate.Threshold);
            timeProvider.Advance(OutboxDrainGate.SuspensionWindow);

            gate.PermitDrain("lease-7").Should().BeTrue();
            gate.PermitDrain("lease-7").Should().BeFalse("only the probe batch is let through, and it has not confirmed anything yet");
        }

        /// <summary>
        /// A probe whose confirmation fails again re-arms the window rather than reopening the suspension: the relay
        /// waits another full window before probing again instead of republishing on every pass.
        /// </summary>
        [Fact]
        public void MustRearmTheWindowWhenAConfirmationFailsWhileAlreadySuspended()
        {
            var timeProvider = new AdvanceableTimeProvider();
            var gate = new OutboxDrainGate(SourceIdentity, new GuardedRelayLog(logger: null), timeProvider);

            FailConfirmations(gate, "lease-7", OutboxDrainGate.Threshold);
            timeProvider.Advance(OutboxDrainGate.SuspensionWindow);
            gate.PermitDrain("lease-7");
            gate.RecordConfirmationFailure("lease-7", _confirmationFault);
            timeProvider.Advance(OutboxDrainGate.SuspensionWindow - TimeSpan.FromSeconds(1));

            gate.PermitDrain("lease-7").Should().BeFalse();
        }

        /// <summary>A confirmation that succeeds is the evidence the probe was looking for: draining resumes at once.</summary>
        [Fact]
        public void MustResumeDrainingWhenAConfirmationSucceeds()
        {
            var gate = new OutboxDrainGate(SourceIdentity, new GuardedRelayLog(logger: null), new AdvanceableTimeProvider());

            FailConfirmations(gate, "lease-7", OutboxDrainGate.Threshold);
            gate.RecordConfirmationSuccess("lease-7");

            gate.PermitDrain("lease-7").Should().BeTrue();
        }

        /// <summary>
        /// The resumption is reported at <see cref="LogLevel.Information"/> and carries no exception: it is a
        /// deliberate decision the relay took, not a fault it suffered. It closes the Error that opened the
        /// suspension, which an operator would otherwise have no way to see the end of.
        /// </summary>
        [Fact]
        public void MustReportTheResumedDrainAtInformation()
        {
            var logger = new RecordingLogger();
            var gate = new OutboxDrainGate(SourceIdentity, new GuardedRelayLog(logger), new AdvanceableTimeProvider());

            FailConfirmations(gate, "lease-7", OutboxDrainGate.Threshold);
            gate.RecordConfirmationSuccess("lease-7");

            var entry = logger.Entries.Should().HaveCount(2).And.Subject.Last();
            entry.Level.Should().Be(LogLevel.Information);
            entry.Message.Should().Contain("lease-7");
            entry.Exception.Should().BeNull();
        }

        /// <summary>
        /// A healthy lease confirms on every batch it drains, so a resumption report on each one would drown the
        /// suspension report it exists to close. Only a lease that WAS suspended reports resuming.
        /// </summary>
        [Fact]
        public void MustReportNothingWhenALeaseThatWasNeverSuspendedConfirms()
        {
            var logger = new RecordingLogger();
            var gate = new OutboxDrainGate(SourceIdentity, new GuardedRelayLog(logger), new AdvanceableTimeProvider());

            FailConfirmations(gate, "lease-7", OutboxDrainGate.Threshold - 1);
            gate.RecordConfirmationSuccess("lease-7");
            gate.RecordConfirmationSuccess("lease-7");

            logger.Entries.Should().BeEmpty();
        }

        /// <summary>
        /// Only CONSECUTIVE Confirmation Failures count. The success EVICTS the lease's entry outright, so the next
        /// failure starts a fresh count rather than resuming a stale one — and the map holds entries only for leases
        /// currently failing, which is what bounds it without a capacity that could fill and stop counting.
        /// </summary>
        [Fact]
        public void MustCountFromZeroAgainAfterASuccessfulConfirmation()
        {
            var gate = new OutboxDrainGate(SourceIdentity, new GuardedRelayLog(logger: null), new AdvanceableTimeProvider());

            FailConfirmations(gate, "lease-7", OutboxDrainGate.Threshold - 1);
            gate.RecordConfirmationSuccess("lease-7");
            FailConfirmations(gate, "lease-7", OutboxDrainGate.Threshold - 1);

            gate.PermitDrain("lease-7").Should().BeTrue();
        }

        /// <summary>
        /// The suspension is per-LEASE. One lease whose confirmations fail says nothing about another's: the change
        /// feed delivers distinct leases concurrently on one host, and suspending them together would stop draining
        /// partitions that are perfectly healthy.
        /// </summary>
        [Fact]
        public void MustSuspendOnlyTheLeaseWhoseConfirmationsFailed()
        {
            var gate = new OutboxDrainGate(SourceIdentity, new GuardedRelayLog(logger: null), new AdvanceableTimeProvider());

            FailConfirmations(gate, "lease-7", OutboxDrainGate.Threshold);

            gate.PermitDrain("lease-9").Should().BeTrue();
        }

        /// <summary>
        /// The carrier the relay raises at its one post-publish confirmation site holds the underlying fault as its
        /// INNER exception, which is what lets the host rethrow that inner fault and keep the <c>error.type</c> on the
        /// shipped drain-failure count byte-identical to what it reported before the gate existed.
        /// </summary>
        [Fact]
        public void MustCarryTheConfirmationFaultAsTheInnerException()
        {
            var confirmationFailed = new OutboxConfirmationFailedException(_confirmationFault);

            confirmationFailed.InnerException.Should().BeSameAs(_confirmationFault);
        }

        /// <summary>
        /// A carrier with nothing to carry is unrepresentable: the host rethrows the inner fault, so a null one would
        /// leave it with no failure to report and would surface this internal carrier where the original belonged.
        /// </summary>
        [Fact]
        public void MustRejectACarrierWithNoConfirmationFault()
        {
            Action constructing = () => new OutboxConfirmationFailedException(confirmationFailure: null);

            constructing.Should().Throw<ArgumentNullException>();
        }

        /// <summary>
        /// The suspension the gate raises is reported under the Change-Feed Source Identity the gate was CONSTRUCTED
        /// with, so a Lease Token named "0" — a partition-key-range id every monitored container has — stays
        /// attributable to the source that suspended it.
        /// </summary>
        [Fact]
        public void MustReportTheSuspensionUnderTheSourceIdentityItWasBuiltWith()
        {
            var gate = new OutboxDrainGate(SourceIdentity, new GuardedRelayLog(logger: null), new AdvanceableTimeProvider());

            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                FailConfirmations(gate, "0", OutboxDrainGate.Threshold);

                var measurement = meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainSuspensionsInstrumentName).Should().ContainSingle().Subject;

                measurement.TryGetTag(CosmosReliabilityDiagnostics.SourceIdentity, out var sourceIdentity).Should().BeTrue();
                sourceIdentity.Should().Be(SourceIdentity);
                measurement.TryGetTag(CosmosReliabilityDiagnostics.LeaseToken, out var leaseToken).Should().BeTrue();
                leaseToken.Should().Be("0");
            }
        }

        /// <summary>
        /// A gate with no Change-Feed Source Identity is UNCONSTRUCTIBLE. There is no identity-less overload, and a
        /// null one is refused, because such a gate could still reach <c>RecordDrainSuspension</c> — publishing a
        /// suspension an operator could not attribute to any source, which is the whole defect this closes.
        /// </summary>
        [Fact]
        public void MustRefuseAGateWithNoSourceIdentity()
        {
            Action constructing = () => new OutboxDrainGate(sourceIdentity: null, new GuardedRelayLog(logger: null), new AdvanceableTimeProvider());

            constructing.Should().Throw<ArgumentNullException>();
        }

        private static void FailConfirmations(OutboxDrainGate gate, string leaseToken, int failureCount)
        {
            for (int failure = 0; failure < failureCount; failure++)
            {
                gate.RecordConfirmationFailure(leaseToken, _confirmationFault);
            }
        }

        /// <summary>
        /// A <see cref="TimeProvider"/> whose monotonic timestamp only moves when a test moves it, so the suspension
        /// window is exercised with no wall-clock sleep and no hand-rolled clock interface.
        /// </summary>
        private sealed class AdvanceableTimeProvider : TimeProvider
        {
            private long _timestamp;

            public override long TimestampFrequency => TimeSpan.TicksPerSecond;

            public override long GetTimestamp() => _timestamp;

            public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
        }

        /// <summary>An <see cref="ILogger"/> recorder that captures the level, rendered message and exception of each log call.</summary>
        private sealed class RecordingLogger : ILogger
        {
            public List<(LogLevel Level, string Message, Exception Exception)> Entries { get; } = new List<(LogLevel, string, Exception)>();

            public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
                => Entries.Add((logLevel, formatter(state, exception), exception));

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new NullScope();

                public void Dispose()
                {
                }
            }
        }
    }
}
