using Chatter.SqlChangeFeed.Configuration;
using Chatter.SqlChangeFeed.Scripts;
using FluentAssertions;
using System;
using Xunit;

namespace Chatter.SqlChangeFeed.Tests.Scripts.UsingInstallChangeFeedScript
{
    public class WhenEmitting : Testing.Core.Context
    {
        private const string ConnectionString = "Server=.;Database=Db;";
        private const string Database = "Db";
        private const string Table = "MyTable";
        private const string Schema = "dbo";
        private const string Queue = "MyQueue";
        private const string InstallProcedure = "MyInstallProc";
        private const string Service = "MyService";
        private const string Trigger = "MyTrigger";
        private const string DeadLetterQueue = "MyDeadLetterQueue";
        private const string DeadLetterService = "MyDeadLetterService";

        private static SqlChangeFeedOptions Options(string connectionString = ConnectionString)
            => new SqlChangeFeedOptions(connectionString, Database, Table, Schema,
                ChangeTypes.Insert | ChangeTypes.Update | ChangeTypes.Delete, true, Queue, DeadLetterQueue);

        private static InstallChangeFeedScript Create()
            => new InstallChangeFeedScript(Options(), InstallProcedure, Queue, Service, Trigger, DeadLetterQueue, DeadLetterService);

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowArgumentExceptionWhenOptionsConnectionStringIsWhitespace(string connectionString)
            => FluentActions.Invoking(() => new InstallChangeFeedScript(Options(connectionString), InstallProcedure, Queue, Service, Trigger, DeadLetterQueue, DeadLetterService))
                .Should().Throw<ArgumentException>();

        [Fact]
        public void MustThrowArgumentExceptionWhenOptionsIsNull()
            => FluentActions.Invoking(() => new InstallChangeFeedScript(null, InstallProcedure, Queue, Service, Trigger, DeadLetterQueue, DeadLetterService))
                .Should().Throw<ArgumentException>();

        [Fact]
        public void MustNotThrowNullReferenceExceptionWhenOptionsIsNull()
            => FluentActions.Invoking(() => new InstallChangeFeedScript(null, InstallProcedure, Queue, Service, Trigger, DeadLetterQueue, DeadLetterService))
                .Should().NotThrow<NullReferenceException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenInstallationProcedureNameIsNullOrWhitespace(string installationProcedureName)
            => FluentActions.Invoking(() => new InstallChangeFeedScript(Options(), installationProcedureName, Queue, Service, Trigger, DeadLetterQueue, DeadLetterService))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenConversationQueueNameIsNullOrWhitespace(string conversationQueueName)
            => FluentActions.Invoking(() => new InstallChangeFeedScript(Options(), InstallProcedure, conversationQueueName, Service, Trigger, DeadLetterQueue, DeadLetterService))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenConversationServiceNameIsNullOrWhitespace(string conversationServiceName)
            => FluentActions.Invoking(() => new InstallChangeFeedScript(Options(), InstallProcedure, Queue, conversationServiceName, Trigger, DeadLetterQueue, DeadLetterService))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenConversationTriggerNameIsNullOrWhitespace(string conversationTriggerName)
            => FluentActions.Invoking(() => new InstallChangeFeedScript(Options(), InstallProcedure, Queue, Service, conversationTriggerName, DeadLetterQueue, DeadLetterService))
                .Should().Throw<ArgumentException>();

        [Fact]
        public void MustNotGuardDeadLetterNamesAtConstruction()
            => FluentActions.Invoking(() => new InstallChangeFeedScript(Options(), InstallProcedure, Queue, Service, Trigger, null, null))
                .Should().NotThrow();

        [Fact]
        public void MustThrowWhenEmittingWithNullDeadLetterNames()
            => FluentActions.Invoking(() => new InstallChangeFeedScript(Options(), InstallProcedure, Queue, Service, Trigger, null, null).ToString())
                .Should().Throw<ArgumentException>();

        [Fact]
        public void MustEmitCreateProcedureForInstallationProcedure()
            => Create().ToString().Should().Contain($"CREATE PROCEDURE [{Schema}].[{InstallProcedure}]");

        [Fact]
        public void MustEmitUseDatabaseFromOptions()
            => Create().ToString().Should().Contain($"USE [{Database}]");

        [Fact]
        public void MustEmitNestedServiceBrokerContentForConfiguredQueue()
            => Create().ToString().Should().Contain($"CREATE QUEUE [{Schema}].[{Queue}]".Replace("'", "''"));

        [Fact]
        public void MustEmitNestedTriggerContentForConfiguredTable()
            => Create().ToString().Should().Contain($"CREATE TRIGGER [{Schema}].[{Trigger}]".Replace("'", "''''"));

        [Fact]
        public void MustEscapeHostileTableNameInNestedTableLiteral()
        {
            var options = new SqlChangeFeedOptions(ConnectionString, Database, "My]Table'; DROP TABLE Users;--", Schema,
                ChangeTypes.Insert, true, Queue, DeadLetterQueue);
            var script = new InstallChangeFeedScript(options, InstallProcedure, Queue, Service, Trigger, DeadLetterQueue, DeadLetterService).ToString();
            script.Should().Contain("tab.TABLE_NAME = ''My]Table''''; DROP TABLE Users;--''");
        }
    }
}
