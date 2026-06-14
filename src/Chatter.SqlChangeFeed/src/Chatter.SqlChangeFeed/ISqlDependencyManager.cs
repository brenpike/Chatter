using Chatter.CQRS;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.SqlChangeFeed
{
    public interface ISqlDependencyManager
    {
        Task UninstallSqlDependencies(string uninstallationProcedureName = "", CancellationToken token = default);
        Task InstallSqlDependencies(string installationProcedureName = "",
                                    string uninstallationProcedureName = "",
                                    string conversationQueueName = "",
                                    string conversationServiceName = "",
                                    string conversationTriggerName = "",
                                    string deadLetterQueueName = "",
                                    string deadLetterServiceName = "",
                                    CancellationToken token = default);
    }

    public interface ISqlDependencyManager<TRowChangedData> : ISqlDependencyManager where TRowChangedData : class, IMessage, new() { }
}
