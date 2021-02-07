using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using System.Threading.Tasks;

namespace Chatter.CQRS.Pipeline
{
    public delegate Task CommandHandlerDelegate();

    public interface ICommandBehavior<TMessage> where TMessage : ICommand
    {
        Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next);
    }
}
