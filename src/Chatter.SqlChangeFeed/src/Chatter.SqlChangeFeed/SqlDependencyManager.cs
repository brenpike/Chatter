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

            await installChangeFeedScript.ExecuteAsync(token).ConfigureAwait(false);
            await execInstallationProcedureScript.ExecuteAsync(token).ConfigureAwait(false);

            // INVARIANT: the uninstall procedure is regenerated only after the installation procedure has
            // executed successfully. Both procedures are emitted with CREATE OR ALTER, so regenerating it
            // first would overwrite the already-installed uninstall procedure - the consumer's only handle
            // on the objects installed by a previous run - before knowing whether this run succeeds. A
            // failed or refused installation leaves that previously-installed procedure body intact
            // because the exec above propagates its failure and short-circuits this statement.
            await uninstallChangeFeedScript.ExecuteAsync(token).ConfigureAwait(false);
        }

        public async Task UninstallSqlDependencies(string uninstallationProcedureName = "", CancellationToken token = default)
        {
            var execUninstallationProcedureScript =
                new SafeExecuteStoredProcedure(
                Options.ConnectionString,
                Options.DatabaseName,
                uninstallationProcedureName,
                Options.SchemaName);

            await execUninstallationProcedureScript.ExecuteAsync(token).ConfigureAwait(false);
        }
    }
}
