using Chatter.MessageBrokers.Reliability.Cosmos.Diagnostics;
using Chatter.MessageBrokers.Reliability.Cosmos.Tests.Diagnostics;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using System;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingCosmosReliabilityDiagnostics
{
    /// <summary>
    /// The on state of this module's drain instruments: what an application that opted into the
    /// <see cref="CosmosReliabilityDiagnostics.MeterName"/> scope actually receives when the Outbox Relay records
    /// a measurement.
    /// </summary>
    [Collection(DiagnosticsCollection.Name)]
    public class WhenRecordingDrainMeasurements : Testing.Core.Context
    {
        [Fact]
        public void MustCountOneResolvedDocumentTaggedWithItsOutcome()
        {
            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                CosmosReliabilityDiagnostics.RecordDrainedDocument(CosmosReliabilityDiagnostics.DrainOutcomes.Admitted);

                var measurement = meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainedDocumentsInstrumentName).Should().ContainSingle().Subject;

                measurement.Value.Should().Be(1);
                measurement.TryGetTag(CosmosReliabilityDiagnostics.DrainOutcome, out var outcome).Should().BeTrue();
                outcome.Should().Be(CosmosReliabilityDiagnostics.DrainOutcomes.Admitted);
            }
        }

        /// <summary>
        /// The lag is derived from the RAW Cosmos <c>_ts</c> the caller hands over — Unix epoch SECONDS — so the
        /// value reaching the histogram is an age in seconds, not the timestamp itself and not milliseconds.
        /// </summary>
        [Fact]
        public void MustRecordTheAgeOfAnAdmittedDocumentInSeconds()
        {
            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                CosmosReliabilityDiagnostics.RecordDrainLag(DateTimeOffset.UtcNow.AddSeconds(-300).ToUnixTimeSeconds());

                var measurement = meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainLagInstrumentName).Should().ContainSingle().Subject;

                measurement.Value.Should().BeInRange(299, 330);
                measurement.Tags.Should().BeEmpty("a lag carries no dimension of its own");
            }
        }

        /// <summary>
        /// A Cosmos <c>_ts</c> stamped by a node whose clock runs ahead of this one dates the document into the
        /// FUTURE, which would otherwise report a negative age. A negative lag is not representable — a document
        /// cannot be admitted before it was written — so skew is clamped to zero here, at the one place that
        /// computes an age. Call sites hand over the raw <c>_ts</c> and never clamp.
        /// </summary>
        [Fact]
        public void MustClampTheLagOfADocumentStampedIntoTheFutureToZero()
        {
            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                CosmosReliabilityDiagnostics.RecordDrainLag(DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds());

                var measurement = meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainLagInstrumentName).Should().ContainSingle().Subject;

                measurement.Value.Should().Be(0);
            }
        }

        /// <summary>
        /// A <c>_ts</c> outside the range <see cref="DateTimeOffset"/> can represent is not a measurable age, so
        /// it is treated exactly as an ABSENT one: no lag is recorded and the caller is not disturbed. Converting
        /// it would throw <see cref="ArgumentOutOfRangeException"/>, and the Outbox Relay records the admission
        /// lag BEFORE it reconstructs and publishes — so an unrepresentable timestamp would fault the change-feed
        /// handler, block the checkpoint and re-surface the batch forever. OPTIONAL telemetry may never stop a
        /// delivery.
        /// </summary>
        [Theory]
        [InlineData(long.MaxValue)]
        [InlineData(long.MinValue)]
        public void MustRecordNoLagForATimestampOutsideTheRepresentableRange(long unrepresentableUnixSeconds)
        {
            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                Action recording = () => CosmosReliabilityDiagnostics.RecordDrainLag(unrepresentableUnixSeconds);

                recording.Should().NotThrow("optional telemetry may never fault the drain that carries the message");
                meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainLagInstrumentName).Should().BeEmpty();
            }
        }

        /// <summary>
        /// One handed-over change-feed batch is TWO measurements — how many documents it carried and that it
        /// happened — and BOTH carry the lease token, so partition progress is readable off either instrument.
        /// </summary>
        [Fact]
        public void MustSizeAndCountOneBatchAgainstTheLeaseItWasDeliveredFor()
        {
            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                CosmosReliabilityDiagnostics.RecordDrainedBatch("lease-7", documentCount: 12);

                var batchSize = meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainBatchSizeInstrumentName).Should().ContainSingle().Subject;
                var drainedBatches = meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainedBatchesInstrumentName).Should().ContainSingle().Subject;

                batchSize.Value.Should().Be(12);
                drainedBatches.Value.Should().Be(1);

                foreach (var measurement in new[] { batchSize, drainedBatches })
                {
                    measurement.TryGetTag(CosmosReliabilityDiagnostics.LeaseToken, out var leaseToken).Should().BeTrue();
                    leaseToken.Should().Be("lease-7");
                }
            }
        }

        /// <summary>
        /// An application that attached a .NET <c>MeterListener</c> and NO .NET <c>ActivityListener</c> receives
        /// every drain measurement. Guarding an emit site on the module's <c>ActivitySource.HasListeners</c> would
        /// silently make this application's whole metrics opt-in a no-op (ADR-0010 R4).
        /// </summary>
        [Fact]
        public void MustRecordEveryDrainMeasurementForAMetricsOnlyOptIn()
        {
            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                CosmosReliabilityDiagnostics.IsEnabled.Should().BeTrue();

                RecordOneDrainedBatchOfOneAdmittedDocument();

                meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainedDocumentsInstrumentName).Should().ContainSingle();
                meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainLagInstrumentName).Should().ContainSingle();
                meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainBatchSizeInstrumentName).Should().ContainSingle();
                meterScope.MeasurementsFor(CosmosReliabilityDiagnostics.DrainedBatchesInstrumentName).Should().ContainSingle();
            }
        }

        /// <summary>
        /// The mirror-image opt-in: a .NET <c>ActivityListener</c> on this module's scope and NO .NET
        /// <c>MeterListener</c>. Tracing alone makes <see cref="CosmosReliabilityDiagnostics.IsEnabled"/> TRUE,
        /// because that property ORs in <c>ActivitySource.HasListeners</c> — which is precisely why no emit method
        /// may guard on it. Each one guards on its OWN instrument, so this application pays a boolean read and
        /// nothing else: no clock is read and no attribute is built for a metric nobody subscribed to
        /// (ADR-0010 R1, R2).
        /// </summary>
        /// <remarks>
        /// The module declares no span, so an opted-in .NET <c>ActivityListener</c> observing nothing is also the
        /// standing statement that these emit methods are metrics-only.
        /// </remarks>
        [Fact]
        public void MustRecordNothingForATracingOnlyOptIn()
        {
            using (var activityScope = new RecordingActivityScope(CosmosReliabilityDiagnostics.ActivitySourceName))
            {
                CosmosReliabilityDiagnostics.IsEnabled.Should().BeTrue();

                var measurement = GuardCostProbe.Measure(RecordOneDrainedBatchOfOneAdmittedDocument);

                measurement.MedianAllocatedBytesPerBatch.Should().Be(0, "no attribute may be built for an instrument nobody enabled: " + measurement);
                activityScope.StartedActivities.Should().BeEmpty();
            }
        }

        /// <summary>Drives every emit method once, as the Outbox Relay does for a single-document batch.</summary>
        private static void RecordOneDrainedBatchOfOneAdmittedDocument()
        {
            CosmosReliabilityDiagnostics.RecordDrainedBatch("lease-0", documentCount: 1);
            CosmosReliabilityDiagnostics.RecordDrainedDocument(CosmosReliabilityDiagnostics.DrainOutcomes.Admitted);
            CosmosReliabilityDiagnostics.RecordDrainLag(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }
    }
}
