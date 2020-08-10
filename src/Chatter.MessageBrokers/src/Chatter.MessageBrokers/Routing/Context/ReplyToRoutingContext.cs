using Chatter.CQRS.Context;

namespace Chatter.MessageBrokers.Routing.Context
{
    /// <summary>
    /// Contains contextual information about how a received message should be routed to the reply destination
    /// </summary>
    public sealed class ReplyToRoutingContext : RoutingContext
    {
        /// <summary>
        /// Creates an object which contains contextual information about how a received message should be routed to the 'reply to' destination.
        /// </summary>
        /// <param name="destinationPath">The destination message receiver to be routed to</param>
        /// <param name="replyToGroupId"></param>
        /// <param name="inheritedContext">An optional container with additional contextual information</param>
        public ReplyToRoutingContext(string destinationPath, string replyToGroupId, ContextContainer inheritedContext = null)
            : base(destinationPath, inheritedContext)
        {
            ReplyToGroupId = replyToGroupId;
        }

        public string ReplyToGroupId { get; } = null;
    }
}
