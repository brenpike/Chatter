using Chatter.CQRS.Context;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Saga
{
    public interface ISagaPersister
    {
        Task<SagaContext> GetById(string id);
        Task Persist(SagaContext saga, ISagaMessage message, IMessageHandlerContext context);
    }
}
