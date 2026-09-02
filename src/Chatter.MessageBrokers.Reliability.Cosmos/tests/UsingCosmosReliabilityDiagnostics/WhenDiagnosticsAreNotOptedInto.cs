using Chatter.MessageBrokers.Reliability.Cosmos.Diagnostics;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingCosmosReliabilityDiagnostics
{
    /// <summary>
    /// The off state of this module's diagnostics surface: an application that never subscribed to the
    /// <see cref="CosmosReliabilityDiagnostics.ActivitySourceName"/> scope or the
    /// <see cref="CosmosReliabilityDiagnostics.MeterName"/> scope must pay one boolean read and nothing else
    /// (ADR-0010 R1, R2, R4).
    /// </summary>
    /// <remarks>
    /// The subscribed cases live here too, and they are still off-state facts: the surface DECLARES its instruments
    /// and emits nothing at all yet, so a scope attached to both names recording NOTHING is the stronger statement —
    /// if nothing reaches a listener that IS attached, nothing reaches one that is not.
    /// </remarks>
    // The collection name is the repository-wide literal every module's diagnostics tests are serialised onto, spelled
    // out rather than taken from a DiagnosticsCollection constant because no such definition exists in THIS test
    // assembly yet. xunit v2 discovers collection definitions only in the assembly under run, so the step that adds
    // this module's first opted-in diagnostics tests has to declare one here; these absence facts join it by name the
    // moment it does, and until then this attribute costs nothing.
    [Collection("chatter-diagnostics")]
    public class WhenDiagnosticsAreNotOptedInto : Testing.Core.Context
    {
        [Fact]
        public void MustReportDiagnosticsDisabledInAnEmptyProcess()
        {
            CosmosReliabilityDiagnostics.IsEnabled.Should().BeFalse();
        }

        [Fact]
        public void MustReportDiagnosticsDisabledWhileForeignInstrumentationIsRunning()
        {
            using (var foreignInstrumentation = new ForeignInstrumentationScope())
            {
                Activity.Current.Should().BeSameAs(foreignInstrumentation.ForeignActivity);

                // INVARIANT: ADR-0010 R2/R3 - a non-null ambient Activity is NOT an opt-in.
                CosmosReliabilityDiagnostics.IsEnabled.Should().BeFalse();
            }
        }

        [Fact]
        public void MustNotAllocateWhileEvaluatingTheOffGuard()
        {
            CosmosReliabilityDiagnostics.IsEnabled.Should().BeFalse();

            var measurement = GuardCostProbe.Measure<bool>(() => CosmosReliabilityDiagnostics.IsEnabled);

            measurement.MedianAllocatedBytesPerBatch.Should().Be(0, "the off-guard is a boolean read: " + measurement);
        }

        [Fact]
        public void MustEmitNothingWhileTheInstrumentsAreDeclaredOnly()
        {
            using (var activityScope = new RecordingActivityScope(CosmosReliabilityDiagnostics.ActivitySourceName))
            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                CosmosReliabilityDiagnostics.IsEnabled.Should().BeTrue();

                activityScope.StartedActivities.Should().BeEmpty();
                meterScope.Measurements.Should().BeEmpty();
            }
        }

        [Fact]
        public void MustDeclareTheDrainInstrumentsOnTheModuleMeter()
        {
            DeclareInstruments();

            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                meterScope.TryGetInstrument(CosmosReliabilityDiagnostics.DrainLagInstrumentName, out var drainLag).Should().BeTrue();
                drainLag.Unit.Should().Be("s");

                meterScope.TryGetInstrument(CosmosReliabilityDiagnostics.DrainedDocumentsInstrumentName, out var drainedDocuments).Should().BeTrue();
                drainedDocuments.Unit.Should().Be("{document}");

                meterScope.TryGetInstrument(CosmosReliabilityDiagnostics.DrainBatchSizeInstrumentName, out var batchSize).Should().BeTrue();
                batchSize.Unit.Should().Be("{document}");

                meterScope.TryGetInstrument(CosmosReliabilityDiagnostics.DrainedBatchesInstrumentName, out var drainedBatches).Should().BeTrue();
                drainedBatches.Unit.Should().Be("{batch}");
            }
        }

        /// <summary>
        /// The bucket boundaries a collector aggregates the drain lag into. The instrument reports SECONDS, so it must
        /// publish seconds-sized boundaries as instrument advice; without them a collector falls back to its own
        /// millisecond-sized defaults, every realistic lag lands in the first bucket, and every percentile the
        /// histogram reports is the same meaningless number (issue #399).
        /// </summary>
        /// <remarks>
        /// The boundaries reach further than the send path's own duration histogram because a drain lag is not one
        /// client call: a document waits for the change feed, and a restarted lease or a backlog can leave it pending
        /// for minutes.
        /// </remarks>
        [Fact]
        public void MustPublishSecondsSizedBucketBoundariesOnTheDrainLagHistogram()
        {
            DeclareInstruments();

            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                meterScope.TryGetInstrument(CosmosReliabilityDiagnostics.DrainLagInstrumentName, out var drainLag).Should().BeTrue();

#if NET9_0_OR_GREATER
                var drainLagHistogram = drainLag.Should().BeAssignableTo<Instrument<double>>().Subject;

                drainLagHistogram.Advice.Should().NotBeNull();
                drainLagHistogram.Advice.HistogramBucketBoundaries.Should().Equal(new[] { 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 300, 600 });
#else
                // The net8.0 shared framework carries no InstrumentAdvice<T> and no Instrument<T>.Advice, so there
                // is no advice to publish on this leg. This pins a BCL FACT, not a Chatter behaviour, and is
                // deliberately weak for that reason - do not read it as a behavioural guarantee. Issue #395
                // ([Epic] Drop net8.0 and single-target net10.0 after .NET 8 EOL 2026-11-10) is the trigger to
                // delete BOTH the production #if and this assertion.
                drainLag.GetType().GetProperty("Advice").Should().BeNull();
#endif
            }
        }

        /// <summary>
        /// The same unit-sized-boundary rule on the batch-size histogram, whose unit is DOCUMENTS: a change-feed batch
        /// carries single or double digits of documents far more often than the thousands a collector's default
        /// boundaries are shaped for.
        /// </summary>
        [Fact]
        public void MustPublishDocumentSizedBucketBoundariesOnTheBatchSizeHistogram()
        {
            DeclareInstruments();

            using (var meterScope = new RecordingMeterScope(CosmosReliabilityDiagnostics.MeterName))
            {
                meterScope.TryGetInstrument(CosmosReliabilityDiagnostics.DrainBatchSizeInstrumentName, out var batchSize).Should().BeTrue();

#if NET9_0_OR_GREATER
                var batchSizeHistogram = batchSize.Should().BeAssignableTo<Instrument<int>>().Subject;

                batchSizeHistogram.Advice.Should().NotBeNull();
                batchSizeHistogram.Advice.HistogramBucketBoundaries.Should().Equal(new[] { 1, 2, 5, 10, 25, 50, 100, 250, 500, 1000 });
#else
                // As above: a BCL fact on the net8.0 leg, deleted with the production #if under issue #395.
                batchSize.GetType().GetProperty("Advice").Should().BeNull();
#endif
            }
        }

        /// <summary>
        /// Reads the off-guard so the static surface initialises, and with it publishes its instruments, BEFORE a
        /// listener attaches. A <c>MeterListener</c> replays already-published instruments when it starts, so the
        /// lookup is timing-proof in both orders.
        /// </summary>
        private static void DeclareInstruments() => _ = CosmosReliabilityDiagnostics.IsEnabled;
    }
}
