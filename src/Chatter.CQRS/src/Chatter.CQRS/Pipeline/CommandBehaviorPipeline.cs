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
            // INVARIANT: the behaviors are materialized per execution, never once in the constructor. A behavior
            // registered after this pipeline instance was resolved must still take part in the next execution.
            var behaviors = _behaviors as ICommandBehavior<TMessage>[] ?? _behaviors.ToArray();

            if (behaviors.Length == 0)
            {
                return messageHandler.Handle(message, messageHandlerContext);
            }

            return ComposeBehaviorChain(behaviors, message, messageHandlerContext, messageHandler)();
        }

        // INVARIANT: the chain is composed here rather than inline in Execute so that Execute captures nothing.
        // A lambda anywhere in Execute would make the compiler allocate its closure on entry, before the
        // no-behavior fast path can return.
        private static CommandHandlerDelegate ComposeBehaviorChain(
            ICommandBehavior<TMessage>[] behaviors,
            TMessage message,
            IMessageHandlerContext messageHandlerContext,
            IMessageHandler<TMessage> messageHandler)
        {
            CommandHandlerDelegate next = () => messageHandler.Handle(message, messageHandlerContext);

            // INVARIANT: composing from the last behavior to the first makes the FIRST-registered behavior the
            // outermost one. Modules outside Chatter.CQRS depend on that ordering.
            for (var index = behaviors.Length - 1; index >= 0; index--)
            {
                var behavior = behaviors[index];
                var innerHandler = next;
                next = () => behavior.Handle(message, messageHandlerContext, innerHandler);
            }

            return next;
        }
    }
}
