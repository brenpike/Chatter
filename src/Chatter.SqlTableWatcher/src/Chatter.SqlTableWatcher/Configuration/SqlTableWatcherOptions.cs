using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.SqlServiceBroker.Configuration;

namespace Chatter.SqlTableWatcher.Configuration
{
    public class SqlTableWatcherOptions
    {
        /// <summary>
        /// The connection string use to connect to the SQL Service with table(s) to watch for changes
        /// </summary>
        public string ConnectionString { get; set; }
        /// <summary>
        /// Optional. The database containing the table to watch for changes specified by <see cref="NotificationsToReceive"/>
        /// </summary>
        public string DatabaseName { get; set; }
        /// <summary>
        /// The table to watch for changes specified by <see cref="NotificationsToReceive"/>
        /// </summary>
        public string TableName { get; set; }
        public string SchemaName { get; set; } = "dbo";
        /// <summary>
        /// The types of changes to watch for on <see cref="TableName"/>
        /// </summary>
        public ChangeTypes NotificationsToReceive { get; set; } = ChangeTypes.Insert | ChangeTypes.Update | ChangeTypes.Delete;
        public string TableWatcherQueueName { get; set; }
        public string TableWatcherDeadLetterServiceName { get; set; }

        /// <summary>
        /// When true, inserts, updates and deletes made to <see cref="TableName"/> will be processed by Chatter. The consumer will be required to handle
        /// <see cref="RowInsertedEvent{TRowChangeData}"/>, <see cref="RowUpdatedEvent{TRowChangeData}"/>, and <see cref="RowDeletedEvent{TRowChangeData}"/> to receive table changes.
        /// Otherwise, the consumer must handle <see cref="ProcessTableChangesCommand{TRowChangeData}"/> and process the table changes manually.
        /// Defaulted to true.
        /// </summary>
        public bool ProcessTableChangesViaChatter { get; set; } = true;
        internal SqlServiceBrokerOptions ServiceBrokerOptions { get; set; }
        internal ReceiverOptions ReceiverOptions { get; set; }

        internal SqlTableWatcherOptions(string connectionString,
                                      string databaseName,
                                      string tableName,
                                      string schemaName = "dbo",
                                      ChangeTypes changesToWatch =
                                          ChangeTypes.Insert | ChangeTypes.Update | ChangeTypes.Delete,
                                      bool processTableChangesViaChatter = true,
                                      string tableWatcherQueueName = null,
                                      string tableWatcherDeadLetterQueueName = null)
        {
            ConnectionString = connectionString;
            DatabaseName = databaseName;
            TableName = tableName;
            SchemaName = schemaName;
            NotificationsToReceive = changesToWatch;
            ProcessTableChangesViaChatter = processTableChangesViaChatter;
            TableWatcherQueueName = tableWatcherQueueName;
            TableWatcherDeadLetterServiceName = tableWatcherDeadLetterQueueName;
        }
    }
}
