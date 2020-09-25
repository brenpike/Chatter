using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;

namespace Chatter.CQRS.Queries
{
    /// <summary>
    /// An <see cref="IMessageDispatcher"/> implementation to dispatch <see cref="IQuery"/> and <see cref="IQuery{T}"/> messages.
    /// </summary>
    internal sealed class QueryDispatcher : IQueryDispatcher
    {
        private readonly IServiceProvider _serviceProvider;

        public QueryDispatcher(IServiceProvider serviceProvider) 
            => _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

        ///<inheritdoc/>
        public async Task<TResult> Query<TResult>(IQuery<TResult> query)
        {
            var handlerType = typeof(IQueryHandler<,>).MakeGenericType(query.GetType(), typeof(TResult));
            dynamic handler = _serviceProvider.GetRequiredService(handlerType);
            return await handler.Handle((dynamic)query);
        }

        ///<inheritdoc/>
        public async Task<TResult> Query<TQuery, TResult>(TQuery query) where TQuery : class, IQuery<TResult>
        {
            var handler = _serviceProvider.GetRequiredService<IQueryHandler<TQuery, TResult>>();
            return await handler.Handle(query);
        }
    }
}
