using Chatter.CQRS;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Routing.Options;
using Chatter.MessageBrokers.Sending;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Outbox
{
    public sealed class OutboxMessageRouter : IRouteMessages
    {
        private readonly ITransactionalBrokeredMessageOutbox _brokeredMessageOutboxDispatcher;

        public OutboxMessageRouter(ITransactionalBrokeredMessageOutbox brokeredMessageOutboxDispatcher)
        {
            _brokeredMessageOutboxDispatcher = brokeredMessageOutboxDispatcher ?? throw new ArgumentNullException(nameof(brokeredMessageOutboxDispatcher));
        }

        public Task Route<TMessage>(TMessage message, string destinationPath, TransactionContext transactionContext = null) where TMessage : IMessage
        {
            //TODO: fix newing up json converter
            var outbound = new OutboundBrokeredMessage(message, destinationPath, new JsonBodyConverter());
            return this.Route(outbound, transactionContext);
        }

        /// <summary>
        /// Routes an <see cref="OutboundBrokeredMessage"/> to a receiver via the brokered message outbox.
        /// </summary>
        /// <param name="outboundBrokeredMessage">The outbound brokered message to be routed to the destination receiver</param>
        /// <param name="transactionContext">The contextual transaction information to be used while routing the message to its destination</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        public Task Route(OutboundBrokeredMessage outboundBrokeredMessage, TransactionContext transactionContext)
        {
            return _brokeredMessageOutboxDispatcher.SendToOutbox(outboundBrokeredMessage, transactionContext);
        }

        /// <summary>
        /// Routes a batch of <see cref="OutboundBrokeredMessage"/> to their receivers via the brokered message outbox.
        /// </summary>
        /// <param name="outboundBrokeredMessages">The outbound brokered messages to be routed to the destination receivers</param>
        /// <param name="transactionContext">The contextual transaction information to be used while routing the message to its destination</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        public Task Route(IList<OutboundBrokeredMessage> outboundBrokeredMessages, TransactionContext transactionContext)
        {
            return _brokeredMessageOutboxDispatcher.SendToOutbox(outboundBrokeredMessages, transactionContext);
        }

        public Task Route<TMessage, TOptions>(TMessage message, TransactionContext transactionContext, TOptions options)
            where TMessage : IMessage
            where TOptions : RoutingOptions, new()
        {
            throw new NotImplementedException();
        }

        public Task Route<TMessage, TOptions>(TMessage message, string destinationPath, TransactionContext transactionContext, TOptions options)
            where TMessage : IMessage
            where TOptions : RoutingOptions, new()
        {
            throw new NotImplementedException();
        }
    }
}
