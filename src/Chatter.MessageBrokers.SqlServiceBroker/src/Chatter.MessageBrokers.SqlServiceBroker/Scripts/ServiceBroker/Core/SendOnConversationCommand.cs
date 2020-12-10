using System;
using System.Data;
using System.Data.SqlClient;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.SqlServiceBroker.Scripts.ServiceBroker.Core
{
    public class SendOnConversationCommand
    {
        private readonly SqlConnection _connection;
        private readonly Guid _conversationHandle;
        private readonly string _messageType;
        private readonly byte[] _body;
        private readonly SqlTransaction _transaction = null;
        private readonly bool _compress;

        public SendOnConversationCommand(SqlConnection connection,
                                         Guid conversationHandle,
                                         byte[] body,
                                         SqlTransaction transaction = null,
                                         bool compress = false,
                                         string messageType = "")
        {
            _connection = connection;
            _conversationHandle = conversationHandle;
            _messageType = messageType;
            _body = body;
            _transaction = transaction;
            _compress = compress;
        }

        public async Task ExecuteAsync(CancellationToken cancellationToken = default)
        {
            using var sendOnConvoCommand = Create();
            await sendOnConvoCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        public SqlCommand Create()
        {
            var sendOnConvoCommand = _connection.CreateCommand();
            sendOnConvoCommand.Transaction = _transaction;
            sendOnConvoCommand.Connection = _connection;
            sendOnConvoCommand.CommandType = CommandType.Text;

            var query = new StringBuilder();

            query.Append("SEND ON CONVERSATION @conversationHandle ");
            sendOnConvoCommand.Parameters.Add(new SqlParameter("@conversationHandle", _conversationHandle));
            sendOnConvoCommand.Parameters.Add("@message", SqlDbType.VarBinary).Value = _body;

            if (!string.IsNullOrWhiteSpace(_messageType))
            {
                query.Append("MESSAGE TYPE @messageType ");
                sendOnConvoCommand.Parameters.Add(new SqlParameter("@messageType", _messageType));
            }

            if (_compress)
            {
                query.Append("(compress(@message));");
            }
            else
            {
                query.Append("(@message);");
            }

            sendOnConvoCommand.CommandText = query.ToString();
            sendOnConvoCommand.Transaction = _transaction;
            return sendOnConvoCommand;
        }
    }
}
