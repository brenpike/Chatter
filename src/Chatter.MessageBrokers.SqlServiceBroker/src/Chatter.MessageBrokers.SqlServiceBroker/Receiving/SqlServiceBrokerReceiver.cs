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
            ReceivedMessage message = null;
            MessageBrokerContext messageContext = null;
            SqlConnection connection = null;
            SqlTransaction transaction = null;

            try
            {
                connection = new SqlConnection(_ssbOptions.ConnectionString);
                await connection.OpenAsync(cancellationToken);
                transaction = await CreateTransaction(connection, cancellationToken);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
            {
                transaction?.Dispose();
                connection?.Dispose();
                throw new CriticalReceiverException("Error connecting to sql", ex);
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
            catch (SqlException e) when (e.Number == 208 || e.Number == 102)
            {
                throw new CriticalReceiverException($"Unable to receive message from configured queue '{_options.MessageReceiverPath}'", e);
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Error receiving sql service broker message from queue '{_options.MessageReceiverPath}'");
                throw;
            }
            finally
            {
                if (message == null)
                {
                    transaction?.Dispose();
                    connection?.Dispose();
                }
            }

            if (message is null)
            {
                await DiscardMessageAsync(connection, transaction, "Discarding null message", cancellationToken);
                return null;
            }

            if (message.MessageTypeName != ServicesMessageTypes.DefaultType && message.MessageTypeName != ServicesMessageTypes.ChatterBrokeredMessageType)
            {
                await DiscardMessageAsync(connection, transaction
                    , $"Discarding message of type '{message.MessageTypeName}'. Only messages of type '{ServicesMessageTypes.DefaultType}' or '{ServicesMessageTypes.ChatterBrokeredMessageType}' will be received."
                    , cancellationToken);
                return null;
            }

            if (message.Body == null)
            {
                await DiscardMessageAsync(connection, transaction
                    , $"Discarding message of type '{message.MessageTypeName}' with null message body"
                    , cancellationToken);
                return null;
            }

            transactionContext.Container.Include(connection);
            if (_transactionMode != TransactionMode.None && transaction != null)
            {
                transactionContext.Container.Include(transaction);
            }

            _localReceiverDeliveryAttempts.AddOrUpdate(message.ConvHandle, 1, (ch, deliveryAttempts) => deliveryAttempts + 1);

            IBrokeredMessageBodyConverter bodyConverter = new JsonUnicodeBodyConverter();
            byte[] messagePayload = message.Body;
            string messageId = message.ConvHandle.ToString();
            IDictionary<string, object> headers = new Dictionary<string, object>();

            try
            {
                bodyConverter = _bodyConverterFactory.CreateBodyConverter(_ssbOptions.MessageBodyType);
                if (message.MessageTypeName == ServicesMessageTypes.ChatterBrokeredMessageType)
                {
                    var brokeredMessage = bodyConverter.Convert<OutboundBrokeredMessage>(message.Body);

                    if (brokeredMessage == null)
                    {
                        throw new ArgumentNullException(nameof(brokeredMessage), $"Unable to deserialize {nameof(OutboundBrokeredMessage)} from message body");
                    }

                    messagePayload = brokeredMessage.Body;
                    messageId = brokeredMessage.MessageId;
                    headers = brokeredMessage.MessageContext;
                }
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, $"Error creating body converter for content type '{_ssbOptions.MessageBodyType}'. Defaulting to {nameof(JsonUnicodeBodyConverter)}.");
            }
            finally
            {
                _localReceiverDeliveryAttempts.TryGetValue(message.ConvHandle, out var deliveryAttempts);

                headers[SSBMessageContext.ConversationGroupId] = message.ConvGroupHandle;
                headers[SSBMessageContext.ConversationHandle] = message.ConvHandle;
                headers[SSBMessageContext.MessageSequenceNumber] = message.MessageSeqNo;
                headers[SSBMessageContext.ServiceName] = message.ServiceName;
                headers[SSBMessageContext.ServiceContractName] = message.ServiceContractName;
                headers[SSBMessageContext.MessageTypeName] = message.MessageTypeName;
                headers[MessageContext.InfrastructureType] = SSBMessageContext.InfrastructureType;
                headers[MessageContext.ReceiveAttempts] = deliveryAttempts;

                messageContext = new MessageBrokerContext(messageId, messagePayload, headers, _options.MessageReceiverPath, cancellationToken, bodyConverter);
                messageContext.Container.Include(message);
            }

            return messageContext;
        }

        private async Task DiscardMessageAsync(SqlConnection connection, SqlTransaction transaction, string discardMessage, CancellationToken cancellationToken)
        {
            await transaction?.CommitAsync(cancellationToken);
            transaction?.Dispose();
            connection?.Dispose();
            _logger.LogTrace(discardMessage);
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

                _localReceiverDeliveryAttempts.TryGetValue(msg.ConvHandle, out var deliveryAttempts);

                var headers = new Dictionary<string, object>()
                {
                    [SSBMessageContext.ConversationHandle] = msg.ConvHandle,
                    [SSBMessageContext.ServiceName] = msg.ServiceName,
                    [MessageContext.FailureDescription] = deadLetterErrorDescription,
                    [MessageContext.FailureDetails] = deadLetterReason,
                    [MessageContext.InfrastructureType] = SSBMessageContext.InfrastructureType,
                    [SSBMessageContext.MessageTypeName] = ServicesMessageTypes.ChatterBrokeredMessageType,
                    [SSBMessageContext.ServiceContractName] = ServicesMessageTypes.ChatterServiceContract,
                    [MessageContext.ReceiveAttempts] = deliveryAttempts
                };
                await ssbSender.Dispatch(new OutboundBrokeredMessage(context.BrokeredMessage.MessageId, msg.Body, headers, _options.DeadLetterQueuePath, bodyConverter), transactionContext);
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
