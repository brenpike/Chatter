using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Sending;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Outbox
{
    public sealed class OutboxBrokeredMessageRouter : IRouteBrokeredMessages
    {
        private readonly IBrokeredMessageOutbox _brokeredMessageOutbox;

        public OutboxBrokeredMessageRouter(IBrokeredMessageOutbox brokeredMessageOutbox)
            => _brokeredMessageOutbox = brokeredMessageOutbox ?? throw new ArgumentNullException(nameof(brokeredMessageOutbox));

        /// <summary>
        /// Routes an <see cref="OutboundBrokeredMessage"/> to a receiver via the brokered message outbox.
        /// </summary>
        /// <param name="outboundBrokeredMessage">The outbound brokered message to be routed to the destination receiver</param>
        /// <param name="transactionContext">The contextual transaction information to be used while routing the message to its destination</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        public Task Route(OutboundBrokeredMessage outboundBrokeredMessage, TransactionContext transactionContext)
            => _brokeredMessageOutbox.SendToOutbox(outboundBrokeredMessage, transactionContext);

        /// <summary>
        /// Routes a batch of <see cref="OutboundBrokeredMessage"/> to their receivers via the brokered message outbox.
        /// </summary>
        /// <param name="outboundBrokeredMessages">The outbound brokered messages to be routed to the destination receivers</param>
        /// <param name="transactionContext">The contextual transaction information to be used while routing the message to its destination</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        public Task Route(IEnumerable<OutboundBrokeredMessage> outboundBrokeredMessages, TransactionContext transactionContext)
            => _brokeredMessageOutbox.SendToOutbox(outboundBrokeredMessages, transactionContext);
    }
}
