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
            cancellationToken.ThrowIfCancellationRequested();

            var receiveMessageFromQueue = new ReceiveMessageFromQueueCommand(connection,
                                             _options.MessageReceiverPath,
                                             _ssbOptions.ReceiverTimeoutInMilliseconds,
                                             transaction: transaction);

            return await receiveMessageFromQueue.ExecuteAsync(cancellationToken);
        }

        public async Task<MessageBrokerContext> ReceiveMessageAsync(TransactionContext transactionContext, CancellationToken cancellationToken)
        {
            //TODO: Sql Service Broker needs some sort of message envelope.  When deadlettering, for example, the message body is the only thing saved
            //      none of the headers or anything is saved incuding the deadletter reason and description etc.  We also need to store things like
            //      delivery counts and other types of metadata
            ReceivedMessage message = null;
            MessageBrokerContext messageContext = null;
            SqlConnection connection;
            SqlTransaction transaction;

            try
            {
                connection = new SqlConnection(_ssbOptions.ConnectionString);
                await connection.OpenAsync(cancellationToken);
                transaction = await CreateTransaction(connection, cancellationToken);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
            {
                throw new CriticalReceiverException("Error connecting to sql", ex);
            }

            transactionContext.Container.Include(connection);
            if (_transactionMode != TransactionMode.None && transaction != null)
            {
                transactionContext.Container.Include(transaction);
            }

            try
            {
                message = await ReceiveAsync(connection, transaction, cancellationToken);
            }
#if NET5_0_OR_GREATER
            catch (SqlException e) when (e.IsTransient)
            {
                _logger.LogWarning(e, "Failure to receive message from Sql Service Broker due to transient error");
                throw;
            }
#endif
            catch (SqlException e) when (e.Number == 208)
            {
                throw new CriticalReceiverException($"Unable to receive message from configured queue '{_options.MessageReceiverPath}'", e);
            }
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

            _localReceiverDeliveryAttempts.AddOrUpdate(message.ConvHandle, 1, (ch, deliveryAttempts) => deliveryAttempts + 1);

            IBrokeredMessageBodyConverter bodyConverter = new JsonUnicodeBodyConverter();

            try
            {
                bodyConverter = _bodyConverterFactory.CreateBodyConverter(_ssbOptions.MessageBodyType);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, $"Error creating body converter for content type '{_ssbOptions.MessageBodyType}'. Defaulting to {nameof(JsonUnicodeBodyConverter)}.");
            }
            finally
            {
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
            }

            return messageContext;
        }

        private async Task<SqlTransaction> CreateTransaction(SqlConnection connection, CancellationToken cancellationToken)
            => (_transactionMode != TransactionMode.None ? await connection.BeginTransactionAsync(cancellationToken) : null) as SqlTransaction;

        public async Task<bool> AckMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken)
        {
            ReceivedMessage msg = null;
            if (!context?.Container.TryGet(out msg) ?? false)
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
                else
                {
                    _logger.LogTrace($"Unable end dialog conversation during message acknowledgment. {nameof(msg)} is null.");
                }
                await transaction?.CommitAsync(cancellationToken);
                _logger.LogTrace("Message acknowledgment complete");
                return true;
            }
            finally
            {
                transaction?.Dispose();
                connection?.Dispose();
            }
        }

        public async Task<bool> NackMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken)
        {
            transactionContext.Container.TryGet<SqlConnection>(out var connection);
            transactionContext.Container.TryGet<SqlTransaction>(out var transaction);

            try
            {
                await transaction?.RollbackAsync(cancellationToken);
                _logger.LogTrace("Message negative acknowledgment complete");
                return true;
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
            if (!context?.Container.TryGet(out msg) ?? false)
            {
                throw new ArgumentException($"Unable to deadletter message. No {nameof(ReceivedMessage)} contained in {nameof(context)}.", nameof(msg));
            }

            transactionContext.Container.TryGet<SqlConnection>(out var connection);
            transactionContext.Container.TryGet<SqlTransaction>(out var transaction);

            try
            {
                var edc = new EndDialogConversationCommand(connection,
                                       msg.ConvHandle,
                                       enableCleanup: _ssbOptions.CleanupOnEndConversation,
                                       transaction: transaction);
                await edc.ExecuteAsync(cancellationToken);

                using var scope = _serviceFactory.CreateScope();
                var ssbSender = scope.ServiceProvider.GetRequiredService<SqlServiceBrokerSender>();
                var bodyConverter = _bodyConverterFactory.CreateBodyConverter(_ssbOptions.MessageBodyType);
                var headers = new Dictionary<string, object>()
                {
                    [SSBMessageContext.ServiceName] = null,
                    [MessageContext.FailureDescription] = deadLetterErrorDescription,
                    [MessageContext.FailureDetails] = deadLetterReason,
                    [MessageContext.InfrastructureType] = SSBMessageContext.InfrastructureType
                };

                await ssbSender.Dispatch(new OutboundBrokeredMessage(Guid.NewGuid().ToString(), msg.Body, headers, _options.DeadLetterQueuePath, bodyConverter), transactionContext);
                await transaction?.CommitAsync(cancellationToken);
                _localReceiverDeliveryAttempts.TryRemove(msg.ConvHandle, out var _);
                _logger.LogTrace($"Message deadlettered.");
                return true;
            }
            finally
            {
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
