using Chatter.SqlTableWatcher.Configuration;
using Chatter.SqlTableWatcher.Scripts;
using Chatter.SqlTableWatcher.Scripts.StoredProcedures;
using System.Threading.Tasks;

namespace Chatter.TableWatcher
{
    public class SqlDependencyManager : ISqlDependencyManager
    {
        public async Task InstallSqlDependencies(SqlTableWatcherOptions options,
                                                 string installationProcedureName = "",
                                                 string uninstallationProcedureName = "",
                                                 string conversationQueueName = "",
                                                 string conversationServiceName = "",
                                                 string conversationTriggerName = "")
        {
            var execInstallationProcedureScript
                = new SafeExecuteStoredProcedure(options.ConnectionString,
                                                 options.DatabaseName,
                                                 installationProcedureName,
                                                 options.SchemaName);

            var installNotificationScript
                = new InstallNotificationsScript(options,
                                                 installationProcedureName,
                                                 conversationQueueName,
                                                 conversationServiceName,
                                                 conversationTriggerName);

            var uninstallNotificationScript
                = new UninstallNotificationsScript(options,
                                                   uninstallationProcedureName,
                                                   conversationQueueName,
                                                   conversationServiceName,
                                                   conversationTriggerName,
                                                   installationProcedureName);

            await installNotificationScript.ExecuteAsync().ConfigureAwait(false);
            await uninstallNotificationScript.ExecuteAsync().ConfigureAwait(false);
            await execInstallationProcedureScript.ExecuteAsync().ConfigureAwait(false);
        }

        public Task UninstallSqlDependencies(SqlTableWatcherOptions options,
                                             string uninstallationProcedureName = "")
        {
            var execUninstallationProcedureScript =
                new SafeExecuteStoredProcedure(
                options.ConnectionString,
                options.DatabaseName,
                uninstallationProcedureName,
                options.SchemaName);

            return execUninstallationProcedureScript.ExecuteAsync();
        }
    }
}
