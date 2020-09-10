using Chatter.CQRS.Context;
using System.Threading.Tasks;

namespace Chatter.CQRS.Pipeline
{
    public interface ICommandBehaviorPipeline
    {
        Task Execute<TMessage>(TMessage message, IMessageHandlerContext messageHandlerContext, IMessageHandler<TMessage> messageHandler) where TMessage : IMessage;
    }
}
