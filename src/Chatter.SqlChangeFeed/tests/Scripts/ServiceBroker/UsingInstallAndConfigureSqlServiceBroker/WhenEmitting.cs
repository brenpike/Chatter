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
        public void MustQuoteEscapeHostileQueueNameInQueueExistenceLiteral()
            => CreateHostile().ToString().Should().Contain("sys.service_queues WHERE name = 'My]Queue''; DROP TABLE Users;--'");

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
    }
}
