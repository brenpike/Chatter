using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing
{
    /// <summary>
    /// An <see cref="ICompensationRoutingStrategy"/> implementation that performs compensation by dispatching a compensation message to the message broker.
    /// </summary>
    public class DispatchMessageCompensatingStrategy : ICompensationRoutingStrategy
    {
        private readonly IMessageDestinationRouter<CompensateContext> _messageDestinationRouter;

        /// <summary>
        /// Creates a compensation routiung strategy that dispatches a compensating message to the message broker
        /// </summary>
        /// <param name="messageBrokerMessageDispatcher"></param>
        public DispatchMessageCompensatingStrategy(IMessageDestinationRouter<CompensateContext> messageDestinationRouter)
        {
            _messageDestinationRouter = messageDestinationRouter ?? throw new ArgumentNullException(nameof(messageDestinationRouter));
        }

        ///<inheritdoc/>
        public Task Compensate(InboundBrokeredMessage inboundBrokeredMessage, string details, string description, TransactionContext transactionContext, CompensateContext compensateContext)
        {
            return _messageDestinationRouter.Route(inboundBrokeredMessage, transactionContext, compensateContext);
        }
    }
}
