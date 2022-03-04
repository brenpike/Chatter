using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.SqlServiceBroker.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Data.SqlClient;

namespace Chatter.SqlChangeFeed.Configuration
{
    public class SqlChangeFeedOptionsBuilder
    {
        public IServiceCollection Services { get; }

        private readonly string _connectionString;
        private string _databaseName;
        private readonly string _tableName;
        private string _schemaName = "dbo";
        private ChangeTypes _changeTypes = ChangeTypes.Insert | ChangeTypes.Update | ChangeTypes.Delete;
        private bool _processTableChangesViaChatter = true;
        private string _messageBodyType = "application/json; charset=utf-16";
        private int _receiverTimeoutInMilliseconds = -1;
        private int _conversationLifetimeInSeconds = int.MaxValue;
        private bool _coversationEncryption = false;
        private bool _compressMessageBody = true;
        private string _changeFeedQueueName = null;
        private string _errorQueueName = null;
        private TransactionMode _transactionMode = TransactionMode.FullAtomicityViaInfrastructure;
        private string _changeFeedDeadLetterServiceName;
        private int _maxReceiveAttempts = 10;

        internal SqlChangeFeedOptionsBuilder(IServiceCollection services, string connectionString, string databaseName, string tableName)
        {
            Services = services ?? throw new ArgumentNullException(nameof(services));

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentNullException(nameof(connectionString), "A connection string is required.");
            }

            if (string.IsNullOrWhiteSpace(tableName))
            {
                throw new ArgumentNullException(nameof(tableName), "The name of a table is required.");
            }

            _connectionString = connectionString;
            _tableName = tableName;
            _databaseName = databaseName;
        }

        /// <summary>
        /// Sets the database containing the table for which to create a change feed
        /// </summary>
        /// <param name="databaseName">The name of the database</param>
        /// <returns><see cref="SqlChangeFeedOptionsBuilder"/></returns>
        public SqlChangeFeedOptionsBuilder WithNameOfDatabaseToWatch(string databaseName)
        {
            _databaseName = databaseName;
            return this;
        }

        /// <summary>
        /// Sets the schema of the table for which to create a change feed
        /// </summary>
        /// <param name="schemaName">The name of the database</param>
        /// <returns><see cref="SqlChangeFeedOptionsBuilder"/></returns>
        public SqlChangeFeedOptionsBuilder WithSchema(string schemaName)
        {
            _schemaName = schemaName;
            return this;
        }

        /// <summary>
        /// Sets the types of table changes to watch for. By default it watches for inserts, updates and deletes.
        /// </summary>
        /// <param name="changeTypes">Any combination of insert, update and/or delete</param>
        /// <returns><see cref="SqlChangeFeedOptionsBuilder"/></returns>
        public SqlChangeFeedOptionsBuilder WithTypesOfChangesToWatch(ChangeTypes changeTypes)
        {
            _changeTypes = changeTypes;
            return this;
        }

        /// <summary>    
        /// Configured change types made to table being watched will be processed by Chatter. The consumer will be required to handle <see cref="RowInsertedEvent{TRowChangeData}"/>, 
        /// <see cref="RowUpdatedEvent{TRowChangeData}"/>, and <see cref="RowDeletedEvent{TRowChangeData}"/> to receive table changes.
        /// </summary>
        /// <returns><see cref="SqlChangeFeedOptionsBuilder"/></returns>
        public SqlChangeFeedOptionsBuilder EmitRowChangeEvents()
        {
            _processTableChangesViaChatter = true;
            return this;
        }

        /// <summary>
        /// The consumer must handle <see cref="ProcessChangeFeedCommand{TRowChangeData}"/> and process the table changes manually.
        /// </summary>
        /// <returns><see cref="SqlChangeFeedOptionsBuilder"/></returns>
        public SqlChangeFeedOptionsBuilder ProcessTableChangesManually()
        {
            _processTableChangesViaChatter = false;
            return this;
        }

        public SqlChangeFeedOptionsBuilder WithApplicationJsonUtf16CharsetMessageBodyType()
        {
            _messageBodyType = "application/json; charset=utf-16";
            return this;
        }

        public SqlChangeFeedOptionsBuilder WithMessageBodyType(string messageBodyType)
        {
            _messageBodyType = messageBodyType;
            return this;
        }

        /// <summary>
        /// Sets the amount of milliseconds the sql service broker receiver will wait to receive a table change message before timing out
        /// and re-issuing a wait and receive command. Default is -1 (unlimited).
        /// </summary>
        /// <param name="receiverTimeoutInMilliseconds">The receiver timeout in milliseconds</param>
        /// <returns><see cref="SqlChangeFeedOptionsBuilder"/></returns>
        public SqlChangeFeedOptionsBuilder WithReceiverTimeoutInMilliseconds(int receiverTimeoutInMilliseconds)
        {
            _receiverTimeoutInMilliseconds = receiverTimeoutInMilliseconds;
            return this;
        }

