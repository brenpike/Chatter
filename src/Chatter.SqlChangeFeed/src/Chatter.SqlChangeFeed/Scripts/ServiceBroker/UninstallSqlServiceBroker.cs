using Chatter.SqlChangeFeed.Scripts.Sql;
using System;

namespace Chatter.SqlChangeFeed.Scripts.ServiceBroker
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

        // INVARIANT: a T-SQL local variable name cannot be delimited, so the SERVICE name can never be spliced
        // into one. Each block gets a code-owned discriminator instead; the cursor is deallocated before the
        // next block declares it, so both blocks can share a single cursor name.
        private const string ConversationVariablePrefix = "Conversation";
        private const string DeadLetterVariablePrefix = "DeadLetter";

        private static string Uninstall(string queueName, string serviceName, string schemaName, string variablePrefix)
        {
            var qualifiedQueueName = SqlIdentifier.EscapeQualified(schemaName, queueName);

            return string.Format(@"
                DECLARE @{0}_Id INT
                SELECT @{0}_Id = service_id FROM sys.services
                WHERE sys.services.name = '{1}'

                DECLARE @{0}_CovHandle uniqueidentifier
                DECLARE Conv CURSOR FOR
                SELECT CEP.conversation_handle FROM sys.conversation_endpoints CEP
                WHERE CEP.service_id = @{0}_Id AND ([state] != 'CD' OR [lifetime] > GETDATE() + 1)

                OPEN Conv;
                FETCH NEXT FROM Conv INTO @{0}_CovHandle;
                WHILE (@@FETCH_STATUS = 0) BEGIN
    	            END CONVERSATION @{0}_CovHandle WITH CLEANUP;
                    FETCH NEXT FROM Conv INTO @{0}_CovHandle;
                END
                CLOSE Conv;
                DEALLOCATE Conv;

                IF (@{0}_Id IS NOT NULL)
                    DROP SERVICE {2};
                IF OBJECT_ID ('{3}', 'SQ') IS NOT NULL
	                DROP QUEUE {4};
            ",
             variablePrefix,
             SqlIdentifier.QuoteLiteral(serviceName),
             SqlIdentifier.Escape(serviceName),
             SqlIdentifier.QuoteLiteral(qualifiedQueueName),
             qualifiedQueueName);
        }

        public override string ToString()
        {
            return $"{Uninstall(_conversationQueueName, _conversationServiceName, _schemaName, ConversationVariablePrefix)}" +
                   $"{Environment.NewLine}" +
                   $"{Uninstall(_deadLetterQueueName, _deadLetterServiceName, _schemaName, DeadLetterVariablePrefix)}" +
                   $"{Environment.NewLine}";
        }
    }
}
