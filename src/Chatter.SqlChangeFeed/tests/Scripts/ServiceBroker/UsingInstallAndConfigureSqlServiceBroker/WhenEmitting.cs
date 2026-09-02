using Chatter.SqlChangeFeed.Scripts.ServiceBroker;
using FluentAssertions;
using System;
using Xunit;

namespace Chatter.SqlChangeFeed.Tests.Scripts.ServiceBroker.UsingInstallAndConfigureSqlServiceBroker
{
    public class WhenEmitting : Testing.Core.Context
    {
        private const string ConnectionString = "Server=.;Database=Db;";
        private const string Database = "Db";
        private const string Queue = "MyQueue";
        private const string Service = "MyService";
        private const string Schema = "dbo";
        private const string DeadLetterQueue = "MyDeadLetterQueue";
        private const string DeadLetterService = "MyDeadLetterService";
        private const string HostileDatabase = "My]Db'; DROP TABLE Users;--";
        private const string HostileQueue = "My]Queue'; DROP TABLE Users;--";
        private const string HostileService = "My]Service'; DROP TABLE Users;--";
        private const string HostileSchema = "My]Schema'; DROP TABLE Users;--";
        private const string HostileDeadLetterQueue = "My]DlQueue'; DROP TABLE Users;--";
        private const string HostileDeadLetterService = "My]DlService'; DROP TABLE Users;--";

        private static InstallAndConfigureSqlServiceBroker Create()
            => new InstallAndConfigureSqlServiceBroker(ConnectionString, Database, Queue, Service, Schema, DeadLetterQueue, DeadLetterService);

        private static InstallAndConfigureSqlServiceBroker CreateHostile()
            => new InstallAndConfigureSqlServiceBroker(ConnectionString, HostileDatabase, HostileQueue, HostileService, HostileSchema, HostileDeadLetterQueue, HostileDeadLetterService);

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenConnectionStringIsNullOrWhitespace(string connectionString)
            => FluentActions.Invoking(() => new InstallAndConfigureSqlServiceBroker(connectionString, Database, Queue, Service, Schema, DeadLetterQueue, DeadLetterService))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenDatabaseNameIsNullOrWhitespace(string databaseName)
            => FluentActions.Invoking(() => new InstallAndConfigureSqlServiceBroker(ConnectionString, databaseName, Queue, Service, Schema, DeadLetterQueue, DeadLetterService))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenConversationQueueNameIsNullOrWhitespace(string queueName)
            => FluentActions.Invoking(() => new InstallAndConfigureSqlServiceBroker(ConnectionString, Database, queueName, Service, Schema, DeadLetterQueue, DeadLetterService))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenConversationServiceNameIsNullOrWhitespace(string serviceName)
            => FluentActions.Invoking(() => new InstallAndConfigureSqlServiceBroker(ConnectionString, Database, Queue, serviceName, Schema, DeadLetterQueue, DeadLetterService))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenSchemaNameIsNullOrWhitespace(string schemaName)
            => FluentActions.Invoking(() => new InstallAndConfigureSqlServiceBroker(ConnectionString, Database, Queue, Service, schemaName, DeadLetterQueue, DeadLetterService))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenDeadLetterQueueNameIsNullOrWhitespace(string deadLetterQueueName)
            => FluentActions.Invoking(() => new InstallAndConfigureSqlServiceBroker(ConnectionString, Database, Queue, Service, Schema, deadLetterQueueName, DeadLetterService))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenDeadLetterServiceNameIsNullOrWhitespace(string deadLetterServiceName)
            => FluentActions.Invoking(() => new InstallAndConfigureSqlServiceBroker(ConnectionString, Database, Queue, Service, Schema, DeadLetterQueue, deadLetterServiceName))
                .Should().Throw<ArgumentException>();

        [Fact]
        public void MustEmitAlterDatabaseEnableBroker()
            => Create().ToString().Should().Contain($"ALTER DATABASE [{Database}] SET ENABLE_BROKER");

        [Fact]
        public void MustNotEmitAlterAuthorization()
            => Create().ToString().Should().NotContain("ALTER AUTHORIZATION");

