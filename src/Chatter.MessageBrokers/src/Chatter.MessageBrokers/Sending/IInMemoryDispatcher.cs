using Chatter.CQRS;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Sending
{
    public interface IInMemoryDispatcher
    {
        /// <summary>
        /// Dispatches a <typeparamref name="TMessage"/> to a handler within the same process  
        /// </summary>
        /// <typeparam name="TMessage">The type of message to dispatch</typeparam>
        /// <param name="message">The message to dispatch</param>
        /// <returns>An awaitable task.</returns>
        Task Dispatch<TMessage>(TMessage message) where TMessage : IMessage;
    }
}
