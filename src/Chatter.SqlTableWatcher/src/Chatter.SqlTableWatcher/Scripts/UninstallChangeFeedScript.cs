using Chatter.SqlTableWatcher.Configuration;
using Chatter.SqlTableWatcher.Scripts.ServiceBroker;
using Chatter.SqlTableWatcher.Scripts.StoredProcedures;
using Chatter.SqlTableWatcher.Scripts.Triggers;
using System;

namespace Chatter.SqlTableWatcher.Scripts
{
    public class UninstallChangeFeedScript : ExecutableSqlScript
    {
        private readonly SqlChangeFeedOptions _options;
        private readonly string _uninstallationProcedureName;
        private readonly string _conversationQueueName;
        private readonly string _conversationServiceName;
        private readonly string _conversationTriggerName;
        private readonly string _installationProcedureName;
        private readonly string _deadLetterQueueName;
        private readonly string _deadLetterServiceName;

        public UninstallChangeFeedScript(SqlChangeFeedOptions options,
                                          string uninstallationProcedureName,
                                          string conversationQueueName,
                                          string conversationServiceName,
                                          string conversationTriggerName,
                                          string installationProcedureName,
                                          string deadLetterQueueName,
                                          string deadLetterServiceName)
            : base(options?.ConnectionString)
        {
            if (string.IsNullOrWhiteSpace(uninstallationProcedureName))
            {
                throw new ArgumentException($"'{nameof(uninstallationProcedureName)}' cannot be null or whitespace", nameof(uninstallationProcedureName));
            }

            if (string.IsNullOrWhiteSpace(conversationQueueName))
            {
                throw new ArgumentException($"'{nameof(conversationQueueName)}' cannot be null or whitespace", nameof(conversationQueueName));
            }

            if (string.IsNullOrWhiteSpace(conversationServiceName))
            {
                throw new ArgumentException($"'{nameof(conversationServiceName)}' cannot be null or whitespace", nameof(conversationServiceName));
            }

            if (string.IsNullOrWhiteSpace(conversationTriggerName))
            {
                throw new ArgumentException($"'{nameof(conversationTriggerName)}' cannot be null or whitespace", nameof(conversationTriggerName));
            }

            if (string.IsNullOrWhiteSpace(installationProcedureName))
            {
                throw new ArgumentException($"'{nameof(installationProcedureName)}' cannot be null or whitespace", nameof(installationProcedureName));
            }

            _options = options ?? throw new ArgumentNullException(nameof(options));
            _uninstallationProcedureName = uninstallationProcedureName;
            _conversationQueueName = conversationQueueName;
            _conversationServiceName = conversationServiceName;
            _conversationTriggerName = conversationTriggerName;
            _installationProcedureName = installationProcedureName;
            _deadLetterQueueName = deadLetterQueueName;
            _deadLetterServiceName = deadLetterServiceName;
        }

        public override string ToString()
        {
            var uninstallServiceBrokerChangeFeedScript =
                new UninstallSqlServiceBroker(
                _options.ConnectionString,
                _conversationQueueName,
                _conversationServiceName,
                _options.SchemaName,
                _deadLetterQueueName,
                _deadLetterServiceName);

            var uninstallChangeFeedTriggerScript =
                new DeleteChangeFeedTrigger(
                _conversationTriggerName,
                _options.SchemaName);

            return new CreateUninstallProcedure(
                    _options.ConnectionString,
                    _options.DatabaseName,
                    _uninstallationProcedureName,
                    uninstallChangeFeedTriggerScript,
                    uninstallServiceBrokerChangeFeedScript,
                    _options.SchemaName,
                    _installationProcedureName).ToString();
        }
    }
}
