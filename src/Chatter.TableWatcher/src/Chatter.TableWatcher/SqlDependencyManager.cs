using Chatter.SqlTableWatcher.Configuration;
using Chatter.SqlTableWatcher.Scripts;
using Chatter.SqlTableWatcher.Scripts.StoredProcedures;
using System.Threading.Tasks;

namespace Chatter.TableWatcher
{
    public class SqlDependencyManager : ISqlDependencyManager
    {
        private readonly SqlTableWatcherOptions _options;

        public SqlDependencyManager(SqlTableWatcherOptions options)
            => _options = options;

        public Task InstallSqlDependencies(string installationProcedureName = "",
                                           string uninstallationProcedureName = "",
                                           string conversationQueueName = "",
                                           string conversationServiceName = "",
                                           string conversationTriggerName = "")
        {
            var execInstallationProcedureScript
                = new SafeExecuteStoredProcedure(_options.ConnectionString,
                                                 _options.DatabaseName,
                                                 installationProcedureName,
                                                 _options.SchemaName);

            var installNotificationScript
                = new InstallNotificationsScript(_options,
                                                 installationProcedureName,
                                                 conversationQueueName,
                                                 conversationServiceName,
                                                 conversationTriggerName);

            var uninstallNotificationScript
                = new UninstallNotificationsScript(_options,
                                                   uninstallationProcedureName,
                                                   conversationQueueName,
                                                   conversationServiceName,
                                                   conversationTriggerName,
                                                   installationProcedureName);

            installNotificationScript.Execute();
            uninstallNotificationScript.Execute();
            execInstallationProcedureScript.Execute();

            return Task.CompletedTask;
        }

        public Task UninstallSqlDependencies(string uninstallationProcedureName = "")
        {
            var execUninstallationProcedureScript =
                new SafeExecuteStoredProcedure(
                _options.ConnectionString,
                _options.DatabaseName,
                uninstallationProcedureName,
                _options.SchemaName);

            execUninstallationProcedureScript.Execute();

            return Task.CompletedTask;
        }
    }
}
