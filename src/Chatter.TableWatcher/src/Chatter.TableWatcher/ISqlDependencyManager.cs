using Chatter.SqlTableWatcher.Configuration;
using System.Threading.Tasks;

namespace Chatter.TableWatcher
{
    public interface ISqlDependencyManager
    {
        Task UninstallSqlDependencies(SqlTableWatcherOptions options,
                                      string uninstallationProcedureName = "");
        Task InstallSqlDependencies(SqlTableWatcherOptions options,
                                    string installationProcedureName = "",
                                    string uninstallationProcedureName = "",
                                    string conversationQueueName = "",
                                    string conversationServiceName = "",
                                    string conversationTriggerName = "");
    }
}
