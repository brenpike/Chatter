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
        private readonly SqlTransaction _transaction = null;
        private readonly int _errorCode;
        private readonly string _errorDescription;
        private readonly bool _enableCleanup;

        public EndDialogConversationCommand(SqlConnection connection,
                                     int errorCode = 0,
                                     string errorDescription = "",
                                     bool withCleanup = false,
                                     SqlTransaction transaction = null)
        {
            _connection = connection;
            _errorCode = errorCode;
            _errorDescription = errorDescription;
            _enableCleanup = withCleanup;
            _transaction = transaction;
        }

        public Task ExecuteAsync(Guid conversationHandle, CancellationToken cancellationToken = default)
        {
            using (var endConvoCommand = _connection.CreateCommand())
            {
                endConvoCommand.Transaction = _transaction;
                endConvoCommand.CommandText = ToString();
                endConvoCommand.Connection = _connection;
                endConvoCommand.CommandType = CommandType.Text;

                endConvoCommand.Parameters.Add(new SqlParameter("@conversationHandle", conversationHandle));

                if (_errorCode != 0 && !string.IsNullOrWhiteSpace(_errorDescription))
                {
                    endConvoCommand.Parameters.Add(new SqlParameter("@errorCode", _errorCode));
                    endConvoCommand.Parameters.Add("@errorDescription", SqlDbType.NVarChar, 3000).Value = _errorDescription;
                }

                return endConvoCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        public override string ToString()
        {
            var query = new StringBuilder();

            query.Append("END CONVERSATION @conversationHandle");

            if (_errorCode != 0 && !string.IsNullOrWhiteSpace(_errorDescription))
            {
                query.Append(" WITH ERROR = @errorCode DESCRIPTION = @errorDescription");
                return query.ToString();
            }

            if (_enableCleanup)
            {
                query.Append(" WITH CLEANUP");
            }

            return query.ToString();
        }
    }
}
