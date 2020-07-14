using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using System;

namespace Chatter.MessageBrokers.Context
{
    /// <summary>
    /// Contains contextual information about how a received message should be routed to another destination
    /// </summary>
    public class DestinationRouterContext : IContainDestinationToRouteContext
    {
        /// <summary>
        /// Creates an object which contains contextual information about how a received message should be routed to another destination.
        /// </summary>
        /// <param name="destinationPath">The destination message receiver to be routed to</param>
        /// <param name="destinationMessageCreator">The delegate that creates an outbound message from the received inbound message</param>
        /// <param name="inheritedContext">An optional container with additional contextual information</param>
        public DestinationRouterContext(string destinationPath, Func<InboundBrokeredMessage, OutboundBrokeredMessage> destinationMessageCreator, ContextContainer inheritedContext = null)
        {
            this.DestinationPath = destinationPath;
            this.DestinationMessageCreator = destinationMessageCreator;
            this.Container = new ContextContainer(inheritedContext);
        }

        ///<inheritdoc/>
        public string DestinationPath { get; }
        /// <summary>
        /// Transforms an <see cref="InboundBrokeredMessage"/> to an <see cref="OutboundBrokeredMessage"/> when routing to the <see cref="DestinationPath"/>
        /// </summary>
        protected Func<InboundBrokeredMessage, OutboundBrokeredMessage> DestinationMessageCreator { get; set; }
        ///<inheritdoc/>
        public ContextContainer Container { get; }

        /// <summary>
        /// Sets the deletgate function that will be used to create the outbound message from the received inbound message.
        /// The outbound message will be routed to the <see cref="DestinationPath"/>.
        /// </summary>
        /// <param name="destinationMessageCreator">The delegate function to create an outbound message for message to route</param>
        /// <returns>The current <see cref="DestinationRouterContext"/> instance</returns>
        public DestinationRouterContext SetDestinationMessageCreator(Func<InboundBrokeredMessage, OutboundBrokeredMessage> destinationMessageCreator)
        {
            DestinationMessageCreator = destinationMessageCreator;
            return this;
        }

        /// <summary>
        /// Describes how an <see cref="InboundBrokeredMessage"/> should be transformed to an <see cref="OutboundBrokeredMessage"/> when being routed.
        /// When a <see cref="DestinationMessageCreator"/> is supplied, it will be used to create the <see cref="OutboundBrokeredMessage"/>, otherwise
        /// the message will simply be forwarded via <see cref="OutboundBrokeredMessage.Forward(InboundBrokeredMessage, string)"/>.
        /// </summary>
        /// <param name="inboundBrokeredMessage">The inbound message that was received and is being routed to <see cref="DestinationPath"/></param>
        /// <returns>The outbound message to be sent to <see cref="DestinationPath"/></returns>
        public OutboundBrokeredMessage CreateDestinationMessage(InboundBrokeredMessage inboundBrokeredMessage)
        {
            return this.DestinationMessageCreator?.Invoke(inboundBrokeredMessage) ?? OutboundBrokeredMessage.Forward(inboundBrokeredMessage, this.DestinationPath);
        }
    }
}
