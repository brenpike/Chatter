using Chatter.CQRS.Diagnostics;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using System.Diagnostics.Metrics;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.CQRS.Tests.Diagnostics
{
    /// <summary>
    /// The bucket advice <c>chatter.cqrs.dispatch.duration</c> publishes on itself. The instrument records
    /// SECONDS, while a collector given no advice falls back to the OpenTelemetry .NET SDK's millisecond-sized
    /// default boundaries — under which every realistic dispatch lands in the first bucket and P50, P90 and P99
    /// all report the same number forever.
    /// </summary>
    [Collection(DiagnosticsCollection.Name)]
    public class WhenAggregatingTheDispatchDurationHistogram
    {
        // INVARIANT: this is the boundary set documented in src/README.md. Nothing but this literal pins the
        // two together, so it is transcribed exactly.
        private static readonly double[] SecondsSizedBucketBoundaries =
            { 0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10 };

        [Fact]
        public async Task MustPublishSecondsSizedBucketAdvice()
        {
            using (var harness = new DiagnosticsDispatchHarness())
            using (var meterScope = new RecordingMeterScope(ChatterDiagnostics.MeterName))
            {
                await harness.DispatchCommand();

                meterScope.TryGetInstrument(ChatterDiagnostics.DispatchDurationInstrumentName, out var instrument)
                    .Should().BeTrue("the dispatch above recorded on the histogram, so this scope has observed the instrument");

                AssertBucketAdvice((Instrument<double>)instrument);
            }
        }

        private static void AssertBucketAdvice(Instrument<double> dispatchDuration)
        {
            dispatchDuration.Advice.Should().NotBeNull("without advice a collector applies its own millisecond-sized default boundaries");
            dispatchDuration.Advice.HistogramBucketBoundaries.Should().Equal(SecondsSizedBucketBoundaries);
        }
    }
}
