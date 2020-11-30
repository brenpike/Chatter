using Chatter.MessageBrokers.SqlServiceBroker.Configuration;
using Chatter.MessageBrokers.SqlServiceBroker.Scripts.Misc;
using Chatter.MessageBrokers.SqlServiceBroker.Scripts.ServiceBroker;
using Chatter.MessageBrokers.SqlServiceBroker.Scripts.StoredProcedures;
using Chatter.MessageBrokers.SqlServiceBroker.Scripts.Triggers;
using System;

namespace Chatter.MessageBrokers.SqlServiceBroker.Scripts
{
    public class InstallNotificationsScript : ExecutableSqlScript
    {
        private readonly SqlServiceBrokerOptions _options;
        private readonly string _installationProcedureName;
        private readonly string _conversationQueueName;
        private readonly string _conversationServiceName;
        private readonly string _conversationTriggerName;

        public InstallNotificationsScript(SqlServiceBrokerOptions options,
                                          string installationProcedureName,
                                          string conversationQueueName,
                                          string conversationServiceName,
                                          string conversationTriggerName)
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
        }

        public override string ToString()
        {
            var installServiceBrokerNotificationScript =
                new InstallAndConfigureSqlServiceBroker(
                    _options.ConnectionString,
                    _options.DatabaseName,
                    _conversationQueueName,
                    _conversationServiceName,
                    _options.SchemaName);

            var installNotificationTriggerScript =
                new CreateNotificationTrigger(_options.TableName,
                                              _conversationTriggerName,
                                              _options.NotificationsToReceive,
                                              _conversationServiceName,
                                              _options.SchemaName);

            var checkNotificationTriggerScript =
                new CheckIfNotificationTriggerExists(_conversationTriggerName,
                                                     _options.SchemaName);

            return new CreateInstallationProcedure(_options.ConnectionString,
                                                   new PermissionInfoDisplayScript(_options.ConnectionString),
                                                   _options.DatabaseName,
                                                   _installationProcedureName,
                                                   installServiceBrokerNotificationScript,
                                                   installNotificationTriggerScript,
                                                   checkNotificationTriggerScript,
                                                   _options.TableName,
                                                   _options.SchemaName).ToString();
        }
    }
}
