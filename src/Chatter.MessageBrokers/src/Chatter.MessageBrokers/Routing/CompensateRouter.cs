using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing
{
    public class CompensateRouter : ICompensateRouter
    {
        private readonly ICompensationStrategy _compensationStrategy;

        public CompensateRouter(ICompensationStrategy compensationStrategy)
        {
            _compensationStrategy = compensationStrategy;
        }

        public Task Route(string compensateDestinationPath, InboundBrokeredMessage inboundMessage, MessageBrokerContext messageContext, TransactionContext transactionContext, string reason, string errorDescription)
        {
            if (!(messageContext.Container.TryGet<CompensateContext>(out var compensateContext)))
            {
                compensateContext = new CompensateContext(compensateDestinationPath, null, reason, errorDescription, messageContext.Container);
                messageContext.Container.Set(messageContext);
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
    }
}
