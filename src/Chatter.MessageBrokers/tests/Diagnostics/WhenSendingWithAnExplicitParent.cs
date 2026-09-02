using Chatter.MessageBrokers.Diagnostics;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Diagnostics
{
    /// <summary>
    /// The DEFERRED send path: a dispatch call whose causal parent is a trace context read back from storage rather
    /// than whatever <see cref="Activity"/> happens to be current. An outbox drain sends minutes after, and in
    /// another process from, the transaction that wrote the row, so the ambient activity at drain time is the drain
    /// loop — never the writer.
    /// </summary>
    [Collection(DiagnosticsCollection.Name)]
    public class WhenSendingWithAnExplicitParent : Testing.Core.Context
    {
        // The W3C Trace Context specification's own example traceparent, standing in for the value an outbox row
        // persisted at write time.
        private const string PersistedTraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        private const string PersistedTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
        private const string PersistedSpanId = "00f067aa0ba902b7";
        private const int BatchSize = 3;

        [Fact]
        public void MustParentTheSendSpanToTheSuppliedContextRatherThanTheAmbientActivity()
        {
            using (var foreignInstrumentation = new ForeignInstrumentationScope())
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                var persistedParent = ActivityContext.Parse(PersistedTraceParent, null);

                using (var span = BrokerDiagnostics.StartSend(
                    DiagnosticsSendHarness.MessagingSystem,
                    BrokerDiagnostics.OperationTypes.Send,
                    DiagnosticsSendHarness.DestinationPath,
                    1,
                    persistedParent))
                {
                    span.Should().NotBeNull("the drain hop is a broker send in its own right and must be observable");
                }

                var stopped = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                stopped.TraceId.ToHexString().Should().Be(PersistedTraceId, "the drain hop belongs to the trace that wrote the row");
                stopped.ParentSpanId.ToHexString().Should().Be(PersistedSpanId);
                stopped.TraceId.Should().NotBe(foreignInstrumentation.ForeignActivity.TraceId,
                    "the ambient activity at drain time is the drain loop, not the writer");
            }
        }

        [Fact]
        public void MustNotFallBackToTheAmbientActivityWhenNoPersistedParentWasFound()
        {
            // THE LOAD-BEARING CASE. A default ActivityContext means "the caller found no persisted parent", never
            // "use whatever is current": falling back would graft the drain hop onto the drain loop's trace and
            // report a causality that never happened.
            using (var foreignInstrumentation = new ForeignInstrumentationScope())
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                using (var span = BrokerDiagnostics.StartSend(
                    DiagnosticsSendHarness.MessagingSystem,
                    BrokerDiagnostics.OperationTypes.Send,
                    DiagnosticsSendHarness.DestinationPath,
                    1,
                    default(ActivityContext)))
                {
                    span.Should().NotBeNull("a drain hop with no persisted parent is still a broker send worth a span");
                }

                var stopped = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                stopped.TraceId.Should().NotBe(foreignInstrumentation.ForeignActivity.TraceId,
                    "an absent parent must start a fresh trace, never adopt the ambient one");
                stopped.ParentSpanId.Should().Be(default(ActivitySpanId), "the span is a root");
                stopped.Parent.Should().BeNull();
            }
        }

        [Fact]
        public void MustCarryTheSameSendAttributesAsTheParentlessOverload()
        {
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                var persistedParent = ActivityContext.Parse(PersistedTraceParent, null);

                using (BrokerDiagnostics.StartSend(
                    DiagnosticsSendHarness.MessagingSystem,
                    BrokerDiagnostics.OperationTypes.Send,
                    DiagnosticsSendHarness.DestinationPath,
                    BatchSize,
                    persistedParent))
                {
                }

                // Only the parent differs between the two overloads; a deferred send is still a send, so it must be
                // indistinguishable from an immediate one on every attribute a collector aggregates by.
                var stopped = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                stopped.Source.Name.Should().Be(BrokerDiagnostics.ActivitySourceName);
                stopped.Kind.Should().Be(ActivityKind.Producer);
                stopped.DisplayName.Should().Be(BrokerDiagnostics.OperationTypes.Send + " " + DiagnosticsSendHarness.DestinationPath);
                stopped.GetTagItem(BrokerDiagnostics.MessagingSystem).Should().Be(DiagnosticsSendHarness.MessagingSystem);
                stopped.GetTagItem(BrokerDiagnostics.OperationName).Should().Be(BrokerDiagnostics.OperationTypes.Send);
                stopped.GetTagItem(BrokerDiagnostics.OperationType).Should().Be(BrokerDiagnostics.OperationTypes.Send);
                stopped.GetTagItem(BrokerDiagnostics.DestinationName).Should().Be(DiagnosticsSendHarness.DestinationPath);
                stopped.GetTagItem(BrokerDiagnostics.BatchMessageCount).Should().Be(BatchSize);
            }
        }

        [Fact]
        public void MustLinkTheAmbientActivityToTheParentedSendSpan()
        {
            // ADR-0010 D6: the ambient is never promoted to parent, but it is not discarded either — the drain pass
            // that performed the send is real causality and rides along as a link.
            using (var foreignInstrumentation = new ForeignInstrumentationScope())
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                var persistedParent = ActivityContext.Parse(PersistedTraceParent, null);

                using (BrokerDiagnostics.StartSend(
                    DiagnosticsSendHarness.MessagingSystem,
                    BrokerDiagnostics.OperationTypes.Send,
                    DiagnosticsSendHarness.DestinationPath,
                    1,
                    persistedParent))
                {
                }

                var stopped = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                stopped.Links.Should().ContainSingle().Which.Context.Should().Be(foreignInstrumentation.ForeignActivity.Context);
            }
        }

        [Fact]
        public void MustLinkTheAmbientActivityToTheParentlessSendSpan()
        {
            using (var foreignInstrumentation = new ForeignInstrumentationScope())
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                using (BrokerDiagnostics.StartSend(
                    DiagnosticsSendHarness.MessagingSystem,
                    BrokerDiagnostics.OperationTypes.Send,
                    DiagnosticsSendHarness.DestinationPath,
                    1,
                    default(ActivityContext)))
                {
                }

                var stopped = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                stopped.Links.Should().ContainSingle().Which.Context.Should().Be(foreignInstrumentation.ForeignActivity.Context);
            }
        }

        [Fact]
        public void MustLeaveTheAmbientActivityCurrentWhenNoSendSpanIsSampled()
        {
            // ADR-0010 D9's head-sampling condition. The parentless branch clears Activity.Current so the fallback
            // cannot fire, so when nothing is sampled there is no span for the suppression to serve and the ambient
            // must already be back before this returns.
            using (var foreignInstrumentation = new ForeignInstrumentationScope())
            using (var sampledOutScope = new SampledOutActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                var persistedParent = ActivityContext.Parse(PersistedTraceParent, null);

                BrokerDiagnostics.StartSend(
                    DiagnosticsSendHarness.MessagingSystem,
                    BrokerDiagnostics.OperationTypes.Send,
                    DiagnosticsSendHarness.DestinationPath,
                    1,
                    persistedParent).Should().BeNull();

                Activity.Current.Should().BeSameAs(foreignInstrumentation.ForeignActivity);

                BrokerDiagnostics.StartSend(
                    DiagnosticsSendHarness.MessagingSystem,
                    BrokerDiagnostics.OperationTypes.Send,
                    DiagnosticsSendHarness.DestinationPath,
                    1,
                    default(ActivityContext)).Should().BeNull();

                Activity.Current.Should().BeSameAs(foreignInstrumentation.ForeignActivity);
                sampledOutScope.StartedActivities.Should().BeEmpty();
            }
        }

        [Fact]
        public void MustStartNoSpanAndLeaveTheAmbientAloneWhileChatterTracingIsNotOptedInto()
        {
            using (var foreignInstrumentation = new ForeignInstrumentationScope())
            {
                BrokerDiagnostics.Source.HasListeners().Should().BeFalse();

                BrokerDiagnostics.StartSend(
                    DiagnosticsSendHarness.MessagingSystem,
                    BrokerDiagnostics.OperationTypes.Send,
                    DiagnosticsSendHarness.DestinationPath,
                    1,
                    default(ActivityContext)).Should().BeNull();

                // The off-guard is the FIRST statement, so nothing below it runs — including the Activity.Current
                // write the parentless branch would otherwise perform (ADR-0010 R1, R3).
                Activity.Current.Should().BeSameAs(foreignInstrumentation.ForeignActivity);
            }
        }

        [Fact]
        public void MustNotAllocateWhileStartingADeferredSendSpanThatIsOff()
        {
            BrokerDiagnostics.Source.HasListeners().Should().BeFalse();

            var persistedParent = ActivityContext.Parse(PersistedTraceParent, null);

            var measurement = GuardCostProbe.Measure<Activity>(
                () => BrokerDiagnostics.StartSend(
                    DiagnosticsSendHarness.MessagingSystem,
                    BrokerDiagnostics.OperationTypes.Send,
                    DiagnosticsSendHarness.DestinationPath,
                    1,
                    persistedParent));

            measurement.MedianAllocatedBytesPerBatch.Should().Be(0, "no span name, link or activity may be built while off: " + measurement);
        }

        [Fact]
        public void MustLeaveTheParentlessOverloadParentingToTheAmbientActivity()
        {
            // The existing overload is untouched: an immediate send still adopts the activity that is genuinely its
            // caller. Only the deferred overload refuses the ambient, and only because its caller's parent is
            // elsewhere.
            using (var foreignInstrumentation = new ForeignInstrumentationScope())
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                using (BrokerDiagnostics.StartSend(
                    DiagnosticsSendHarness.MessagingSystem,
                    BrokerDiagnostics.OperationTypes.Send,
                    DiagnosticsSendHarness.DestinationPath,
                    1))
                {
                }

                var stopped = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                stopped.Parent.Should().BeSameAs(foreignInstrumentation.ForeignActivity);
                stopped.TraceId.Should().Be(foreignInstrumentation.ForeignActivity.TraceId);
                stopped.Links.Should().BeEmpty("the ambient IS the parent here, so linking it as well would double-count it");
            }
        }

        [Fact]
        public void MustKeepTheParentedSendSpanBehindSendScopeRatherThanAPublicStartSendOverload()
        {
            // INVARIANT: no public Chatter surface hands back a send span whose start suppressed the ambient
            // activity while leaving the restore to the caller. Stopping such a span sets Activity.Current to the
            // null parent it recorded, so the host's ambient would be lost for the rest of that async flow.
            var publicContextTakingStartSend = typeof(BrokerDiagnostics)
                .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(candidate => candidate.Name == "StartSend")
                .Where(candidate => candidate.GetParameters().Any(parameter => parameter.ParameterType == typeof(ActivityContext)))
                .ToArray();

            publicContextTakingStartSend.Should().BeEmpty(
                "a parented send span is reachable only through SendScope, which owns the ambient restore");

            typeof(SendScope).GetMethod(
                nameof(SendScope.Open),
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string), typeof(string), typeof(string), typeof(int), typeof(ActivityContext) },
                null)
                .Should().NotBeNull("SendScope.Open is the public door to a parented send span, and its Dispose restores the ambient");
        }
    }
}
