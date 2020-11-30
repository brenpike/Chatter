using Chatter.SqlChangeNotifier.Configuration;
using Chatter.SqlChangeNotifier.Scripts.Misc;
using Chatter.SqlChangeNotifier.Scripts.ServiceBroker;
using Chatter.SqlChangeNotifier.Scripts.StoredProcedures;
using Chatter.SqlChangeNotifier.Scripts.Triggers;
using System;

namespace Chatter.SqlChangeNotifier.Scripts
{
    public class UninstallNotificationsScript : ExecutableSqlScript
    {
        private readonly SqlServiceBrokerOptions _options;
        private readonly string _uninstallationProcedureName;
        private readonly string _conversationQueueName;
        private readonly string _conversationServiceName;
        private readonly string _conversationTriggerName;
        private readonly string _installationProcedureName;

        public UninstallNotificationsScript(SqlServiceBrokerOptions options,
                                          string uninstallationProcedureName,
                                          string conversationQueueName,
                                          string conversationServiceName,
                                          string conversationTriggerName,
                                          string installationProcedureName)
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
        }

        public override string ToString()
        {
            var uninstallServiceBrokerNotificationScript =
                new UninstallSqlServiceBroker(
                _options.ConnectionString,
                _conversationQueueName,
                _conversationServiceName,
                _options.SchemaName);

            var uninstallNotificationTriggerScript =
                new DeleteNotificationTrigger(
                _conversationTriggerName,
                _options.SchemaName);

            return new CreateUninstallProcedure(
                    _options.ConnectionString,
                    new PermissionInfoDisplayScript(_options.ConnectionString),
                    _options.DatabaseName,
                    _uninstallationProcedureName,
                    uninstallNotificationTriggerScript,
                    uninstallServiceBrokerNotificationScript,
                    _options.SchemaName,
                    _installationProcedureName).ToString();
        }
    }
}
