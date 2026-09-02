using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Diagnostics;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Routing.Context;
using Chatter.MessageBrokers.Tests.Receiving.Fakes;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Diagnostics
{
    /// <summary>
    /// The off-state proof for ADR-0010's off-guard, asserted AT THE WIRE LEVEL: with Chatter never opted into, the
    /// outbound context a message carries must be indistinguishable from the untraced baseline.
    /// </summary>
    /// <remarks>
    /// The load-bearing shape here is FOREIGN instrumentation — an unrelated library's own .NET
    /// <c>ActivityListener</c> making <see cref="Activity.Current"/> non-null — because that is the very common host
    /// shape in which a guard keyed on <c>Activity.Current is null</c> would read as "tracing on" and would put a
    /// <c>traceparent</c> on the wire an application never asked for (ADR-0010 R2/R3).
    /// The METRICS-ONLY case is covered separately and deliberately: an application that subscribes a .NET
    /// <c>MeterListener</c> without any .NET <c>ActivityListener</c> turns <see cref="BrokerDiagnostics.IsEnabled"/>
    /// TRUE, so the instrumented dispatch path runs — and must still write nothing to the wire, because injection is
    /// keyed on <see cref="ActivitySource.HasListeners"/> rather than on that broader gate.
    /// </remarks>
    [Collection(DiagnosticsCollection.Name)]
    public class WhenChatterTracingIsNotOptedInto : Testing.Core.Context
    {
        private const string ForeignMeterName = "Contoso.Unrelated.Metrics";

        [Fact]
        public void MustReportDiagnosticsDisabledInAnEmptyProcess()
        {
            BrokerDiagnostics.Source.HasListeners().Should().BeFalse();
            BrokerDiagnostics.IsEnabled.Should().BeFalse();
        }

        [Fact]
        public void MustReportDiagnosticsDisabledWhileForeignInstrumentationIsRunning()
        {
            using (var foreignInstrumentation = new ForeignInstrumentationScope())
            {
                Activity.Current.Should().BeSameAs(foreignInstrumentation.ForeignActivity);

                // INVARIANT: ADR-0010 R2/R3 — a non-null ambient Activity is NOT an opt-in.
                BrokerDiagnostics.Source.HasListeners().Should().BeFalse();
                BrokerDiagnostics.IsEnabled.Should().BeFalse();
            }
        }

        [Fact]
        public async Task MustWriteTheSameOutboundContextKeysAsTheUntracedBaselineWhileForeignInstrumentationIsRunning()
        {
            var baselineHarness = new DiagnosticsSendHarness();
            await baselineHarness.SendOne();
            var baselineKeys = baselineHarness.RoutedContextKeys();

            using (new ForeignInstrumentationScope())
            {
                var foreignHarness = new DiagnosticsSendHarness();
                await foreignHarness.SendOne();

                foreignHarness.RoutedContextKeys().Should().Equal(baselineKeys);
                BrokerDiagnostics.IsEnabled.Should().BeFalse();
            }
        }

        [Fact]
        public async Task MustNotWriteTraceContextOntoTheWireWhileForeignInstrumentationIsRunning()
        {
            using (new ForeignInstrumentationScope())
            {
                var harness = new DiagnosticsSendHarness();

                await harness.SendOne();

                var outboundContext = harness.RoutedMessages.Should().ContainSingle().Subject.MessageContext;
                outboundContext.Should().NotContainKey(TraceContextHeaders.TraceParent);
                outboundContext.Should().NotContainKey(TraceContextHeaders.TraceState);
            }
        }

        [Fact]
        public async Task MustLeaveTheInboundTraceParentUnchangedWhenForwardingOrReplyingWhileForeignInstrumentationIsRunning()
        {
            // The forward and reply sites each keep their OWN off-guard, so with Chatter never opted into neither
            // opens a send scope at all and the traceparent the delivery arrived with rides out untouched. A site
            // that lost its guard would overwrite it with this hop's context, which is the wire write ADR-0010 R1/R2
            // exist to prevent — and it would be invisible to a span assertion, because there is no listener to
            // record a span with.
            using (new ForeignInstrumentationScope())
            {
                var forwardRouting = new CapturingRoutingHarness();
                var replyRouting = new CapturingRoutingHarness();

                await new ForwardingRouter(forwardRouting.Router, forwardRouting.MessageIdGenerator)
                    .Route(CapturingRoutingHarness.BuildInbound(ProducerTraceParent), ForwardDestination, null);

                await new ReplyRouter(replyRouting.Router, replyRouting.MessageIdGenerator)
                    .Route(CapturingRoutingHarness.BuildInbound(ProducerTraceParent), null, new ReplyToRoutingContext(ReplyDestination, ReplyGroupId));

                BrokerDiagnostics.IsEnabled.Should().BeFalse();
                forwardRouting.RoutedMessage.MessageContext[TraceContextHeaders.TraceParent].Should().Be(ProducerTraceParent);
                replyRouting.RoutedMessage.MessageContext[TraceContextHeaders.TraceParent].Should().Be(ProducerTraceParent);
            }
        }

        [Fact]
        public async Task MustNotStartAReceiveSpanWhileForeignInstrumentationIsRunning()
        {
            using (var foreignInstrumentation = new ForeignInstrumentationScope())
            using (var harness = new DiagnosticsReceiveHarness())
            {
                Activity ambientWhileDispatching = null;
                harness.OnDispatch = _ => ambientWhileDispatching = Activity.Current;

                // The delivery CARRIES a producer trace context, so a receive path that extracted while off would be
                // visible as a span parented to it. No span is started at all, so nothing extracts it.
                harness.Deliver(new Dictionary<string, object> { [TraceContextHeaders.TraceParent] = ProducerTraceParent });

                await harness.RunUntilSettledAsync(ReceiverCall.Ack);

                ambientWhileDispatching.Should().BeSameAs(foreignInstrumentation.ForeignActivity);
                BrokerDiagnostics.IsEnabled.Should().BeFalse();
            }
        }

        [Fact]
        public async Task MustNotWriteTraceContextOntoTheWireWhenOnlyMetricsAreOptedInto()
        {
            // A .NET MeterListener without any .NET ActivityListener: BrokerDiagnostics.IsEnabled is TRUE, so the
            // instrumented dispatch path runs and the measurement below proves it ran. Injection is nonetheless keyed
            // on ActivitySource.HasListeners, so the sampled-out fallback resolves to a null Activity and the wire is
            // untouched even though an ambient Activity exists.
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            using (new ForeignInstrumentationScope())
            {
                var harness = new DiagnosticsSendHarness();

                BrokerDiagnostics.IsEnabled.Should().BeTrue();
                BrokerDiagnostics.Source.HasListeners().Should().BeFalse();

                await harness.SendOne();

                var outboundContext = harness.RoutedMessages.Should().ContainSingle().Subject.MessageContext;
                outboundContext.Should().NotContainKey(TraceContextHeaders.TraceParent);
                outboundContext.Should().NotContainKey(TraceContextHeaders.TraceState);
                meterScope.MeasurementsFor(BrokerDiagnostics.SentMessagesInstrumentName).Should().ContainSingle();
            }
        }

        [Fact]
        public void MustNotAllocateWhileEvaluatingTheOffGuard()
        {
            BrokerDiagnostics.IsEnabled.Should().BeFalse();

            var measurement = GuardCostProbe.Measure<bool>(() => BrokerDiagnostics.IsEnabled);

            measurement.MedianAllocatedBytesPerBatch.Should().Be(0, "the off-guard is a boolean read: " + measurement);
        }

        [Fact]
        public void MustNotAllocateWhileStartingASendSpanThatIsOff()
        {
            BrokerDiagnostics.Source.HasListeners().Should().BeFalse();

            var measurement = GuardCostProbe.Measure<Activity>(
                () => BrokerDiagnostics.StartSend(DiagnosticsSendHarness.MessagingSystem, BrokerDiagnostics.OperationTypes.Send, DiagnosticsSendHarness.DestinationPath, 1));

            measurement.MedianAllocatedBytesPerBatch.Should().Be(0, "no span name, tag or activity may be built while off: " + measurement);
        }

        [Fact]
        public void MustNotAllocateWhileStartingAReceiveSpanThatIsOff()
        {
            BrokerDiagnostics.Source.HasListeners().Should().BeFalse();

            var messageContext = (IReadOnlyDictionary<string, object>)new Dictionary<string, object>
            {
                [TraceContextHeaders.TraceParent] = ProducerTraceParent,
            };

            var measurement = GuardCostProbe.Measure<BrokerDiagnostics.ReceiveSpan>(
                () => BrokerDiagnostics.StartReceive(DiagnosticsReceiveHarness.MessagingSystem, BrokerDiagnostics.OperationTypes.Receive, DiagnosticsReceiveHarness.ReceiverPath, "message-id", messageContext));

            measurement.MedianAllocatedBytesPerBatch.Should().Be(0, "no trace context may be extracted while off: " + measurement);
        }

        [Fact]
        public void MustNotAllocateWhileRecordingASendThatIsOff()
        {
            BrokerDiagnostics.IsEnabled.Should().BeFalse();

            var startTimestamp = Stopwatch.GetTimestamp();
            var measurement = GuardCostProbe.Measure(
                () => BrokerDiagnostics.RecordSend(startTimestamp, DiagnosticsSendHarness.MessagingSystem, BrokerDiagnostics.OperationTypes.Send, DiagnosticsSendHarness.DestinationPath, 1, null));

            measurement.MedianAllocatedBytesPerBatch.Should().Be(0, "no tag list may be built while off: " + measurement);
        }

        [Fact]
        public void MustNotAllocateWhileRecordingAReceiveThatIsOff()
        {
            BrokerDiagnostics.IsEnabled.Should().BeFalse();

            var startTimestamp = Stopwatch.GetTimestamp();
            var failure = new DiagnosticsProbeException("never recorded");

            var successMeasurement = GuardCostProbe.Measure(
                () => BrokerDiagnostics.RecordReceive(startTimestamp, DiagnosticsReceiveHarness.MessagingSystem, BrokerDiagnostics.OperationTypes.Receive, DiagnosticsReceiveHarness.ReceiverPath, (Exception)null));
            var failureMeasurement = GuardCostProbe.Measure(
                () => BrokerDiagnostics.RecordReceive(startTimestamp, DiagnosticsReceiveHarness.MessagingSystem, BrokerDiagnostics.OperationTypes.Receive, DiagnosticsReceiveHarness.ReceiverPath, failure));

            successMeasurement.MedianAllocatedBytesPerBatch.Should().Be(0, "no tag list may be built while off: " + successMeasurement);
            failureMeasurement.MedianAllocatedBytesPerBatch.Should().Be(0, "no error type may be resolved while off: " + failureMeasurement);
        }

        [Fact]
        public void MustNotAllocateWhileRecordingAReceiveThatFailedWithoutAnExceptionWhileOff()
        {
            BrokerDiagnostics.IsEnabled.Should().BeFalse();

            var startTimestamp = Stopwatch.GetTimestamp();
            var measurement = GuardCostProbe.Measure(
                () => BrokerDiagnostics.RecordReceive(startTimestamp, DiagnosticsReceiveHarness.MessagingSystem, BrokerDiagnostics.OperationTypes.Receive, DiagnosticsReceiveHarness.ReceiverPath, BrokerDiagnostics.ErrorTypes.SettlementFailed));

            measurement.MedianAllocatedBytesPerBatch.Should().Be(0, "no tag list may be built while off: " + measurement);
        }

        [Fact]
        public void MustNotAllocateWhileRecordingFailureAndSettlementThatAreOff()
        {
            BrokerDiagnostics.Source.HasListeners().Should().BeFalse();

            var failure = new DiagnosticsProbeException("never recorded");

            var failureMeasurement = GuardCostProbe.Measure(() => BrokerDiagnostics.RecordFailure(null, failure));
            var settlementMeasurement = GuardCostProbe.Measure(() => BrokerDiagnostics.RecordSettlement(null, BrokerDiagnostics.Settlements.Ack));

            failureMeasurement.MedianAllocatedBytesPerBatch.Should().Be(0, "no exception detail may be materialised while off: " + failureMeasurement);
            settlementMeasurement.MedianAllocatedBytesPerBatch.Should().Be(0, "no tag may be set while off: " + settlementMeasurement);
        }

        [Fact]
        public void MustNotAllocateWhileRecordingAFailureWithoutAnExceptionThatIsOff()
        {
            BrokerDiagnostics.Source.HasListeners().Should().BeFalse();

            var measurement = GuardCostProbe.Measure(
                () => BrokerDiagnostics.RecordFailure(null, BrokerDiagnostics.ErrorTypes.SettlementFailed, "never recorded"));

            measurement.MedianAllocatedBytesPerBatch.Should().Be(0, "no status or error type may be set while off: " + measurement);
        }

        [Fact]
        public void MustNotAllocateWhileInjectingWithoutASpan()
        {
            // ADR-0010 R2: injection is a pure function of an explicitly passed Activity, and a null one — which is
            // what every caller holds while Chatter tracing is off — returns before touching the dictionary.
            var messageContext = new Dictionary<string, object>();

            var measurement = GuardCostProbe.Measure(() => TraceContextPropagator.Inject(null, messageContext));

            measurement.MedianAllocatedBytesPerBatch.Should().Be(0, "a null Activity must return before any header work: " + measurement);
            messageContext.Should().BeEmpty();
        }

        /// <summary>A well-formed W3C <c>traceparent</c> standing in for an upstream producer's trace context.</summary>
        private const string ProducerTraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

        private const string ForwardDestination = "forward-destination";
        private const string ReplyDestination = "reply-destination";
        private const string ReplyGroupId = "reply-group";
    }
}
