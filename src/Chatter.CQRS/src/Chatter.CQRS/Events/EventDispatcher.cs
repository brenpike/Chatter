using Chatter.CQRS.Context;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;

namespace Chatter.CQRS.Events
{
    /// <summary>
    /// An <see cref="IMessageDispatcher"/> implementation to dispatch <see cref="IEvent"/> messages.
    /// </summary>
    internal sealed class EventDispatcher : IDispatchMessages
    {
        private readonly IServiceScopeFactory _serviceFactory;

        public EventDispatcher(IServiceScopeFactory serviceFactory)
        {
            _serviceFactory = serviceFactory;
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
            using var scope = _serviceFactory.CreateScope();
            var handlers = scope.ServiceProvider.GetServices<IMessageHandler<TMessage>>();
            foreach (var handler in handlers)
            {
                await handler.Handle(message, messageHandlerContext).ConfigureAwait(false);
            }
        }
    }
}
