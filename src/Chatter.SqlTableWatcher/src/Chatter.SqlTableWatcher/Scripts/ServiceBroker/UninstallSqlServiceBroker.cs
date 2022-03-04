using System;

namespace Chatter.SqlTableWatcher.Scripts.ServiceBroker
{
    /// <summary>
    /// Removes all SQL Service Broker QUEUES, SERVICES and CONVERSATIONS for the specified SERVICE
    /// </summary>
    public class UninstallSqlServiceBroker : ExecutableSqlScript
    {
        private readonly string _conversationQueueName;
        private readonly string _conversationServiceName;
        private readonly string _schemaName;
        private readonly string _deadLetterQueueName;
        private readonly string _deadLetterServiceName;

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
                                         string schemaName,
                                         string deadLetterQueueName,
                                         string deadLetterServiceName)
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
            _deadLetterQueueName = deadLetterQueueName;
            _deadLetterServiceName = deadLetterServiceName;
        }

        private string Uninstall(string queueName, string serviceName, string schemaName)
        {
            return string.Format(@"
                DECLARE @{1}_Id INT
                SELECT @{1}_Id = service_id FROM sys.services 
                WHERE sys.services.name = '{1}'

                DECLARE @{1}_CovHandle uniqueidentifier
                DECLARE Conv CURSOR FOR
                SELECT CEP.conversation_handle FROM sys.conversation_endpoints CEP
                WHERE CEP.service_id = @{1}_Id AND ([state] != 'CD' OR [lifetime] > GETDATE() + 1)

                OPEN Conv;
                FETCH NEXT FROM Conv INTO @{1}_CovHandle;
                WHILE (@@FETCH_STATUS = 0) BEGIN
    	            END CONVERSATION @{1}_CovHandle WITH CLEANUP;
                    FETCH NEXT FROM Conv INTO @{1}_CovHandle;
                END
                CLOSE Conv;
                DEALLOCATE Conv;

                IF (@{1}_Id IS NOT NULL)
                    DROP SERVICE [{1}];
                IF OBJECT_ID ('{2}.{0}', 'SQ') IS NOT NULL
	                DROP QUEUE {2}.[{0}];
            ", queueName, serviceName, schemaName);
        }

        public override string ToString()
        {
            return $"{Uninstall(_conversationQueueName, _conversationServiceName, _schemaName)}" +
                   $"{Environment.NewLine}" +
                   $"{Uninstall(_deadLetterQueueName, _deadLetterServiceName, _schemaName)}" +
                   $"{Environment.NewLine}";
        }
    }
}
