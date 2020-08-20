using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing.Context;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing
{
    /// <summary>
    /// Routes a brokered message to a receiver responsible for compensating a received message
    /// </summary>
    public class CompensateRouter : IRouteCompensationMessages
    {
        private readonly IForwardMessages _router;

        /// <summary>
        /// Creates a router for sending a brokered message to a brokered message receiver responsible for compensating a received message
        /// </summary>
        /// <param name="router">The strategy used to compensate the a received message</param>
        public CompensateRouter(IForwardMessages router)
        {
            _router = router ?? throw new ArgumentNullException(nameof(router));
        }

        /// <summary>
        /// Routes a brokered message to a brokered message receiver responsible for compensating a received message
        /// </summary>
        /// <param name="inboundBrokeredMessage">The inbound brokered message to be routed to the compensation destination</param>
        /// <param name="transactionContext">The transaction information that was received with <paramref name="inboundBrokeredMessage"/></param>
        /// <param name="destinationRouterContext">The <see cref="CompensationRoutingContext"/> containing contextual information describing the compensating action</param>
        /// <exception cref="CompensationRoutingException">An exception containing contextual information describing the failure during compensation and routing details</exception>
        /// <returns>An awaitable <see cref="Task"/></returns>
        public Task Route(InboundBrokeredMessage inboundBrokeredMessage, TransactionContext transactionContext, CompensationRoutingContext destinationRouterContext)
        {
            try
            {
                if (destinationRouterContext is null)
                {
                    throw new ArgumentNullException(nameof(destinationRouterContext), $"A '{typeof(CompensationRoutingContext).Name}' is required to route a compensation message");
                }

                if (string.IsNullOrWhiteSpace(destinationRouterContext.CompensateDetails))
                {
                    throw new ArgumentNullException(nameof(destinationRouterContext.CompensateDetails), $"A compensation reason is required to route a compensation message");
                }

                if (string.IsNullOrWhiteSpace(destinationRouterContext.CompensateDescription))
                {
                    throw new ArgumentNullException(nameof(destinationRouterContext.CompensateDescription), $"A compensation description is required to route a compensation message");
                }

                //TODO: get rid of IForwardRouter dependency and use IRouteMessages instead
                //TODO: should call OutboundBrokeredMessage.Forward directly from here and then we can remove WithFailureDetails, description, setfailure, etc
                //      from inboundbrokered message and to CompensateOptions or outboundbrokeredmessage where they belong
                inboundBrokeredMessage.WithFailureDetails(destinationRouterContext.CompensateDetails);
                inboundBrokeredMessage.WithFailureDescription(destinationRouterContext.CompensateDescription);
                inboundBrokeredMessage.SetFailure();

                return _router.Route(inboundBrokeredMessage, destinationRouterContext.DestinationPath, transactionContext);
            }
            catch (Exception causeOfRoutingFailure)
            {
                throw new CompensationRoutingException(destinationRouterContext, causeOfRoutingFailure);
            }
        }
    }
}
