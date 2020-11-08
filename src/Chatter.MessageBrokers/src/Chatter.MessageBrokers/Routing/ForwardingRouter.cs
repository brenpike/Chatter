using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing
{
    class ForwardingRouter : IForwardMessages
    {
        private readonly IRouteBrokeredMessages _router;

        public ForwardingRouter(IRouteBrokeredMessages router)
        {
            _router = router ?? throw new ArgumentNullException(nameof(router));
        }

        /// <summary>
        /// Forwards an inbound brokered message to a destination
        /// </summary>
        /// <param name="inboundBrokeredMessage">The inbound brokered message to be forwarded to a receiver</param>
        /// <param name="forwardDestination">The destination path to forward the inbound brokered message to</param>
        /// <param name="transactionContext">The transactional information to use while routing</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        public Task Route(InboundBrokeredMessage inboundBrokeredMessage, string forwardDestination, TransactionContext transactionContext)
        {
            if (inboundBrokeredMessage is null)
            {
                throw new ArgumentNullException(nameof(inboundBrokeredMessage), $"An {typeof(InboundBrokeredMessage).Name} is required to be routed to the destination.");
            }

            if (string.IsNullOrWhiteSpace(forwardDestination))
            {
                return Task.CompletedTask;
            }

            var outboundMessage = OutboundBrokeredMessage.Forward(inboundBrokeredMessage, forwardDestination);
            return _router.Route(outboundMessage, transactionContext);
        }
    }
}
