using Chatter.CQRS.Context;

namespace Chatter.MessageBrokers.Routing.Context
{
    /// <summary>
    /// Contains contextual information about how a received message should be routed to another destination
    /// </summary>
    public class RoutingContext : IContainRoutingContext
    {
        /// <summary>
        /// Creates an object which contains contextual information about how a received message should be routed to another destination.
        /// </summary>
        /// <param name="destinationPath">The destination message receiver to be routed to</param>
        /// <param name="inheritedContext">An optional container with additional contextual information</param>
        public RoutingContext(string destinationPath, ContextContainer inheritedContext = null)
        {
            DestinationPath = destinationPath;
            Container = new ContextContainer(inheritedContext);
        }

        ///<inheritdoc/>
        public string DestinationPath { get; }

        ///<inheritdoc/>
        public ContextContainer Container { get; }
    }
}
