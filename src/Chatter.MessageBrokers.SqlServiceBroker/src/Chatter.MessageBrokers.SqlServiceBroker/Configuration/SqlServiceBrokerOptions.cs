namespace Chatter.MessageBrokers.SqlServiceBroker.Configuration
{
    public class SqlServiceBrokerOptions
    {
        public string ConnectionString { get; set; }
        public string DatabaseName { get; set; }
        public string TableName { get; set; }
        public string SchemaName { get; set; } = "dbo";
        public NotificationTypes NotificationsToReceive { get; set; } = NotificationTypes.Insert | NotificationTypes.Update | NotificationTypes.Delete;

        public SqlServiceBrokerOptions(string connectionString,
                                        string databaseName,
                                        string tableName,
                                        string schemaName = "dbo",
                                        NotificationTypes listenerType =
                                            NotificationTypes.Insert | NotificationTypes.Update | NotificationTypes.Delete)
        {
            ConnectionString = connectionString;
            DatabaseName = databaseName;
            TableName = tableName;
            SchemaName = schemaName;
            NotificationsToReceive = listenerType;
        }
    }
}
