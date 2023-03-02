using Chatter.CQRS;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery;
using Chatter.MessageBrokers.Sending;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.SqlChangeFeed
{
    class ChangeFeedReceiver<TRowChangeData> : BrokeredMessageReceiver<ProcessChangeFeedCommand<TRowChangeData>>
        where TRowChangeData : class, IMessage
    {
        private readonly IServiceScopeFactory _serviceFactory;

        public ChangeFeedReceiver(IMessagingInfrastructureProvider infrastructureProvider,
                                   MessageBrokerOptions messageBrokerOptions,
                                   ILogger<BrokeredMessageReceiver<ProcessChangeFeedCommand<TRowChangeData>>> logger,
                                   IServiceScopeFactory serviceFactory,
                                   IMaxReceivesExceededAction recoveryAction,
                                   ICriticalFailureNotifier criticalFailureNotifier,
                                   IRecoveryStrategy recoveryStrategy,
                                   IReceivedMessageDispatcher receivedMessageDispatcher)
            : base(infrastructureProvider, messageBrokerOptions, logger, recoveryAction, criticalFailureNotifier, recoveryStrategy, receivedMessageDispatcher)
        {
            _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
        }

        public override async Task DispatchReceivedMessageAsync(ProcessChangeFeedCommand<TRowChangeData> payload, MessageBrokerContext messageContext, CancellationToken receiverTokenSource)
        {
            receiverTokenSource.ThrowIfCancellationRequested();

            using var scope = _serviceFactory.CreateScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<IMessageDispatcher>();
            messageContext.Container.Include((IExternalDispatcher)scope.ServiceProvider.GetRequiredService<IBrokeredMessageDispatcher>());

            int totalChangeCount = payload.Changes.Count();

            if (totalChangeCount == 0)
            {
                _logger.LogTrace("No inserted or deleted records contained in {CommandType}", nameof(ProcessChangeFeedCommand<TRowChangeData>));
                return;
            }

            int inserted = 0, updated = 0, deleted = 0;
            _logger.LogTrace("Processing {TotalNumChanges} changes from Change Feed", totalChangeCount);
            foreach(var changeFeedItem in payload.Changes)
            {
                if (changeFeedItem.Inserted != null && changeFeedItem.Deleted != null)
                {
                    await dispatcher.Dispatch(new RowUpdatedEvent<TRowChangeData>(changeFeedItem.Inserted, changeFeedItem.Deleted), messageContext);
                    _logger.LogTrace("Processed UPDATE from change feed");
                    updated++;
                }
                else if (changeFeedItem.Inserted != null && changeFeedItem.Deleted == null)
                {
                    await dispatcher.Dispatch(new RowInsertedEvent<TRowChangeData>(changeFeedItem.Inserted), messageContext);
                    _logger.LogTrace("Processed INSERT from change feed");
                    inserted++;
                }
                else if (changeFeedItem.Inserted == null && changeFeedItem.Deleted != null)
                {
                    await dispatcher.Dispatch(new RowDeletedEvent<TRowChangeData>(changeFeedItem.Deleted), messageContext);
                    _logger.LogTrace("Processed DELETE from change feed");
                    deleted++;
                }
                else
                {
                    _logger.LogWarning("No changes to process in Change Feed");
                }
            }
            _logger.LogTrace("Finished processing {TotalNumChanges} changes. {NumInserts} inserts, {NumUpdates} updates, {NumDeletes} deletes.", totalChangeCount, inserted, updated, deleted);
        }
    }
}
