using Chatter.CQRS.Context;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

[assembly: InternalsVisibleTo("Chatter.CQRS.Tests")]
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2, PublicKey=0024000004800000940000000602000000240000525341310004000001000100c547cac37abd99c8db225ef2f6c8a3602f3b3606cc9891605d02baa56104f4cfc0734aa39b93bf7852f7d9266654753cc297e7d2edfe0bac1cdcf9f717241550e0a7b191195b7667bb4f64bcb8e2121380fd1d9d46ad2d92d2d15605093924cceaf74c4861eff62abf69b9291ed0a340e113be11e6a7d3113e92484cf7045cc7")]
namespace Chatter.CQRS.Events
{
    /// <summary>
    /// An <see cref="IMessageDispatcher"/> implementation to dispatch <see cref="IEvent"/> messages.
    /// </summary>
    internal sealed class EventDispatcher : IDispatchMessages
    {
        private readonly IServiceProvider _serviceFactory;
        private readonly ILogger<EventDispatcher> _logger;

        public EventDispatcher(IServiceProvider serviceFactory, ILogger<EventDispatcher> logger)
        {
            _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Type DispatchType => typeof(IEvent);

        /// <summary>
        /// Dispatches an <see cref="IEvent"/> to all <see cref="IMessageHandler{TMessage}"/> with additional context.
        /// </summary>
        /// <typeparam name="TMessage">The type of event to be dispatched.</typeparam>
        /// <param name="message">The event to be dispatched.</param>
        /// <param name="messageHandlerContext">The context to be dispatched with <paramref name="message"/>.</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        /// <remarks><see cref="IEvent"/> can have multiple handlers and all will be invoked when 
        /// the <paramref name="message"/> is dispatched by <see cref="IMessageDispatcher"/></remarks>
        public async Task Dispatch<TMessage>(TMessage message, IMessageHandlerContext messageHandlerContext) where TMessage : IMessage
        {
            try
            {
                var handlers = _serviceFactory.GetServices<IMessageHandler<TMessage>>();
                foreach (var handler in handlers)
                {
                    await handler.Handle(message, messageHandlerContext).ConfigureAwait(false);
                    _logger.LogTrace($"Invoked handler for '{typeof(TMessage)}'.");
                }
            }
            catch (Exception e)
            {
                _logger.LogError($"Error dispatching event of type '{typeof(TMessage).Name}': {e.StackTrace}");
                throw;
            }
        }
    }
}
