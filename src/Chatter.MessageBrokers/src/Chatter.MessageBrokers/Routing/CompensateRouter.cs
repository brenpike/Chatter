using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing
{
    /// <summary>
    /// Routes a brokered message to a receiver responsible for compensating a received message
    /// </summary>
    public class CompensateRouter : ICompensateRouter
    {
        private readonly ICompensationRoutingStrategy _compensationStrategy;

        /// <summary>
        /// Creates a router for sending a brokered message to a brokered message receiver responsible for compensating a received message
        /// </summary>
        /// <param name="compensationStrategy">The strategy used to compensate the a received message</param>
        public CompensateRouter(ICompensationRoutingStrategy compensationStrategy)
        {
            _compensationStrategy = compensationStrategy;
        }

        /// <summary>
        /// Routes a brokered message to a brokered message receiver responsible for compensating a received message
        /// </summary>
        /// <param name="compensateDestinationPath">The destination path for the receiver responsible for compensating a received message</param>
        /// <param name="inboundMessage">The inbound message that was unsuccesfully received and requires compensation</param>
        /// <param name="messageContext">The context that was received with <paramref name="inboundMessage"/></param>
        /// <param name="transactionContext">The transaction information that was received with <paramref name="inboundMessage"/></param>
        /// <param name="details">The details of the error that caused the compensation</param>
        /// <param name="description">The description of the error that caused the compensation</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        public Task Route(string compensateDestinationPath, InboundBrokeredMessage inboundMessage, MessageBrokerContext messageContext, TransactionContext transactionContext, string details, string description)
        {
            if (!(messageContext.Container.TryGet<CompensateContext>(out var compensateContext)))
            {
                compensateContext = new CompensateContext(compensateDestinationPath, null, details, description, messageContext.Container);
                messageContext.Container.Include(messageContext);
            }

            return this.Route(inboundMessage, transactionContext, compensateContext);
        }

        public Task Route(InboundBrokeredMessage inboundBrokeredMessage, TransactionContext transactionContext, CompensateContext destinationRouterContext)
        {
            if (destinationRouterContext is null)
            {
                throw new ArgumentNullException(nameof(destinationRouterContext), $"A '{typeof(CompensateContext).Name}' is required to route a compensation message");
            }

            if (string.IsNullOrWhiteSpace(destinationRouterContext.CompensateDetails))
            {
                throw new ArgumentNullException(nameof(destinationRouterContext.CompensateDetails), $"A compensation reason is required to route a compensation message");
            }

            if (string.IsNullOrWhiteSpace(destinationRouterContext.CompensateDescription))
            {
                throw new ArgumentNullException(nameof(destinationRouterContext.CompensateDescription), $"A compensation description is required to route a compensation message");
            }

            inboundBrokeredMessage.WithFailureDetails(destinationRouterContext.CompensateDetails);
            inboundBrokeredMessage.WithFailureDescription(destinationRouterContext.CompensateDescription);
            inboundBrokeredMessage.SetError();

            return _compensationStrategy.Compensate(inboundBrokeredMessage, destinationRouterContext.CompensateDetails, destinationRouterContext.CompensateDescription, transactionContext, destinationRouterContext);
        }

        public Task Route(OutboundBrokeredMessage outboundBrokeredMessage, TransactionContext transactionContext)
        {
            throw new NotImplementedException(); //TODO: fix, how?
        }
    }
}
