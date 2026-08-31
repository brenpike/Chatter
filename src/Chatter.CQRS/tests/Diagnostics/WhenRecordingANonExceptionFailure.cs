using Chatter.CQRS.Diagnostics;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using System.Diagnostics;
using Xunit;

namespace Chatter.CQRS.Tests.Diagnostics
{
    /// <summary>
    /// The non-exception failure path of <see cref="ActivityOutcome"/>: an operation that did not succeed
    /// without any exception being raised. Marking such a failure with a never-thrown marker exception would
    /// attach a synthetic stack trace to the span, so the failure is recorded from a caller-supplied error type
    /// and description instead and NO <c>exception</c> event is added.
    /// </summary>
    [Collection(DiagnosticsCollection.Name)]
    public class WhenRecordingANonExceptionFailure
    {
        private const string ErrorType = "probe_failed";
        private const string Description = "The operation did not complete and raised nothing.";

        [Fact]
        public void MustSetTheErrorStatusWithTheSuppliedDescription()
        {
            using (new RecordingActivityScope(ChatterDiagnostics.ActivitySourceName))
            {
                var span = StartSpan();

                ActivityOutcome.RecordFailure(span, ErrorType, Description);

                span.Status.Should().Be(ActivityStatusCode.Error);
                span.StatusDescription.Should().Be(Description);
            }
        }

        [Fact]
        public void MustStampTheSuppliedErrorType()
        {
            using (new RecordingActivityScope(ChatterDiagnostics.ActivitySourceName))
            {
                var span = StartSpan();

                ActivityOutcome.RecordFailure(span, ErrorType, Description);

                span.GetTagItem(ChatterTelemetryTags.ErrorType).Should().Be(ErrorType);
            }
        }

        [Fact]
        public void MustNotAddAnExceptionEventEvenWhenAllDataIsRequested()
        {
            using (new RecordingActivityScope(ChatterDiagnostics.ActivitySourceName))
            {
                var span = StartSpan();
                span.IsAllDataRequested.Should().BeTrue();

                ActivityOutcome.RecordFailure(span, ErrorType, Description);

                // A non-exception failure has no exception to describe, so fabricating an exception event —
                // with the empty stack trace a never-thrown marker exception would carry — is the one thing
                // this overload exists to avoid.
                span.Events.Should().NotContain(activityEvent => activityEvent.Name == ChatterTelemetryTags.ExceptionEventName);
            }
        }

        [Fact]
        public void MustIgnoreANullActivity()
        {
            FluentActions.Invoking(() => ActivityOutcome.RecordFailure(null, ErrorType, Description)).Should().NotThrow();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustLeaveTheSpanUnmarkedWhenNoErrorTypeIsSupplied(string missingErrorType)
        {
            using (new RecordingActivityScope(ChatterDiagnostics.ActivitySourceName))
            {
                var span = StartSpan();

                ActivityOutcome.RecordFailure(span, missingErrorType, Description);

                span.Status.Should().Be(ActivityStatusCode.Unset);
                span.StatusDescription.Should().BeNull();
                span.GetTagItem(ChatterTelemetryTags.ErrorType).Should().BeNull();
            }
        }

        private static Activity StartSpan()
        {
            var span = ChatterDiagnostics.StartDispatch<TracedCommand>(ChatterTelemetryTags.DispatchKinds.Command);
            span.Should().NotBeNull("the recording scope samples every activity in");
            return span;
        }
    }
}
