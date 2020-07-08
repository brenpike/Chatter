using System.Threading.Tasks;

namespace Chatter.CQRS.Queries
{
    public interface IQueryDispatcher
    {
        /// <summary>
        /// Dispatches an <see cref="IQuery"/> to its <see cref="IQueryHandler{TQuery, TResult}"/>.
        /// </summary>
        /// <typeparam name="TResult">The type of result to be returned by the query</typeparam>
        /// <param name="query">The query to be dispatched.</param>
        /// <returns>An awaitable <see cref="Task"/> containing the query result</returns>
        Task<TResult> Query<TResult>(IQuery<TResult> query);
        /// <summary>
        /// Dispatches an <see cref="IQuery"/> to its <see cref="IQueryHandler{TQuery, TResult}"/>.
        /// </summary>
        /// <typeparam name="TResult">The type of result to be returned by the query</typeparam>
        /// <typeparam name="TQuery">The type of query to be executed</typeparam>
        /// <param name="query">The query to be dispatched.</param>
        /// <returns>An awaitable <see cref="Task"/> containing the query result</returns>
        Task<TResult> Query<TQuery, TResult>(TQuery query) where TQuery : class, IQuery<TResult>;
    }
}
