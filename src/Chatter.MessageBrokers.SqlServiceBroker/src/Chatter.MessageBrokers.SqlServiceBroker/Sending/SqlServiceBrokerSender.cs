using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Sending;
using Chatter.MessageBrokers.SqlServiceBroker.Configuration;
using Chatter.MessageBrokers.SqlServiceBroker.Scripts.ServiceBroker.Core;
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
            IDbTransaction receiverTransaction = null;
            transactionContext?.Container.TryGet(out receiverTransaction);

            SqlConnection connection = (SqlConnection)receiverTransaction?.Connection ?? new SqlConnection(_options.ConnectionString);
            if (connection.State != ConnectionState.Open)
            {
                await connection.OpenAsync();
            }

            var transaction = (SqlTransaction)receiverTransaction ?? (SqlTransaction)await connection.BeginTransactionAsync();
            Guid conversationHandle = default;

            try
            {
                foreach (var brokeredMessage in brokeredMessages)
                {
                    brokeredMessage.MessageContext.TryGetValue(SSBMessageContext.ConversationGroupId, out var convGroupHandle);
                    brokeredMessage.MessageContext.TryGetValue(SSBMessageContext.ConversationHandle, out var convHandle);
                    brokeredMessage.MessageContext.TryGetValue(SSBMessageContext.ServiceName, out var initiatorService);
                    brokeredMessage.MessageContext.TryGetValue(SSBMessageContext.ServiceContractName, out var serviceContractName);
                    brokeredMessage.MessageContext.TryGetValue(SSBMessageContext.MessageTypeName, out var messageTypeName);

                    Guid cgh = default;
                    if (convGroupHandle != null)
                    {
                        cgh = (Guid)convGroupHandle;
                    }

                    if (convHandle != null)
                    {
                        conversationHandle = (Guid)convHandle;
                    }

                    Guid newConvHandle = await BeginConvo(connection, transaction, conversationHandle, brokeredMessage, initiatorService, serviceContractName, cgh).ConfigureAwait(false);

                    await SendMessageOnConvo(connection, transaction, brokeredMessage, (string)messageTypeName, newConvHandle).ConfigureAwait(false);

                    await EndConvo(connection, transaction, conversationHandle, newConvHandle).ConfigureAwait(false);
                }
                if (conversationHandle == default)
                {
                    transaction?.Commit();
                }
            }
            catch (Exception)
            {
                if (conversationHandle == default)
                {
                    transaction?.Rollback();
                }
                throw;
            }
            finally
            {
                if (conversationHandle == default)
                {
                    transaction?.Dispose();
                    connection.Dispose();
                }
            }
        }

        private static async Task EndConvo(SqlConnection connection, SqlTransaction transaction, Guid conversationHandle, Guid newConvHandle)
        {
            var edcc = new EndDialogConversationCommand(connection,
                                                        newConvHandle,
                                                        transaction: transaction);

            if (conversationHandle == default)
            {
                await edcc.ExecuteAsync().ConfigureAwait(false);
            }
        }

        private static async Task SendMessageOnConvo(SqlConnection connection,
                                                     SqlTransaction transaction,
                                                     OutboundBrokeredMessage brokeredMessage,
                                                     string messageTypeName,
                                                     Guid newConvHandle)
        {
            var socc = new SendOnConversationCommand(connection,
                                                     newConvHandle,
                                                     brokeredMessage.Body,
                                                     transaction,
                                                     false,
                                                     messageTypeName);

            await socc.ExecuteAsync().ConfigureAwait(false);
        }

        private static async Task<Guid> BeginConvo(SqlConnection connection,
                                                   SqlTransaction transaction,
                                                   Guid conversationHandle,
                                                   OutboundBrokeredMessage brokeredMessage,
                                                   object initiatorService,
                                                   object serviceContractName,
                                                   Guid relatedConversationGroupId)
        {
            var bdc = new BeginDialogConversationCommand(connection,
                                                         brokeredMessage.Destination,
                                                         (string)initiatorService,
                                                         (string)serviceContractName,
                                                         0,
                                                         relatedConversationGroupId: relatedConversationGroupId,
                                                         relatedConversationId: conversationHandle,
                                                         transaction: transaction);

            return conversationHandle == default ? await bdc.ExecuteAsync().ConfigureAwait(false) : conversationHandle;
        }

        public Task Dispatch(OutboundBrokeredMessage brokeredMessage, TransactionContext transactionContext)
            => Dispatch(new[] { brokeredMessage }, transactionContext);
    }
}
