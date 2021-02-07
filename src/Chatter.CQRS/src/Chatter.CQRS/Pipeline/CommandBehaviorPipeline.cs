using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Chatter.CQRS.Pipeline
{
    internal class CommandBehaviorPipeline<TMessage> : ICommandBehaviorPipeline<TMessage> where TMessage : ICommand
    {
        private readonly IEnumerable<ICommandBehavior<TMessage>> _behaviors;

        public CommandBehaviorPipeline(IEnumerable<ICommandBehavior<TMessage>> behaviors) 
            => _behaviors = behaviors ?? throw new ArgumentNullException(nameof(behaviors));

        public Task Execute(TMessage message, IMessageHandlerContext messageHandlerContext, IMessageHandler<TMessage> messageHandler)
        {
            Task theHandler() => messageHandler.Handle(message, messageHandlerContext);

            return _behaviors
                .Reverse()
                .Aggregate((CommandHandlerDelegate)theHandler, (next, pipeline) => () => pipeline.Handle(message, messageHandlerContext, next))();
        }
    }
}