        [Fact]
        public void MustEmitEnableBrokerWithRollbackImmediate()
            => Create().ToString().Should().Contain($"ALTER DATABASE [{Database}] SET ENABLE_BROKER WITH ROLLBACK IMMEDIATE");

        [Fact]
        public void MustEmitCreateMessageTypeWithChatterBrokeredMessageType()
        {
            var script = Create().ToString();
            script.Should().Contain("CREATE MESSAGE TYPE [//Chatter/BrokeredMessage]");
        }

        [Fact]
        public void MustEmitCreateContractWithChatterServiceContract()
            => Create().ToString().Should().Contain("CREATE CONTRACT [//Chatter]");

        [Fact]
        public void MustEmitCreateQueueForConversationQueue()
            => Create().ToString().Should().Contain($"CREATE QUEUE [{Schema}].[{Queue}]");

        // Queues are SCHEMA-scoped objects, so an unqualified sys.service_queues name probe matches a
        // same-named queue in ANY schema and skips creating the target-schema queue the CREATE SERVICE
        // below then binds to. The guard must key on the schema-qualified object identity instead.
        [Fact]
        public void MustGuardConversationQueueCreationOnSchemaQualifiedQueueIdentity()
            => Create().ToString().Should().Contain($"IF OBJECT_ID('[{Schema}].[{Queue}]', 'SQ') IS NULL");

        [Fact]
        public void MustGuardDeadLetterQueueCreationOnSchemaQualifiedQueueIdentity()
            => Create().ToString().Should().Contain($"IF OBJECT_ID('[{Schema}].[{DeadLetterQueue}]', 'SQ') IS NULL");

        [Fact]
        public void MustNotGuardQueueCreationOnAnUnqualifiedQueueName()
            => Create().ToString().Should().NotContain("sys.service_queues WHERE name =");

        [Fact]
        public void MustEmitCreateServiceOnConversationQueue()
            => Create().ToString().Should().Contain($"CREATE SERVICE [{Service}] ON QUEUE [{Schema}].[{Queue}]");

        [Fact]
        public void MustEmitCreateQueueForDeadLetterQueue()
            => Create().ToString().Should().Contain($"CREATE QUEUE [{Schema}].[{DeadLetterQueue}]");

        [Fact]
        public void MustEmitCreateServiceOnDeadLetterQueue()
            => Create().ToString().Should().Contain($"CREATE SERVICE [{DeadLetterService}] ON QUEUE [{Schema}].[{DeadLetterQueue}]");

        [Fact]
        public void MustQuoteEscapeHostileDatabaseNameInBrokerEnabledLiteral()
            => CreateHostile().ToString().Should().Contain("WHERE name = 'My]Db''; DROP TABLE Users;--' AND is_broker_enabled = 0");

        [Fact]
        public void MustBracketEscapeHostileDatabaseNameInAlterDatabase()
            => CreateHostile().ToString().Should().Contain("ALTER DATABASE [My]]Db'; DROP TABLE Users;--] SET ENABLE_BROKER");

        [Fact]
        public void MustQuoteEscapeHostileSchemaAndQueueInQueueExistenceProbe()
            => CreateHostile().ToString().Should().Contain("OBJECT_ID('[My]]Schema''; DROP TABLE Users;--].[My]]Queue''; DROP TABLE Users;--]', 'SQ')");

        [Fact]
        public void MustQuoteEscapeHostileSchemaAndDeadLetterQueueInQueueExistenceProbe()
            => CreateHostile().ToString().Should().Contain("OBJECT_ID('[My]]Schema''; DROP TABLE Users;--].[My]]DlQueue''; DROP TABLE Users;--]', 'SQ')");

        [Fact]
        public void MustBracketEscapeHostileSchemaAndQueueInCreateQueue()
            => CreateHostile().ToString().Should().Contain("CREATE QUEUE [My]]Schema'; DROP TABLE Users;--].[My]]Queue'; DROP TABLE Users;--]");

