using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Sending;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing
{
    public sealed class BrokeredMessageRouter : IRouteBrokeredMessages
    {
        private readonly IMessagingInfrastructureDispatcher _brokeredMessageInfrastructureDispatcher;

        public BrokeredMessageRouter(IMessagingInfrastructureDispatcher brokeredMessageInfrastructureDispatcher) 
            => _brokeredMessageInfrastructureDispatcher = brokeredMessageInfrastructureDispatcher ?? throw new ArgumentNullException(nameof(brokeredMessageInfrastructureDispatcher));

        /// <summary>
        /// Routes an <see cref="OutboundBrokeredMessage"/> to the receiver via the message broker infrastructure.
        /// </summary>
        /// <param name="outboundBrokeredMessage">The outbound brokered message to be routed to the destination receiver</param>
        /// <param name="transactionContext">The contextual transaction information to be used while routing the message to its destination</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        public Task Route(OutboundBrokeredMessage outboundBrokeredMessage, TransactionContext transactionContext)
        {
            if (string.IsNullOrWhiteSpace(outboundBrokeredMessage.Destination))
            {
                throw new ArgumentNullException(nameof(outboundBrokeredMessage.Destination), $"Unable to route message with no destination path specified");
            }

            return _brokeredMessageInfrastructureDispatcher.Dispatch(outboundBrokeredMessage, transactionContext);
        }

        /// <summary>
        /// Routes a batch of <see cref="OutboundBrokeredMessage"/> to their receivers via the message broker infrastructure.
        /// </summary>
        /// <param name="outboundBrokeredMessages">The outbound brokered messages to be routed to the destination receivers</param>
        /// <param name="transactionContext">The contextual transaction information to be used while routing the message to its destination</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        public Task Route(IList<OutboundBrokeredMessage> outboundBrokeredMessages, TransactionContext transactionContext)
            => _brokeredMessageInfrastructureDispatcher.Dispatch(outboundBrokeredMessages, transactionContext);
    }
}