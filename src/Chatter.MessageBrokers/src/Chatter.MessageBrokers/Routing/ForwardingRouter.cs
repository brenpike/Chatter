using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Diagnostics;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing
{
    class ForwardingRouter : IForwardMessages
    {
        /// <summary>A forward carries exactly one message, so the batch count on its send span is always one.</summary>
        private const int ForwardedMessageCount = 1;

        private readonly IRouteBrokeredMessages _router;
        private readonly IMessageIdGenerator _messageIdGenerator;

        public ForwardingRouter(IRouteBrokeredMessages router, IMessageIdGenerator messageIdGenerator)
        {
            _router = router ?? throw new ArgumentNullException(nameof(router));
            _messageIdGenerator = messageIdGenerator ?? throw new ArgumentNullException(nameof(messageIdGenerator));
        }

        /// <summary>
        /// Forwards an inbound brokered message to a destination
        /// </summary>
        /// <param name="inboundBrokeredMessage">The inbound brokered message to be forwarded to a receiver</param>
        /// <param name="forwardDestination">The destination path to forward the inbound brokered message to</param>
        /// <param name="transactionContext">The transactional information to use while routing</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        public Task Route(InboundBrokeredMessage inboundBrokeredMessage, string forwardDestination, TransactionContext transactionContext)
        {
            if (inboundBrokeredMessage is null)
            {
                throw new ArgumentNullException(nameof(inboundBrokeredMessage), $"An {typeof(InboundBrokeredMessage).Name} is required to be routed to the destination.");
            }

            if (string.IsNullOrWhiteSpace(forwardDestination))
            {
                return Task.CompletedTask;
            }

            var outboundMessage = new OutboundBrokeredMessage(_messageIdGenerator?.GenerateId(inboundBrokeredMessage.Body).ToString(),
                                                              inboundBrokeredMessage.Body,
                                                              (IDictionary<string, object>)inboundBrokeredMessage.MessageContext,
                                                              forwardDestination,
                                                              inboundBrokeredMessage.BodyConverter);

            // INVARIANT: ADR-0010 R1/R4 - Chatter's own off-guard is what decides. The off path is the on path minus
            // the payload: every off-path diagnostics call it reaches is a documented branch-and-return that
            // allocates nothing, reads no start timestamp, starts no span, and writes no trace-context header, and
            // this path itself adds no async state machine when an application has not opted into broker
            // diagnostics.
            if (!BrokerDiagnostics.IsEnabled)
            {
                return _router.Route(outboundMessage, transactionContext);
            }

            return RouteWithDiagnostics(outboundMessage, transactionContext);
        }

        /// <summary>
        /// Routes the forwarded message under its own send span (ADR-0010 D7), writing the span's trace context onto
        /// the message before it reaches the router.
        /// </summary>
        /// <remarks>A forward knows its destination and its message count before it starts, so it needs nothing from
        /// <see cref="SendScope"/> beyond opening it: the count and destination the scope reports at stop are the ones
        /// it was opened with.</remarks>
        private async Task RouteWithDiagnostics(OutboundBrokeredMessage outboundMessage, TransactionContext transactionContext)
        {
            // The Messaging Infrastructure the message names is the only messaging-system identity this package
            // has; it is passed through here AS-IS. BrokerDiagnostics normalizes a blank identifier to an unset
            // span attribute (the metric keeps the key with a null value) rather than inventing one.
            using (var sendScope = SendScope.Open(outboundMessage.InfrastructureType, BrokerDiagnostics.OperationTypes.Send, outboundMessage.Destination, ForwardedMessageCount))
            {
                // ALIASING, DELIBERATE: the outbound message was handed the INBOUND message's context dictionary by
                // reference (the OutboundBrokeredMessage constructor already mutates that same instance), so writing
                // here OVERWRITES the inbound record in place and a later reader - the routing slip's next hop,
                // deadletter stamping - sees this hop's traceparent. The overwrite happens ONLY when the scope has a
                // trace context to write; on the null-activity paths - diagnostics off, metrics-only, or sampled out
                // with no ambient activity - the inbound traceparent rides out unchanged, deliberately (see
                // TraceContextPropagator.SetTraceContextValue).
                // It is safe because of an ORDERING RULE: trace context is extracted at Brokered Message
                // Receiver worker entry, strictly before the Received Message Dispatcher hands the message to any
                // handler, so the receive span is already built from the original context by the time a handler can
                // forward. Preserve that ordering if the receiving path is ever restructured.
                sendScope.Inject(outboundMessage.MessageContext);

                try
                {
                    await _router.Route(outboundMessage, transactionContext).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    sendScope.RecordFailure(e);
                    throw;
                }
            }
        }
    }
}
