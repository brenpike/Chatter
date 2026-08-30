using Chatter.CQRS.Diagnostics;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.CQRS.Tests.Diagnostics
{
    /// <summary>
    /// The opted-in counterpart to <see cref="WhenChatterTracingIsNotOptedInto"/>, so the off-state absence
    /// assertions are not vacuous: with a .NET <c>ActivityListener</c> attached to the Chatter source, or the
    /// dispatch duration histogram enabled on a .NET <c>MeterListener</c>, the same dispatch paths emit the
    /// spans, tags and measurements ADR-0010 specifies.
    /// </summary>
    [Collection(DiagnosticsCollection.Name)]
    public class WhenChatterTracingIsOptedInto : IDisposable
    {
        private readonly DiagnosticsDispatchHarness _harness = new DiagnosticsDispatchHarness();

        public void Dispose() => _harness.Dispose();

        [Fact]
        public async Task MustStartOneSpanForACommandDispatch()
        {
            using (var activityScope = new RecordingActivityScope(ChatterDiagnostics.ActivitySourceName))
            {
                await _harness.DispatchCommand();

                var span = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                span.Source.Name.Should().Be(ChatterDiagnostics.ActivitySourceName);
                span.OperationName.Should().Be("dispatch " + nameof(TracedCommand));
                span.Kind.Should().Be(ActivityKind.Internal);
                span.GetTagItem(ChatterTelemetryTags.MessageType).Should().Be(typeof(TracedCommand).FullName);
                span.GetTagItem(ChatterTelemetryTags.DispatchKind).Should().Be(ChatterTelemetryTags.DispatchKinds.Command);
                span.GetTagItem(ChatterTelemetryTags.ErrorType).Should().BeNull();
                span.Status.Should().Be(ActivityStatusCode.Unset);
            }
        }

        [Fact]
        public async Task MustStartOneSpanForAnEventDispatch()
        {
            using (var activityScope = new RecordingActivityScope(ChatterDiagnostics.ActivitySourceName))
            {
                await _harness.DispatchEvent();

                var span = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                span.Source.Name.Should().Be(ChatterDiagnostics.ActivitySourceName);
                span.OperationName.Should().Be("dispatch " + nameof(TracedEvent));
                span.GetTagItem(ChatterTelemetryTags.MessageType).Should().Be(typeof(TracedEvent).FullName);
                span.GetTagItem(ChatterTelemetryTags.DispatchKind).Should().Be(ChatterTelemetryTags.DispatchKinds.Event);
                span.Status.Should().Be(ActivityStatusCode.Unset);
            }
        }

        [Fact]
        public async Task MustSurroundTheHandlerWithTheDispatchSpan()
        {
            using (new RecordingActivityScope(ChatterDiagnostics.ActivitySourceName))
            {
                await _harness.DispatchCommand();

                _harness.CommandHandler.AmbientActivityWhileHandling.Should().NotBeNull();
                _harness.CommandHandler.AmbientActivityWhileHandling.Source.Name.Should().Be(ChatterDiagnostics.ActivitySourceName);
            }
        }

        [Fact]
        public async Task MustRecordTheDispatchDurationForACommandDispatch()
        {
            using (var meterScope = new RecordingMeterScope(ChatterDiagnostics.MeterName))
            {
                await _harness.DispatchCommand();

                var measurement = meterScope.MeasurementsFor(ChatterDiagnostics.DispatchDurationInstrumentName).Should().ContainSingle().Subject;
                measurement.MeterName.Should().Be(ChatterDiagnostics.MeterName);
                measurement.Value.Should().BeGreaterThanOrEqualTo(0d);
                ResolveTag(measurement, ChatterTelemetryTags.MessageType).Should().Be(typeof(TracedCommand).FullName);
                ResolveTag(measurement, ChatterTelemetryTags.DispatchKind).Should().Be(ChatterTelemetryTags.DispatchKinds.Command);
                measurement.TryGetTag(ChatterTelemetryTags.ErrorType, out _).Should().BeFalse();
            }
        }

        [Fact]
        public async Task MustRecordTheDispatchDurationForAnEventDispatch()
        {
            using (var meterScope = new RecordingMeterScope(ChatterDiagnostics.MeterName))
            {
                await _harness.DispatchEvent();

                var measurement = meterScope.MeasurementsFor(ChatterDiagnostics.DispatchDurationInstrumentName).Should().ContainSingle().Subject;
                ResolveTag(measurement, ChatterTelemetryTags.MessageType).Should().Be(typeof(TracedEvent).FullName);
                ResolveTag(measurement, ChatterTelemetryTags.DispatchKind).Should().Be(ChatterTelemetryTags.DispatchKinds.Event);
                measurement.TryGetTag(ChatterTelemetryTags.ErrorType, out _).Should().BeFalse();
            }
        }

        [Fact]
        public async Task MustMarkTheSpanAndTheMeasurementWithTheSameErrorTypeWhenTheHandlerFails()
        {
            var expectedFailure = _harness.FailingCommandHandler.Failure;

            using (var activityScope = new RecordingActivityScope(ChatterDiagnostics.ActivitySourceName))
            using (var meterScope = new RecordingMeterScope(ChatterDiagnostics.MeterName))
            {
                var thrown = await FluentActions.Invoking(async () => await _harness.DispatchFailingCommand())
                    .Should().ThrowAsync<DiagnosticsProbeException>();

                thrown.Which.Should().BeSameAs(expectedFailure);

                var span = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                span.OperationName.Should().Be("dispatch " + nameof(FailingCommand));
                span.Status.Should().Be(ActivityStatusCode.Error);
                span.StatusDescription.Should().Be(expectedFailure.Message);
                span.GetTagItem(ChatterTelemetryTags.ErrorType).Should().Be(typeof(DiagnosticsProbeException).FullName);

                var measurement = meterScope.MeasurementsFor(ChatterDiagnostics.DispatchDurationInstrumentName).Should().ContainSingle().Subject;
                ResolveTag(measurement, ChatterTelemetryTags.ErrorType).Should().Be(typeof(DiagnosticsProbeException).FullName);
                ResolveTag(measurement, ChatterTelemetryTags.DispatchKind).Should().Be(ChatterTelemetryTags.DispatchKinds.Command);
            }
        }

        [Fact]
        public async Task MustAddTheExceptionEventWhenAllDataIsRequested()
        {
            var expectedFailure = _harness.FailingCommandHandler.Failure;

            using (var activityScope = new RecordingActivityScope(ChatterDiagnostics.ActivitySourceName))
            {
                await FluentActions.Invoking(async () => await _harness.DispatchFailingCommand())
                    .Should().ThrowAsync<DiagnosticsProbeException>();

                var span = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                span.IsAllDataRequested.Should().BeTrue();

                // ActivityOutcome adds this event through Activity.AddException on net9.0+ and through a
                // hand-rolled ActivityEvent on net8.0. Asserting the emitted event and its tags — never the API
                // that produced them — is what keeps both target frameworks covered by one assertion.
                var exceptionEvent = ResolveSingleEvent(span, ChatterTelemetryTags.ExceptionEventName);
                ResolveEventTag(exceptionEvent, ChatterTelemetryTags.ExceptionType).Should().Be(typeof(DiagnosticsProbeException).FullName);
                ResolveEventTag(exceptionEvent, ChatterTelemetryTags.ExceptionMessage).Should().Be(expectedFailure.Message);
                ResolveEventTag(exceptionEvent, ChatterTelemetryTags.ExceptionStackTrace).Should().BeOfType<string>()
                    .Which.Should().Contain(typeof(DiagnosticsProbeException).FullName);
            }
        }

        [Fact]
        public async Task MustNotMaterialiseExceptionDetailWhenAllDataIsNotRequested()
        {
            using (var propagationOnlyScope = new PropagationOnlyActivityScope(ChatterDiagnostics.ActivitySourceName))
            {
                await FluentActions.Invoking(async () => await _harness.DispatchFailingCommand())
                    .Should().ThrowAsync<DiagnosticsProbeException>();

                var span = propagationOnlyScope.StoppedActivities.Should().ContainSingle().Subject;
                span.IsAllDataRequested.Should().BeFalse();
                span.Status.Should().Be(ActivityStatusCode.Error);
                span.GetTagItem(ChatterTelemetryTags.ErrorType).Should().Be(typeof(DiagnosticsProbeException).FullName);
                span.Events.Should().BeEmpty();
            }
        }

        private static object ResolveTag(RecordedMeasurement measurement, string tagName)
        {
            measurement.TryGetTag(tagName, out var tagValue).Should().BeTrue($"the measurement should carry '{tagName}'");
            return tagValue;
        }

        private static ActivityEvent ResolveSingleEvent(Activity span, string eventName)
        {
            var matches = new List<ActivityEvent>();

            foreach (var candidate in span.Events)
            {
                if (candidate.Name == eventName)
                {
                    matches.Add(candidate);
                }
            }

            return matches.Should().ContainSingle().Subject;
        }

        private static object ResolveEventTag(ActivityEvent activityEvent, string tagName)
        {
            foreach (var tag in activityEvent.Tags)
            {
                if (tag.Key == tagName)
                {
                    return tag.Value;
                }
            }

            return null;
        }

        /// <summary>
        /// Attaches a .NET <see cref="ActivityListener"/> that samples <see cref="ActivitySamplingResult.PropagationData"/>,
        /// so spans are created and observed but report <see cref="Activity.IsAllDataRequested"/> as <c>false</c>.
        /// </summary>
        /// <remarks>
        /// The shared <see cref="RecordingActivityScope"/> forces <see cref="ActivitySamplingResult.AllDataAndRecorded"/>
        /// by design, so it cannot express the other side of the <see cref="Activity.IsAllDataRequested"/> gate this
        /// scope exists to exercise.
        /// </remarks>
        private sealed class PropagationOnlyActivityScope : IDisposable
        {
            private readonly ActivityListener _netActivityListener;
            private readonly List<Activity> _stoppedActivities = new List<Activity>();
            private readonly Activity _priorActivity;

            public PropagationOnlyActivityScope(string sourceName)
            {
                _priorActivity = Activity.Current;

                _netActivityListener = new ActivityListener
                {
                    ShouldListenTo = source => source.Name == sourceName,
                    Sample = SamplePropagationData,
                    SampleUsingParentId = SamplePropagationDataFromParentId,
                    ActivityStopped = _stoppedActivities.Add,
                };

                ActivitySource.AddActivityListener(_netActivityListener);
            }

            public IReadOnlyList<Activity> StoppedActivities => _stoppedActivities.ToArray();

            public void Dispose()
            {
                _netActivityListener.Dispose();
                Activity.Current = _priorActivity;
            }

            private static ActivitySamplingResult SamplePropagationData(ref ActivityCreationOptions<ActivityContext> options)
                => ActivitySamplingResult.PropagationData;

            private static ActivitySamplingResult SamplePropagationDataFromParentId(ref ActivityCreationOptions<string> options)
                => ActivitySamplingResult.PropagationData;
        }
    }
}
