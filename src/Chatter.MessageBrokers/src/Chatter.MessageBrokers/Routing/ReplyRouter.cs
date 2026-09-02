using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Diagnostics;
using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing.Context;
using Chatter.MessageBrokers.Sending;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing
{
    class ReplyRouter : IReplyRouter
    {
        /// <summary>A reply carries exactly one message, so the batch count on its send span is always one.</summary>
        private const int RepliedMessageCount = 1;

        private readonly IRouteBrokeredMessages _router;
        private readonly IMessageIdGenerator _messageIdGenerator;

        /// <summary>
        /// Creates a router for sending a brokered message to a brokered message receiver designated by the 'reply to' application property
        /// </summary>
        /// <param name="router">The strategy used to compensate the a received message</param>
        public ReplyRouter(IRouteBrokeredMessages router, IMessageIdGenerator messageIdGenerator)
        {
            _router = router ?? throw new ArgumentNullException(nameof(router));
            _messageIdGenerator = messageIdGenerator ?? throw new ArgumentNullException(nameof(messageIdGenerator));
        }

        public Task Route(InboundBrokeredMessage inboundBrokeredMessage, TransactionContext transactionContext, ReplyToRoutingContext destinationRouterContext)
        {
            if (destinationRouterContext is null)
            {
                //TODO: log
                return Task.CompletedTask;
            }

            // INVARIANT: ADR-0010 R1/R4 - Chatter's own off-guard is what decides. The off path is the on path minus
            // the payload: every off-path diagnostics call it reaches is a documented branch-and-return that
            // allocates nothing, reads no start timestamp, starts no span, and writes no trace-context header, and
            // this path itself adds no async state machine when an application has not opted into broker
            // diagnostics.
            if (!BrokerDiagnostics.IsEnabled)
            {
                try
                {
                    return _router.Route(BuildReply(inboundBrokeredMessage, destinationRouterContext), transactionContext);
                }
                catch (Exception e)
                {
                    throw new ReplyToRoutingExceptions(destinationRouterContext, e);
                }
            }

            return RouteWithDiagnostics(inboundBrokeredMessage, transactionContext, destinationRouterContext);
        }

        private OutboundBrokeredMessage BuildReply(InboundBrokeredMessage inboundBrokeredMessage, ReplyToRoutingContext destinationRouterContext)
        {
            var outbound = new OutboundBrokeredMessage(_messageIdGenerator?.GenerateId(inboundBrokeredMessage.Body).ToString(),
                                                       inboundBrokeredMessage.Body,
                                                       (IDictionary<string, object>)inboundBrokeredMessage.MessageContext,
                                                       destinationRouterContext?.DestinationPath,
                                                       inboundBrokeredMessage.BodyConverter);

            outbound.MessageContext[MessageContext.ReplyToGroupId] = destinationRouterContext.ReplyToGroupId;

            return outbound;
        }

        /// <summary>
        /// Routes the reply under its own send span (ADR-0010 D7), writing the span's trace context onto the message
        /// before it reaches the router. Failure is wrapped in a <see cref="ReplyToRoutingExceptions"/> on exactly the
        /// same terms as the off path, so an awaiting caller sees the same exception type either way.
        /// </summary>
        /// <remarks>
        /// The scope is opened BEFORE the reply is built, because a reply whose construction fails is still a send
        /// this router attempted: the operation is reported either way, and reporting it only once the message exists
        /// would drop the very failures most worth seeing.
        /// INVARIANT: the count reported is what was actually HANDED to the router, never what was intended, so the
        /// scope opens at zero and is told the real count only once the hand-off has happened. The span tag and the
        /// sent instrument therefore carry the SAME number for one reply, which is the whole reason the count is
        /// reported at stop rather than at start.
        /// </remarks>
        private async Task RouteWithDiagnostics(InboundBrokeredMessage inboundBrokeredMessage, TransactionContext transactionContext, ReplyToRoutingContext destinationRouterContext)
        {
            var sendScope = default(SendScope);
            var isRoutingStarted = false;

            try
            {
                // The Messaging Infrastructure the message names is the only messaging-system identity this package
                // has; it is passed through here AS-IS. BrokerDiagnostics normalizes a blank identifier to an unset
                // span attribute (the metric keeps the key with a null value) rather than inventing one. It is read
                // off the INBOUND context because the reply ALIASES that same dictionary, so it is the identity the
                // reply carries - and it is known before the reply is built.
                inboundBrokeredMessage.MessageContext.TryGetValue(MessageContext.InfrastructureType, out var infraType);

                sendScope = SendScope.Open((string)infraType, BrokerDiagnostics.OperationTypes.Send, destinationRouterContext.DestinationPath, messageCount: 0);

                var outbound = BuildReply(inboundBrokeredMessage, destinationRouterContext);

                // ALIASING, DELIBERATE: the reply was handed the INBOUND message's context dictionary by reference
                // (the OutboundBrokeredMessage constructor already mutates that same instance), so writing here
                // OVERWRITES the inbound record in place and a later reader - the routing slip's next hop, deadletter
                // stamping - sees this hop's traceparent. The overwrite happens ONLY when the scope has a trace
                // context to write; on the null-activity paths - diagnostics off, metrics-only, or sampled out with
                // no ambient activity - the inbound traceparent rides out unchanged, deliberately (see
                // TraceContextPropagator.SetTraceContextValue).
                // It is safe because of an ORDERING RULE: trace context is extracted at Brokered Message Receiver
                // worker entry, strictly before the Received Message Dispatcher hands the message to any handler, so
                // the receive span is already built from the original context by the time a handler can reply.
                // Preserve that ordering if the receiving path is ever restructured.
                sendScope.Inject(outbound.MessageContext);

                var routing = _router.Route(outbound, transactionContext);
                isRoutingStarted = true;
                await routing.ConfigureAwait(false);
            }
            catch (Exception e)
            {
                sendScope.RecordFailure(e);

                // INVARIANT: the off path returns the router's Task without awaiting it, so it wraps only a
                // SYNCHRONOUS throw and lets an asynchronous fault surface unwrapped. Match that exactly - opting into
                // broker diagnostics must not change the exception a caller sees.
                if (isRoutingStarted)
                {
                    throw;
                }

                throw new ReplyToRoutingExceptions(destinationRouterContext, e);
            }
            finally
            {
                // isRoutingStarted is set only once _router.Route has been called with the built reply, so a failure
                // recorded before that point (e.g. BuildReply throwing) leaves the count at the zero the scope was
                // opened with rather than claiming RepliedMessageCount.
                if (isRoutingStarted)
                {
                    sendScope.RecordResolvedMessageCount(RepliedMessageCount);
                }

                sendScope.Dispose();
            }
        }
    }
}
