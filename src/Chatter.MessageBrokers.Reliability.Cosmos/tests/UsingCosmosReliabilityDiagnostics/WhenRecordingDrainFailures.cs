using Chatter.CQRS.Diagnostics;
using Chatter.MessageBrokers.Reliability.Cosmos.Diagnostics;
using Chatter.MessageBrokers.Reliability.Cosmos.Tests.Diagnostics;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using System;
using System.Diagnostics.Metrics;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingCosmosReliabilityDiagnostics
{
    /// <summary>
    /// The on state of this module's drain-durability instruments: what an application that opted into the
    /// <see cref="CosmosReliabilityDiagnostics.MeterName"/> scope receives when a drain attempt faults and when a
    /// document is stamped poisoned.
    /// </summary>
    [Collection(DiagnosticsCollection.Name)]
    public class WhenRecordingDrainFailures : Testing.Core.Context
    {
        // Built ONCE, at class-initialisation time, so the guard-cost probe below measures the record methods and
        // not the allocation of the failure handed to them.
        private static readonly DrainFailureProbeException _drainProbeFailure = new DrainFailureProbeException("the publish faulted");

        /// <summary>
        /// A faulted drain attempt is an ATTEMPT, not a <see cref="CosmosReliabilityDiagnostics.DrainOutcomes"/>
        /// value: the drained-document count already resolved the document, so the failure is a separate
        /// instrument and never a fourth outcome that would count the same document twice.
        /// </summary>
        [Fact]
        public void MustCountOneFailedDrainAttemptAgainstItsLeaseAndErrorType()
        {
            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                CosmosReliabilityDiagnostics.RecordDrainFailure("lease-7", _drainProbeFailure);

                var measurement = meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainFailuresInstrumentName).Should().ContainSingle().Subject;

                measurement.Value.Should().Be(1);
                measurement.TryGetTag(CosmosReliabilityDiagnostics.LeaseToken, out var leaseToken).Should().BeTrue();
                leaseToken.Should().Be("lease-7");
                measurement.TryGetTag(ChatterTelemetryTags.ErrorType, out var errorType).Should().BeTrue();
                errorType.Should().Be(typeof(DrainFailureProbeException).FullName);
            }
        }

        /// <summary>
        /// A poisoned document is counted against the lease it kept faulting under, so a partition whose progress
        /// is being bought by abandoning documents is readable against the same dimension as its batch progress.
        /// </summary>
        [Fact]
        public void MustCountOnePoisonedDocumentAgainstItsLease()
        {
            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                CosmosReliabilityDiagnostics.RecordPoisonedDocument("lease-7");

                var measurement = meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.PoisonedDocumentsInstrumentName).Should().ContainSingle().Subject;

                measurement.Value.Should().Be(1);
                measurement.TryGetTag(CosmosReliabilityDiagnostics.LeaseToken, out var leaseToken).Should().BeTrue();
                leaseToken.Should().Be("lease-7");
            }
        }

        /// <summary>
        /// Enabling ONE of the new instruments is a full opt-in: <see cref="CosmosReliabilityDiagnostics.IsEnabled"/>
        /// is the OR across this module's whole surface, so a call site that checks it before doing instrumented
        /// work still takes the instrumented path for an application that subscribed to nothing else.
        /// </summary>
        [Theory]
        [InlineData(CosmosReliabilityDiagnostics.DrainFailuresInstrumentName)]
        [InlineData(CosmosReliabilityDiagnostics.PoisonedDocumentsInstrumentName)]
        [InlineData(CosmosReliabilityDiagnostics.UnconfirmedGiveUpsInstrumentName)]
        public void MustReportDiagnosticsEnabledForASingleInstrumentOptIn(string instrumentName)
        {
            DeclareInstruments();

            using (new SingleInstrumentMeterScope(CosmosReliabilityDiagnostics.MeterName, instrumentName))
            {
                CosmosReliabilityDiagnostics.IsEnabled.Should().BeTrue();
            }
        }

        /// <summary>
        /// The units the two instruments report in: a faulted attempt is counted in failures, an abandoned document
        /// in documents, so neither is aggregated as if it were the other.
        /// </summary>
        [Fact]
        public void MustDeclareTheDrainDurabilityInstrumentsOnTheModuleMeter()
        {
            DeclareInstruments();

            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                meterScope.TryGetInstrument(CosmosReliabilityDiagnostics.DrainFailuresInstrumentName, out var drainFailures).Should().BeTrue();
                drainFailures.Unit.Should().Be("{failure}");

                meterScope.TryGetInstrument(CosmosReliabilityDiagnostics.PoisonedDocumentsInstrumentName, out var poisonedDocuments).Should().BeTrue();
                poisonedDocuments.Unit.Should().Be("{document}");
            }
        }

        /// <summary>
        /// The off state: an application that subscribed to nothing pays one boolean read per record method and
        /// builds no lease token, no error type and no <c>TagList</c> (ADR-0010 R1).
        /// </summary>
        [Fact]
        public void MustBuildNoTagWhileNothingIsOptedInto()
        {
            CosmosReliabilityDiagnostics.IsEnabled.Should().BeFalse();

            var measurement = GuardCostProbe.Measure(RecordOneFailedDrainAndOnePoisonedDocument);

            measurement.MedianAllocatedBytesPerBatch.Should().Be(0, "no attribute may be built while off: " + measurement);
        }

        /// <summary>
        /// A .NET <c>ActivityListener</c> on this module's scope and NO .NET <c>MeterListener</c> makes
        /// <see cref="CosmosReliabilityDiagnostics.IsEnabled"/> TRUE, which is precisely why neither record method
        /// may guard on it: each guards on its OWN instrument, so this application pays a boolean read and nothing
        /// else — no error type is resolved and no attribute is built for a metric nobody subscribed to
        /// (ADR-0010 R1, R2).
        /// </summary>
        [Fact]
        public void MustRecordNothingForATracingOnlyOptIn()
        {
            using (var activityScope = new RecordingActivityScope(CosmosReliabilityDiagnostics.ActivitySourceName))
            {
                CosmosReliabilityDiagnostics.IsEnabled.Should().BeTrue();

                var measurement = GuardCostProbe.Measure(RecordOneFailedDrainAndOnePoisonedDocument);

                measurement.MedianAllocatedBytesPerBatch.Should().Be(0, "no attribute may be built for an instrument nobody enabled: " + measurement);
                activityScope.StartedActivities.Should().BeEmpty();
            }
        }

        /// <summary>Drives both record methods once, as the Outbox Relay does for a document it gives up on.</summary>
        private static void RecordOneFailedDrainAndOnePoisonedDocument()
        {
            CosmosReliabilityDiagnostics.RecordDrainFailure("lease-0", _drainProbeFailure);
            CosmosReliabilityDiagnostics.RecordPoisonedDocument("lease-0");
        }

        /// <summary>
        /// Reads the off-guard so the static surface initialises, and with it publishes its instruments, BEFORE a
        /// listener attaches. A <c>MeterListener</c> replays already-published instruments when it starts, so the
        /// lookup is timing-proof in both orders.
        /// </summary>
        private static void DeclareInstruments() => _ = CosmosReliabilityDiagnostics.IsEnabled;

        // The failure a deliberately-failed drain carries, so the error-type assertions name a type that exists
        // for no other reason.
        private sealed class DrainFailureProbeException : Exception
        {
            public DrainFailureProbeException(string message)
                : base(message)
            {
            }
        }

        /// <summary>
        /// Attaches a .NET <see cref="MeterListener"/> that enables EXACTLY ONE named instrument on one meter, which
        /// is the state <see cref="RecordingMeterScope"/> cannot express: it enables every instrument the meter
        /// publishes. Disposal disables the instrument it enabled, because disposing the .NET listener alone leaves
        /// the instrument <see cref="Instrument.Enabled"/> for every later test in the process.
        /// </summary>
        private sealed class SingleInstrumentMeterScope : IDisposable
        {
            private readonly string _meterName;
            private readonly string _instrumentName;
            private readonly MeterListener _netMeterListener;
            private Instrument _enabledInstrument;
            private bool _disposed;

            public SingleInstrumentMeterScope(string meterName, string instrumentName)
            {
                _meterName = meterName;
                _instrumentName = instrumentName;

                _netMeterListener = new MeterListener
                {
                    InstrumentPublished = EnableWhenNameMatches,
                };

                _netMeterListener.Start();
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                if (_enabledInstrument != null)
                {
                    _netMeterListener.DisableMeasurementEvents(_enabledInstrument);
                }

                _netMeterListener.Dispose();
            }

            private void EnableWhenNameMatches(Instrument instrument, MeterListener netMeterListener)
            {
                if (instrument.Meter.Name != _meterName || instrument.Name != _instrumentName)
                {
                    return;
                }

                _enabledInstrument = instrument;
                netMeterListener.EnableMeasurementEvents(instrument);
            }
        }
    }
}
