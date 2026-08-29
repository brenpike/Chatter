using Chatter.CQRS.Diagnostics;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.CQRS.Tests.Diagnostics
{
    /// <summary>
    /// The off-state proof for ADR-0010's off-guard. Two shapes are covered: an empty process with no .NET
    /// <c>ActivityListener</c> anywhere, and — the load-bearing one — a process carrying FOREIGN
    /// instrumentation, where an unrelated library's own .NET <c>ActivityListener</c> makes
    /// <see cref="Activity.Current"/> non-null while Chatter is never opted into. The second shape is what
    /// proves ADR-0010 R2/R3: a guard keyed on <c>Activity.Current is null</c> would read as "tracing on" in
    /// that very common host shape and would emit spans and measurements an application never asked for.
    /// </summary>
    [Collection(DiagnosticsCollection.Name)]
    public class WhenChatterTracingIsNotOptedInto : IDisposable
    {
        private const string ForeignMeterName = "Contoso.Unrelated.Metrics";

        private readonly DiagnosticsDispatchHarness _harness = new DiagnosticsDispatchHarness();

        public void Dispose() => _harness.Dispose();

        [Fact]
        public void MustReportDiagnosticsDisabledInAnEmptyProcess()
        {
            ChatterDiagnostics.Source.HasListeners().Should().BeFalse();
            ChatterDiagnostics.IsEnabled.Should().BeFalse();
        }

        [Fact]
        public async Task MustNotStartAnActivityForACommandInAnEmptyProcess()
        {
            Activity.Current.Should().BeNull();

            await _harness.DispatchCommand();

            _harness.CommandHandler.InvocationCount.Should().Be(1);
            _harness.CommandHandler.AmbientActivityWhileHandling.Should().BeNull();
            Activity.Current.Should().BeNull();
        }

        [Fact]
        public async Task MustNotStartAnActivityForAnEventInAnEmptyProcess()
        {
            Activity.Current.Should().BeNull();

            await _harness.DispatchEvent();

            _harness.EventMessageHandler.InvocationCount.Should().Be(1);
            _harness.EventMessageHandler.AmbientActivityWhileHandling.Should().BeNull();
            Activity.Current.Should().BeNull();
        }

        [Fact]
        public void MustReportDiagnosticsDisabledWhileForeignInstrumentationIsRunning()
        {
            using (var foreignInstrumentation = new ForeignInstrumentationScope())
            {
                Activity.Current.Should().BeSameAs(foreignInstrumentation.ForeignActivity);

                // INVARIANT: ADR-0010 R2/R3 — a non-null ambient Activity is NOT an opt-in. IsEnabled is the
                // disjunction of Chatter's own ActivitySource.HasListeners and its own Instrument.Enabled, so a
                // false result is simultaneously the proof that no span and no measurement can be emitted.
                ChatterDiagnostics.Source.HasListeners().Should().BeFalse();
                ChatterDiagnostics.IsEnabled.Should().BeFalse();
            }
        }

        [Fact]
        public async Task MustNotStartAnActivityForACommandWhileForeignInstrumentationIsRunning()
        {
            using (var foreignInstrumentation = new ForeignInstrumentationScope())
            {
                await _harness.DispatchCommand();

                _harness.CommandHandler.InvocationCount.Should().Be(1);
                _harness.CommandHandler.AmbientActivityWhileHandling.Should().BeSameAs(foreignInstrumentation.ForeignActivity);
                Activity.Current.Should().BeSameAs(foreignInstrumentation.ForeignActivity);
                ChatterDiagnostics.IsEnabled.Should().BeFalse();
            }
        }

        [Fact]
        public async Task MustNotStartAnActivityForAnEventWhileForeignInstrumentationIsRunning()
        {
            using (var foreignInstrumentation = new ForeignInstrumentationScope())
            {
                await _harness.DispatchEvent();

                _harness.EventMessageHandler.InvocationCount.Should().Be(1);
                _harness.EventMessageHandler.AmbientActivityWhileHandling.Should().BeSameAs(foreignInstrumentation.ForeignActivity);
                Activity.Current.Should().BeSameAs(foreignInstrumentation.ForeignActivity);
                ChatterDiagnostics.IsEnabled.Should().BeFalse();
            }
        }

        [Fact]
        public async Task MustRecordNoMeasurementWhileForeignInstrumentationIsRunning()
        {
            // A .NET MeterListener is process-global exactly as a .NET ActivityListener is, so this scope puts a
            // foreign one in the process without ever enabling Chatter's instrument. Chatter's own
            // Instrument.Enabled must therefore stay false, which is what makes the histogram Record a no-op.
            using (var foreignMeterScope = new RecordingMeterScope(ForeignMeterName))
            using (new ForeignInstrumentationScope())
            {
                await _harness.DispatchCommand();
                await _harness.DispatchEvent();

                ChatterDiagnostics.IsEnabled.Should().BeFalse();
                foreignMeterScope.MeasurementsFor(ChatterDiagnostics.DispatchDurationInstrumentName).Should().BeEmpty();
                foreignMeterScope.Measurements.Should().BeEmpty();
            }
        }
    }
}
