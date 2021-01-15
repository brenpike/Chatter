using System.Threading.Tasks;

namespace Chatter.SqlTableWatcher
{
    public interface ISqlDependencyManager
    {
        Task UninstallSqlDependencies(string uninstallationProcedureName = "");
        Task InstallSqlDependencies(string installationProcedureName = "",
                                    string uninstallationProcedureName = "",
                                    string conversationQueueName = "",
                                    string conversationServiceName = "",
                                    string conversationTriggerName = "");
    }
}
