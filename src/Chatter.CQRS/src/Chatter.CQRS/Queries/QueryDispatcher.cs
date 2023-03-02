using Chatter.CQRS.Context;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

[assembly: InternalsVisibleTo("Chatter.CQRS.Tests")]
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2, PublicKey=0024000004800000940000000602000000240000525341310004000001000100c547cac37abd99c8db225ef2f6c8a3602f3b3606cc9891605d02baa56104f4cfc0734aa39b93bf7852f7d9266654753cc297e7d2edfe0bac1cdcf9f717241550e0a7b191195b7667bb4f64bcb8e2121380fd1d9d46ad2d92d2d15605093924cceaf74c4861eff62abf69b9291ed0a340e113be11e6a7d3113e92484cf7045cc7")]
namespace Chatter.CQRS.Queries
{
    /// <summary>
    /// An <see cref="IQueryDispatcher"/> implementation to dispatch <see cref="IQuery"/> and <see cref="IQuery{T}"/> messages.
    /// </summary>
    internal sealed class QueryDispatcher : IQueryDispatcher
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<QueryDispatcher> _logger;

        public QueryDispatcher(IServiceProvider serviceProvider, ILogger<QueryDispatcher> logger)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

		///<inheritdoc/>
		public Task<TResult> Query<TResult>(IQuery<TResult> query)
			=> Query<TResult>(query, new MessageHandlerContext());
		
		///<inheritdoc/>
		public async Task<TResult> Query<TResult>(IQuery<TResult> query, IMessageHandlerContext messageHandlerContext)
        {
            try
            {
                var handlerType = typeof(IQueryHandler<,>).MakeGenericType(query.GetType(), typeof(TResult));
                dynamic handler = _serviceProvider.GetRequiredService(handlerType);
                return await handler.Handle((dynamic)query, messageHandlerContext);
            }
            catch (Exception e)
            {
                _logger.LogError($"Error dispatching query of type '{query.GetType().Name}': {e.StackTrace}");
                throw;
            }
        }

        ///<inheritdoc/>
        public Task<TResult> Query<TQuery, TResult>(TQuery query) where TQuery : class, IQuery<TResult>
			=> Query<TQuery, TResult>(query, new MessageHandlerContext());
        
        ///<inheritdoc/>
        public async Task<TResult> Query<TQuery, TResult>(TQuery query, IMessageHandlerContext messageHandlerContext) where TQuery : class, IQuery<TResult>
        {
            try
            {
                var handler = _serviceProvider.GetRequiredService<IQueryHandler<TQuery, TResult>>();
                return await handler.Handle(query, messageHandlerContext);
            }
            catch (Exception e)
            {
                _logger.LogError($"Error dispatching query of type '{typeof(TQuery).Name}': {e.StackTrace}");
                throw;
            }
        }
    }
}
