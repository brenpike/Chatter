using System;
using System.Data;
using System.Data.SqlClient;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.SqlServiceBroker.Scripts
{
    public class BeginDialogConversationCommand
    {
        private readonly SqlConnection _connection;
        private readonly SqlTransaction _transaction = null;
        public string _targetServiceName;
        private string _initiatorServiceName;
        private readonly string _serviceContractName;
        private readonly int _lifetime;
        private readonly bool _encryption;
        private readonly Guid _relatedConversationGroupId;
        private readonly Guid _relatedConversationId;

        public BeginDialogConversationCommand(SqlConnection connection,
                                              string targetServiceName,
                                              string initiatorServiceName = "",
                                              string serviceContractName = "",
                                              int lifetime = 0,
                                              bool encryption = false,
                                              Guid relatedConversationGroupId = default,
                                              Guid relatedConversationId = default,
                                              SqlTransaction transaction = null)
        {
            if (string.IsNullOrWhiteSpace(initiatorServiceName))
            {
                initiatorServiceName = targetServiceName;
            }

            _connection = connection;
            _transaction = transaction;
            _targetServiceName = targetServiceName;
            _initiatorServiceName = initiatorServiceName;
            _serviceContractName = serviceContractName;
            _lifetime = lifetime;
            _encryption = encryption;
            _relatedConversationGroupId = relatedConversationGroupId;
            _relatedConversationId = relatedConversationId;
        }

        public async Task<Guid> ExecuteAsync(CancellationToken cancellationToken = default)
        {
            using var beginConvoCommand = Create();
            await beginConvoCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            var convoHandle = beginConvoCommand.Parameters["@conversationHandle"];
            return (Guid)convoHandle.Value;
        }

        public SqlCommand Create()
        {
            var beginConvoCommand = _connection.CreateCommand();
            beginConvoCommand.Transaction = _transaction;
            beginConvoCommand.Connection = _connection;
            beginConvoCommand.CommandType = CommandType.Text;

            if (!_initiatorServiceName.StartsWith("["))
            {
                _initiatorServiceName = "[" + _initiatorServiceName + "]";
            }

            _targetServiceName = _targetServiceName.Replace("]", "").Replace("[", "");

            var query = new StringBuilder($"BEGIN DIALOG @conversationHandle " +
                                          $"FROM SERVICE {_initiatorServiceName} " +
                                          $"TO SERVICE @targetService ");

            if (!string.IsNullOrWhiteSpace(_serviceContractName))
            {
                query.Append("ON CONTRACT @contractName ");
                beginConvoCommand.Parameters.Add(new SqlParameter("@contractName", _serviceContractName));
            }

            beginConvoCommand.Parameters.Add(new SqlParameter("@targetService", _targetServiceName));
            beginConvoCommand.Parameters.Add("@conversationHandle", SqlDbType.UniqueIdentifier).Direction = ParameterDirection.Output;

            query.Append($"WITH ENCRYPTION = ");

            if (_encryption)
            {
                query.Append("ON ");
            }
            else
            {
                query.Append("OFF ");
            }

            if (_relatedConversationGroupId != default)
            {
                query.Append("WITH RELATED_CONVERSATION_GROUP = @conversationGroupId ");
                beginConvoCommand.Parameters.Add(new SqlParameter("@conversationGroupId", _relatedConversationGroupId));
            }

            if (_relatedConversationId != default)
            {
                query.Append("WITH RELATED_CONVERSATION = @conversationId ");
                beginConvoCommand.Parameters.Add(new SqlParameter("@conversationId", _relatedConversationId));
            }

            if (_lifetime > 0)
            {
                query.Append($", LIFETIME = @lifetime");
                beginConvoCommand.Parameters.Add(new SqlParameter("@lifetime", _lifetime));
            }

            query.Append(";");
            beginConvoCommand.CommandText = query.ToString();
            return beginConvoCommand;
        }
    }
}
