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
    /// The performance gate behind ADR-0010's "off must mean OFF": an application that never opts in must not
    /// pay a measurable per-message cost.
    /// </summary>
    /// <remarks>
    /// HONEST SCOPE OF THE CLAIM. The zero-allocation assertions cover the GUARD HELPERS only —
    /// <see cref="ChatterDiagnostics.IsEnabled"/> and the off-path returns of
    /// <see cref="ChatterDiagnostics.StartDispatch{TMessage}"/> and
    /// <see cref="ChatterDiagnostics.RecordDispatchDuration{TMessage}"/>. They do NOT claim a zero-allocation
    /// dispatch, because the real dispatch path already allocates per dispatch independently of diagnostics:
    /// <c>CommandDispatcher</c> builds an interpolated string for its unconditional <c>LogTrace</c> call, and
    /// <c>CommandBehaviorPipeline.Execute</c> builds a fresh delegate chain per execution. Neither is part of
    /// this change and neither is asserted here.
    ///
    /// The RATIO gate is what covers the real path. It is a SAME-RUN comparison — the guard and a full
    /// <see cref="IMessageDispatcher.Dispatch{TMessage}(TMessage)"/> are measured in one warmed process — so
    /// the threshold is a fraction rather than a machine-dependent nanosecond budget.
    ///
    /// Every measurement runs SYNCHRONOUSLY on the calling thread, because
    /// <see cref="GC.GetAllocatedBytesForCurrentThread"/> reports the calling thread's counter only.
    /// </remarks>
    [Collection(DiagnosticsCollection.Name)]
    public class WhenMeasuringGuardCost : IDisposable
    {
        /// <summary>The guard may cost at most this fraction of a full dispatch measured in the same run.</summary>
        private const double MaxGuardShareOfDispatch = 0.05d;

        private readonly DiagnosticsDispatchHarness _harness = new DiagnosticsDispatchHarness();

        public void Dispose() => _harness.Dispose();

        [Fact]
        public void MustNotAllocateWhileEvaluatingTheOffGuard()
        {
            ChatterDiagnostics.IsEnabled.Should().BeFalse();

            var measurement = GuardCostProbe.Measure<bool>(() => ChatterDiagnostics.IsEnabled);

            measurement.MedianAllocatedBytesPerBatch.Should().Be(0, "the off-guard is a boolean read: " + measurement);
        }

        [Fact]
        public void MustNotAllocateWhileStartingADispatchSpanThatIsOff()
        {
            ChatterDiagnostics.Source.HasListeners().Should().BeFalse();

            var measurement = GuardCostProbe.Measure<Activity>(
                () => ChatterDiagnostics.StartDispatch<TracedCommand>(ChatterTelemetryTags.DispatchKinds.Command));

            measurement.MedianAllocatedBytesPerBatch.Should().Be(0, "no span name, tag or activity may be built while off: " + measurement);
        }

        [Fact]
        public void MustNotAllocateWhileRecordingADispatchDurationThatIsOff()
        {
            ChatterDiagnostics.IsEnabled.Should().BeFalse();

            var startTimestamp = Stopwatch.GetTimestamp();
            var measurement = GuardCostProbe.Measure(
                () => ChatterDiagnostics.RecordDispatchDuration<TracedCommand>(startTimestamp, ChatterTelemetryTags.DispatchKinds.Command, null));

            measurement.MedianAllocatedBytesPerBatch.Should().Be(0, "no tag list may be built while off: " + measurement);
        }

        [Fact]
        public void MustCostASmallFractionOfAFullDispatch()
        {
            ChatterDiagnostics.IsEnabled.Should().BeFalse();

            var command = new TracedCommand();
            var dispatcher = _harness.Dispatcher;

            var dispatchCost = GuardCostProbe.Measure<Task>(() => dispatcher.Dispatch(command));
            var guardCost = GuardCostProbe.Measure<bool>(() => ChatterDiagnostics.IsEnabled);

            _harness.CommandHandler.InvocationCount.Should().BeGreaterThan(0);
            dispatchCost.MedianNanosecondsPerOperation.Should().BeGreaterThan(0d);
            guardCost.MedianNanosecondsPerOperation.Should().BeLessThan(
                dispatchCost.MedianNanosecondsPerOperation * MaxGuardShareOfDispatch,
                $"the off-guard must stay under {MaxGuardShareOfDispatch:P0} of a dispatch (guard {guardCost}, dispatch {dispatchCost})");
        }
    }
}
