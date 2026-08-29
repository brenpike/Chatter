using Chatter.CQRS.Diagnostics;
using Chatter.MessageBrokers.Diagnostics;
using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Tests.Receiving.Fakes;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Diagnostics
{
    /// <summary>
    /// The opted-in receive path: the producer's trace context is the parent, an ambient activity is only ever a
    /// link, one span covers a whole delivery including its Recovery retries, and a poisoned trace-context header
    /// never fails a delivery.
    /// </summary>
    [Collection(DiagnosticsCollection.Name)]
    public class WhenReceivingWithTracingEnabled : Testing.Core.Context
    {
        private const string ProducerTraceId = "0af7651916cd43dd8448eb211c80319c";
        private const string ProducerSpanId = "b7ad6b7169203331";
        private const string ProducerTraceParent = "00-" + ProducerTraceId + "-" + ProducerSpanId + "-01";

        [Fact]
        public async Task MustParentTheReceiveSpanToTheProducersTraceContext()
        {
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            using (var harness = new DiagnosticsReceiveHarness())
            {
                var delivered = harness.Deliver(BuildTraceContext(ProducerTraceParent));

                await harness.RunUntilSettledAsync(ReceiverCall.Ack);

                var span = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                span.Kind.Should().Be(ActivityKind.Consumer);
                span.TraceId.ToHexString().Should().Be(ProducerTraceId);
                span.ParentSpanId.ToHexString().Should().Be(ProducerSpanId);
                span.OperationName.Should().Be(BrokerDiagnostics.OperationTypes.Receive + " " + DiagnosticsReceiveHarness.ReceiverPath);
                span.GetTagItem(BrokerDiagnostics.MessagingSystem).Should().Be(DiagnosticsReceiveHarness.MessagingSystem);
                span.GetTagItem(BrokerDiagnostics.DestinationName).Should().Be(DiagnosticsReceiveHarness.ReceiverPath);
                span.GetTagItem(BrokerDiagnostics.MessageId).Should().Be(delivered.BrokeredMessage.MessageId);
            }
        }

        [Fact]
        public async Task MustExtractATraceParentDeliveredAsUtf8Bytes()
        {
            // ADR-0010 D5: a key declared outside MessageContext is non-core, so RabbitMqHeaderMarshaller preserves
            // it verbatim and the AMQP longstr surfaces in .NET as byte[]. This is the normal RabbitMQ case, not
            // defensive coding.
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            using (var harness = new DiagnosticsReceiveHarness())
            {
                harness.Deliver(new Dictionary<string, object>
                {
                    [TraceContextHeaders.TraceParent] = Encoding.UTF8.GetBytes(ProducerTraceParent),
                });

                await harness.RunUntilSettledAsync(ReceiverCall.Ack);

                var span = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                span.TraceId.ToHexString().Should().Be(ProducerTraceId);
                span.ParentSpanId.ToHexString().Should().Be(ProducerSpanId);
            }
        }

        [Fact]
        public async Task MustStartAFreshRootWhenTheTraceParentIsMalformed()
        {
            await AssertFreshRootAsync(BuildTraceContext("not-a-traceparent"));
        }

        [Fact]
        public async Task MustStartAFreshRootWhenTheTraceParentIsOversized()
        {
            // The extractor refuses anything longer than the W3C tracestate bound rather than decoding it, because
            // inbound headers are external, untrusted content.
            await AssertFreshRootAsync(BuildTraceContext(new string('a', 513)));
        }

        [Fact]
        public async Task MustStartAFreshRootWhenNoTraceParentIsPresent()
        {
            await AssertFreshRootAsync(new Dictionary<string, object>());
        }

        [Fact]
        public async Task MustLinkAnAmbientActivityThatDiffersFromTheExtractedParent()
        {
            // ADR-0010 D6: a message's causal parent is its producer. Re-parenting to whatever activity happened to
            // be current at delivery time would sever the distributed trace at every hop, so the ambient activity is
            // attached as a LINK and never promoted to parent.
            using (var foreignInstrumentation = new ForeignInstrumentationScope())
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            using (var harness = new DiagnosticsReceiveHarness())
            {
                harness.Deliver(BuildTraceContext(ProducerTraceParent));

                await harness.RunUntilSettledAsync(ReceiverCall.Ack);

                var span = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                span.ParentSpanId.ToHexString().Should().Be(ProducerSpanId);
                span.TraceId.ToHexString().Should().Be(ProducerTraceId);

                var link = span.Links.Should().ContainSingle().Subject;
                link.Context.TraceId.Should().Be(foreignInstrumentation.ForeignActivity.TraceId);
                link.Context.SpanId.Should().Be(foreignInstrumentation.ForeignActivity.SpanId);
            }
        }

        [Fact]
        public async Task MustLinkAnAmbientActivityWithoutParentingToItWhenNoTraceParentIsPresent()
        {
            // ADR-0010 D6 applies to a HEADERLESS delivery too. Neither StartActivity(name, kind) nor
            // StartActivity(name, kind, default(ActivityContext)) yields a root — Activity.Start falls back to
            // Activity.Current whenever no parent id and no parent span id were supplied — so a delivery with no
            // trace context would silently become a child of an unrelated host activity that reached the receive
            // loop through Task.Run. The ambient activity is a LINK here exactly as it is on the extracted branch.
            using (var foreignInstrumentation = new ForeignInstrumentationScope())
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            using (var harness = new DiagnosticsReceiveHarness())
            {
                harness.Deliver(new Dictionary<string, object>());

                await harness.RunUntilSettledAsync(ReceiverCall.Ack);

                var span = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                span.Parent.Should().BeNull();
                span.ParentSpanId.Should().Be(default(ActivitySpanId));
                span.TraceId.Should().NotBe(foreignInstrumentation.ForeignActivity.TraceId);

                var link = span.Links.Should().ContainSingle().Subject;
                link.Context.TraceId.Should().Be(foreignInstrumentation.ForeignActivity.TraceId);
                link.Context.SpanId.Should().Be(foreignInstrumentation.ForeignActivity.SpanId);
            }
        }

        [Fact]
        public async Task MustStartOneSpanPerDeliveryAcrossEveryRecoveryAttempt()
        {
            const int totalAttempts = 3;

            // ADR-0010 D7: Recovery WRAPS dispatch, so every retry for one delivery happens inside one span and the
            // attempt count is a tag on it rather than a span of its own.
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            using (var harness = new DiagnosticsReceiveHarness(failedDispatchCount: totalAttempts - 1, maxRecoveryAttempts: totalAttempts))
            {
                harness.Deliver(BuildTraceContext(ProducerTraceParent));

                await harness.RunUntilSettledAsync(ReceiverCall.Ack);

                harness.DispatchCount.Should().Be(totalAttempts);

                var span = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                span.GetTagItem(BrokerDiagnostics.ReceiveAttempts).Should().Be(totalAttempts);

                var retryEvents = span.Events.Where(candidate => candidate.Name == BrokerDiagnostics.ReceiveRetryEventName).ToArray();
                retryEvents.Should().HaveCount(totalAttempts - 1, "an event is added for every attempt after the first");
                ResolveEventTag(retryEvents[retryEvents.Length - 1], BrokerDiagnostics.ReceiveAttempts).Should().Be(totalAttempts);
            }
        }

        [Fact]
        public async Task MustTagTheReceiveSpanWithTheAckSettlement()
        {
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            using (var harness = new DiagnosticsReceiveHarness())
            {
                harness.Deliver(BuildTraceContext(ProducerTraceParent));

                await harness.RunUntilSettledAsync(ReceiverCall.Ack);

                var span = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                span.GetTagItem(BrokerDiagnostics.Settlement).Should().Be(BrokerDiagnostics.Settlements.Ack);
                span.Status.Should().Be(ActivityStatusCode.Unset);
                span.GetTagItem(ChatterTelemetryTags.ErrorType).Should().BeNull();
            }
        }

        [Fact]
        public async Task MustTagTheReceiveSpanWithTheNackSettlementWhenTheDeliveryFailsBelowTheMaximum()
        {
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            using (var harness = new DiagnosticsReceiveHarness(failedDispatchCount: int.MaxValue, deliveryCount: 1, maxReceiveAttempts: 10))
            {
                harness.Deliver(BuildTraceContext(ProducerTraceParent));

                await harness.RunUntilSettledAsync(ReceiverCall.Nack);

                var span = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                span.GetTagItem(BrokerDiagnostics.Settlement).Should().Be(BrokerDiagnostics.Settlements.Nack);
                span.Status.Should().Be(ActivityStatusCode.Error);
                span.GetTagItem(ChatterTelemetryTags.ErrorType).Should().Be(typeof(DiagnosticsProbeException).FullName);
            }
        }

        [Fact]
        public async Task MustTagTheReceiveSpanWithTheDeadletterSettlementWhenTheMessageIsPoisoned()
        {
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            using (var harness = new DiagnosticsReceiveHarness())
            {
                harness.Deliver(BuildTraceContext(ProducerTraceParent), body: new JsonBodyConverter().GetBytes("not-valid-json-object"));

                await harness.RunUntilSettledAsync(ReceiverCall.Deadletter);

                var span = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                span.GetTagItem(BrokerDiagnostics.Settlement).Should().Be(BrokerDiagnostics.Settlements.Deadletter);
                span.Status.Should().Be(ActivityStatusCode.Error);
                span.GetTagItem(ChatterTelemetryTags.ErrorType).Should().Be(typeof(PoisonedMessageException).FullName);
            }
        }

        /// <summary>
        /// Runs one delivery whose trace-context header cannot yield a remote context, and asserts the delivery still
        /// settles normally under a span that is a fresh root rather than a crash.
        /// </summary>
        private static async Task AssertFreshRootAsync(IDictionary<string, object> messageContextValues)
        {
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            using (var harness = new DiagnosticsReceiveHarness())
            {
                harness.Deliver(messageContextValues);

                await harness.RunUntilSettledAsync(ReceiverCall.Ack);

                var span = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                span.ParentSpanId.Should().Be(default(ActivitySpanId));
                span.TraceId.ToHexString().Should().NotBe(ProducerTraceId);
                span.Links.Should().BeEmpty();
            }
        }

        private static IDictionary<string, object> BuildTraceContext(string traceParent)
            => new Dictionary<string, object> { [TraceContextHeaders.TraceParent] = traceParent };

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
