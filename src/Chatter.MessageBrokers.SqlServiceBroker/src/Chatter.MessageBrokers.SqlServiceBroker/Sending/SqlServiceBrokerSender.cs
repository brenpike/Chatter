﻿using Chatter.MessageBrokers.Context;
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

        public SqlServiceBrokerSender(SqlServiceBrokerOptions options,
                                      ILogger<SqlServiceBrokerSender> logger)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task Dispatch(IEnumerable<OutboundBrokeredMessage> brokeredMessages, TransactionContext transactionContext)
        {
            _logger.LogInformation("Sending sql service broker message(s)");
            IDbTransaction contextTransaction = null;
            transactionContext?.Container.TryGet(out contextTransaction);
            var contextTransactionMode = transactionContext?.TransactionMode ?? TransactionMode.None;
            var useContextTransaction = contextTransactionMode == TransactionMode.FullAtomicityViaInfrastructure && contextTransaction != null;

            SqlConnection connection = useContextTransaction
                                            ? (SqlConnection)contextTransaction.Connection
                                            : new SqlConnection(_options.ConnectionString);

            if (connection.State != ConnectionState.Open)
            {
                await connection.OpenAsync();
                _logger.LogTrace("Sql connection opened.");
            }

            var transaction = useContextTransaction
                                    ? (SqlTransaction)contextTransaction
                                    : (SqlTransaction)await connection.BeginTransactionAsync();

            try
            {
                foreach (var brokeredMessage in brokeredMessages)
                {
                    _logger.LogDebug($"Sending brokered message to '{brokeredMessage.Destination}'");
                    brokeredMessage.MessageContext.TryGetValue(SSBMessageContext.ConversationGroupId, out var contextConversationGroupId);
                    brokeredMessage.MessageContext.TryGetValue(SSBMessageContext.ConversationHandle, out var contextConversationHandle);
                    brokeredMessage.MessageContext.TryGetValue(SSBMessageContext.ServiceName, out var contextInitiatorService);
                    brokeredMessage.MessageContext.TryGetValue(SSBMessageContext.ServiceContractName, out var contextServiceContractName);
                    brokeredMessage.MessageContext.TryGetValue(SSBMessageContext.MessageTypeName, out var contextMessageTypeName);

                    Guid conversationGroupId = contextConversationGroupId != null ? (Guid)contextConversationGroupId : default;
                    Guid conversationHandle = contextConversationHandle != null ? (Guid)contextConversationHandle : default;

                    conversationHandle = await BeginConversation(connection, transaction, brokeredMessage, contextInitiatorService, contextServiceContractName).ConfigureAwait(false);
                    _logger.LogDebug("Dialog conversation has begun.");
                    _logger.LogTrace($"Conversation Handle: '{conversationHandle}', Initiator Service: '{contextInitiatorService}', Service Contract Name: '{contextServiceContractName}'");

                    await SendMessageOnConversation(connection, transaction, brokeredMessage, (string)contextMessageTypeName, conversationHandle).ConfigureAwait(false);
                    _logger.LogDebug("Message sent on conversation.");
                    _logger.LogTrace($"Conversation Handle: '{conversationHandle}', Message Type Name: '{contextMessageTypeName}'");

                    if (_options.EndConversationAfterDispatch)
                    {
                        await EndConversation(connection, transaction, conversationHandle).ConfigureAwait(false);
                        _logger.LogDebug("Conversation ended.");
                        _logger.LogTrace($"Conversation Handle: '{conversationHandle}'");
                    }
                }

                if (!useContextTransaction)
                {
                    transaction?.Commit();
                    _logger.LogDebug("Sending sql transaction committed");
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error sending sql service broker message");

                if (!useContextTransaction)
                {
                    transaction?.Rollback();
                    _logger.LogDebug("Rolled back message sending transaction");
                }

                throw;
            }
            finally
            {
                if (!useContextTransaction)
                {
                    transaction?.Dispose();
                    connection.Dispose();
                    _logger.LogDebug("Sending sql connection and sql transaction disposed.");
                }

                _logger.LogInformation("Sending sql service broker message complete.");
            }
        }

        private Task EndConversation(SqlConnection connection, SqlTransaction transaction, Guid conversationHandle)
            => new EndDialogConversationCommand(connection, conversationHandle, enableCleanup: _options.CleanupOnEndConversation, transaction: transaction).ExecuteAsync();

        private Task SendMessageOnConversation(SqlConnection connection, SqlTransaction transaction, OutboundBrokeredMessage brokeredMessage, string messageTypeName, Guid newConvHandle)
            => new SendOnConversationCommand(connection, newConvHandle, brokeredMessage.Body, transaction, _options.CompressMessageBody, messageTypeName).ExecuteAsync();

        private Task<Guid> BeginConversation(SqlConnection connection, SqlTransaction transaction, OutboundBrokeredMessage brokeredMessage, object initiatorService, object serviceContractName)
            => new BeginDialogConversationCommand(connection, brokeredMessage.Destination, (string)initiatorService, (string)serviceContractName, _options.ConversationLifetimeInSeconds, transaction: transaction).ExecuteAsync();

        public Task Dispatch(OutboundBrokeredMessage brokeredMessage, TransactionContext transactionContext)
            => Dispatch(new[] { brokeredMessage }, transactionContext);
    }
}