        [Fact]
        public void MustQuoteEscapeHostileServiceNameInServiceExistenceLiteral()
            => CreateHostile().ToString().Should().Contain("sys.services WHERE name = 'My]Service''; DROP TABLE Users;--'");

        [Fact]
        public void MustBracketEscapeHostileServiceNameInCreateService()
            => CreateHostile().ToString().Should().Contain("CREATE SERVICE [My]]Service'; DROP TABLE Users;--] ON QUEUE");

        [Fact]
        public void MustBracketEscapeHostileSchemaAndDeadLetterQueueInCreateQueue()
            => CreateHostile().ToString().Should().Contain("CREATE QUEUE [My]]Schema'; DROP TABLE Users;--].[My]]DlQueue'; DROP TABLE Users;--]");

        [Fact]
        public void MustBracketEscapeHostileDeadLetterServiceNameInCreateService()
            => CreateHostile().ToString().Should().Contain("CREATE SERVICE [My]]DlService'; DROP TABLE Users;--] ON QUEUE");

        // A SERVICE carries its binding in sys.services.service_queue_id, a column no name probe can see. The
        // precondition block must read identity from the catalog join, never from the name alone.
        [Fact]
        public void MustKeyTheConversationServiceBindingOnTheServiceQueueIdJoin()
        {
            var preconditions = Create().ToServiceBindingPreconditions();

            preconditions.Should().Contain("INNER JOIN sys.service_queues q ON q.object_id = svc.service_queue_id");
            preconditions.Should().Contain("WHERE svc.name = ''MyService''");
        }

        [Fact]
        public void MustRefuseWhenTheConversationServiceIsBoundToAnotherQueue()
            => Create().ToServiceBindingPreconditions()
                .Should().Contain("IF @ChatterInstalledConversationQueueId IS NOT NULL")
                .And.Contain("OR @ChatterInstalledConversationQueueId <> @ChatterExpectedConversationQueueId");

        // On an upgrade that renamed the queue, the newly configured queue does not exist yet, so its OBJECT_ID
        // is NULL and a bare <> comparison evaluates to UNKNOWN and lets the diverged binding through.
        [Fact]
        public void MustTreatAMissingConfiguredConversationQueueAsADivergedBinding()
            => Create().ToServiceBindingPreconditions()
                .Should().Contain("@ChatterExpectedConversationQueueId IS NULL");

        [Fact]
        public void MustNameTheDivergedServiceTheInstalledBindingTheExpectedBindingAndTheRemedyWhenRefusingTheConversationService()
        {
            var preconditions = Create().ToServiceBindingPreconditions();

            preconditions.Should().Contain("SERVICE [MyService] is bound to QUEUE %s");
            preconditions.Should().Contain("configured to use QUEUE [dbo].[MyQueue]");
            preconditions.Should().Contain("Run the Chatter_UninstallChangeFeed_<row changed data type> Stored Procedure");
            preconditions.Should().Contain("re-run the Change Feed Migration");
            preconditions.Should().Contain(", 16, 1, @ChatterInstalledConversationQueue);");
        }

        // The block is spliced verbatim into the install Stored Procedure's EXEC(' ... ') body, so every literal
        // it carries sits two single-quoted layers deep.
        [Fact]
        public void MustQuoteEscapeHostileSchemaAndQueueAtNestingDepthTwoInTheConversationQueueProbe()
            => CreateHostile().ToServiceBindingPreconditions()
                .Should().Contain("OBJECT_ID(''[My]]Schema''''; DROP TABLE Users;--].[My]]Queue''''; DROP TABLE Users;--]'', ''SQ'')");

        [Fact]
        public void MustQuoteEscapeHostileConversationServiceNameAtNestingDepthTwoInTheCatalogLookup()
            => CreateHostile().ToServiceBindingPreconditions()
                .Should().Contain("WHERE svc.name = ''My]Service''''; DROP TABLE Users;--''");

        [Fact]
        public void MustQuoteEscapeHostileConversationServiceNameAtNestingDepthTwoInTheRefusalMessage()
            => CreateHostile().ToServiceBindingPreconditions()
                .Should().Contain("SERVICE [My]]Service''''; DROP TABLE Users;--] is bound to QUEUE %s");

