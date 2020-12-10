using Chatter.MessageBrokers.SqlServiceBroker.Receiving;
using System;
using System.Data;
using System.Data.SqlClient;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.SqlServiceBroker.Scripts.ServiceBroker.Core
{
    public class ReceiveMessageFromQueueCommand
    {
        private readonly SqlConnection _connection;
        private readonly SqlTransaction _transaction = null;
        private readonly string _queueName;
        private readonly string _schema;
        private readonly Guid _conversationHandle;
        private readonly int _timeout;

        public ReceiveMessageFromQueueCommand(SqlConnection connection,
                                       string queueName,
                                       string schema = "dbo",
                                       int timeout = -1,
                                       Guid conversationHandle = default,
                                       SqlTransaction transaction = null)
        {
            _connection = connection;
            _queueName = queueName;
            _schema = schema;
            _conversationHandle = conversationHandle;
            _timeout = timeout;
            _transaction = transaction;
        }

        public async Task<ReceivedMessage> ExecuteAsync(CancellationToken cancellationToken = default)
        {
            using var receiveCommand = Create();
            await using var reader = await receiveCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!reader.Read() || reader.IsDBNull(0))
            {
                return null;
            }

            return new ReceivedMessage(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetSqlBytes(6).Buffer
            );
        }

        public SqlCommand Create()
        {
            var query = new StringBuilder();
            var receiveCommand = _connection.CreateCommand();
            receiveCommand.Transaction = _transaction;
            receiveCommand.Connection = _connection;
            receiveCommand.CommandType = CommandType.Text;

            query.Append("WAITFOR (RECEIVE TOP(1) " +
                         "conversation_group_id, conversation_handle, " +
                         "message_sequence_number, service_name, service_contract_name, " +
                         "message_type_name, " +
                         "CASE WHEN SUBSTRING(message_body, 1, 2) = 0x1F8B " +
                         "THEN CAST(decompress(message_body) AS VARBINARY(MAX)) " +
                         "ELSE message_body END as message_body " +
                         $"FROM {_schema}.[{_queueName}]");

            if (_conversationHandle != default)
            {
                query.Append(" WHERE conversation_handle = @conversationHandle");
                receiveCommand.Parameters.Add(new SqlParameter("@conversationHandle", _conversationHandle));
            }

            query.Append(")");
            if (_timeout > 0)
            {
                query.Append(", TIMEOUT @timeoutInSeconds");
                receiveCommand.Parameters.Add(new SqlParameter("@timeoutInSeconds", _timeout));
                receiveCommand.CommandTimeout = 0;
            }

            receiveCommand.CommandText = query.ToString();

            return receiveCommand;
        }
    }
}
