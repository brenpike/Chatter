using Chatter.SqlChangeFeed.Configuration;
using Chatter.SqlChangeFeed.Scripts;
using FluentAssertions;
using System;
using Xunit;

namespace Chatter.SqlChangeFeed.Tests.Scripts.UsingUninstallChangeFeedScript
{
    public class WhenEmitting : Testing.Core.Context
    {
        private const string ConnectionString = "Server=.;Database=Db;";
        private const string Database = "Db";
        private const string Table = "MyTable";
        private const string Schema = "dbo";
        private const string Queue = "MyQueue";
        private const string UninstallProcedure = "MyUninstallProc";
        private const string InstallProcedure = "MyInstallProc";
        private const string Service = "MyService";
        private const string Trigger = "MyTrigger";
        private const string DeadLetterQueue = "MyDeadLetterQueue";
        private const string DeadLetterService = "MyDeadLetterService";

        private static SqlChangeFeedOptions Options(string connectionString = ConnectionString)
            => new SqlChangeFeedOptions(connectionString, Database, Table, Schema,
                ChangeTypes.Insert | ChangeTypes.Update | ChangeTypes.Delete, true, Queue, DeadLetterQueue);

        private static UninstallChangeFeedScript Create()
            => new UninstallChangeFeedScript(Options(), UninstallProcedure, Queue, Service, Trigger, InstallProcedure, DeadLetterQueue, DeadLetterService);

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowArgumentExceptionWhenOptionsConnectionStringIsWhitespace(string connectionString)
            => FluentActions.Invoking(() => new UninstallChangeFeedScript(Options(connectionString), UninstallProcedure, Queue, Service, Trigger, InstallProcedure, DeadLetterQueue, DeadLetterService))
                .Should().Throw<ArgumentException>();

        [Fact]
        public void MustThrowArgumentExceptionWhenOptionsIsNull()
            => FluentActions.Invoking(() => new UninstallChangeFeedScript(null, UninstallProcedure, Queue, Service, Trigger, InstallProcedure, DeadLetterQueue, DeadLetterService))
                .Should().Throw<ArgumentException>();

        [Fact]
        public void MustNotThrowNullReferenceExceptionWhenOptionsIsNull()
            => FluentActions.Invoking(() => new UninstallChangeFeedScript(null, UninstallProcedure, Queue, Service, Trigger, InstallProcedure, DeadLetterQueue, DeadLetterService))
                .Should().NotThrow<NullReferenceException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenUninstallationProcedureNameIsNullOrWhitespace(string uninstallationProcedureName)
            => FluentActions.Invoking(() => new UninstallChangeFeedScript(Options(), uninstallationProcedureName, Queue, Service, Trigger, InstallProcedure, DeadLetterQueue, DeadLetterService))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenConversationQueueNameIsNullOrWhitespace(string conversationQueueName)
            => FluentActions.Invoking(() => new UninstallChangeFeedScript(Options(), UninstallProcedure, conversationQueueName, Service, Trigger, InstallProcedure, DeadLetterQueue, DeadLetterService))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenConversationServiceNameIsNullOrWhitespace(string conversationServiceName)
            => FluentActions.Invoking(() => new UninstallChangeFeedScript(Options(), UninstallProcedure, Queue, conversationServiceName, Trigger, InstallProcedure, DeadLetterQueue, DeadLetterService))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenConversationTriggerNameIsNullOrWhitespace(string conversationTriggerName)
            => FluentActions.Invoking(() => new UninstallChangeFeedScript(Options(), UninstallProcedure, Queue, Service, conversationTriggerName, InstallProcedure, DeadLetterQueue, DeadLetterService))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenInstallationProcedureNameIsNullOrWhitespace(string installationProcedureName)
            => FluentActions.Invoking(() => new UninstallChangeFeedScript(Options(), UninstallProcedure, Queue, Service, Trigger, installationProcedureName, DeadLetterQueue, DeadLetterService))
                .Should().Throw<ArgumentException>();

        [Fact]
        public void MustEmitCreateOrAlterProcedureForUninstallProcedure()
            => Create().ToString().Should().Contain($"CREATE OR ALTER PROCEDURE [{Schema}].[{UninstallProcedure}]");

        [Fact]
        public void MustEmitUseDatabaseFromOptions()
            => Create().ToString().Should().Contain($"USE [{Database}]");

        [Fact]
        public void MustEmitNestedDropTriggerContent()
            => Create().ToString().Should().Contain($"DROP TRIGGER [{Schema}].[{Trigger}]".Replace("'", "''"));

        [Fact]
        public void MustEmitNestedUninstallBrokerContent()
            => Create().ToString().Should().Contain($"DROP QUEUE [{Schema}].[{Queue}]".Replace("'", "''"));

        [Fact]
        public void MustEscapeHostileTriggerNameThroughTheWholeNestedComposition()
            => new UninstallChangeFeedScript(Options(), UninstallProcedure, Queue, Service, "My]Trig'ger;--", InstallProcedure, DeadLetterQueue, DeadLetterService)
                .ToString().Should().Contain($"DROP TRIGGER [{Schema}].[My]]Trig''ger;--]");

        [Fact]
        public void MustEscapeHostileQueueNameThroughTheWholeNestedComposition()
            => new UninstallChangeFeedScript(Options(), UninstallProcedure, "Ev]il'Q;--", Service, Trigger, InstallProcedure, DeadLetterQueue, DeadLetterService)
                .ToString().Should().Contain($"DROP QUEUE [{Schema}].[Ev]]il''Q;--]");

        [Fact]
        public void MustNotLeaveHostileQueueNameAbleToCloseTheExecStringLiteral()
            => new UninstallChangeFeedScript(Options(), UninstallProcedure, "Ev]il'Q;--", Service, Trigger, InstallProcedure, DeadLetterQueue, DeadLetterService)
                .ToString().Should().NotContain($"DROP QUEUE [{Schema}].[Ev]]il'Q;--]");
    }
}
