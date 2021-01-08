using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Context
{
    public interface IMessageBrokerContextForwarder
    {
        /// <summary>
        /// Forwards a message to the messaging infrastructure that triggered the <see cref="IMessageHandler{TMessage}"/>.
        /// If messaging infrastructure did not trigger the <see cref="IMessageHandler{TMessage}"/> this will be a no op.
        /// Target Messaging Infrastructure can be overridden by using the infrastructure specific Forward overload (if available).
        /// Destination must be configured using <paramref name="forwardDestination"/>.
        /// </summary>
        /// <param name="forwardDestination">The destination to forward the message to.</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Forward(string forwardDestination);
    }
}
