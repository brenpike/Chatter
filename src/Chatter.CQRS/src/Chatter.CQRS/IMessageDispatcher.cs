using Chatter.CQRS.Context;
using System.Threading.Tasks;

namespace Chatter.CQRS
{
    public interface IMessageDispatcher
    {
        Task Dispatch<TMessage>(TMessage message) where TMessage : IMessage;
        Task Dispatch<TMessage>(TMessage message, IMessageHandlerContext messageHandlerContext) where TMessage : IMessage;
    }
}
