using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.SqlServiceBroker.Configuration
{
    public class SqlServiceBrokerOptionsBuilder
    {
        public IServiceCollection Services { get; }
        private SqlServiceBrokerOptions _sqlServiceBrokerOptions;
        private readonly List<Action<IServiceCollection>> _pendingRegistrations = new List<Action<IServiceCollection>>();
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
        /// Returns the options the fluent setters configure, first creating them through
        /// <see cref="AddSqlServiceBrokerOptions(string, string, int, int, bool, bool, bool, bool)"/> without a
        /// connection string when no <c>AddSqlServiceBrokerOptions</c> overload has supplied them.
        /// </summary>
        private SqlServiceBrokerOptions EnsureOptions()
        {
            // INVARIANT: options created here hold the connection-string overload's defaults, so WithConnectionString
            // alone and that overload with the same connection string configure no diverging transport setting.
            // Pinned by WhenBuilding.MustConfigureTheSameTransportSettingsWithWithConnectionStringAsWithTheConnectionStringOverload,
            // WhenBuilding.MustDefaultConversationLifetimeToZeroWhenOnlyWithConnectionStringIsCalled and
            // WhenAddingSqlServiceBroker.MustAcceptASecondCallConfiguredWithWithConnectionStringAloneAfterACallUsingTheConnectionStringOverload;
            // measured: creating them with the SqlServiceBrokerOptions ctor's own defaults (conversation lifetime
            // int.MaxValue) reddens exactly those three.
            // INVARIANT: every With*/Use*/EndConversationAfterDispatch setter reaches the options through this method.
            // Pinned per setter by WhenBuilding.MustRefuseToBuildWithoutAConnectionStringWhenOnlyAnotherSetterIsCalled
            // (one row each) and, for WithConnectionString, WhenBuilding.MustSetTheConnectionStringWhenWithConnectionStringIsTheFirstCall;
            // measured: one setter dereferencing the field directly reddens that setter's row (UseConversationEncryption:
            // 1 red) or, for WithConnectionString, that fact plus the three above (4 red).
            if (_sqlServiceBrokerOptions is null)
            {
                AddSqlServiceBrokerOptions(connectionString: null);
            }

            return _sqlServiceBrokerOptions;
        }

        /// <summary>
        /// Sets the connection string to use for all SQL Service Broker communication
        /// </summary>
        /// <param name="connectionString">The SQL Server connection string</param>
        public SqlServiceBrokerOptionsBuilder WithConnectionString(string connectionString)
        {
            EnsureOptions().ConnectionString = connectionString;
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
            EnsureOptions().MessageBodyType = messageBodyType;
            return this;
        }

        /// <summary>
        /// Sets the content type of the SQL Service Broker message body to application/json. The content type will be used to
        /// encode to/from <see cref="string"/> and <see cref="byte[]"/>. If the content type doesn't match
        /// the message received an error will be thrown.
        /// </summary>
        public SqlServiceBrokerOptionsBuilder WithJsonBodyType()
        {
            EnsureOptions().MessageBodyType = _defaultMessageBodyType;
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
            EnsureOptions().ReceiverTimeoutInMilliseconds = receiverTimeoutInMilliseconds;
            return this;
        }

        /// <summary>
        /// Sets the maximum amount of time a dialog will remain open.
        /// </summary>
        /// <param name="conversationLifetimeInSeconds">The amount of time in milliseconds conversations will remain open.</param>
        public SqlServiceBrokerOptionsBuilder WithConversationLifetime(int conversationLifetimeInSeconds)
        {
            EnsureOptions().ConversationLifetimeInSeconds = conversationLifetimeInSeconds;
            return this;
        }

        /// <summary>
        /// Specifies whether or not messages sent and received on this dialog must be encrypted when they
        /// are sent outside of an instance of Microsoft SQL Server.
        /// </summary>
        public SqlServiceBrokerOptionsBuilder UseConversationEncryption()
        {
            EnsureOptions().ConversationEncryption = true;
            return this;
        }

        /// <summary>
        /// Specifies whether or not messages sent should be compressed (gzip). 
        /// </summary>
        public SqlServiceBrokerOptionsBuilder WithMessageBodyCompression()
        {
            EnsureOptions().CompressMessageBody = true;
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
            EnsureOptions().CleanupOnEndConversation = true;
            return this;
        }

        /// <summary>
        /// Configures <see cref="Sending.SqlServiceBrokerSender"/> to END CONVERSATION after a message has been dispatched
        /// </summary>
        public SqlServiceBrokerOptionsBuilder EndConversationAfterDispatch(bool endConvo)
        {
            EnsureOptions().EndConversationAfterDispatch = endConvo;
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

        /// <summary>
        /// Defers a registration to the end of the <c>AddSqlServiceBroker</c> call that configures this builder. The
        /// registration runs against the host's service collection when <c>AddSqlServiceBroker</c> returns, and only if
        /// <c>AddSqlServiceBroker</c> accepts the configuration: a refused call runs none of its deferred registrations.
        /// Deferred registrations and the receivers added with <c>AddQueueReceiver</c> run in the order they were added
        /// to this builder. Writes made directly through <see cref="Services"/> happen immediately and are not deferred.
        /// </summary>
        /// <param name="registration">The registration to run against the host's service collection.</param>
        /// <returns>This builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="registration"/> is null.</exception>
        public SqlServiceBrokerOptionsBuilder DeferRegistration(Action<IServiceCollection> registration)
        {
            _pendingRegistrations.Add(registration ?? throw new ArgumentNullException(nameof(registration)));
            return this;
        }

        /// <summary>
        /// Runs every deferred registration against <see cref="Services"/>, in the order they were deferred.
        /// </summary>
        internal void RegisterPendingRegistrations()
        {
            foreach (var registration in _pendingRegistrations)
            {
                registration(Services);
            }
        }
    }
}
