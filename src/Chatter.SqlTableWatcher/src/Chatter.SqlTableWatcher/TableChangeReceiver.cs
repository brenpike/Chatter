using Chatter.CQRS;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Chatter.SqlTableWatcher
{
    class TableChangeReceiver<TProcessorCommand, TRowChangeData> : BrokeredMessageReceiver<TProcessorCommand>
        where TRowChangeData : class, IMessage
        where TProcessorCommand : ProcessTableChangesCommand<TRowChangeData>
    {

        public TableChangeReceiver(IMessagingInfrastructureProvider infrastructureProvider,
                                   ILogger<BrokeredMessageReceiver<TProcessorCommand>> logger,
                                   IServiceScopeFactory serviceFactory)
            : base(infrastructureProvider, logger, serviceFactory)
        {
        }

        public override async Task ReceiveInboundBrokeredMessage(MessageBrokerContext context, TransactionContext transactionContext)
        {
            try
            {
                TProcessorCommand message = null;

                try
                {
                    if (context is null)
                    {
                        throw new ArgumentNullException(nameof(context), $"A {typeof(MessageBrokerContext).Name} was not created by the messaging infrastructure.");
                    }

                    var inboundMessage = context.BrokeredMessage;

                    if (transactionContext is null)
                    {
                        transactionContext = new TransactionContext(_options.MessageReceiverPath, _options.TransactionMode.Value);
                    }

                    context.Container.Include(transactionContext);

                    message = inboundMessage.GetMessageFromBody<TProcessorCommand>();
                }
                catch (Exception e)
                {
                    throw new PoisonedMessageException($"Unable to build {typeof(MessageBrokerContext).Name} due to poisoned message", e);
                }

                await ProcessSqlTableChanges(message, context).ConfigureAwait(false);
            }
            catch (PoisonedMessageException)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new ReceiverMessageDispatchingException($"Error processing sql table changes '{typeof(TRowChangeData).Name}' received by '{typeof(TableChangeReceiver<,>).Name}'", e);
            }
        }

        async Task ProcessSqlTableChanges(TProcessorCommand message, MessageBrokerContext context)
        {
            using var scope = _serviceFactory.CreateScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<IMessageDispatcher>();
            context.Container.Include((IExternalDispatcher)scope.ServiceProvider.GetRequiredService<IBrokeredMessageDispatcher>());

            if (message.Inserted is null && message.Deleted is null)
            {
                _logger.LogDebug($"No inserted or deleted records specified by {nameof(ProcessTableChangesCommand<TRowChangeData>)}");
                return;
            }

            if (message.Inserted?.Count() > 0 && message.Deleted?.Count() > 0)
            {
                _logger.LogDebug("Processing table UPDATES");
                for (int i = 0; i < message.Inserted.Count(); i++)
                {
                    _logger.LogTrace($"UPDATE {i + 1} of {message.Inserted.Count()}");
                    var updated = new RowUpdatedEvent<TRowChangeData>(message.Inserted.ElementAt(i), message.Deleted.ElementAt(i));
                    await dispatcher.Dispatch(updated, context).ConfigureAwait(false);
                }
            }
            else if (message.Inserted?.Count() > 0 && message.Deleted?.Count() == 0)
            {
                _logger.LogDebug("Processing table INSERTS");
                for (int i = 0; i < message.Inserted.Count(); i++)
                {
                    _logger.LogTrace($"INSERT {i + 1} of {message.Inserted.Count()}");
                    await dispatcher.Dispatch(new RowInsertedEvent<TRowChangeData>(message.Inserted.ElementAt(i)), context).ConfigureAwait(false);
                }
            }
            else if (message.Inserted?.Count() == 0 && message.Deleted?.Count() > 0)
            {
                _logger.LogDebug("Processing table DELETES");
                for (int i = 0; i < message.Inserted.Count(); i++)
                {
                    _logger.LogTrace($"DELETE {i + 1} of {message.Inserted.Count()}");
                    await dispatcher.Dispatch(new RowDeletedEvent<TRowChangeData>(message.Deleted.ElementAt(i)), context).ConfigureAwait(false);
                }
            }
            else
            {
                var e = new Exception("No table changes we found.");
                _logger.LogError(e, "");
            }
        }
    }
}
