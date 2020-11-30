using System;

namespace Chatter.SqlChangeNotifier.Scripts.ServiceBroker
{
    /// <summary>
    /// When executed, waits for messages on the specified QUEUE and receives them
    /// </summary>
    public class ReceiveMessageFromConversation : ExecutableSqlScript
    {
        private readonly string _databaseName;
        private readonly string _conversationQueueName;
        private readonly int _timeoutInMilliseconds;
        private readonly string _schemaName;

        /// <summary>
        /// When executed, waits for messages on the specified QUEUE and receives them
        /// </summary>
        /// <param name="connectionString">The SQL connection string</param>
        /// <param name="databaseName">The name of the database where SQL Service Broker will be enabled and configured</param>
        /// <param name="conversationQueueName">The name of the QUEUE to create</param>
        /// <param name="timeoutInMilliseconds">The amoutn of time in milliseconds until the operation times out</param>
        /// <param name="schemaName">The database schema of the QUEUE to monitor for messages</param>
        public ReceiveMessageFromConversation(string connectionString,
                                              string databaseName,
                                              string conversationQueueName,
                                              int timeoutInMilliseconds,
                                              string schemaName)
            : base(connectionString)
        {
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                throw new ArgumentException($"'{nameof(databaseName)}' cannot be null or whitespace", nameof(databaseName));
            }

            if (string.IsNullOrWhiteSpace(conversationQueueName))
            {
                throw new ArgumentException($"'{nameof(conversationQueueName)}' cannot be null or whitespace", nameof(conversationQueueName));
            }

            if (string.IsNullOrWhiteSpace(schemaName))
            {
                throw new ArgumentException($"'{nameof(schemaName)}' cannot be null or whitespace", nameof(schemaName));
            }

            //TODO: timeout validation??

            _databaseName = databaseName;
            _conversationQueueName = conversationQueueName;
            _timeoutInMilliseconds = timeoutInMilliseconds;
            _schemaName = schemaName;
        }

        public override string ToString()
        {
            return string.Format(@"
                DECLARE @ConvHandle UNIQUEIDENTIFIER
                DECLARE @message VARBINARY(MAX)
                USE [{0}]
                WAITFOR (RECEIVE TOP(1) @ConvHandle=Conversation_Handle
                            , @message=message_body FROM {3}.[{1}]), TIMEOUT {2};
	            BEGIN TRY END CONVERSATION @ConvHandle; END TRY BEGIN CATCH END CATCH

                SELECT CAST(decompress(@message) AS NVARCHAR(MAX)) 
            ", _databaseName, _conversationQueueName, _timeoutInMilliseconds, _schemaName);
        }
    }
}
