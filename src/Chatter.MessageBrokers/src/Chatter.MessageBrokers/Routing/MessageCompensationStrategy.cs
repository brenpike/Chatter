using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing
{
    /// <summary>
    /// An <see cref="ICompensationStrategy"/> implementation that performs compensation by sending a compensation message to the message broker.
    /// </summary>
    public class MessageCompensationStrategy : ICompensationStrategy
    {
        private readonly MessageDestinationRouter<CompensateContext> _messageDestinationRouter;

        public MessageCompensationStrategy(IBrokeredMessageDispatcher messageBrokerMessageDispatcher)
        {
            if (messageBrokerMessageDispatcher is null)
            {
                throw new ArgumentNullException(nameof(messageBrokerMessageDispatcher));
            }

            _messageDestinationRouter = new MessageDestinationRouter<CompensateContext>(messageBrokerMessageDispatcher);
        }

        ///<inheritdoc/>
        public Task Compensate(InboundBrokeredMessage inboundBrokeredMessage, string compensateReason, string compensateErrorDescription, TransactionContext transactionContext, CompensateContext compensateContext)
        {
            return _messageDestinationRouter.Route(inboundBrokeredMessage, transactionContext, compensateContext);
        }
    }
}
