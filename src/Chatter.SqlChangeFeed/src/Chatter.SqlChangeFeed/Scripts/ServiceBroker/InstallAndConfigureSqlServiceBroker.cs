using Chatter.MessageBrokers.SqlServiceBroker;
using Chatter.SqlChangeFeed.Scripts.Sql;
using System;

namespace Chatter.SqlChangeFeed.Scripts.ServiceBroker
{
    /// <summary>
    /// Enables and configures SQL Service Broker for use by the change feed. Creates the appropriate
    /// QUEUE and SERVICE if they don't already exist.
    /// </summary>
    public class InstallAndConfigureSqlServiceBroker : ExecutableSqlScript
    {
        // The uninstall Stored Procedure is named for the row changed data type, which this script never receives,
        // so the refusal messages below prescribe the remedy by its naming convention rather than by an exact name.
        private const string UninstallProcedureDescription = ChatterServiceBrokerConstants.ChatterUninstallChangeFeedPrefix + "<row changed data type>";

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

            if (string.IsNullOrWhiteSpace(deadLetterQueueName))
            {
                throw new ArgumentException($"'{nameof(deadLetterQueueName)}' cannot be null or whitespace", nameof(deadLetterQueueName));
            }

            if (string.IsNullOrWhiteSpace(deadLetterServiceName))
            {
                throw new ArgumentException($"'{nameof(deadLetterServiceName)}' cannot be null or whitespace", nameof(deadLetterServiceName));
            }

            _databaseName = databaseName;
            _conversationQueueName = conversationQueueName;
            _conversationServiceName = conversationServiceName;
            _schemaName = schemaName;
            _deadLetterQueueName = deadLetterQueueName;
            _deadLetterServiceName = deadLetterServiceName;
        }

        /// <summary>
        /// Emits the precondition block that refuses the Change Feed Migration when the catalog shows a SERVICE
        /// bound to a queue this configuration does not use. Emitted separately from <see cref="ToString"/> so the
        /// install Stored Procedure can run it strictly before any mutation.
        /// </summary>
        /// <returns>The precondition block, already escaped for verbatim splicing into the install Stored Procedure body.</returns>
        public string ToServiceBindingPreconditions()
        {
            // INVARIANT: the returned block is spliced VERBATIM into the install Stored Procedure's EXEC(' ... ')
            // body, so it is emitted pre-escaped at nesting depth 2 - statement literals carry doubled quotes and
            // every value inside them is quoted at depth 2. A caller must not quote it again.
            // INVARIANT: this gate is NON-DESTRUCTIVE. It refuses and returns; it never alters, drops or rebinds a
            // superseded object, because dropping an orphaned queue destroys undelivered notifications.
            // INVARIANT: services are the only binding-bearing objects this script creates. Message types and
            // contracts carry code-owned names, and a queue's identity IS its (schema, name) - none of them carry a
            // binding a name probe cannot see, so none of them belong in this block.
            return string.Format(@"
                        -- Precondition: a SERVICE carries its binding in sys.services.service_queue_id, a column no
                        -- name probe can see. Without this gate the name guards in the Service Broker section skip a
                        -- service that already exists bound to a queue this configuration no longer uses, and the
                        -- Change Feed Trigger keeps delivering to a queue nothing reads.
                        DECLARE @ChatterExpectedConversationQueueId int = OBJECT_ID(''{0}'', ''SQ'');
                        DECLARE @ChatterInstalledConversationQueueId int;
                        DECLARE @ChatterInstalledConversationQueue nvarchar(517);
                        SELECT @ChatterInstalledConversationQueueId = q.object_id,
                               @ChatterInstalledConversationQueue = QUOTENAME(SCHEMA_NAME(q.schema_id)) + ''.'' + QUOTENAME(q.name)
                          FROM sys.services svc
                          INNER JOIN sys.service_queues q ON q.object_id = svc.service_queue_id
                         WHERE svc.name = ''{1}'';

                        -- A configured queue that does not exist yet has a NULL OBJECT_ID, and a bare <> against NULL
                        -- evaluates to UNKNOWN - which is precisely the renamed-queue upgrade this gate exists for.
                        IF @ChatterInstalledConversationQueueId IS NOT NULL
                           AND (@ChatterExpectedConversationQueueId IS NULL
                                OR @ChatterInstalledConversationQueueId <> @ChatterExpectedConversationQueueId)
                        BEGIN
                            RAISERROR(''Chatter change feed cannot be installed: SERVICE {2} is bound to QUEUE %s, but this change feed is configured to use QUEUE {0}. Run the {3} Stored Procedure for this change feed, then re-run the Change Feed Migration.'', 16, 1, @ChatterInstalledConversationQueue);
                            RETURN;
                        END

                        -- Precondition: a superseded dead letter service left bound to this change feed''s dead letter
                        -- queue makes the regenerated uninstall Stored Procedure fail when it removes that queue,
                        -- because that procedure knows only the configured service name. The probe is scoped to the OWN
                        -- queue of this change feed so a consumer-owned service that merely shares the database is
                        -- left alone.
                        DECLARE @ChatterDeadLetterQueueId int = OBJECT_ID(''{4}'', ''SQ'');
                        DECLARE @ChatterConflictingDeadLetterService sysname =
                            (SELECT TOP 1 svc.name
                               FROM sys.services svc
                               INNER JOIN sys.service_queues q ON q.object_id = svc.service_queue_id
                              WHERE q.object_id = @ChatterDeadLetterQueueId
                                AND svc.name <> ''{5}''
                              ORDER BY svc.name);

                        IF @ChatterConflictingDeadLetterService IS NOT NULL
                        BEGIN
                            RAISERROR(''Chatter change feed cannot be installed: SERVICE %s is bound to QUEUE {4}, the dead letter queue of this change feed, but this change feed is configured to use SERVICE {6} on that queue. Run the {3} Stored Procedure for this change feed, then re-run the Change Feed Migration.'', 16, 1, @ChatterConflictingDeadLetterService);
                            RETURN;
                        END
", SqlIdentifier.QuoteLiteral(SqlIdentifier.EscapeQualified(_schemaName, _conversationQueueName), 2),
   SqlIdentifier.QuoteLiteral(_conversationServiceName, 2),
   SqlIdentifier.QuoteLiteral(SqlIdentifier.Escape(_conversationServiceName), 2),
   SqlIdentifier.QuoteLiteral(UninstallProcedureDescription, 2),
   SqlIdentifier.QuoteLiteral(SqlIdentifier.EscapeQualified(_schemaName, _deadLetterQueueName), 2),
   SqlIdentifier.QuoteLiteral(_deadLetterServiceName, 2),
   SqlIdentifier.QuoteLiteral(SqlIdentifier.Escape(_deadLetterServiceName), 2));
        }

