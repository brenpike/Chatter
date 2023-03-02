using System.Threading.Tasks;
using Chatter.CQRS.Context;

namespace Chatter.CQRS.Queries
{
    /// <summary>
    /// Allows the implementing class to handle queries of type <typeparamref name="TQuery"/> which return a result of <typeparamref name="TResult"/>.
    /// </summary>
    /// <typeparam name="TQuery">The type of query.</typeparam>
    /// <typeparam name="TResult">The return type of the query.</typeparam>
    public interface IQueryHandler<in TQuery, TResult> where TQuery : class, IQuery<TResult>
    {
		/// <summary>
		/// The method that is called when a query of type <typeparamref name="TQuery"/> is dispatched by a <see cref="IQueryDispatcher"/>.
		/// </summary>
		/// <param name="query">The query to execute</param>
		/// <param name="context">The context passed to the handler</param>
		/// <returns>A result of type <typeparamref name="TResult"/></returns>
		Task<TResult> Handle(TQuery query, IMessageHandlerContext context);
    }
}
