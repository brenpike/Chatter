using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.CQRS.Pipeline;
using Chatter.MessageBrokers.Context;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability
{
    public class UnitOfWorkBehavior<TMessage> : ICommandBehavior<TMessage> where TMessage : IMessage
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly ILogger<UnitOfWorkBehavior<TMessage>> _logger;

        public UnitOfWorkBehavior(IUnitOfWork unitOfWork, ILogger<UnitOfWorkBehavior<TMessage>> logger)
        {
            _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next)
        {
            using var uofw = await _unitOfWork.BeginAsync().ConfigureAwait(false);

            //TODO: add extension method to more easily add to transactioncontext?
            if (messageHandlerContext.Container.TryGet<TransactionContext>(out var transactionContext))
            {
                transactionContext.Container.Include(uofw);
            }

            try
            {
                await next().ConfigureAwait(false);
                await _unitOfWork.CompleteAsync().ConfigureAwait(false);
                _logger.LogTrace($"Unit of work completed successfully for message '{typeof(TMessage).Name}'.");
            }
            catch (Exception ex)
            {
                await uofw.RollbackAsync().ConfigureAwait(false);
                _logger.LogTrace($"Error occurred during unit of work while handling message '{typeof(TMessage).Name}': {ex.Message}");
                throw;
            }
        }
    }
}
