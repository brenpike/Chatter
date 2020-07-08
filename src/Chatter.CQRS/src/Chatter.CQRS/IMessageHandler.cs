using Chatter.CQRS.Context;
using System.Threading.Tasks;

namespace Chatter.CQRS
{
    /// <summary>
    /// Allows the implementing class to handle <see cref="IMessage"/> of type <typeparamref name="TMessage"/>.
    /// </summary>
    /// <typeparam name="TMessage">The type of message to be handled.</typeparam>
    public interface IMessageHandler<in TMessage> where TMessage : IMessage
    {
        /// <summary>
        /// The method that is called when an <see cref="IMessage"/> of type <typeparamref name="TMessage"/> is dispatched by an <see cref="IMessageDispatcher"/>.
        /// </summary>
        /// <param name="message">The message to handle</param>
        /// <param name="context">The context passed to the handler</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Handle(TMessage message, IMessageHandlerContext context);
    }
}
