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
            ReceivedMessage message = null;
            MessageBrokerContext messageContext = null;

            SqlConnection connection = new SqlConnection(_ssbOptions.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            transactionContext.Container.Include(connection);
            SqlTransaction transaction = await CreateTransaction(connection, cancellationToken);
            if (_transactionMode != TransactionMode.None && transaction != null)
            {
                transactionContext.Container.Include(transaction);
            }

            try
            {
                message = await ReceiveAsync(connection, transaction, cancellationToken);
                //todo: add catch blocks for non-transient exceptions which can never be recovered (critical error), i.e., invalid connection string, etc.
                //_logger.LogCritical(ae, $"Unable to receive messages. SQL Connection string supplied is invalid. {nameof(_ssbOptions.ConnectionString)}='{_ssbOptions.ConnectionString}'");
                //throw new CriticalReceiverException(ae);
            }
#if NET5_0_OR_GREATER
                catch (SqlException e) when (e.IsTransient)
                {
                    _logger.LogWarning(e, "Failure to receive message from Sql Service Broker due to transient error");
                    throw;
                }
#endif
            catch (Exception e)
            {
                _logger.LogError(e, $"Error receiving sql service broker message from queue '{_options.MessageReceiverPath}'");
                throw;
            }

            if (message is null || message?.Body == null || message?.MessageTypeName != "DEFAULT")
            {
                await AckMessageAsync(null, transactionContext, cancellationToken); //discard message
                return null;
            }

            try
            {
                var bodyConverter = _bodyConverterFactory.CreateBodyConverter(_ssbOptions.MessageBodyType);
                _localReceiverDeliveryAttempts.TryGetValue(message.ConvHandle, out var deliveryAttempts);

                var headers = new Dictionary<string, object>
                {
                    [SSBMessageContext.ConversationGroupId] = message.ConvGroupHandle,
                    [SSBMessageContext.ConversationHandle] = message.ConvHandle,
                    [SSBMessageContext.MessageSequenceNumber] = message.MessageSeqNo,
                    [SSBMessageContext.ServiceName] = message.ServiceName,
                    [SSBMessageContext.ServiceContractName] = message.ServiceContractName,
                    [SSBMessageContext.MessageTypeName] = message.MessageTypeName,
                    [MessageContext.InfrastructureType] = SSBMessageContext.InfrastructureType,
                    [MessageContext.ReceiveAttempts] = deliveryAttempts
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

        private async Task<SqlTransaction> CreateTransaction(SqlConnection connection, CancellationToken cancellationToken)
            => (_transactionMode != TransactionMode.None ? await connection.BeginTransactionAsync(cancellationToken) : null) as SqlTransaction;

        public async Task<bool> AckMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken)
        {
            ReceivedMessage msg = null;
            if (!context?.Container.TryGet<ReceivedMessage>(out msg) ?? false)
            {
                _logger.LogTrace($"No {nameof(ReceivedMessage)} contained in {nameof(context)}.");
            }

            transactionContext.Container.TryGet<SqlConnection>(out var connection);
            transactionContext.Container.TryGet<SqlTransaction>(out var transaction);

            try
            {
                if (msg != null)
                {
                    var edc = new EndDialogConversationCommand(connection,
                                           msg.ConvHandle,
                                           enableCleanup: _ssbOptions.CleanupOnEndConversation,
                                           transaction: transaction);
                    await edc.ExecuteAsync(cancellationToken);
                    _localReceiverDeliveryAttempts.TryRemove(msg.ConvHandle, out var _);
                }
                await transaction?.CommitAsync(cancellationToken);
                return true;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Unable to complete receive operation");
                await NackMessageAsync(context, transactionContext, cancellationToken);
                return false;
            }
            finally
            {
                transaction?.Dispose();
                connection?.Dispose();
            }
        }

        public async Task<bool> NackMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken)
        {
            ReceivedMessage msg = null;
            if (!context?.Container.TryGet<ReceivedMessage>(out msg) ?? false)
            {
                _logger.LogTrace($"No {nameof(ReceivedMessage)} contained in {nameof(context)}.");
            }

            transactionContext.Container.TryGet<SqlConnection>(out var connection);
            transactionContext.Container.TryGet<SqlTransaction>(out var transaction);

            try
            {
                await transaction?.RollbackAsync(cancellationToken);
                if (msg != null)
                {
                    _localReceiverDeliveryAttempts.AddOrUpdate(msg.ConvHandle, 1, (ch, deliveryAttempts) => deliveryAttempts + 1);
                }
                return true;
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

        public async Task<bool> DeadletterMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, string deadLetterReason, string deadLetterErrorDescription, CancellationToken cancellationToken)
        {
            ReceivedMessage msg = null;
            if (!context?.Container.TryGet<ReceivedMessage>(out msg) ?? false)
            {
                _logger.LogTrace($"No {nameof(ReceivedMessage)} contained in {nameof(context)}.");
            }

            transactionContext.Container.TryGet<SqlConnection>(out var connection);
            transactionContext.Container.TryGet<SqlTransaction>(out var transaction);

            try
            {
                if (msg != null)
                {
                    var edc = new EndDialogConversationCommand(connection,
                                           msg.ConvHandle,
                                           enableCleanup: _ssbOptions.CleanupOnEndConversation,
                                           transaction: transaction);
                    await edc.ExecuteAsync(cancellationToken);
                    _localReceiverDeliveryAttempts.TryRemove(msg.ConvHandle, out var _);
                }

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

                await ssbSender.Dispatch(new OutboundBrokeredMessage(Guid.NewGuid().ToString(), msg.Body, headers, _options.DeadLetterQueuePath, bodyConverter), transactionContext);
                return true;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Unable deadletter message");
                await NackMessageAsync(context, transactionContext, cancellationToken);
                return false;
            }
            finally
            {
                if (transactionContext.TransactionMode == TransactionMode.FullAtomicityViaInfrastructure)
                {
                    await transaction?.CommitAsync(cancellationToken);
                }
                transaction?.Dispose();
                connection?.Dispose();
            }
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
