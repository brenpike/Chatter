﻿using Chatter.MessageBrokers.SqlServiceBroker;
using System;

namespace Chatter.SqlChangeFeed.Scripts.ServiceBroker
{
    /// <summary>
    /// Enables and configures SQL Service Broker for use by the change feed. Creates the appropriate
    /// QUEUE and SERVICE if they don't already exist.
    /// </summary>
    public class InstallAndConfigureSqlServiceBroker : ExecutableSqlScript
    {
        private readonly string _databaseName;
        private readonly string _conversationQueueName;
        private readonly string _conversationServiceName;
        private readonly string _schemaName;
        private readonly string _deadLetterQueueName;
        private readonly string _deadLetterServiceName;

        /// <summary>
        /// Enables and configures SQL Service Broker for use by the change feed. Creates the appropriate
        /// QUEUE and SERVICE if they don't already exist.
        /// </summary>
        /// <param name="connectionString">The SQL connection string</param>
        /// <param name="databaseName">The name of the database where SQL Service Broker will be enabled and configured</param>
        /// <param name="conversationQueueName">The name of the QUEUE to create</param>
        /// <param name="conversationServiceName">The name of the SERVER to create</param>
        /// <param name="schemaName">The database schema where the QUEUE will be created</param>
        public InstallAndConfigureSqlServiceBroker(string connectionString,
                                                   string databaseName,
                                                   string conversationQueueName,
                                                   string conversationServiceName,
                                                   string schemaName,
                                                   string deadLetterQueueName,
                                                   string deadLetterServiceName)
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

            if (string.IsNullOrWhiteSpace(conversationServiceName))
            {
                throw new ArgumentException($"'{nameof(conversationServiceName)}' cannot be null or whitespace", nameof(conversationServiceName));
            }

            if (string.IsNullOrWhiteSpace(schemaName))
            {
                throw new ArgumentException($"'{nameof(schemaName)}' cannot be null or whitespace", nameof(schemaName));
            }

            _databaseName = databaseName;
            _conversationQueueName = conversationQueueName;
            _conversationServiceName = conversationServiceName;
            _schemaName = schemaName;
            _deadLetterQueueName = deadLetterQueueName;
            _deadLetterServiceName = deadLetterServiceName;
        }

        public override string ToString()
        {
            return string.Format(@"
                IF EXISTS (SELECT * FROM sys.databases 
                                    WHERE name = '{0}' AND is_broker_enabled = 0) 
                BEGIN
                    ALTER DATABASE [{0}] SET ENABLE_BROKER; 

                    ALTER AUTHORIZATION ON DATABASE::[{0}] TO [sa]
                END

                IF NOT EXISTS (SELECT * FROM sys.service_message_types WHERE name = '{6}')
                    CREATE MESSAGE TYPE [{6}] VALIDATION = NONE;

                IF NOT EXISTS (SELECT * FROM sys.service_contracts WHERE name = '{7}')
                    CREATE CONTRACT [{7}] ([{6}] SENT BY ANY, [DEFAULT] SENT BY ANY);

                IF NOT EXISTS (SELECT * FROM sys.service_queues WHERE name = '{1}')
	                CREATE QUEUE {3}.[{1}] WITH POISON_MESSAGE_HANDLING (STATUS = OFF)

                IF NOT EXISTS(SELECT * FROM sys.services WHERE name = '{2}')
	                CREATE SERVICE [{2}] ON QUEUE {3}.[{1}] ([{7}])

                IF NOT EXISTS (SELECT * FROM sys.service_queues WHERE name = '{4}')
	                CREATE QUEUE {3}.[{4}] WITH POISON_MESSAGE_HANDLING (STATUS = OFF)

                IF NOT EXISTS(SELECT * FROM sys.services WHERE name = '{5}')
	                CREATE SERVICE [{5}] ON QUEUE {3}.[{4}] ([{7}]) 
            ", _databaseName, _conversationQueueName, _conversationServiceName, _schemaName, _deadLetterQueueName, _deadLetterServiceName, ServicesMessageTypes.ChatterBrokeredMessageType, ServicesMessageTypes.ChatterServiceContract);
        }
    }
}
