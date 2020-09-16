using Chatter.CQRS.Context;
using System.Threading.Tasks;

namespace Chatter.CQRS.Pipeline
{
    public interface ICommandBehaviorPipeline<TMessage> where TMessage : IMessage
    {
        Task Execute(TMessage message, IMessageHandlerContext messageHandlerContext, IMessageHandler<TMessage> messageHandler);
    }
}
