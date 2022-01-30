using Chatter.CQRS;
using Chatter.SqlTableWatcher.Configuration;
using Chatter.SqlTableWatcher.Scripts;
using Chatter.SqlTableWatcher.Scripts.StoredProcedures;
using System.Threading.Tasks;

namespace Chatter.SqlTableWatcher
{
    public class SqlDependencyManager<TRowChangedData> : ISqlDependencyManager<TRowChangedData> where TRowChangedData : class, IMessage, new()
    {
        public SqlTableWatcherOptions Options { get; }

        public SqlDependencyManager(SqlTableWatcherOptions options)
            => Options = options;

        public Task InstallSqlDependencies(string installationProcedureName = "",
                                           string uninstallationProcedureName = "",
                                           string conversationQueueName = "",
                                           string conversationServiceName = "",
                                           string conversationTriggerName = "",
                                           string deadLetterQueueName = "",
                                           string deadLetterServiceName = "")
        {
            var execInstallationProcedureScript
                = new SafeExecuteStoredProcedure(Options.ConnectionString,
                                                 Options.DatabaseName,
                                                 installationProcedureName,
                                                 Options.SchemaName);

            var installNotificationScript
                = new InstallNotificationsScript(Options,
                                                 installationProcedureName,
                                                 conversationQueueName,
                                                 conversationServiceName,
                                                 conversationTriggerName,
                                                 deadLetterQueueName,
                                                 deadLetterServiceName);

            var uninstallNotificationScript
                = new UninstallNotificationsScript(Options,
                                                   uninstallationProcedureName,
                                                   conversationQueueName,
                                                   conversationServiceName,
                                                   conversationTriggerName,
                                                   installationProcedureName,
                                                   deadLetterQueueName,
                                                   deadLetterServiceName);

            installNotificationScript.Execute();
            uninstallNotificationScript.Execute();
            execInstallationProcedureScript.Execute();

            return Task.CompletedTask;
        }

        public Task UninstallSqlDependencies(string uninstallationProcedureName = "")
        {
            var execUninstallationProcedureScript =
                new SafeExecuteStoredProcedure(
                Options.ConnectionString,
                Options.DatabaseName,
                uninstallationProcedureName,
                Options.SchemaName);

            execUninstallationProcedureScript.Execute();

            return Task.CompletedTask;
        }
    }
}
