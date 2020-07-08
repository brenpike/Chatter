using Chatter.CQRS.Context;
using System.Threading.Tasks;

namespace Chatter.CQRS
{
    /// <summary>
    /// Dispatches <see cref="IMessage"/> to be handled by <see cref="IMessageHandler{TMessage}"/>
    /// </summary>
    public interface IMessageDispatcher
    {
        /// <summary>
        /// Dispatch an <see cref="IMessage"/>.
        /// </summary>
        /// <typeparam name="TMessage">The type of message to be dispatched.</typeparam>
        /// <param name="message">The message to be dispatched.</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Dispatch<TMessage>(TMessage message) where TMessage : IMessage;
        /// <summary>
        /// Dispatch an <see cref="IMessage"/> with additional context.
        /// </summary>
        /// <typeparam name="TMessage">The type of message to be dispatched.</typeparam>
        /// <param name="message">The message to be dispatched.</param>
        /// <param name="messageHandlerContext">The context to be dispatched with <paramref name="message"/>.</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Dispatch<TMessage>(TMessage message, IMessageHandlerContext messageHandlerContext) where TMessage : IMessage;
    }
}
