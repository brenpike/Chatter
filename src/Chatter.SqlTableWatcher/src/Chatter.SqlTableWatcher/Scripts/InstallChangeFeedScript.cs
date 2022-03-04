using Chatter.SqlTableWatcher.Configuration;
using Chatter.SqlTableWatcher.Scripts.ServiceBroker;
using Chatter.SqlTableWatcher.Scripts.StoredProcedures;
using Chatter.SqlTableWatcher.Scripts.Triggers;
using System;

namespace Chatter.SqlTableWatcher.Scripts
{
    public class InstallChangeFeedScript : ExecutableSqlScript
    {
        private readonly SqlChangeFeedOptions _options;
        private readonly string _installationProcedureName;
        private readonly string _conversationQueueName;
        private readonly string _conversationServiceName;
        private readonly string _conversationTriggerName;
        private readonly string _deadLetterQueueName;
        private readonly string _deadLetterServiceName;

        public InstallChangeFeedScript(SqlChangeFeedOptions options,
                                          string installationProcedureName,
                                          string conversationQueueName,
                                          string conversationServiceName,
                                          string conversationTriggerName,
                                          string deadLetterQueueName,
                                          string deadLetterServiceName)
            : base(options?.ConnectionString)
        {
            if (string.IsNullOrWhiteSpace(installationProcedureName))
            {
                throw new ArgumentException($"'{nameof(installationProcedureName)}' cannot be null or whitespace", nameof(installationProcedureName));
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

            _options = options ?? throw new ArgumentNullException(nameof(options));
            _installationProcedureName = installationProcedureName;
            _conversationQueueName = conversationQueueName;
            _conversationServiceName = conversationServiceName;
            _conversationTriggerName = conversationTriggerName;
            _deadLetterQueueName = deadLetterQueueName;
            _deadLetterServiceName = deadLetterServiceName;
        }

        public override string ToString()
        {
            var installServiceBrokerChangeFeedScript =
                new InstallAndConfigureSqlServiceBroker(
                    _options.ConnectionString,
                    _options.DatabaseName,
                    _conversationQueueName,
                    _conversationServiceName,
                    _options.SchemaName,
                    _deadLetterQueueName,
                    _deadLetterServiceName);

            var installChangeFeedTriggerScript =
                new CreateChangeFeedTrigger(_options.TableName,
                                            _conversationTriggerName,
                                            _options.ChangeFeedTriggerTypes,
                                            _conversationServiceName,
                                            _options.SchemaName);

            return new CreateInstallationProcedure(_options.ConnectionString,
                                                   _options.DatabaseName,
                                                   _installationProcedureName,
                                                   installServiceBrokerChangeFeedScript,
                                                   installChangeFeedTriggerScript,
                                                   _options.TableName,
                                                   _options.SchemaName,
                                                   _conversationTriggerName).ToString();
        }
    }
}
