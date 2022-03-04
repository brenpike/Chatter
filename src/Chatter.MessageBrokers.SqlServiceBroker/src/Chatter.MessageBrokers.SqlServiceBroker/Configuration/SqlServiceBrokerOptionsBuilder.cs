using Microsoft.Extensions.DependencyInjection;
using System;

namespace Chatter.MessageBrokers.SqlServiceBroker.Configuration
{
    public class SqlServiceBrokerOptionsBuilder
    {
        public IServiceCollection Services { get; }
        private SqlServiceBrokerOptions _sqlServiceBrokerOptions;
        private const string _defaultMessageBodyType = "application/json; charset=utf-16";

        public SqlServiceBrokerOptionsBuilder(IServiceCollection services)
        {
            Services = services ?? throw new ArgumentNullException(nameof(services));
        }

        public SqlServiceBrokerOptionsBuilder AddSqlServiceBrokerOptions(SqlServiceBrokerOptions options)
        {
            _sqlServiceBrokerOptions = options;
            return this;
        }

        public SqlServiceBrokerOptionsBuilder AddSqlServiceBrokerOptions(Func<SqlServiceBrokerOptions> optionsBuilder)
        {
            _sqlServiceBrokerOptions = optionsBuilder();
            return this;
        }

        public SqlServiceBrokerOptionsBuilder AddSqlServiceBrokerOptions(string connectionString,
                                                                         string messageBodyType = _defaultMessageBodyType,
                                                                         int receiverTimeoutInMilliseconds = -1,
                                                                         int conversationLifetimeInSeconds = 0,
                                                                         bool coversationEncryption = false,
                                                                         bool compressMessageBody = true,
                                                                         bool cleanupOnEndConversation = false,
                                                                         bool endConversationAfterDispatch = true)
        {
            _sqlServiceBrokerOptions = new SqlServiceBrokerOptions(connectionString,
                                                                   messageBodyType,
                                                                   receiverTimeoutInMilliseconds,
                                                                   conversationLifetimeInSeconds,
                                                                   coversationEncryption,
                                                                   compressMessageBody,
                                                                   cleanupOnEndConversation,
                                                                   endConversationAfterDispatch);
            return this;
        }

        /// <summary>
        /// Sets the connection string to use for all SQL Service Broker communication
        /// </summary>
        /// <param name="connectionString">The SQL Server connection string</param>
        public SqlServiceBrokerOptionsBuilder WithConnectionString(string connectionString)
        {
            _sqlServiceBrokerOptions.ConnectionString = connectionString;
            return this;
        }

        /// <summary>
        /// Sets the content type of the SQL Service Broker message body. The content type will be used to
        /// encode to/from <see cref="string"/> and <see cref="byte[]"/>. If the content type doesn't match
        /// the message received an error will be thrown.
        /// </summary>
        /// <param name="messageBodyType">The message body type to be used for encoding the SQL Service Broker message body</param>
        public SqlServiceBrokerOptionsBuilder WithMessageBodyType(string messageBodyType)
        {
            _sqlServiceBrokerOptions.MessageBodyType = messageBodyType;
            return this;
        }

        /// <summary>
        /// Sets the content type of the SQL Service Broker message body to application/json. The content type will be used to
        /// encode to/from <see cref="string"/> and <see cref="byte[]"/>. If the content type doesn't match
        /// the message received an error will be thrown.
        /// </summary>
        public SqlServiceBrokerOptionsBuilder WithJsonBodyType()
        {
            _sqlServiceBrokerOptions.MessageBodyType = _defaultMessageBodyType;
            return this;
        }

        /// <summary>
        /// Sets the amount of time, in milliseconds, for the statement to wait for a message. 
        /// This clause can only be used with the WAITFOR clause. If this clause is not specified, or the time-out is -1, the wait time is unlimited. 
        /// If the time-out expires, RECEIVE returns an empty result set.
        /// </summary>
        /// <param name="receiverTimeoutInMilliseconds">The amount of time in seconds the receiver will wait for a message.</param>
        public SqlServiceBrokerOptionsBuilder WithReceiverTimeout(int receiverTimeoutInMilliseconds)
        {
            _sqlServiceBrokerOptions.ReceiverTimeoutInMilliseconds = receiverTimeoutInMilliseconds;
            return this;
        }

        /// <summary>
        /// Sets the maximum amount of time a dialog will remain open.
        /// </summary>
        /// <param name="conversationLifetimeInSeconds">The amount of time in milliseconds conversations will remain open.</param>
        public SqlServiceBrokerOptionsBuilder WithConversationLifetime(int conversationLifetimeInSeconds)
        {
            _sqlServiceBrokerOptions.ConversationLifetimeInSeconds = conversationLifetimeInSeconds;
            return this;
        }

        /// <summary>
        /// Specifies whether or not messages sent and received on this dialog must be encrypted when they
        /// are sent outside of an instance of Microsoft SQL Server.
        /// </summary>
        public SqlServiceBrokerOptionsBuilder UseConversationEncryption()
        {
            _sqlServiceBrokerOptions.ConversationEncryption = true;
            return this;
        }

        /// <summary>
        /// Specifies whether or not messages sent should be compressed (gzip). 
        /// </summary>
        public SqlServiceBrokerOptionsBuilder WithMessageBodyCompression()
        {
            _sqlServiceBrokerOptions.CompressMessageBody = true;
            return this;
        }

        /// <summary>
        /// Removes all messages and catalog view entries for one side of a conversation that cannot complete normally.
        /// The other side of the conversation is not notified of the cleanup. Microsoft SQL Server drops the conversation
        /// endpoint, all messages for the conversation in the transmission queue, and all messages for the conversation
        /// in the service queue. Administrators can use this option to remove conversations which cannot complete normally
        /// </summary>
        public SqlServiceBrokerOptionsBuilder WithConversationCleanup()
        {
            _sqlServiceBrokerOptions.CleanupOnEndConversation = true;
            return this;
        }

        /// <summary>
        /// Configures <see cref="Sending.SqlServiceBrokerSender"/> to END CONVERSATION after a message has been dispatched
        /// </summary>
        public SqlServiceBrokerOptionsBuilder EndConversationAfterDispatch(bool endConvo)
        {
            _sqlServiceBrokerOptions.EndConversationAfterDispatch = endConvo;
            return this;
        }

        public SqlServiceBrokerOptions Build()
        {
            if (_sqlServiceBrokerOptions is null)
            {
                throw new ArgumentNullException(nameof(_sqlServiceBrokerOptions),
                    $"Use an overload of {nameof(AddSqlServiceBrokerOptions)} or {nameof(WithConnectionString)} to configure {typeof(SqlServiceBrokerOptions).Name}");
            }

            if (string.IsNullOrWhiteSpace(_sqlServiceBrokerOptions.ConnectionString))
            {
                throw new ArgumentNullException(nameof(_sqlServiceBrokerOptions.ConnectionString), "A connection string is required.");
            }

            if (string.IsNullOrWhiteSpace(_sqlServiceBrokerOptions.MessageBodyType))
            {
                throw new ArgumentNullException(nameof(_sqlServiceBrokerOptions.MessageBodyType), "A message body type is required.");
            }

            return _sqlServiceBrokerOptions;
        }
    }
}
