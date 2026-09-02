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
#if NET9_0_OR_GREATER
        // INVARIANT: this is the boundary set documented in src/README.md. Nothing but this literal pins the
        // two together, so it is transcribed exactly.
        private static readonly double[] SecondsSizedBucketBoundaries =
            { 0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10 };
#endif

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

#if NET9_0_OR_GREATER
        private static void AssertBucketAdvice(Instrument<double> dispatchDuration)
        {
            dispatchDuration.Advice.Should().NotBeNull("without advice a collector applies its own millisecond-sized default boundaries");
            dispatchDuration.Advice.HistogramBucketBoundaries.Should().Equal(SecondsSizedBucketBoundaries);
        }
#else
        // INVARIANT: this arm pins a BASE CLASS LIBRARY fact, not a Chatter behaviour — the net8.0 shared
        // framework has no InstrumentAdvice<T>, so Instrument<T> exposes no Advice at all and no instrument
        // built against it can carry any. It is deliberately weak and must not be read as a guarantee that
        // Chatter chose to publish nothing here. Delete it together with the production #if when net8.0 is
        // dropped — issue #395, "[Epic] Drop net8.0 and single-target net10.0 after .NET 8 EOL 2026-11-10".
        private static void AssertBucketAdvice(Instrument<double> dispatchDuration)
            => dispatchDuration.GetType()
                               .GetProperties()
                               .Should().NotContain(property => property.Name == "Advice", "net8.0 carries no instrument advice to publish");
#endif
    }
}
