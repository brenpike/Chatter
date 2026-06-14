using Chatter.CQRS;
using Chatter.SqlChangeFeed.Configuration;
using Chatter.SqlChangeFeed.Scripts;
using Chatter.SqlChangeFeed.Scripts.StoredProcedures;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.SqlChangeFeed
{
    public class SqlDependencyManager<TRowChangedData> : ISqlDependencyManager<TRowChangedData> where TRowChangedData : class, IMessage, new()
    {
        public SqlChangeFeedOptions Options { get; }

        public SqlDependencyManager(SqlChangeFeedOptions options)
            => Options = options;

        public async Task InstallSqlDependencies(string installationProcedureName = "",
                                                 string uninstallationProcedureName = "",
                                                 string conversationQueueName = "",
                                                 string conversationServiceName = "",
                                                 string conversationTriggerName = "",
                                                 string deadLetterQueueName = "",
                                                 string deadLetterServiceName = "",
                                                 CancellationToken token = default)
        {
            var execInstallationProcedureScript
                = new SafeExecuteStoredProcedure(Options.ConnectionString,
                                                 Options.DatabaseName,
                                                 installationProcedureName,
                                                 Options.SchemaName);

            var installChangeFeedScript
                = new InstallChangeFeedScript(Options,
                                                 installationProcedureName,
                                                 conversationQueueName,
                                                 conversationServiceName,
                                                 conversationTriggerName,
                                                 deadLetterQueueName,
                                                 deadLetterServiceName);

            var uninstallChangeFeedScript
                = new UninstallChangeFeedScript(Options,
                                                   uninstallationProcedureName,
                                                   conversationQueueName,
                                                   conversationServiceName,
                                                   conversationTriggerName,
                                                   installationProcedureName,
                                                   deadLetterQueueName,
                                                   deadLetterServiceName);

            await installChangeFeedScript.ExecuteAsync(token);
            await uninstallChangeFeedScript.ExecuteAsync(token);
            await execInstallationProcedureScript.ExecuteAsync(token);
        }

        public async Task UninstallSqlDependencies(string uninstallationProcedureName = "", CancellationToken token = default)
        {
            var execUninstallationProcedureScript =
                new SafeExecuteStoredProcedure(
                Options.ConnectionString,
                Options.DatabaseName,
                uninstallationProcedureName,
                Options.SchemaName);

            await execUninstallationProcedureScript.ExecuteAsync(token);
        }
    }
}
