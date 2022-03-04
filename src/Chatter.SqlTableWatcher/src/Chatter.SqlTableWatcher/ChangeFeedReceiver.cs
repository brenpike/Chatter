using Chatter.CQRS;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery;
using Chatter.MessageBrokers.Sending;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.SqlTableWatcher
{
    class ChangeFeedReceiver<TProcessorCommand, TRowChangeData> : BrokeredMessageReceiver<TProcessorCommand>
        where TRowChangeData : class, IMessage
        where TProcessorCommand : ProcessChangeFeedCommand<TRowChangeData>
    {
        private readonly IServiceScopeFactory _serviceFactory;

        public ChangeFeedReceiver(IMessagingInfrastructureProvider infrastructureProvider,
                                   MessageBrokerOptions messageBrokerOptions,
                                   ILogger<BrokeredMessageReceiver<TProcessorCommand>> logger,
                                   IServiceScopeFactory serviceFactory,
                                   IMaxReceivesExceededAction recoveryAction,
                                   ICriticalFailureNotifier criticalFailureNotifier,
                                   IRecoveryStrategy recoveryStrategy,
                                   IReceivedMessageDispatcher receivedMessageDispatcher)
            : base(infrastructureProvider, messageBrokerOptions, logger, recoveryAction, criticalFailureNotifier, recoveryStrategy, receivedMessageDispatcher)
        {
            _serviceFactory = serviceFactory ?? throw new System.ArgumentNullException(nameof(serviceFactory));
        }

        public override async Task DispatchReceivedMessageAsync(TProcessorCommand message, MessageBrokerContext context, CancellationToken receiverTokenSource)
        {
            receiverTokenSource.ThrowIfCancellationRequested();

            using var scope = _serviceFactory.CreateScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<IMessageDispatcher>();
            context.Container.Include((IExternalDispatcher)scope.ServiceProvider.GetRequiredService<IBrokeredMessageDispatcher>());

            if (message.Inserted is null && message.Deleted is null)
            {
                _logger.LogTrace($"No inserted or deleted records contained in {nameof(ProcessChangeFeedCommand<TRowChangeData>)}");
                return;
            }

            if (message.Inserted?.Count() > 0 && message.Deleted?.Count() > 0)
            {
                _logger.LogTrace("Processing table UPDATES");
                for (int i = 0; i < message.Inserted.Count(); i++)
                {
                    _logger.LogDebug($"UPDATE {i + 1} of {message.Inserted.Count()}");
                    var updated = new RowUpdatedEvent<TRowChangeData>(message.Inserted.ElementAt(i), message.Deleted.ElementAt(i));
                    await dispatcher.Dispatch(updated, context);
                }
            }
            else if (message.Inserted?.Count() > 0 && message.Deleted?.Count() == 0)
            {
                _logger.LogTrace("Processing table INSERTS");
                for (int i = 0; i < message.Inserted.Count(); i++)
                {
                    _logger.LogDebug($"INSERT {i + 1} of {message.Inserted.Count()}");
                    await dispatcher.Dispatch(new RowInsertedEvent<TRowChangeData>(message.Inserted.ElementAt(i)), context);
                }
            }
            else if (message.Inserted?.Count() == 0 && message.Deleted?.Count() > 0)
            {
                _logger.LogTrace("Processing table DELETES");
                for (int i = 0; i < message.Deleted.Count(); i++)
                {
                    _logger.LogDebug($"DELETE {i + 1} of {message.Deleted.Count()}");
                    await dispatcher.Dispatch(new RowDeletedEvent<TRowChangeData>(message.Deleted.ElementAt(i)), context);
                }
            }
            else
            {
                _logger.LogWarning("No table changes were found.");
            }
        }
    }
}
