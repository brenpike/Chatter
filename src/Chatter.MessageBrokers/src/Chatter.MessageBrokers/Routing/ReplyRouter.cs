using Chatter.MessageBrokers.Context;
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

            try
            {
                var outbound = new OutboundBrokeredMessage(_messageIdGenerator?.GenerateId(inboundBrokeredMessage.Body).ToString(),
                                                           inboundBrokeredMessage.Body,
                                                           (IDictionary<string, object>)inboundBrokeredMessage.MessageContext,
                                                           destinationRouterContext?.DestinationPath,
                                                           inboundBrokeredMessage.BodyConverter);

                outbound.MessageContext[MessageContext.ReplyToGroupId] = destinationRouterContext.ReplyToGroupId;

                return _router.Route(outbound, transactionContext);
            }
            catch (Exception e)
            {
                throw new ReplyToRoutingExceptions(destinationRouterContext, e);
            }
        }
    }
}
