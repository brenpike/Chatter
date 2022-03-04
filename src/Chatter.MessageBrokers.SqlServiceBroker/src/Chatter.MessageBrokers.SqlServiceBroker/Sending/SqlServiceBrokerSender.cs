using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using Chatter.MessageBrokers.SqlServiceBroker.Configuration;
using Chatter.MessageBrokers.SqlServiceBroker.Scripts;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.SqlServiceBroker.Sending
{
    public class SqlServiceBrokerSender : IMessagingInfrastructureDispatcher
    {
        private readonly SqlServiceBrokerOptions _options;
        private readonly ILogger<SqlServiceBrokerSender> _logger;
        private readonly IBodyConverterFactory _bodyConverterFactory;

        public SqlServiceBrokerSender(SqlServiceBrokerOptions options,
                                      ILogger<SqlServiceBrokerSender> logger,
                                      IBodyConverterFactory bodyConverterFactory)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _bodyConverterFactory = bodyConverterFactory ?? throw new ArgumentNullException(nameof(bodyConverterFactory));
        }

        public async Task Dispatch(IEnumerable<OutboundBrokeredMessage> brokeredMessages, TransactionContext transactionContext)
        {
            _logger.LogInformation("Sending sql service broker message(s)");
            SqlTransaction contextTransaction = null;
            transactionContext?.Container.TryGet(out contextTransaction);
            var contextTransactionMode = transactionContext?.TransactionMode ?? TransactionMode.None;
            var useContextTransaction = contextTransactionMode == TransactionMode.FullAtomicityViaInfrastructure && contextTransaction != null;

            SqlConnection connection = useContextTransaction
                                            ? contextTransaction.Connection
                                            : new SqlConnection(_options.ConnectionString);

            if (connection.State != ConnectionState.Open)
            {
                await connection.OpenAsync();
                _logger.LogTrace("Sql connection opened.");
            }

            var transaction = useContextTransaction
                                    ? contextTransaction
                                    : (SqlTransaction)await connection.BeginTransactionAsync();

            try
            {
                foreach (var brokeredMessage in brokeredMessages)
                {
                    _logger.LogTrace($"Sending brokered message to '{brokeredMessage.Destination}'");
                    brokeredMessage.MessageContext.TryGetValue(SSBMessageContext.ConversationGroupId, out var contextConversationGroupId);
                    brokeredMessage.MessageContext.TryGetValue(SSBMessageContext.ConversationHandle, out var contextConversationHandle);
                    brokeredMessage.MessageContext.TryGetValue(SSBMessageContext.ServiceName, out var contextInitiatorService);
                    brokeredMessage.MessageContext.TryGetValue(SSBMessageContext.ServiceContractName, out var contextServiceContractName);
                    brokeredMessage.MessageContext.TryGetValue(SSBMessageContext.MessageTypeName, out var contextMessageTypeName);

                    Guid conversationGroupId = contextConversationGroupId != null ? (Guid)contextConversationGroupId : default;
                    Guid conversationHandle = contextConversationHandle != null ? (Guid)contextConversationHandle : default;

                    conversationHandle = await BeginConversation(connection, transaction, brokeredMessage, contextInitiatorService, contextServiceContractName).ConfigureAwait(false);
                    _logger.LogTrace("Dialog conversation has begun.");
                    _logger.LogDebug($"Conversation Handle: '{conversationHandle}', Initiator Service: '{contextInitiatorService}', Service Contract Name: '{contextServiceContractName}'");

                    await SendMessageOnConversation(connection, transaction, brokeredMessage, (string)contextMessageTypeName, conversationHandle).ConfigureAwait(false);
                    _logger.LogTrace("Message sent on conversation.");
                    _logger.LogDebug($"Conversation Handle: '{conversationHandle}', Message Type Name: '{contextMessageTypeName}'");

                    if (_options.EndConversationAfterDispatch)
                    {
                        await EndConversation(connection, transaction, conversationHandle).ConfigureAwait(false);
                        _logger.LogTrace($"Conversation ended with handle '{conversationHandle}'.");
                    }
                }

                if (!useContextTransaction)
                {
                    transaction?.Commit();
                    _logger.LogTrace("Sending sql transaction committed");
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error sending sql service broker message");

                if (!useContextTransaction)
                {
                    transaction?.Rollback();
                    _logger.LogTrace("Rolled back message sending transaction");
                }

                throw;
            }
            finally
            {
                if (!useContextTransaction)
                {
                    transaction?.Dispose();
                    connection.Dispose();
                    _logger.LogTrace("Sending sql connection and sql transaction disposed.");
                }

                _logger.LogInformation("Sending sql service broker message complete.");
            }
        }

        private Task EndConversation(SqlConnection connection, SqlTransaction transaction, Guid conversationHandle)
            => new EndDialogConversationCommand(connection, conversationHandle, enableCleanup: _options.CleanupOnEndConversation, transaction: transaction).ExecuteAsync();

        private Task SendMessageOnConversation(SqlConnection connection, SqlTransaction transaction, OutboundBrokeredMessage brokeredMessage, string messageTypeName, Guid newConvHandle)
        {
            byte[] message = brokeredMessage.Body;
            if (messageTypeName == ServicesMessageTypes.ChatterBrokeredMessageType)
            {
                var bodyConverter = _bodyConverterFactory.CreateBodyConverter(_options.MessageBodyType);
                message = bodyConverter.Convert(brokeredMessage);
            }

            return new SendOnConversationCommand(connection, newConvHandle, message, transaction, _options.CompressMessageBody, messageTypeName).ExecuteAsync();
        }

        private Task<Guid> BeginConversation(SqlConnection connection, SqlTransaction transaction, OutboundBrokeredMessage brokeredMessage, object initiatorService, object serviceContractName)
            => new BeginDialogConversationCommand(connection, brokeredMessage.Destination, (string)initiatorService, (string)serviceContractName, _options.ConversationLifetimeInSeconds, transaction: transaction).ExecuteAsync();

        public Task Dispatch(OutboundBrokeredMessage brokeredMessage, TransactionContext transactionContext)
            => Dispatch(new[] { brokeredMessage }, transactionContext);
    }
}
