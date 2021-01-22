using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.SqlServiceBroker.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Chatter.SqlTableWatcher.Configuration
{
    public class SqlTableWatcherOptionsBuilder
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
        private string _tableWatcherQueueName = null;
        private string _errorQueueName = null;
        private TransactionMode _transactionMode = TransactionMode.ReceiveOnly;

        internal SqlTableWatcherOptionsBuilder(IServiceCollection services, string connectionString, string databaseName, string tableName)
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
        /// Sets the database to watch for changes.
        /// </summary>
        /// <param name="databaseName"></param>
        /// <param name="schemaName"></param>
        /// <returns></returns>
        public SqlTableWatcherOptionsBuilder WithNameOfDatabaseToWatch(string databaseName, string schemaName = "dbo")
        {
            _databaseName = databaseName;
            _schemaName = schemaName;
            return this;
        }

        /// <summary>
        /// Sets the types of table changes to watch for. By default it watches for inserts, updates and deletes.
        /// </summary>
        /// <param name="changeTypes">Any combination of insert, update and/or delete</param>
        /// <returns></returns>
        public SqlTableWatcherOptionsBuilder WithTypesOfChangesToWatch(ChangeTypes changeTypes)
        {
            _changeTypes = changeTypes;
            return this;
        }

        /// <summary>    
        /// Configured change types made to table being watched will be processed by Chatter. The consumer will be required to handle <see cref="RowInsertedEvent{TRowChangeData}"/>, 
        /// <see cref="RowUpdatedEvent{TRowChangeData}"/>, and <see cref="RowDeletedEvent{TRowChangeData}"/> to receive table changes.
        /// </summary>
        /// <returns><see cref="SqlTableWatcherOptionsBuilder"/></returns>
        public SqlTableWatcherOptionsBuilder EmitRowChangeEvents()
        {
            _processTableChangesViaChatter = true;
            return this;
        }

        /// <summary>
        /// The consumer must handle <see cref="ProcessTableChangesCommand{TRowChangeData}"/> and process the table changes manually.
        /// </summary>
        /// <returns><see cref="SqlTableWatcherOptionsBuilder"/></returns>
        public SqlTableWatcherOptionsBuilder ProcessTableChangesManually()
        {
            _processTableChangesViaChatter = false;
            return this;
        }

        public SqlTableWatcherOptionsBuilder WithApplicationJsonUtf16CharsetMessageBodyType()
        {
            _messageBodyType = "application/json; charset=utf-16";
            return this;
        }

        public SqlTableWatcherOptionsBuilder WithMessageBodyType(string messageBodyType)
        {
            _messageBodyType = messageBodyType;
            return this;
        }

        /// <summary>
        /// Sets the amount of milliseconds the sql service broker receiver will wait to receive a table change message before timing out
        /// and re-issuing a wait and receive command. Default is -1 (unlimited).
        /// </summary>
        /// <param name="receiverTimeoutInMilliseconds">The receiver timeout in milliseconds</param>
        /// <returns><see cref="SqlTableWatcherOptionsBuilder"/></returns>
        public SqlTableWatcherOptionsBuilder WithReceiverTimeoutInMilliseconds(int receiverTimeoutInMilliseconds)
        {
            _receiverTimeoutInMilliseconds = receiverTimeoutInMilliseconds;
            return this;
        }

        public SqlTableWatcherOptionsBuilder WithConversationLifetimeInSeconds(int conversationLifetimeInSeconds = int.MaxValue)
        {
            _conversationLifetimeInSeconds = conversationLifetimeInSeconds;
            return this;
        }

        public SqlTableWatcherOptionsBuilder EnableConversationEncryption()
        {
            _coversationEncryption = true;
            return this;
        }

        public SqlTableWatcherOptionsBuilder DisableConversationEncryption()
        {
            _coversationEncryption = false;
            return this;
        }

        public SqlTableWatcherOptionsBuilder WithCompressedMessageBody()
        {
            _compressMessageBody = true;
            return this;
        }

        public SqlTableWatcherOptionsBuilder WithUncompressedMessageBody()
        {
            _compressMessageBody = false;
            return this;
        }

        /// <summary>
        /// Set the name of the queue that the underlying sql service broker will use to propogate table changes to the Chatter framework. If not set, a
        /// default queue name will be set by Chatter.
        /// </summary>
        /// <param name="tableWatcherQueueName">The queue name</param>
        /// <returns><see cref="SqlTableWatcherOptionsBuilder"/></returns>
        public SqlTableWatcherOptionsBuilder WithTableWatcherQueueName(string tableWatcherQueueName)
        {
            _tableWatcherQueueName = tableWatcherQueueName;
            return this;
        }

        /// <summary>
        /// Set the name of the queue to send messages to if sql service broker is unable to receive a queue message
        /// </summary>
        /// <param name="errorQueueName">The name of the queue to send messages to</param>
        /// <returns><see cref="SqlTableWatcherOptionsBuilder"/></returns>
        public SqlTableWatcherOptionsBuilder WithErrorQueueName(string errorQueueName)
        {
            _errorQueueName = errorQueueName;
            return this;
        }

        /// <summary>
        /// Sets the atomicity of the receiver responsible for receving the table change notification. <see cref="TransactionMode.ReceiveOnly"/> is the default.
        /// </summary>
        /// <param name="transactionMode">The <see cref="TransactionMode"/> to use</param>
        /// <returns><see cref="SqlTableWatcherOptionsBuilder"/></returns>
        public SqlTableWatcherOptionsBuilder WithTransactionMode(TransactionMode transactionMode)
        {
            _transactionMode = transactionMode;
            return this;
        }

        internal SqlTableWatcherOptions Build()
        {
            return new SqlTableWatcherOptions(_connectionString, _databaseName, _tableName, _schemaName, _changeTypes, _processTableChangesViaChatter, _tableWatcherQueueName)
            {
                ServiceBrokerOptions = new SqlServiceBrokerOptions(_connectionString, _messageBodyType, _receiverTimeoutInMilliseconds, _conversationLifetimeInSeconds, _coversationEncryption, _compressMessageBody, false),
                ReceiverOptions = new ReceiverOptions() { ErrorQueuePath = _errorQueueName, TransactionMode = _transactionMode }
            };
        }
    }
}
