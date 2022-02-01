using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Exceptions;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using Chatter.MessageBrokers.SqlServiceBroker.Configuration;
using Chatter.MessageBrokers.SqlServiceBroker.Scripts;
using Chatter.MessageBrokers.SqlServiceBroker.Sending;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.SqlServiceBroker.Receiving
{
    public class SqlServiceBrokerReceiver : IMessagingInfrastructureReceiver
    {
        private readonly SqlServiceBrokerOptions _ssbOptions;
        private readonly ILogger<SqlServiceBrokerReceiver> _logger;
        private readonly IBodyConverterFactory _bodyConverterFactory;
        private TransactionMode _transactionMode;
        private readonly ConcurrentDictionary<Guid, int> _localReceiverDeliveryAttempts;
        private readonly IServiceScopeFactory _serviceFactory;
        private ReceiverOptions _options;

        public SqlServiceBrokerReceiver(SqlServiceBrokerOptions ssbOptions,
                                        MessageBrokerOptions messageBrokerOptions,
                                        ILogger<SqlServiceBrokerReceiver> logger,
                                        IBodyConverterFactory bodyConverterFactory,
                                        IServiceScopeFactory serviceFactory)
        {
            _ssbOptions = ssbOptions ?? throw new ArgumentNullException(nameof(ssbOptions));
            _logger = logger;
            _bodyConverterFactory = bodyConverterFactory;
            _transactionMode = messageBrokerOptions?.TransactionMode ?? TransactionMode.ReceiveOnly;
            _localReceiverDeliveryAttempts = new ConcurrentDictionary<Guid, int>();
            _serviceFactory = serviceFactory;
        }

        public Task InitializeAsync(ReceiverOptions options, CancellationToken cancellationToken)
        {
            _options = options;
            return Task.CompletedTask;
        }

        public Task StopReceiver()
        {
            Cancel();
            return Task.CompletedTask;
        }

        private void Cancel()
        {
        }

        private async Task<ReceivedMessage> ReceiveAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
        {
            var receiveMessageFromQueue = new ReceiveMessageFromQueueCommand(connection,
                                             _options.MessageReceiverPath,
                                             _ssbOptions.ReceiverTimeoutInMilliseconds,
                                             transaction: transaction);

            return await receiveMessageFromQueue.ExecuteAsync(cancellationToken);
        }

        public async Task<MessageBrokerContext> ReceiveMessageAsync(TransactionContext transactionContext, CancellationToken cancellationToken)
        {
            try
            {
                SqlConnection connection = new SqlConnection(_ssbOptions.ConnectionString);
                await connection.OpenAsync();
                transactionContext.Container.Include(connection);
                //for TransactionMode.ReceiveOnly and TransactionMode.FullAtomicityViaInfrastructure, we want that receive operation to be part of the transaction so it can be rolled back handling the message fails
                SqlTransaction transaction = await CreateTransaction(connection, cancellationToken);
                if (_transactionMode != TransactionMode.None && transaction != null)
                {
                    transactionContext.Container.Include(transaction);
                }

                ReceivedMessage message = null;

                try
                {
                    message = await ReceiveAsync(connection, transaction, cancellationToken);
                }
                catch (Exception)
                {
                    _logger.LogError($"Error receiving sql service broker message from queue '{_options.MessageReceiverPath}'");
                    throw;
                }

                if (message == null)
                {
                    return null;
                }

                var bodyConverter = _bodyConverterFactory.CreateBodyConverter(_ssbOptions.MessageBodyType);

                if (message?.Body == null)
                {
                    var mbc = new MessageBrokerContext(message.ConvHandle.ToString(), new byte[] { }, null, _options.MessageReceiverPath, cancellationToken, bodyConverter);
                    mbc.Container.Include(message);
                    await AckMessageAsync(mbc, transactionContext, cancellationToken);
                    return null;
                }

                MessageBrokerContext messageContext = null;

                try
                {

                    var headers = new Dictionary<string, object>
                    {
                        [SSBMessageContext.ConversationGroupId] = message.ConvGroupHandle,
                        [SSBMessageContext.ConversationHandle] = message.ConvHandle,
                        [SSBMessageContext.MessageSequenceNumber] = message.MessageSeqNo,
                        [SSBMessageContext.ServiceName] = message.ServiceName,
                        [SSBMessageContext.ServiceContractName] = message.ServiceContractName,
                        [SSBMessageContext.MessageTypeName] = message.MessageTypeName,
                        [MessageContext.InfrastructureType] = SSBMessageContext.InfrastructureType
                    };

                    messageContext = new MessageBrokerContext(message.ConvHandle.ToString(), message.Body, headers, _options.MessageReceiverPath, cancellationToken, bodyConverter);
                    messageContext.Container.Include(message);

                    return messageContext;
                }
                catch (Exception e)
                {
                    throw new PoisonedMessageException($"Unable to build {typeof(MessageBrokerContext).Name} due to poisoned message", e);
                }
            }
            catch (ArgumentException ae)
            {
                _logger.LogCritical(ae, $"Unable to receive messages. SQL Connection string supplied is invalid. {nameof(_ssbOptions.ConnectionString)}='{_ssbOptions.ConnectionString}'");
                return null;
            }
        }

        private async Task<SqlTransaction> CreateTransaction(SqlConnection connection, CancellationToken cancellationToken) 
            => (_transactionMode != TransactionMode.None ? await connection.BeginTransactionAsync(cancellationToken) : null) as SqlTransaction;

        public async Task AckMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken)
        {
            if (!context.Container.TryGet<ReceivedMessage>(out var message))
            {
                _logger.LogWarning($"No {nameof(ReceivedMessage)} was found in {nameof(context)}. Unable to acknowledge message.");
                return;
            }

            if (!transactionContext.Container.TryGet<SqlConnection>(out var connection))
            {
                _logger.LogWarning($"No {nameof(SqlConnection)} was found in {nameof(context)}. Unable to acknowledge message.");
                return;
            }

            transactionContext.Container.TryGet<SqlTransaction>(out var transaction);

            try
            {
                var edc = new EndDialogConversationCommand(connection,
                                                           message.ConvHandle,
                                                           enableCleanup: _ssbOptions.CleanupOnEndConversation,
                                                           transaction: transaction);
                await edc.ExecuteAsync(cancellationToken);
                _localReceiverDeliveryAttempts.TryRemove(message.ConvHandle, out var _);
                transaction?.Commit();
            }
            catch (Exception e)
            {
                await NackMessageAsync(context, transactionContext, cancellationToken);
                _logger.LogError(e, "Unable to complete receive operation");
            }
            finally
            {
                transaction?.Dispose();
                connection?.Dispose();
            }
        }

        public Task NackMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken)
        {
            if (!context.Container.TryGet<ReceivedMessage>(out var message))
            {
                _logger.LogWarning($"No {nameof(ReceivedMessage)} was found in {nameof(context)}. Unable to send negative acknowlegement.");
                return Task.CompletedTask;
            }

            transactionContext.Container.TryGet<SqlConnection>(out var connection);
            transactionContext.Container.TryGet<SqlTransaction>(out var transaction);

            try
            {
                transaction?.Rollback();
                _localReceiverDeliveryAttempts.AddOrUpdate(message.ConvHandle, 1, (ch, deliveryAttempts) => deliveryAttempts + 1);
                return Task.CompletedTask;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error sending negative acknowledgement for message");
                throw;
            }
            finally
            {
                transaction?.Dispose();
                connection?.Dispose();
            }
        }

        public async Task DeadletterMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, string deadLetterReason, string deadLetterErrorDescription, CancellationToken cancellationToken)
        {
            if (!context.Container.TryGet<ReceivedMessage>(out var message))
            {
                _logger.LogWarning($"No {nameof(ReceivedMessage)} was found in {nameof(context)}. Unable to dead letter message.");
                return;
            }

            if (!transactionContext.Container.TryGet<SqlConnection>(out var connection))
            {
                _logger.LogWarning($"No {nameof(SqlConnection)} was found in {nameof(context)}. Unable to dead letter message.");
                return;
            }

            transactionContext.Container.TryGet<SqlTransaction>(out var transaction);

            try
            {
                var edc = new EndDialogConversationCommand(connection,
                                                           message.ConvHandle,
                                                           enableCleanup: _ssbOptions.CleanupOnEndConversation,
                                                           transaction: transaction);
                await edc.ExecuteAsync(cancellationToken);

                using var scope = _serviceFactory.CreateScope();
                var ssbSender = scope.ServiceProvider.GetRequiredService<SqlServiceBrokerSender>(); //refactor
                var bodyConverter = _bodyConverterFactory.CreateBodyConverter(_ssbOptions.MessageBodyType);
                var headers = new Dictionary<string, object>()
                {
                    [SSBMessageContext.ServiceName] = null,
                    [MessageContext.FailureDescription] = deadLetterErrorDescription,
                    [MessageContext.FailureDetails] = deadLetterReason,
                    [MessageContext.InfrastructureType] = SSBMessageContext.InfrastructureType
                };

                await ssbSender.Dispatch(new OutboundBrokeredMessage(Guid.NewGuid().ToString(), message.Body, headers, _options.DeadLetterQueuePath, bodyConverter), transactionContext);

                if (transactionContext.TransactionMode != TransactionMode.None && transaction != null)
                {
                    transaction?.Commit();
                }

                _localReceiverDeliveryAttempts.TryRemove(message.ConvHandle, out var _);
                _logger.LogDebug("Message successfully deadlettered");
            }
            catch (Exception e)
            {
                await NackMessageAsync(context, transactionContext, cancellationToken);
                _logger.LogError(e, "Unable to complete receive operation");
            }
            finally
            {
                transaction?.Dispose();
                connection?.Dispose();
            }
        }

        public Task<int> CurrentMessageDeliveryCountAsync(MessageBrokerContext context, CancellationToken cancellationToken)
        {
            if (!context.Container.TryGet<ReceivedMessage>(out var message))
            {
                _logger.LogWarning($"No {nameof(ReceivedMessage)} was found in {nameof(context)}. Unable to fetch delivery count message.");
                return Task.FromResult(999);
            }

            _localReceiverDeliveryAttempts.TryGetValue(message.ConvHandle, out var deliveryAttempts);
            return Task.FromResult(deliveryAttempts);
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        public async ValueTask DisposeAsync()
        {
            await Task.CompletedTask;
            Dispose(disposing: false);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                Cancel();
            }
        }
    }
}
