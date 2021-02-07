using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.Pipeline;
using Chatter.MessageBrokers.Context;
using System.Threading.Tasks;
using System.Transactions;

namespace Chatter.MessageBrokers.AzureServiceBus.Receiving
{
    class TransactionScopeSupressionBehavior<TMessage> : ICommandBehavior<TMessage> where TMessage : ICommand
    {
        public async Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next)
        {
            if (messageHandlerContext is IMessageBrokerContext messageBrokerContext)
            {
                messageBrokerContext.Container.TryGet<TransactionContext>(out var transactionContext);

                if (!(transactionContext is null))
                {
                    using var scope = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
                    await next();
                    scope.Complete();
                    return;
                }
            }

            await next();
        }
    }
}
