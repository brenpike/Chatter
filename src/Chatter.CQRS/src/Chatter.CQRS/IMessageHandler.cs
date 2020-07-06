using Chatter.CQRS.Context;
using System.Threading.Tasks;

namespace Chatter.CQRS
{
    public interface IMessageHandler<in TMessage> where TMessage : IMessage
    {
        Task Handle(TMessage message, IMessageHandlerContext context);
    }
}
