using Chatter.CQRS.Context;
using System.Threading.Tasks;

namespace Chatter.CQRS.Pipeline
{
    public delegate Task CommandHandlerDelegate();

    public interface ICommandBehavior 
    {
        Task Handle<TMessage>(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next) where TMessage : IMessage;
    }
}
