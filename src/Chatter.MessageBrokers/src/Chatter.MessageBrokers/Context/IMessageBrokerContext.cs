using Chatter.CQRS.Context;
using Chatter.CQRS;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Options;

namespace Chatter.MessageBrokers.Context
{
    /// <summary>
    /// Used to pass contextual information of a <see cref="BrokeredMessageReceiver{TMessage}"/> to a <see cref="IMessageHandler{TMessage}"/>
    /// </summary>
    public interface IMessageBrokerContext : IMessageHandlerContext
    {
        /// <summary>
        /// The message received by a <see cref="BrokeredMessageReceiver{TMessage}"/>
        /// </summary>
        InboundBrokeredMessage BrokeredMessage { get; }

        /// <summary>
        /// Used to route a message to the next destination
        /// </summary>
        IRouteMessages<RoutingContext> NextDestinationRouter { get; }
        /// <summary>
        /// Used to route a message to the reply destination as defined in the <see cref="Headers.ReplyTo"/>
        /// </summary>
        IRouteMessages<ReplyRoutingContext> ReplyRouter { get; }
        /// <summary>
        /// Used to route a message to the compensation destination
        /// </summary>
        IRouteMessages<CompensationRoutingContext> CompensateRouter { get; }
    }
}
