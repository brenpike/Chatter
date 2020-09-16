using Chatter.CQRS.Context;
using System.Threading.Tasks;

namespace Chatter.CQRS.Pipeline
{
    public delegate Task CommandHandlerDelegate();

    public interface ICommandBehavior<TMessage> where TMessage : IMessage
    {
        Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next);
    }
}
