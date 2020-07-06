using Chatter.CQRS.Context;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Saga
{
    public interface ISagaInitializer
    {
        Task<SagaContext> Initialize(ISagaMessage message, IMessageHandlerContext context);
    }
}
