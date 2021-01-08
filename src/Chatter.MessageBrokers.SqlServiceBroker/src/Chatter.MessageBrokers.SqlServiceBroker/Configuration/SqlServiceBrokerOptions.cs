namespace Chatter.MessageBrokers.SqlServiceBroker.Configuration
{
    public sealed class SqlServiceBrokerOptions
    {
        /// <summary>
        /// The SQL Server connection string
        /// </summary>
        public string ConnectionString { get; set; }
        /// <summary>
        /// The content type of the message body. The default is application/json.
        /// </summary>
        public string MessageBodyType { get; set; } = "application/json; charset=utf-16";
        /// <summary>
        /// Specifies the amount of time, in milliseconds, for the statement to wait for a message. 
        /// This clause can only be used with the WAITFOR clause. If this clause is not specified, or the time-out is -1, the wait time is unlimited. 
        /// If the time-out expires, RECEIVE returns an empty result set.
        /// </summary>
        public int ReceiverTimeoutInMilliseconds { get; set; } = -1;
        /// <summary>
        /// The maximum amount of time a dialog will remain open.
        /// </summary>
        public int ConversationLifetimeInSeconds { get; set; } = 0;
        /// <summary>
        /// Specifies whether or not messages sent and received on this dialog must be encrypted when they
        /// are sent outside of an instance of Microsoft SQL Server.
        /// </summary>
        public bool ConversationEncryption { get; set; } = false;
        /// <summary>
        /// Specifies whether or not messages sent should be compressed (gzip). 
        /// </summary>
        public bool CompressMessageBody { get; set; } = true;
        /// <summary>
        /// Removes all messages and catalog view entries for one side of a conversation that cannot complete normally.
        /// The other side of the conversation is not notified of the cleanup. Microsoft SQL Server drops the conversation
        /// endpoint, all messages for the conversation in the transmission queue, and all messages for the conversation
        /// in the service queue. Administrators can use this option to remove conversations which cannot complete normally
        /// </summary>
        public bool CleanupOnEndConversation { get; set; } = false;

        public SqlServiceBrokerOptions(string connectionString,
                                       string messageBodyType,
                                       int receiverTimeoutInMilliseconds = -1,
                                       int conversationLifetimeInSeconds = int.MaxValue,
                                       bool coversationEncryption = false,
                                       bool compressMessageBody = true,
                                       bool cleanupOnEndConversation = false)
        {
            ConnectionString = connectionString;
            MessageBodyType = messageBodyType;
            ReceiverTimeoutInMilliseconds = receiverTimeoutInMilliseconds;
            ConversationLifetimeInSeconds = conversationLifetimeInSeconds;
            ConversationEncryption = coversationEncryption;
            CompressMessageBody = compressMessageBody;
            CleanupOnEndConversation = cleanupOnEndConversation;
        }
    }
}
