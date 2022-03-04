using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.SqlServiceBroker.Configuration;

namespace Chatter.SqlChangeFeed.Configuration
{
    public class SqlChangeFeedOptions
    {
        /// <summary>
        /// The connection string use to connect to the SQL Service with table(s) to watch for changes
        /// </summary>
        public string ConnectionString { get; set; }
        /// <summary>
        /// Optional. The database containing the table to watch for changes specified by <see cref="ChangeFeedTriggerTypes"/>. If not supplied,
        /// the database in the <see cref="ConnectionString"/> will be used.
        /// </summary>
        public string DatabaseName { get; set; }
        /// <summary>
        /// The table to watch for changes specified by <see cref="ChangeFeedTriggerTypes"/>
        /// </summary>
        public string TableName { get; set; }
        public string SchemaName { get; set; } = "dbo";
        /// <summary>
        /// The types of changes to watch for on on the table specified by <see cref="TableName"/>
        /// </summary>
        public ChangeTypes ChangeFeedTriggerTypes { get; set; } = ChangeTypes.Insert | ChangeTypes.Update | ChangeTypes.Delete;
        /// <summary>
        /// The name of the SQL Service Broker queue backing the sql change feed
        /// </summary>
        public string ChangeFeedQueueName { get; set; }
        /// <summary>
        /// The SQL Service Broker service where deadletter queue messages will be routed
        /// </summary>
        public string ChangeFeedDeadLetterServiceName { get; set; }

        /// <summary>
        /// When true, inserts, updates and deletes made to <see cref="TableName"/> will be processed by Chatter. The consumer will be required to handle
        /// <see cref="RowInsertedEvent{TRowChangeData}"/>, <see cref="RowUpdatedEvent{TRowChangeData}"/>, and <see cref="RowDeletedEvent{TRowChangeData}"/> to receive table changes.
        /// Otherwise, the consumer must handle <see cref="ProcessChangeFeedCommand{TRowChangeData}"/> and process the table changes manually.
        /// Defaulted to true.
        /// </summary>
        public bool ProcessChangeFeedCommandViaChatter { get; set; } = true;
        internal SqlServiceBrokerOptions ServiceBrokerOptions { get; set; }
        internal ReceiverOptions ReceiverOptions { get; set; }

        internal SqlChangeFeedOptions(string connectionString,
                                      string databaseName,
                                      string tableName,
                                      string schemaName = "dbo",
                                      ChangeTypes changesToWatch =
                                          ChangeTypes.Insert | ChangeTypes.Update | ChangeTypes.Delete,
                                      bool processChangeFeedCommandViaChatter = true,
                                      string changeFeedQueueName = null,
                                      string changeFeedDeadLetterQueueName = null)
        {
            ConnectionString = connectionString;
            DatabaseName = databaseName;
            TableName = tableName;
            SchemaName = schemaName;
            ChangeFeedTriggerTypes = changesToWatch;
            ProcessChangeFeedCommandViaChatter = processChangeFeedCommandViaChatter;
            ChangeFeedQueueName = changeFeedQueueName;
            ChangeFeedDeadLetterServiceName = changeFeedDeadLetterQueueName;
        }
    }
}
