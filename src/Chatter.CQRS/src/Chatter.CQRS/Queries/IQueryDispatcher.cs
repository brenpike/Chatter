using System.Threading.Tasks;

namespace Chatter.CQRS.Queries
{
    public interface IQueryDispatcher
    {
        Task<TResult> Query<TResult>(IQuery<TResult> query);
        Task<TResult> Query<TQuery, TResult>(TQuery query) where TQuery : class, IQuery<TResult>;
    }
}
