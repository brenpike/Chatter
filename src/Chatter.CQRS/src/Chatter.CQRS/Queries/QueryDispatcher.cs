using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;

namespace Chatter.CQRS.Queries
{
    /// <summary>
    /// An <see cref="IMessageDispatcher"/> implementation to dispatch <see cref="IQuery"/> and <see cref="IQuery{T}"/> messages.
    /// </summary>
    internal sealed class QueryDispatcher : IQueryDispatcher
    {
        private readonly IServiceScopeFactory _serviceFactory;

        public QueryDispatcher(IServiceScopeFactory serviceFactory)
        {
            _serviceFactory = serviceFactory;
        }

        ///<inheritdoc/>
        public async Task<TResult> Query<TResult>(IQuery<TResult> query)
        {
            using var scope = _serviceFactory.CreateScope();
            var handlerType = typeof(IQueryHandler<,>).MakeGenericType(query.GetType(), typeof(TResult));
            dynamic handler = scope.ServiceProvider.GetRequiredService(handlerType);
            return await handler.Handle(query);
        }

        ///<inheritdoc/>
        public async Task<TResult> Query<TQuery, TResult>(TQuery query) where TQuery : class, IQuery<TResult>
        {
            using var scope = _serviceFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<IQueryHandler<TQuery, TResult>>();
            return await handler.Handle(query);
        }
    }
}
