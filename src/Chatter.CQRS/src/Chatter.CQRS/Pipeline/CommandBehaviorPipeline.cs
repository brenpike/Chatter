using Chatter.CQRS.Context;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Chatter.CQRS.Pipeline
{
    public class CommandBehaviorPipeline : ICommandBehaviorPipeline
    {
        private readonly IEnumerable<ICommandBehavior> _behaviors;

        public CommandBehaviorPipeline(IEnumerable<ICommandBehavior> behaviors)
        {
            _behaviors = behaviors ?? throw new ArgumentNullException(nameof(behaviors));
        }

        public Task Execute<TMessage>(TMessage message, IMessageHandlerContext messageHandlerContext, IMessageHandler<TMessage> messageHandler) where TMessage : IMessage
        {
            Task theHandler() => messageHandler.Handle(message, messageHandlerContext);

            return _behaviors
                .Reverse()
                .Aggregate((CommandHandlerDelegate)theHandler, (next, pipeline) => () => pipeline.Handle(message, messageHandlerContext, next))();
        }
    }
}
