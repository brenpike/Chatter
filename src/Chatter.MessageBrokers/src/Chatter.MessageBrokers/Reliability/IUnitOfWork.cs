using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability
{
    public interface IUnitOfWork
    {
        IPersistanceTransaction CurrentTransaction { get; }
        bool HasActiveTransaction { get; }

        ValueTask<IPersistanceTransaction> BeginAsync();
        Task CompleteAsync();
    }
}
