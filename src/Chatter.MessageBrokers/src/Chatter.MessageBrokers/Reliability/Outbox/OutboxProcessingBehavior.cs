using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.CQRS.Pipeline;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Outbox
{
    public sealed class OutboxProcessingBehavior<TMessage> : ICommandBehavior<TMessage> where TMessage : IMessage
    {
        private readonly IOutboxProcessor _outboxProcessor;

        public OutboxProcessingBehavior(IOutboxProcessor outboxProcessor)
        {
            _outboxProcessor = outboxProcessor ?? throw new ArgumentNullException(nameof(outboxProcessor));
        }

        public async Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next)
        {
            await next();
            //TODO: implement
            //TODO: get transaction id from TransactionContext...only problem is that it will likely be null after the unit of work is committed...aka here.
            //await _outboxProcessor.ProcessBatch(transactionId);
        }
    }
}
