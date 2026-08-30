using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Diagnostics;
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

        private static InboundBrokeredMessage BuildInbound(string traceParent)
        {
            var bodyConverter = new JsonBodyConverter();
            var messageContext = new Dictionary<string, object>
            {
                [MessageContext.InfrastructureType] = DiagnosticsSendHarness.MessagingSystem,
                [TraceContextHeaders.TraceParent] = traceParent,
            };

            return new InboundBrokeredMessage("inbound-message-id", bodyConverter.Convert(new TracedDelivery { Value = "inbound" }), messageContext, "receiver-path", bodyConverter);
        }

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

        /// <summary>
        /// A Router that captures the single message routed to it, plus the message-id generator the forward and
        /// reply routers need.
        /// </summary>
        private sealed class CapturingRoutingHarness
        {
            private readonly Mock<IRouteBrokeredMessages> _router = new Mock<IRouteBrokeredMessages>();
            private readonly Mock<IMessageIdGenerator> _messageIdGenerator = new Mock<IMessageIdGenerator>();

            internal CapturingRoutingHarness()
            {
                _messageIdGenerator.Setup(generator => generator.GenerateId(It.IsAny<byte[]>())).Returns(() => Guid.NewGuid());

                _router
                    .Setup(router => router.Route(It.IsAny<OutboundBrokeredMessage>(), It.IsAny<TransactionContext>()))
                    .Callback<OutboundBrokeredMessage, TransactionContext>((outboundMessage, _) => RoutedMessage = outboundMessage)
                    .Returns(Task.CompletedTask);
            }

            internal IRouteBrokeredMessages Router => _router.Object;

            internal IMessageIdGenerator MessageIdGenerator => _messageIdGenerator.Object;

            internal OutboundBrokeredMessage RoutedMessage { get; private set; }
        }
    }
}
