using Chatter.CQRS.Diagnostics;
using Chatter.MessageBrokers.Diagnostics;
using Chatter.MessageBrokers.Sending;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Diagnostics
{
    /// <summary>
    /// The opted-in send path: one span per dispatch call, its trace context written onto every message the call
    /// carries, and propagation that survives lazy enumeration, outbox replay and a sampled-out span.
    /// </summary>
    /// <remarks>
    /// The batch count is the load-bearing case here. It is derived from the ONE enumeration the Router already
    /// performs, so a caller's lazily-built batch is walked at exactly the same moment, with the same side effects
    /// and the same exception origin, whether or not diagnostics are on — including in a host that opted into
    /// METRICS ONLY, which turns <see cref="BrokerDiagnostics.IsEnabled"/> true with no .NET
    /// <c>ActivityListener</c> attached at all.
    /// </remarks>
    [Collection(DiagnosticsCollection.Name)]
    public class WhenSendingWithTracingEnabled : Testing.Core.Context
    {
        private const int BatchSize = 3;
        private const int YieldedBeforeFault = 2;

        [Fact]
        public async Task MustStartExactlyOneSpanForABatchDispatchCall()
        {
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                var harness = new DiagnosticsSendHarness();

                await harness.PublishBatch(BatchSize);

                // ADR-0010 D7: one span per DISPATCH CALL, not per message — all N messages share one context
                // dictionary, so a per-message trace context is not representable without changing that shape.
                var span = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                span.Source.Name.Should().Be(BrokerDiagnostics.ActivitySourceName);
                span.Kind.Should().Be(ActivityKind.Producer);
                span.GetTagItem(BrokerDiagnostics.MessagingSystem).Should().Be(DiagnosticsSendHarness.MessagingSystem);
                span.GetTagItem(BrokerDiagnostics.OperationType).Should().Be(BrokerDiagnostics.OperationTypes.Send);
                span.GetTagItem(BrokerDiagnostics.BatchMessageCount).Should().Be(BatchSize);
                harness.RoutedMessages.Should().HaveCount(BatchSize);
            }
        }

        [Fact]
        public async Task MustNameTheSendSpanForItsOperationAndDestination()
        {
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                var harness = new DiagnosticsSendHarness();

                await harness.SendOne();

                var span = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                span.OperationName.Should().Be(BrokerDiagnostics.OperationTypes.Send + " " + DiagnosticsSendHarness.DestinationPath);
                span.GetTagItem(BrokerDiagnostics.DestinationName).Should().Be(DiagnosticsSendHarness.DestinationPath);
                span.GetTagItem(BrokerDiagnostics.BatchMessageCount).Should().Be(1);
            }
        }

        [Fact]
        public async Task MustWriteOneSharedTraceParentAcrossEveryMessageOfABatch()
        {
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                var harness = new DiagnosticsSendHarness();

                await harness.PublishBatch(BatchSize);

                var span = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                var expectedTraceParent = ResolveTraceParent(harness.RoutedMessages[0]);
                expectedTraceParent.Should().StartWith("00-" + span.TraceId.ToHexString() + "-" + span.SpanId.ToHexString());

                for (var index = 1; index < BatchSize; index++)
                {
                    // The messages share ONE context dictionary instance, which is precisely why per-message spans
                    // are unrepresentable and why one traceparent covers the whole batch.
                    harness.RoutedMessages[index].MessageContext.Should().BeSameAs(harness.RoutedMessages[0].MessageContext);
                    ResolveTraceParent(harness.RoutedMessages[index]).Should().Be(expectedTraceParent);
                }
            }
        }

        [Fact]
        public async Task MustInjectTraceContextThroughTheLazyDispatchIterator()
        {
            using (new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                var harness = new DiagnosticsSendHarness();

                await harness.PublishBatch(BatchSize);

                // BrokeredMessageDispatcher.Dispatch is a `yield return` iterator: the sequence handed to the Router
                // is not a materialised collection, so the injection the messages carry below happened at ENUMERATION
                // time inside the Router rather than eagerly at dispatch time.
                harness.RoutedSequence.Should().NotBeNull();
                harness.RoutedSequence.Should().NotBeAssignableTo<ICollection<OutboundBrokeredMessage>>();

                foreach (var routedMessage in harness.RoutedMessages)
                {
                    ResolveTraceParent(routedMessage).Should().NotBeNullOrWhiteSpace();
                }
            }
        }

        [Fact]
        public async Task MustNotWalkTheCallersBatchBeforeTheRouterDoes()
        {
            using (new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                var harness = new DiagnosticsSendHarness();
                var batch = harness.CreateSinglePassBatch(BatchSize);

                await harness.PublishSequence(batch);

                // INVARIANT: the batch count is derived from the Router's own single enumeration, never from a walk
                // or copy of the caller's sequence. Instrumentation that walked it to count it would show here as an
                // enumerator requested BEFORE the Router was entered, or as a second enumeration the batch refuses.
                batch.EnumeratorRequestCount.Should().Be(1);
                harness.DispatchTimeline.Should().Equal(BuildExpectedPullTimeline(BatchSize));
            }
        }

        [Fact]
        public async Task MustTagTheBatchCountTakenFromTheRoutersEnumerationOfALazyBatch()
        {
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                var harness = new DiagnosticsSendHarness();
                var batch = harness.CreateSinglePassBatch(BatchSize);

                await harness.PublishSequence(batch);

                var span = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                span.GetTagItem(BrokerDiagnostics.BatchMessageCount).Should().Be(BatchSize);
                batch.YieldedCount.Should().Be(BatchSize);
                harness.RoutedMessages.Should().HaveCount(BatchSize);
            }
        }

        [Fact]
        public async Task MustTagTheYieldedCountWhenTheCallersBatchRaisesPartway()
        {
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                var harness = new DiagnosticsSendHarness();
                var batch = harness.CreateSinglePassBatch(BatchSize, faultAfterYieldCount: YieldedBeforeFault);

                Func<Task> publish = () => harness.PublishSequence(batch);

                // The caller's OWN exception instance surfaces, raised from the Router's enumeration rather than
                // from an earlier instrumentation walk, so the failure's origin is unmoved.
                (await publish.Should().ThrowAsync<DiagnosticsProbeException>()).Which.Should().BeSameAs(batch.Fault);

                var span = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                span.GetTagItem(BrokerDiagnostics.BatchMessageCount).Should().Be(YieldedBeforeFault);
                span.GetTagItem(ChatterTelemetryTags.ErrorType).Should().Be(typeof(DiagnosticsProbeException).FullName);
                batch.YieldedCount.Should().Be(YieldedBeforeFault);
                harness.DispatchTimeline.Should().Equal(BuildExpectedPullTimeline(YieldedBeforeFault));
            }
        }

        [Fact]
        public async Task MustTagAZeroBatchCountWhenTheRouterNeverEnumerates()
        {
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                var harness = new DiagnosticsSendHarness(routerEnumerates: false);
                var batch = harness.CreateSinglePassBatch(BatchSize);

                await harness.PublishSequence(batch);

                // A Router that hands the sequence on without walking it has yielded nothing, so zero is the truthful
                // count for this dispatch call rather than a total the call never actually carried.
                var span = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                span.GetTagItem(BrokerDiagnostics.BatchMessageCount).Should().Be(0);
                batch.EnumeratorRequestCount.Should().Be(0);
                batch.YieldedCount.Should().Be(0);
                harness.DispatchTimeline.Should().Equal(DiagnosticsSendHarness.RouterEnteredEntry);
            }
        }

        [Fact]
        public async Task MustNotWalkTheCallersBatchWhenOnlyMetricsAreOptedInto()
        {
            // BrokerDiagnostics.IsEnabled is HasListeners OR an enabled instrument, so a host carrying ONLY a .NET
            // MeterListener and no .NET ActivityListener at all still takes the instrumented dispatch path. Eager
            // materialisation would move the caller's iterator side effects in THAT host too, which is why
            // non-materialisation is pinned here and not only under tracing.
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            {
                var harness = new DiagnosticsSendHarness();
                var batch = harness.CreateSinglePassBatch(BatchSize);

                BrokerDiagnostics.IsEnabled.Should().BeTrue();
                BrokerDiagnostics.Source.HasListeners().Should().BeFalse();

                await harness.PublishSequence(batch);

                batch.EnumeratorRequestCount.Should().Be(1);
                harness.DispatchTimeline.Should().Equal(BuildExpectedPullTimeline(BatchSize));

                var sentMessages = meterScope.MeasurementsFor(BrokerDiagnostics.SentMessagesInstrumentName).Should().ContainSingle().Subject;
                sentMessages.Value.Should().Be(BatchSize);
            }
        }

        [Fact]
        public async Task MustCountOnlyTheYieldedMessagesOnTheSentInstrumentWhenTheCallersBatchRaisesPartway()
        {
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            {
                var harness = new DiagnosticsSendHarness();
                var batch = harness.CreateSinglePassBatch(BatchSize, faultAfterYieldCount: YieldedBeforeFault);

                Func<Task> publish = () => harness.PublishSequence(batch);

                await publish.Should().ThrowAsync<DiagnosticsProbeException>();

                // messaging.client.sent.messages counts what was actually handed to broker infrastructure, so a
                // dispatch that raises partway reports the YIELDED count and not the batch the caller intended.
                var sentMessages = meterScope.MeasurementsFor(BrokerDiagnostics.SentMessagesInstrumentName).Should().ContainSingle().Subject;
                sentMessages.Value.Should().Be(YieldedBeforeFault);
                sentMessages.TryGetTag(ChatterTelemetryTags.ErrorType, out var errorType).Should().BeTrue();
                errorType.Should().Be(typeof(DiagnosticsProbeException).FullName);
            }
        }

        [Fact]
        public async Task MustSurviveOutboxReplayAsAString()
        {
            using (new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                var harness = new DiagnosticsSendHarness();

                await harness.SendOne();

                var routedMessage = harness.RoutedMessages.Should().ContainSingle().Subject;
                var sentTraceParent = ResolveTraceParent(routedMessage);

                // The outbox persists the whole context as JSON and rehydrates it through this one materialisation
                // recipe. A W3C traceparent is not ISO-8601-shaped, so JsonElement.TryGetDateTime must decline it and
                // the value must round-trip as a STRING rather than being coerced to a DateTime.
                var persisted = JsonSerializer.Serialize(routedMessage.MessageContext, ChatterJson.Options);
                var replayed = MessageContext.MaterializePersistedContext(persisted);

                replayed[TraceContextHeaders.TraceParent].Should().BeOfType<string>().And.Be(sentTraceParent);
            }
        }

        [Fact]
        public async Task MustPropagateFromTheAmbientContextWhenTheSendSpanIsSampledOut()
        {
            // ADR-0010 D9: head sampling makes StartActivity return null while Chatter .NET ActivityListeners are
            // still attached. A downstream hop samples independently, so the trace must not be broken — propagation
            // continues from the ambient context even though no Chatter span exists.
            using (var foreignInstrumentation = new ForeignInstrumentationScope())
            using (var sampledOutScope = new SampledOutActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                var harness = new DiagnosticsSendHarness();

                BrokerDiagnostics.Source.HasListeners().Should().BeTrue();

                await harness.SendOne();

                sampledOutScope.StartedActivities.Should().BeEmpty();

                var routedMessage = harness.RoutedMessages.Should().ContainSingle().Subject;
                ResolveTraceParent(routedMessage).Should().StartWith(
                    "00-" + foreignInstrumentation.ForeignActivity.TraceId.ToHexString() + "-" + foreignInstrumentation.ForeignActivity.SpanId.ToHexString());
            }
        }

        /// <summary>
        /// The timeline a single-pass batch must produce: the Router entered FIRST, then exactly one enumerator
        /// request, then one entry per message the Router pulled.
        /// </summary>
        private static string[] BuildExpectedPullTimeline(int yieldedCount)
        {
            var timeline = new List<string>
            {
                DiagnosticsSendHarness.RouterEnteredEntry,
                SinglePassEventSequence.EnumeratorRequestedEntry,
            };

            for (var index = 0; index < yieldedCount; index++)
            {
                timeline.Add(SinglePassEventSequence.YieldedEntryPrefix + index);
            }

            return timeline.ToArray();
        }

        private static string ResolveTraceParent(OutboundBrokeredMessage outboundMessage)
        {
            outboundMessage.MessageContext.TryGetValue(TraceContextHeaders.TraceParent, out var traceParent)
                .Should().BeTrue("the outbound message should carry a '" + TraceContextHeaders.TraceParent + "'");

            return traceParent.Should().BeOfType<string>().Subject;
        }

        /// <summary>
        /// Attaches a .NET <see cref="ActivityListener"/> that samples <see cref="ActivitySamplingResult.None"/>, so
        /// <see cref="ActivitySource.HasListeners"/> is <c>true</c> while <see cref="ActivitySource.StartActivity(string, ActivityKind)"/>
        /// returns <c>null</c>.
        /// </summary>
        /// <remarks>
        /// The shared <see cref="RecordingActivityScope"/> forces <see cref="ActivitySamplingResult.AllDataAndRecorded"/>
        /// by design, so it cannot express the sampled-out side of the gate this scope exists to exercise.
        /// </remarks>
        private sealed class SampledOutActivityScope : IDisposable
        {
            private readonly ActivityListener _netActivityListener;
            private readonly List<Activity> _startedActivities = new List<Activity>();
            private readonly Activity _priorActivity;

            public SampledOutActivityScope(string sourceName)
            {
                _priorActivity = Activity.Current;

                _netActivityListener = new ActivityListener
                {
                    ShouldListenTo = source => source.Name == sourceName,
                    Sample = SampleNone,
                    SampleUsingParentId = SampleNoneFromParentId,
                    ActivityStarted = _startedActivities.Add,
                };

                ActivitySource.AddActivityListener(_netActivityListener);
            }

            public IReadOnlyList<Activity> StartedActivities => _startedActivities.ToArray();

            public void Dispose()
            {
                _netActivityListener.Dispose();
                Activity.Current = _priorActivity;
            }

            private static ActivitySamplingResult SampleNone(ref ActivityCreationOptions<ActivityContext> options)
                => ActivitySamplingResult.None;

            private static ActivitySamplingResult SampleNoneFromParentId(ref ActivityCreationOptions<string> options)
                => ActivitySamplingResult.None;
        }
    }
}
