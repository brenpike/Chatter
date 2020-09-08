using Chatter.CQRS.Context;
using System.Threading.Tasks;

namespace Chatter.CQRS.Pipeline
{
    public delegate Task StepHandler();

    public interface IMessageHandlerPipelineStep 
    {
        Task Handle<TMessage>(TMessage message, IMessageHandlerContext messageHandlerContext, StepHandler next) where TMessage : IMessage;
    }
}
