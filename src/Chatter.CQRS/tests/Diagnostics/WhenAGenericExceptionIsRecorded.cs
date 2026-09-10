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
    /// Pins ONE canonical spelling of <c>exception.type</c> across both target frameworks. A generic exception
    /// type is what makes the assertion meaningful: for a non-generic type every candidate spelling renders
    /// identically, so only a generic probe can tell the net8.0 hand-rolled event apart from the net9.0+ event
    /// the BCL writes through <c>Activity.AddException</c>.
    /// </summary>
    [Collection(DiagnosticsCollection.Name)]
    public class WhenAGenericExceptionIsRecorded : IDisposable
    {
        private readonly DiagnosticsDispatchHarness _harness = new DiagnosticsDispatchHarness();

        public void Dispose() => _harness.Dispose();

        [Fact]
        public async Task MustSpellTheExceptionTypeIdenticallyOnEveryTargetFramework()
        {
            using (var activityScope = new RecordingActivityScope(ChatterDiagnostics.ActivitySourceName))
            {
                await FluentActions.Invoking(async () => await _harness.DispatchGenericFailingCommand())
                    .Should().ThrowAsync<DiagnosticsProbeException<string>>();

                var span = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                span.IsAllDataRequested.Should().BeTrue();

                var probeType = typeof(DiagnosticsProbeException<string>);

                // Guards the assertion below against becoming vacuous: it only has teeth while the probe is
                // generic, because a non-generic type renders both spellings identically.
                probeType.ToString().Should().NotBe(probeType.FullName);

                var exceptionEvent = ResolveSingleEvent(span, ChatterTelemetryTags.ExceptionEventName);
                ResolveEventTag(exceptionEvent, ChatterTelemetryTags.ExceptionType).Should().Be(probeType.ToString());
            }
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
    }
}
