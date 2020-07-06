using Chatter.CQRS.Context;
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
                compensateContext = new CompensateContext(compensateDestinationPath, null, reason, errorDescription, new ContextContainer(messageContext.Container));
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

            if (string.IsNullOrWhiteSpace(destinationRouterContext.CompensateReason))
            {
                throw new ArgumentNullException(nameof(destinationRouterContext.CompensateReason), $"A compensation reason is required to route a compensation message");
            }

            if (string.IsNullOrWhiteSpace(destinationRouterContext.CompensateErrorDescription))
            {
                throw new ArgumentNullException(nameof(destinationRouterContext.CompensateErrorDescription), $"A compensation description is required to route a compensation message");
            }

            inboundBrokeredMessage.WithFailureDetails(destinationRouterContext.CompensateReason);
            inboundBrokeredMessage.WithFailureDescription(destinationRouterContext.CompensateErrorDescription);
            inboundBrokeredMessage.SetError();

            return _compensationStrategy.Compensate(inboundBrokeredMessage, destinationRouterContext.CompensateReason, destinationRouterContext.CompensateErrorDescription, transactionContext, destinationRouterContext);
        }
    }
}
