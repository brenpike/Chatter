using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.Pipeline;
using Chatter.MessageBrokers.Context;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Outbox
{
    public sealed class OutboxProcessingBehavior<TMessage> : ICommandBehavior<TMessage> where TMessage : ICommand
    {
        private readonly IOutboxProcessor _outboxProcessor;
        private readonly ILogger<OutboxProcessingBehavior<TMessage>> _logger;

        public OutboxProcessingBehavior(IOutboxProcessor outboxProcessor, ILogger<OutboxProcessingBehavior<TMessage>> logger)
        {
            _outboxProcessor = outboxProcessor ?? throw new ArgumentNullException(nameof(outboxProcessor));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next)
        {
            await next();
            _logger.LogTrace($"Processing outbox messages via {nameof(OutboxProcessingBehavior<TMessage>)}.");
            if (messageHandlerContext.Container.TryGet<TransactionContext>(out var transactionContext))
            {
                transactionContext.Container.TryGet<Guid>("CurrentTransactionId", out var persistanceTransactionId);
                _logger.LogTrace($"Retrieved transaction id '{persistanceTransactionId}' from {nameof(TransactionContext)}.");
                await _outboxProcessor.ProcessBatch(persistanceTransactionId);
            }
            else
            {
                _logger.LogTrace($"No {nameof(TransactionContext)} found. Unable to process outbox messages.");
            }
        }
    }
}
