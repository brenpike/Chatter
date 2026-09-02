using Chatter.CQRS.Diagnostics;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Diagnostics;
using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Routing.Context;
using Chatter.MessageBrokers.Sending;
using Chatter.MessageBrokers.Tests.Receiving.Fakes;
using Chatter.Testing.Core.Diagnostics;
using FluentAssertions;
using Moq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Diagnostics
{
    /// <summary>
    /// The forward and reply paths, where the outbound message is handed the INBOUND message's context dictionary by
    /// reference. Writing this hop's trace context therefore mutates the inbound record in place. That aliasing is
    /// DECIDED and accepted, and this class pins both halves of the decision: the overwrite that makes a stale
    /// upstream <c>traceparent</c> unable to ride out, and the ORDERING RULE that makes the mutation safe — the
    /// receive span is built from the ORIGINAL inbound context at Brokered Message Receiver worker entry, strictly
    /// before any handler can forward or reply.
    /// </summary>
    [Collection(DiagnosticsCollection.Name)]
    public class WhenForwardingOrReplying : Testing.Core.Context
    {
        private const string ProducerTraceId = "0af7651916cd43dd8448eb211c80319c";
        private const string ProducerSpanId = "b7ad6b7169203331";
        private const string ProducerTraceParent = "00-" + ProducerTraceId + "-" + ProducerSpanId + "-01";
        private const string StaleTraceParent = "00-11111111111111111111111111111111-2222222222222222-01";
        private const string ForwardDestination = "forward-destination";
        private const string ReplyDestination = "reply-destination";
        private const string ReplyGroupId = "reply-group";

        [Fact]
        public async Task MustOverwriteAStaleTraceParentWhenForwarding()
        {
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                var routing = new CapturingRoutingHarness();
                var inbound = BuildInbound(StaleTraceParent);

                await new ForwardingRouter(routing.Router, routing.MessageIdGenerator).Route(inbound, ForwardDestination, null);

                var span = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                var forwardedTraceParent = ResolveTraceParent(routing.RoutedMessage.MessageContext);

                forwardedTraceParent.Should().NotBe(StaleTraceParent, "the inbound context is reused wholesale, so a stale traceparent must not ride out");
                forwardedTraceParent.Should().StartWith("00-" + span.TraceId.ToHexString() + "-" + span.SpanId.ToHexString());
            }
        }

        [Fact]
        public async Task MustMutateTheInboundRecordInPlaceWhenForwarding()
        {
            using (new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                var routing = new CapturingRoutingHarness();
                var inbound = BuildInbound(StaleTraceParent);

                await new ForwardingRouter(routing.Router, routing.MessageIdGenerator).Route(inbound, ForwardDestination, null);

                // DECIDED ALIASING: the outbound message holds the very same dictionary instance, so the inbound
                // record now reads this hop's trace context. Later readers — the routing slip's next hop, deadletter
                // stamping — see it, and that is accepted.
                ReferenceEquals(routing.RoutedMessage.MessageContext, inbound.MessageContext).Should().BeTrue();
                ResolveTraceParent(inbound.MessageContext).Should().NotBe(StaleTraceParent);
            }
        }

        [Fact]
        public async Task MustOverwriteAStaleTraceParentWhenReplying()
        {
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                var routing = new CapturingRoutingHarness();
                var inbound = BuildInbound(StaleTraceParent);

                await new ReplyRouter(routing.Router, routing.MessageIdGenerator)
                    .Route(inbound, null, new ReplyToRoutingContext(ReplyDestination, "reply-group"));

                var span = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                var repliedTraceParent = ResolveTraceParent(routing.RoutedMessage.MessageContext);

                repliedTraceParent.Should().NotBe(StaleTraceParent);
                repliedTraceParent.Should().StartWith("00-" + span.TraceId.ToHexString() + "-" + span.SpanId.ToHexString());
            }
        }

        [Fact]
        public async Task MustMutateTheInboundRecordInPlaceWhenReplying()
        {
            using (new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            {
                var routing = new CapturingRoutingHarness();
                var inbound = BuildInbound(StaleTraceParent);

                await new ReplyRouter(routing.Router, routing.MessageIdGenerator)
                    .Route(inbound, null, new ReplyToRoutingContext(ReplyDestination, "reply-group"));

                ReferenceEquals(routing.RoutedMessage.MessageContext, inbound.MessageContext).Should().BeTrue();
                ResolveTraceParent(inbound.MessageContext).Should().NotBe(StaleTraceParent);
            }
        }

        [Fact]
        public async Task MustBuildTheReceiveSpanFromTheOriginalContextBeforeAForwardMutatesIt()
        {
            // THE ORDERING RULE THE ALIASING SAFETY ARGUMENT DEPENDS ON. The receive span is opened at worker entry,
            // before the body is deserialized and before the Received Message Dispatcher can hand the message to
            // anything that forwards. If this test ever shows the receive span parented to the FORWARD's context
            // instead of the producer's, the ordering has been broken and the aliasing is no longer safe.
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            using (var harness = new DiagnosticsReceiveHarness())
            {
                var routing = new CapturingRoutingHarness();
                var forwardingRouter = new ForwardingRouter(routing.Router, routing.MessageIdGenerator);
                var delivered = harness.Deliver(new Dictionary<string, object> { [TraceContextHeaders.TraceParent] = ProducerTraceParent });

                harness.OnDispatch = messageContext =>
                    forwardingRouter.Route(messageContext.BrokeredMessage, ForwardDestination, null).GetAwaiter().GetResult();

                await harness.RunUntilSettledAsync(ReceiverCall.Ack);

                var receiveSpan = ResolveSingleSpan(activityScope, BrokerDiagnostics.OperationTypes.Receive + " " + DiagnosticsReceiveHarness.ReceiverPath);
                var forwardSpan = ResolveSingleSpan(activityScope, BrokerDiagnostics.OperationTypes.Send + " " + ForwardDestination);

                receiveSpan.TraceId.ToHexString().Should().Be(ProducerTraceId);
                receiveSpan.ParentSpanId.ToHexString().Should().Be(ProducerSpanId, "the receive span is built from the ORIGINAL inbound context");

                var mutatedTraceParent = ResolveTraceParent(delivered.BrokeredMessage.MessageContext);
                mutatedTraceParent.Should().NotBe(ProducerTraceParent, "the forward overwrote the inbound record in place");
                mutatedTraceParent.Should().StartWith("00-" + ProducerTraceId + "-" + forwardSpan.SpanId.ToHexString());
            }
        }

        [Fact]
        public async Task MustEmitExactlyOneSendSpanAndOneSentMeasurementWhenForwarding()
        {
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            {
                var routing = new CapturingRoutingHarness();

                await new ForwardingRouter(routing.Router, routing.MessageIdGenerator).Route(BuildInbound(StaleTraceParent), ForwardDestination, null);

                AssertOneSendEmitted(activityScope, meterScope, ForwardDestination, expectedMessageCount: 1);
            }
        }

        [Fact]
        public async Task MustEmitExactlyOneSendSpanAndOneSentMeasurementWhenReplying()
        {
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            {
                var routing = new CapturingRoutingHarness();

                await new ReplyRouter(routing.Router, routing.MessageIdGenerator)
                    .Route(BuildInbound(StaleTraceParent), null, new ReplyToRoutingContext(ReplyDestination, ReplyGroupId));

                AssertOneSendEmitted(activityScope, meterScope, ReplyDestination, expectedMessageCount: 1);
            }
        }

        [Fact]
        public async Task MustReportTheSameMessageCountOnTheReplySpanAndTheSentInstrumentWhenRoutingNeverStarts()
        {
            // The reply span used to tag messaging.batch.message_count = 1 the moment it started, while
            // messaging.client.sent.messages recorded 0 for a reply the router was never handed. One event cannot
            // honestly carry two counts, so both now report what was actually handed off — zero here.
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            {
                var failure = new DiagnosticsProbeException("the router refused the reply");

                Func<Task> reply = () => ReplyThrough(BuildRefusingRouter(failure));

                // The SYNCHRONOUS throw is wrapped, matching the off path, which returns the router's Task without
                // awaiting it.
                (await reply.Should().ThrowAsync<ReplyToRoutingExceptions>()).Which.InnerException.Should().BeSameAs(failure);

                AssertOneSendEmitted(activityScope, meterScope, ReplyDestination, expectedMessageCount: 0);
            }
        }

        [Fact]
        public async Task MustLeaveAnAsynchronousReplyRoutingFaultUnwrapped()
        {
            // The other half of the deliberate asymmetry: the off path returns the router's Task without awaiting
            // it, so an ASYNCHRONOUS fault surfaces unwrapped. The reply was still handed to the router, so the
            // count is one on the span and on the instrument alike.
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            {
                var failure = new DiagnosticsProbeException("the router faulted after accepting the reply");

                Func<Task> reply = () => ReplyThrough(BuildFaultingRouter(failure));

                (await reply.Should().ThrowAsync<DiagnosticsProbeException>()).Which.Should().BeSameAs(failure);

                AssertOneSendEmitted(activityScope, meterScope, ReplyDestination, expectedMessageCount: 1);
            }
        }

        [Fact]
        public async Task MustEmitOneSendSpanAndOneSentMeasurementWhenAReplyCannotBeBuilt()
        {
            // A reply whose construction fails is still a send that was attempted and failed, and the instruments
            // have always recorded it. The span records it on the same terms rather than leaving the failure
            // visible on the metric alone.
            using (var activityScope = new RecordingActivityScope(BrokerDiagnostics.ActivitySourceName))
            using (var meterScope = new RecordingMeterScope(BrokerDiagnostics.MeterName))
            {
                var routing = new CapturingRoutingHarness();

                // A blank destination makes OutboundBrokeredMessage's constructor throw INSIDE BuildReply, before
                // the router is ever reached.
                Func<Task> reply = () => new ReplyRouter(routing.Router, routing.MessageIdGenerator)
                    .Route(BuildInbound(StaleTraceParent), null, new ReplyToRoutingContext(" ", ReplyGroupId));

                (await reply.Should().ThrowAsync<ReplyToRoutingExceptions>()).Which.InnerException.Should().BeOfType<ArgumentException>();

                var span = activityScope.StoppedActivities.Should().ContainSingle().Subject;
                span.GetTagItem(BrokerDiagnostics.BatchMessageCount).Should().Be(0);
                span.GetTagItem(ChatterTelemetryTags.ErrorType).Should().Be(typeof(ArgumentException).FullName);

                var sentMessages = meterScope.MeasurementsFor(BrokerDiagnostics.SentMessagesInstrumentName).Should().ContainSingle().Subject;
                sentMessages.Value.Should().Be(0);
            }
        }

        /// <summary>
        /// THE NO-DOUBLE-EMISSION PROOF for a single-message send site: one logical send stops exactly ONE span and
        /// publishes exactly ONE measurement on each send instrument, and the span's batch count and the sent
        /// instrument's value are the SAME number.
        /// </summary>
        private static void AssertOneSendEmitted(RecordingActivityScope activityScope, RecordingMeterScope meterScope, string destination, int expectedMessageCount)
        {
            var span = activityScope.StoppedActivities.Should().ContainSingle().Subject;
            span.OperationName.Should().Be(BrokerDiagnostics.OperationTypes.Send + " " + destination);
            span.GetTagItem(BrokerDiagnostics.BatchMessageCount).Should().Be(expectedMessageCount);

            var sentMessages = meterScope.MeasurementsFor(BrokerDiagnostics.SentMessagesInstrumentName).Should().ContainSingle().Subject;
            sentMessages.Value.Should().Be(expectedMessageCount);
            meterScope.MeasurementsFor(BrokerDiagnostics.OperationDurationInstrumentName).Should().ContainSingle();
        }

        private static Task ReplyThrough(IRouteBrokeredMessages router)
        {
            var messageIdGenerator = new Mock<IMessageIdGenerator>();
            messageIdGenerator.Setup(generator => generator.GenerateId(It.IsAny<byte[]>())).Returns(Guid.NewGuid());

            return new ReplyRouter(router, messageIdGenerator.Object)
                .Route(BuildInbound(StaleTraceParent), null, new ReplyToRoutingContext(ReplyDestination, ReplyGroupId));
        }

        /// <summary>A router that throws SYNCHRONOUSLY, so the reply is never handed off.</summary>
        private static IRouteBrokeredMessages BuildRefusingRouter(Exception failure)
        {
            var router = new Mock<IRouteBrokeredMessages>();
            router.Setup(routeBrokeredMessages => routeBrokeredMessages.Route(It.IsAny<OutboundBrokeredMessage>(), It.IsAny<TransactionContext>())).Throws(failure);

            return router.Object;
        }

        /// <summary>A router that accepts the reply and returns a FAULTED task, so the hand-off did happen.</summary>
        private static IRouteBrokeredMessages BuildFaultingRouter(Exception failure)
        {
            var router = new Mock<IRouteBrokeredMessages>();
            router.Setup(routeBrokeredMessages => routeBrokeredMessages.Route(It.IsAny<OutboundBrokeredMessage>(), It.IsAny<TransactionContext>())).Returns(Task.FromException(failure));

            return router.Object;
        }

        private static InboundBrokeredMessage BuildInbound(string traceParent)
            => CapturingRoutingHarness.BuildInbound(traceParent);

        private static string ResolveTraceParent(IReadOnlyDictionary<string, object> messageContext)
        {
            messageContext.TryGetValue(TraceContextHeaders.TraceParent, out var traceParent)
                .Should().BeTrue("the context should carry a '" + TraceContextHeaders.TraceParent + "'");

            return traceParent.Should().BeOfType<string>().Subject;
        }

        private static string ResolveTraceParent(IDictionary<string, object> messageContext)
            => ResolveTraceParent((IReadOnlyDictionary<string, object>)messageContext);

        private static Activity ResolveSingleSpan(RecordingActivityScope activityScope, string operationName)
            => activityScope.StoppedNamed(operationName).Should().ContainSingle().Subject;
    }
}