        public override string ToString()
        {
            // INVARIANT: ENABLE_BROKER carries ROLLBACK IMMEDIATE. Without it the statement waits for
            // every other session on the database to close, so a first install behind a connection pool
            // blocks indefinitely with no timeout and no diagnostic.
            // INVARIANT: database ownership is left alone. Transferring it to [sa] would silently widen
            // the privileges of every EXECUTE AS OWNER module in the consumer's database.
            // INVARIANT: the queue guards key on SCHEMA-QUALIFIED object identity (OBJECT_ID ... 'SQ'), not on
            // an unqualified sys.service_queues name. Queues are schema-scoped, so a name probe matches a
            // same-named queue in ANY schema and skips creating the target-schema queue that the CREATE SERVICE
            // below binds to. Services, message types and contracts are database-scoped and have no schema, so
            // their name probes stay as they are.
            return string.Format(@"
                IF EXISTS (SELECT * FROM sys.databases 
                                    WHERE name = '{0}' AND is_broker_enabled = 0) 
                BEGIN
                    ALTER DATABASE {8} SET ENABLE_BROKER WITH ROLLBACK IMMEDIATE; 
                END

                IF NOT EXISTS (SELECT * FROM sys.service_message_types WHERE name = '{13}')
                    CREATE MESSAGE TYPE {6} VALIDATION = NONE;

                IF NOT EXISTS (SELECT * FROM sys.service_contracts WHERE name = '{14}')
                    CREATE CONTRACT {7} ({6} SENT BY ANY, [DEFAULT] SENT BY ANY);

                IF OBJECT_ID('{1}', 'SQ') IS NULL
	                CREATE QUEUE {3}.{9} WITH POISON_MESSAGE_HANDLING (STATUS = OFF)

                IF NOT EXISTS(SELECT * FROM sys.services WHERE name = '{2}')
	                CREATE SERVICE {10} ON QUEUE {3}.{9} ({7})

                IF OBJECT_ID('{4}', 'SQ') IS NULL
	                CREATE QUEUE {3}.{11} WITH POISON_MESSAGE_HANDLING (STATUS = OFF)

                IF NOT EXISTS(SELECT * FROM sys.services WHERE name = '{5}')
	                CREATE SERVICE {12} ON QUEUE {3}.{11} ({7}) 
            ", SqlIdentifier.QuoteLiteral(_databaseName),
               SqlIdentifier.QuoteLiteral(SqlIdentifier.EscapeQualified(_schemaName, _conversationQueueName)),
               SqlIdentifier.QuoteLiteral(_conversationServiceName),
               SqlIdentifier.Escape(_schemaName),
               SqlIdentifier.QuoteLiteral(SqlIdentifier.EscapeQualified(_schemaName, _deadLetterQueueName)),
               SqlIdentifier.QuoteLiteral(_deadLetterServiceName),
               SqlIdentifier.Escape(ServicesMessageTypes.ChatterBrokeredMessageType),
               SqlIdentifier.Escape(ServicesMessageTypes.ChatterServiceContract),
               SqlIdentifier.Escape(_databaseName),
               SqlIdentifier.Escape(_conversationQueueName),
               SqlIdentifier.Escape(_conversationServiceName),
               SqlIdentifier.Escape(_deadLetterQueueName),
               SqlIdentifier.Escape(_deadLetterServiceName),
               SqlIdentifier.QuoteLiteral(ServicesMessageTypes.ChatterBrokeredMessageType),
               SqlIdentifier.QuoteLiteral(ServicesMessageTypes.ChatterServiceContract));
        }
    }
}
