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

        private static InstallAndConfigureSqlServiceBroker Create()
            => new InstallAndConfigureSqlServiceBroker(ConnectionString, Database, Queue, Service, Schema, DeadLetterQueue, DeadLetterService);

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

        [Fact]
        public void MustNotGuardDeadLetterNames()
            => FluentActions.Invoking(() => new InstallAndConfigureSqlServiceBroker(ConnectionString, Database, Queue, Service, Schema, null, null))
                .Should().NotThrow();

        [Fact]
        public void MustEmitAlterDatabaseEnableBroker()
            => Create().ToString().Should().Contain($"ALTER DATABASE [{Database}] SET ENABLE_BROKER");

        [Fact]
        public void MustEmitAlterAuthorizationToSa()
            => Create().ToString().Should().Contain($"ALTER AUTHORIZATION ON DATABASE::[{Database}] TO [sa]");

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
            => Create().ToString().Should().Contain($"CREATE QUEUE {Schema}.[{Queue}]");

        [Fact]
        public void MustEmitCreateServiceOnConversationQueue()
            => Create().ToString().Should().Contain($"CREATE SERVICE [{Service}] ON QUEUE {Schema}.[{Queue}]");

        [Fact]
        public void MustEmitCreateQueueForDeadLetterQueue()
            => Create().ToString().Should().Contain($"CREATE QUEUE {Schema}.[{DeadLetterQueue}]");

        [Fact]
        public void MustEmitCreateServiceOnDeadLetterQueue()
            => Create().ToString().Should().Contain($"CREATE SERVICE [{DeadLetterService}] ON QUEUE {Schema}.[{DeadLetterQueue}]");

        [Fact]
        public void MustEmitEmptyBracketDeadLetterQueueWhenDeadLetterNamesAreNull()
        {
            var script = new InstallAndConfigureSqlServiceBroker(ConnectionString, Database, Queue, Service, Schema, null, null).ToString();
            script.Should().Contain($"CREATE QUEUE {Schema}.[]");
        }

        [Fact]
        public void MustEmitEmptyBracketDeadLetterServiceWhenDeadLetterNamesAreNull()
        {
            var script = new InstallAndConfigureSqlServiceBroker(ConnectionString, Database, Queue, Service, Schema, null, null).ToString();
            script.Should().Contain("CREATE SERVICE [] ON QUEUE");
        }
    }
}
