using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.SqlServiceBroker.Configuration;
using Chatter.TableWatcher;

namespace Chatter.SqlTableWatcher.Configuration
{
    public class SqlTableWatcherOptions
    {
        public string ConnectionString { get; set; }
        public string DatabaseName { get; set; }
        public string TableName { get; set; }
        public string SchemaName { get; set; } = "dbo";
        public ChangeTypes NotificationsToReceive { get; set; } = ChangeTypes.Insert | ChangeTypes.Update | ChangeTypes.Delete;
        /// <summary>
        /// When true, inserts, updates and deletes made to <see cref="TableName"/> will be processed by Chatter. The consumer will be required to handle
        /// <see cref="RowInsertedEvent{TRowChangeData}"/>, <see cref="RowUpdatedEvent{TRowChangeData}"/>, and <see cref="RowDeletedEvent{TRowChangeData}"/> to receive table changes.
        /// Otherwise, the consumer must handle <see cref="ProcessTableChangesCommand{TRowChangeData}"/> and process the table changes manually.
        /// Defaulted to true.
        /// </summary>
        public bool ProcessTableChangesViaChatter { get; set; } = true;
        internal SqlServiceBrokerOptions ServiceBrokerOptions { get; set; }
        internal ReceiverOptions ReceiverOptions { get; set; }

        public SqlTableWatcherOptions(string connectionString,
                                        string databaseName,
                                        string tableName,
                                        string schemaName = "dbo",
                                        ChangeTypes changesToWatch =
                                            ChangeTypes.Insert | ChangeTypes.Update | ChangeTypes.Delete,
                                        bool processTableChangesViaChatter = true)
        {
            ConnectionString = connectionString;
            DatabaseName = databaseName;
            TableName = tableName;
            SchemaName = schemaName;
            NotificationsToReceive = changesToWatch;
            ProcessTableChangesViaChatter = processTableChangesViaChatter;
        }
    }
}
