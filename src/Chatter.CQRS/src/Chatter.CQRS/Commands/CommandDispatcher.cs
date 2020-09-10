using Chatter.CQRS.Context;
using Chatter.CQRS.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;

namespace Chatter.CQRS.Commands
{
    /// <summary>
    /// An <see cref="IMessageDispatcher"/> implementation to dispatch <see cref="ICommand"/> messages.
    /// </summary>
    internal sealed class CommandDispatcher : IDispatchMessages
    {
        private readonly IServiceScopeFactory _serviceFactory;

        public CommandDispatcher(IServiceScopeFactory serviceFactory)
        {
            _serviceFactory = serviceFactory;
        }

        public Type DispatchType => typeof(ICommand);

        /// <summary>
        /// Dispatches an <see cref="ICommand"/> to its <see cref="IMessageHandler{TMessage}"/> with additional context.
        /// </summary>
        /// <typeparam name="TMessage">The type of command to be dispatched.</typeparam>
        /// <param name="message">The command to be dispatched.</param>
        /// <param name="messageHandlerContext">The context to be dispatched with <paramref name="message"/>.</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        /// <remarks><see cref="ICommand"/> can only have a single handler that will be invoked when 
        /// the <paramref name="message"/> is dispatched by <see cref="IMessageDispatcher"/>.</remarks>
        public Task Dispatch<TMessage>(TMessage message, IMessageHandlerContext messageHandlerContext) where TMessage : IMessage
        {
            using var scope = _serviceFactory.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<IMessageHandler<TMessage>>();
            var pipeline = scope.ServiceProvider.GetService<ICommandBehaviorPipeline>();

            if (pipeline == null)
            {
                return handler.Handle(message, messageHandlerContext);
            }

            return pipeline.Execute(message, messageHandlerContext, handler);
        }
    }
}
