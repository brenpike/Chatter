using Chatter.CQRS.Commands;
using Chatter.MessageBrokers.Routing.Options;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Context
{
    public interface IMessageBrokerContextSender
    {
        /// <summary>
        /// Sends a command to the messaging infrastructure that triggered the <see cref="IMessageHandler{TMessage}"/>.
        /// If messaging infrastructure did not trigger the <see cref="IMessageHandler{TMessage}"/> this will be a no op.
        /// Target Messaging Infrastructure can be overridden by setting <see cref="MessageContext.InfrastructureType"/> via <paramref name="options"/>, <see cref="BrokeredMessageAttribute.InfrastructureType"/> or infrastructure specific Send overload (if available).
        /// Destination must be configured using <paramref name="destinationPath"/>.
        /// </summary>
        /// <typeparam name="TMessage">The type of message to send.</typeparam>
        /// <param name="message">The message to be sent.</param>
        /// <param name="destinationPath">The destination path of the receiver that will receive this message.</param>
        /// <param name="options">The options to be used while sending <paramref name="message"/></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Send<TMessage>(TMessage message, string destinationPath, SendOptions options = null) where TMessage : ICommand;

        /// <summary>
        /// Sends a command to the messaging infrastructure that triggered the <see cref="IMessageHandler{TMessage}"/>.
        /// If messaging infrastructure did not trigger the <see cref="IMessageHandler{TMessage}"/> this will be a no op.
        /// Target Messaging Infrastructure can be overridden by setting <see cref="MessageContext.InfrastructureType"/> via <paramref name="options"/>, <see cref="BrokeredMessageAttribute.InfrastructureType"/> or infrastructure specific Send overload (if available).
        /// Destination must be configured using <see cref="BrokeredMessageAttribute"/>.
        /// </summary>
        /// <typeparam name="TMessage">The type of command to send.</typeparam>
        /// <param name="message">The command to be sent.</param>
        /// <param name="options">The options to be used while sending <paramref name="message"/></param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Send<TMessage>(TMessage message, SendOptions options = null) where TMessage : ICommand;
    }
}
