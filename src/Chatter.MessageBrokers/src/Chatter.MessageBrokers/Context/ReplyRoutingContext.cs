using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using System;

namespace Chatter.MessageBrokers.Context
{
    /// <summary>
    /// Contains contextual information about how a received message should be routed to the reply destination
    /// </summary>
    public sealed class ReplyRoutingContext : RoutingContext
    {
        /// <summary>
        /// Creates an object which contains contextual information about how a received message should be routed to the reply destination.
        /// </summary>
        /// <param name="destinationPath">The destination message receiver to be routed to</param>
        /// <param name="replyToGroupId"></param>
        /// <param name="inheritedContext">An optional container with additional contextual information</param>
        public ReplyRoutingContext(string destinationPath, string replyToGroupId, ContextContainer inheritedContext = null)
            : base(destinationPath, inheritedContext)
        {
            ReplyToGroupId = replyToGroupId;
        }

        public string ReplyToGroupId { get; } = null;
    }
}