        [Fact]
        public void MustRefuseWhenAServiceOtherThanTheConfiguredDeadLetterServiceIsBoundToTheDeadLetterQueue()
            => Create().ToServiceBindingPreconditions()
                .Should().Contain("AND svc.name <> ''MyDeadLetterService''")
                .And.Contain("IF @ChatterConflictingDeadLetterService IS NOT NULL");

        // An unscoped probe would refuse a legitimate consumer-owned service that merely shares the database, so
        // the dead-letter arm keys on this change feed's own dead-letter queue object id.
        [Fact]
        public void MustScopeTheDeadLetterProbeToTheDeadLetterQueueOfThisChangeFeed()
            => Create().ToServiceBindingPreconditions()
                .Should().Contain("DECLARE @ChatterDeadLetterQueueId int = OBJECT_ID(''[dbo].[MyDeadLetterQueue]'', ''SQ'');")
                .And.Contain("WHERE q.object_id = @ChatterDeadLetterQueueId");

        [Fact]
        public void MustNameTheDivergedServiceTheInstalledBindingTheExpectedBindingAndTheRemedyWhenRefusingTheDeadLetterQueue()
        {
            var preconditions = Create().ToServiceBindingPreconditions();

            preconditions.Should().Contain("SERVICE %s is bound to QUEUE [dbo].[MyDeadLetterQueue]");
            preconditions.Should().Contain("configured to use SERVICE [MyDeadLetterService] on that queue");
            preconditions.Should().Contain("Run the Chatter_UninstallChangeFeed_<row changed data type> Stored Procedure");
            preconditions.Should().Contain(", 16, 1, @ChatterConflictingDeadLetterService);");
        }

        [Fact]
        public void MustQuoteEscapeHostileSchemaAndDeadLetterQueueAtNestingDepthTwoInTheDeadLetterQueueProbe()
            => CreateHostile().ToServiceBindingPreconditions()
                .Should().Contain("OBJECT_ID(''[My]]Schema''''; DROP TABLE Users;--].[My]]DlQueue''''; DROP TABLE Users;--]'', ''SQ'')");

        [Fact]
        public void MustQuoteEscapeHostileDeadLetterServiceNameAtNestingDepthTwoInTheCatalogLookup()
            => CreateHostile().ToServiceBindingPreconditions()
                .Should().Contain("AND svc.name <> ''My]DlService''''; DROP TABLE Users;--''");

        [Fact]
        public void MustQuoteEscapeHostileDeadLetterServiceNameAtNestingDepthTwoInTheRefusalMessage()
            => CreateHostile().ToServiceBindingPreconditions()
                .Should().Contain("configured to use SERVICE [My]]DlService''''; DROP TABLE Users;--] on that queue");

        // The gate refuses; it never reconciles. Rebinding or dropping a superseded object destroys undelivered
        // notifications, so no mutation belongs in the precondition block.
        [Fact]
        public void MustNotEmitAnyMutationInTheServiceBindingPreconditions()
        {
            var preconditions = Create().ToServiceBindingPreconditions();

            preconditions.Should().NotContain("ALTER ");
            preconditions.Should().NotContain("DROP ");
            preconditions.Should().NotContain("CREATE ");
        }

        // The gate must be separable from the mutation script so the install Stored Procedure can run it strictly
        // before anything is created.
        [Fact]
        public void MustKeepTheServiceBindingPreconditionsOutOfTheMutationScript()
            => Create().ToString().Should().NotContain("service_queue_id");

        [Fact]
        public void MustLeaveTheConversationServiceNameGuardInTheMutationScript()
            => Create().ToString().Should().Contain($"IF NOT EXISTS(SELECT * FROM sys.services WHERE name = '{Service}')");

        [Fact]
        public void MustLeaveTheDeadLetterServiceNameGuardInTheMutationScript()
            => Create().ToString().Should().Contain($"IF NOT EXISTS(SELECT * FROM sys.services WHERE name = '{DeadLetterService}')");
    }
}