        public SqlChangeFeedOptionsBuilder WithConversationLifetimeInSeconds(int conversationLifetimeInSeconds = int.MaxValue)
        {
            _conversationLifetimeInSeconds = conversationLifetimeInSeconds;
            return this;
        }

        public SqlChangeFeedOptionsBuilder EnableConversationEncryption()
        {
            _coversationEncryption = true;
            return this;
        }

        public SqlChangeFeedOptionsBuilder DisableConversationEncryption()
        {
            _coversationEncryption = false;
            return this;
        }

        public SqlChangeFeedOptionsBuilder WithCompressedMessageBody()
        {
            _compressMessageBody = true;
            return this;
        }

        public SqlChangeFeedOptionsBuilder WithUncompressedMessageBody()
        {
            _compressMessageBody = false;
            return this;
        }

        /// <summary>
        /// Set the name of the change feed queue that will store messages created by sql table changes. If not set, a
        /// default queue name will be set by Chatter.
        /// </summary>
        /// <param name="changeFeedQueueName">The queue name</param>
        /// <returns><see cref="SqlChangeFeedOptionsBuilder"/></returns>
        public SqlChangeFeedOptionsBuilder WithChangeFeedQueueName(string changeFeedQueueName)
        {
            _changeFeedQueueName = changeFeedQueueName;
            return this;
        }

        /// <summary>
        /// Set the name of the change feed dead letter service that will be used to queue messages that were unable to be processed. If not set, a
        /// default service will be set by Chatter.
        /// </summary>
        /// <param name="changeFeedDeadLetterServiceName">The service name</param>
        /// <returns><see cref="SqlChangeFeedOptionsBuilder"/></returns>
        public SqlChangeFeedOptionsBuilder WithChangeFeedDeadLetterServiceName(string changeFeedDeadLetterServiceName)
        {
            _changeFeedDeadLetterServiceName = changeFeedDeadLetterServiceName;
            return this;
        }

        /// <summary>
        /// Set the name of the queue to send messages to if sql service broker is unable to receive a queue message
        /// </summary>
        /// <param name="errorQueueName">The name of the queue to send messages to</param>
        /// <returns><see cref="SqlChangeFeedOptionsBuilder"/></returns>
        public SqlChangeFeedOptionsBuilder WithErrorQueueName(string errorQueueName)
        {
            _errorQueueName = errorQueueName;
            return this;
        }

        /// <summary>
        /// Sets the atomicity of the receiver responsible for receving messages from the change feed. <see cref="TransactionMode.ReceiveOnly"/> is the default.
        /// </summary>
        /// <param name="transactionMode">The <see cref="TransactionMode"/> to use</param>
        /// <returns><see cref="SqlChangeFeedOptionsBuilder"/></returns>
        public SqlChangeFeedOptionsBuilder WithTransactionMode(TransactionMode transactionMode)
        {
            _transactionMode = transactionMode;
            return this;
        }

        public SqlChangeFeedOptionsBuilder WithMaxReceiveAttempts(int maxReceiveAttempts)
        {
            _maxReceiveAttempts = maxReceiveAttempts;
            return this;
        }

        internal SqlChangeFeedOptions Build()
        {
            var connStrBuilder = new SqlConnectionStringBuilder(_connectionString);

            if (string.IsNullOrWhiteSpace(connStrBuilder.InitialCatalog) && string.IsNullOrWhiteSpace(_databaseName))
            {
                throw new InvalidOperationException($"Cannot build {nameof(SqlChangeFeedOptions)} if a database is not specified via {nameof(_connectionString)} or {_databaseName}");
            }

            if (string.IsNullOrWhiteSpace(_databaseName))
            {
                _databaseName = connStrBuilder.InitialCatalog;
            }

            return new SqlChangeFeedOptions(_connectionString, _databaseName, _tableName, _schemaName, _changeTypes, _processTableChangesViaChatter, _changeFeedQueueName)
            {
                ServiceBrokerOptions = new SqlServiceBrokerOptions(_connectionString, _messageBodyType, _receiverTimeoutInMilliseconds, _conversationLifetimeInSeconds, _coversationEncryption, _compressMessageBody, false),
                ReceiverOptions = new ReceiverOptions() { ErrorQueuePath = _errorQueueName, TransactionMode = _transactionMode, DeadLetterQueuePath = _changeFeedDeadLetterServiceName, MaxReceiveAttempts = _maxReceiveAttempts }
            };
        }
    }
}
