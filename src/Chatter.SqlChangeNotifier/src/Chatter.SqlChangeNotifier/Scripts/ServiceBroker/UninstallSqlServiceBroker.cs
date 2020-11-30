using System;

namespace Chatter.MessageBrokers.SqlServiceBroker.Scripts.ServiceBroker
{
    /// <summary>
    /// Removes all SQL Service Broker QUEUES, SERVICES and CONVERSATIONS for the specified SERVICE
    /// </summary>
    public class UninstallSqlServiceBroker : ExecutableSqlScript
    {
        private readonly string _conversationQueueName;
        private readonly string _conversationServiceName;
        private readonly string _schemaName;

        /// <summary>
        /// Removes all SQL Service Broker QUEUES, SERVICES and CONVERSATIONS for the specified SERVICE
        /// </summary>
        /// <param name="connectionString">The SQL connection string</param>
        /// <param name="conversationQueueName">The name of the QUEUE to uninstall</param>
        /// <param name="conversationServiceName">The name of the SERVICE to uninstall</param>
        /// <param name="schemaName">The database schema of the QUEUE to uninstall</param>
        public UninstallSqlServiceBroker(string connectionString,
                                         string conversationQueueName,
                                         string conversationServiceName,
                                         string schemaName)
            : base(connectionString)
        {
            if (string.IsNullOrWhiteSpace(conversationQueueName))
            {
                throw new ArgumentException($"'{nameof(conversationQueueName)}' cannot be null or whitespace", nameof(conversationQueueName));
            }

            if (string.IsNullOrWhiteSpace(conversationServiceName))
            {
                throw new ArgumentException($"'{nameof(conversationServiceName)}' cannot be null or whitespace", nameof(conversationServiceName));
            }

            if (string.IsNullOrWhiteSpace(schemaName))
            {
                throw new ArgumentException($"'{nameof(schemaName)}' cannot be null or whitespace", nameof(schemaName));
            }

            _conversationQueueName = conversationQueueName;
            _conversationServiceName = conversationServiceName;
            _schemaName = schemaName;
        }

        public override string ToString()
        {
            return string.Format(@"
                DECLARE @serviceId INT
                SELECT @serviceId = service_id FROM sys.services 
                WHERE sys.services.name = '{1}'

                DECLARE @ConvHandle uniqueidentifier
                DECLARE Conv CURSOR FOR
                SELECT CEP.conversation_handle FROM sys.conversation_endpoints CEP
                WHERE CEP.service_id = @serviceId AND ([state] != 'CD' OR [lifetime] > GETDATE() + 1)

                OPEN Conv;
                FETCH NEXT FROM Conv INTO @ConvHandle;
                WHILE (@@FETCH_STATUS = 0) BEGIN
    	            END CONVERSATION @ConvHandle WITH CLEANUP;
                    FETCH NEXT FROM Conv INTO @ConvHandle;
                END
                CLOSE Conv;
                DEALLOCATE Conv;

                IF (@serviceId IS NOT NULL)
                    DROP SERVICE [{1}];
                IF OBJECT_ID ('{2}.{0}', 'SQ') IS NOT NULL
	                DROP QUEUE {2}.[{0}];
            ", _conversationQueueName, _conversationServiceName, _schemaName);
        }
    }
}
