using Chatter.SqlChangeFeed.Scripts.ServiceBroker;
using FluentAssertions;
using System;
using Xunit;

namespace Chatter.SqlChangeFeed.Tests.Scripts.ServiceBroker.UsingUninstallSqlServiceBroker
{
    public class WhenEmitting : Testing.Core.Context
    {
        private const string ConnectionString = "Server=.;Database=Db;";
        private const string Queue = "MyQueue";
        private const string Service = "MyService";
        private const string Schema = "dbo";
        private const string DeadLetterQueue = "MyDeadLetterQueue";
        private const string DeadLetterService = "MyDeadLetterService";

        private static UninstallSqlServiceBroker Create()
            => new UninstallSqlServiceBroker(ConnectionString, Queue, Service, Schema, DeadLetterQueue, DeadLetterService);

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenConnectionStringIsNullOrWhitespace(string connectionString)
            => FluentActions.Invoking(() => new UninstallSqlServiceBroker(connectionString, Queue, Service, Schema, DeadLetterQueue, DeadLetterService))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenConversationQueueNameIsNullOrWhitespace(string queueName)
            => FluentActions.Invoking(() => new UninstallSqlServiceBroker(ConnectionString, queueName, Service, Schema, DeadLetterQueue, DeadLetterService))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenConversationServiceNameIsNullOrWhitespace(string serviceName)
            => FluentActions.Invoking(() => new UninstallSqlServiceBroker(ConnectionString, Queue, serviceName, Schema, DeadLetterQueue, DeadLetterService))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenSchemaNameIsNullOrWhitespace(string schemaName)
            => FluentActions.Invoking(() => new UninstallSqlServiceBroker(ConnectionString, Queue, Service, schemaName, DeadLetterQueue, DeadLetterService))
                .Should().Throw<ArgumentException>();

        [Fact]
        public void MustNotGuardDeadLetterNames()
            => FluentActions.Invoking(() => new UninstallSqlServiceBroker(ConnectionString, Queue, Service, Schema, null, null))
                .Should().NotThrow();

        [Fact]
        public void MustEmitDropServiceForConversationService()
            => Create().ToString().Should().Contain($"DROP SERVICE [{Service}]");

        [Fact]
        public void MustEmitObjectIdGuardForConversationQueue()
            => Create().ToString().Should().Contain($"OBJECT_ID ('{Schema}.{Queue}', 'SQ')");

        [Fact]
        public void MustEmitDropQueueForConversationQueue()
            => Create().ToString().Should().Contain($"DROP QUEUE {Schema}.[{Queue}]");

        [Fact]
        public void MustEmitEndConversationCursorCleanup()
            => Create().ToString().Should().Contain("END CONVERSATION");

        [Fact]
        public void MustEmitDropServiceForDeadLetterService()
            => Create().ToString().Should().Contain($"DROP SERVICE [{DeadLetterService}]");

        [Fact]
        public void MustEmitDropQueueForDeadLetterQueue()
            => Create().ToString().Should().Contain($"DROP QUEUE {Schema}.[{DeadLetterQueue}]");

        [Fact]
        public void MustEmitEmptyNameDropQueueWhenDeadLetterNamesAreNull()
        {
            var script = new UninstallSqlServiceBroker(ConnectionString, Queue, Service, Schema, null, null).ToString();
            script.Should().Contain($"DROP QUEUE {Schema}.[]");
        }

        [Fact]
        public void MustEmitEmptyNameDropServiceWhenDeadLetterNamesAreNull()
        {
            var script = new UninstallSqlServiceBroker(ConnectionString, Queue, Service, Schema, null, null).ToString();
            script.Should().Contain("DROP SERVICE [];");
        }
    }
}
