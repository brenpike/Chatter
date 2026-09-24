using Chatter.MessageBrokers.Diagnostics;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using System.Diagnostics.Metrics;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Diagnostics
{
    /// <summary>
    /// The bucket boundaries a collector aggregates <c>messaging.client.operation.duration</c> into. The instrument
    /// reports SECONDS, so it must publish seconds-sized boundaries as instrument advice; without them a collector
    /// falls back to its own millisecond-sized defaults, every realistic duration lands in the first bucket, and
    /// every percentile the histogram reports is the same meaningless number (issue #399).
    /// </summary>
    /// <remarks>
    /// The assertion is on the instrument's PUBLISHED ADVICE rather than on rendered percentiles: advice is the only
    /// thing Chatter controls — a collector remains free to override it — so the advice is the contract and the
    /// percentiles are the collector's business.
    /// Reached through <c>TryGetInstrument</c> rather than through <c>MeterListener.Start</c>'s publication replay:
    /// <c>BrokerDiagnostics</c> is static and initialises once per test process, so the instrument may already have
    /// been published before this scope opened. Driving one real dispatch first makes the lookup timing-proof.
    /// </remarks>
    [Collection(DiagnosticsCollection.Name)]
    public class WhenAggregatingTheOperationDurationHistogram : Testing.Core.Context
    {
        [Fact]
        public async Task MustPublishSecondsSizedBucketBoundariesAsInstrumentAdvice()
        {
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            {
                var harness = new DiagnosticsSendHarness();

                await harness.SendOne();

                meterScope.TryGetInstrument(BrokerDiagnostics.OperationDurationInstrumentName, out var instrument).Should().BeTrue();

                var durationHistogram = instrument.Should().BeAssignableTo<Instrument<double>>().Subject;

                durationHistogram.Advice.Should().NotBeNull();
                durationHistogram.Advice.HistogramBucketBoundaries.Should().Equal(new[] { 0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10 });
            }
        }
    }
}
