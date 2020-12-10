using System;
using System.Data;
using System.Data.SqlClient;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.SqlServiceBroker.Scripts.ServiceBroker.Core
{
    public class EndDialogConversationCommand
    {
        private readonly SqlConnection _connection;
        private readonly Guid _conversationHandle;
        private readonly SqlTransaction _transaction = null;
        private readonly int _errorCode;
        private readonly string _errorDescription;
        private readonly bool _enableCleanup;

        public EndDialogConversationCommand(SqlConnection connection,
                                            Guid conversationHandle = default,
                                            int errorCode = 0,
                                            string errorDescription = "",
                                            bool enableCleanup = false,
                                            SqlTransaction transaction = null)
        {
            _connection = connection;
            _conversationHandle = conversationHandle;
            _errorCode = errorCode;
            _errorDescription = errorDescription;
            _enableCleanup = enableCleanup;
            _transaction = transaction;
        }

        public Task ExecuteAsync(CancellationToken cancellationToken = default)
        {
            using var endConvoCommand = Create();
            return endConvoCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        public SqlCommand Create()
        {
            var endConvoCommand = _connection.CreateCommand();
            endConvoCommand.Transaction = _transaction;
            endConvoCommand.Connection = _connection;
            endConvoCommand.CommandType = CommandType.Text;

            var query = new StringBuilder();

            query.Append("END CONVERSATION @conversationHandle");
            endConvoCommand.Parameters.Add(new SqlParameter("@conversationHandle", _conversationHandle));

            if (_errorCode != 0 && !string.IsNullOrWhiteSpace(_errorDescription))
            {
                query.Append(" WITH ERROR = @errorCode DESCRIPTION = @errorDescription;");
                endConvoCommand.Parameters.Add(new SqlParameter("@errorCode", _errorCode));
                endConvoCommand.Parameters.Add("@errorDescription", SqlDbType.NVarChar, 3000).Value = _errorDescription;
                endConvoCommand.CommandText = query.ToString();
                return endConvoCommand;
            }

            if (_enableCleanup)
            {
                query.Append(" WITH CLEANUP");
            }

            query.Append(";");
            endConvoCommand.CommandText = query.ToString();
            return endConvoCommand;
        }
    }
}
