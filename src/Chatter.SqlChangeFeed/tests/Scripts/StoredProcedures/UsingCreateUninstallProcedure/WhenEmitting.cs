using Chatter.SqlChangeFeed.Scripts.ServiceBroker;
using Chatter.SqlChangeFeed.Scripts.StoredProcedures;
using Chatter.SqlChangeFeed.Scripts.Triggers;
using FluentAssertions;
using System;
using Xunit;

namespace Chatter.SqlChangeFeed.Tests.Scripts.StoredProcedures.UsingCreateUninstallProcedure
{
    public class WhenEmitting : Testing.Core.Context
    {
        private const string ConnectionString = "Server=.;Database=Db;";
        private const string Database = "Db";
        private const string UninstallProcedure = "MyUninstallProc";
        private const string InstallProcedure = "MyInstallProc";
        private const string Schema = "dbo";
        private const string Trigger = "MyTrigger";

        private static DeleteChangeFeedTrigger DropTriggerScript()
            => new DeleteChangeFeedTrigger(Trigger, Schema);

        private static UninstallSqlServiceBroker UninstallScript()
            => new UninstallSqlServiceBroker(ConnectionString, "MyQueue", "MyService", Schema, "MyDeadLetterQueue", "MyDeadLetterService");

        private static CreateUninstallProcedure Create()
            => new CreateUninstallProcedure(ConnectionString, Database, UninstallProcedure, DropTriggerScript(), UninstallScript(), Schema, InstallProcedure);

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenConnectionStringIsNullOrWhitespace(string connectionString)
            => FluentActions.Invoking(() => new CreateUninstallProcedure(connectionString, Database, UninstallProcedure, DropTriggerScript(), UninstallScript(), Schema, InstallProcedure))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenDatabaseNameIsNullOrWhitespace(string databaseName)
            => FluentActions.Invoking(() => new CreateUninstallProcedure(ConnectionString, databaseName, UninstallProcedure, DropTriggerScript(), UninstallScript(), Schema, InstallProcedure))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenUninstallProcedureNameIsNullOrWhitespace(string uninstallProcedureName)
            => FluentActions.Invoking(() => new CreateUninstallProcedure(ConnectionString, Database, uninstallProcedureName, DropTriggerScript(), UninstallScript(), Schema, InstallProcedure))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenSchemaNameIsNullOrWhitespace(string schemaName)
            => FluentActions.Invoking(() => new CreateUninstallProcedure(ConnectionString, Database, UninstallProcedure, DropTriggerScript(), UninstallScript(), schemaName, InstallProcedure))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenInstallProcedureNameIsNullOrWhitespace(string installProcedureName)
            => FluentActions.Invoking(() => new CreateUninstallProcedure(ConnectionString, Database, UninstallProcedure, DropTriggerScript(), UninstallScript(), Schema, installProcedureName))
                .Should().Throw<ArgumentException>();

        [Fact]
        public void MustThrowArgumentNullExceptionWhenDropChangeFeedTriggerScriptIsNull()
            => FluentActions.Invoking(() => new CreateUninstallProcedure(ConnectionString, Database, UninstallProcedure, null, UninstallScript(), Schema, InstallProcedure))
                .Should().Throw<ArgumentNullException>();

        [Fact]
        public void MustThrowArgumentNullExceptionWhenServiceBrokerUninstallScriptIsNull()
            => FluentActions.Invoking(() => new CreateUninstallProcedure(ConnectionString, Database, UninstallProcedure, DropTriggerScript(), null, Schema, InstallProcedure))
                .Should().Throw<ArgumentNullException>();

        [Fact]
        public void MustEmitUseDatabase()
            => Create().ToString().Should().Contain($"USE [{Database}]");

        [Fact]
        public void MustEmitCreateProcedureWithSchemaAndUninstallProcedureName()
            => Create().ToString().Should().Contain($"CREATE PROCEDURE {Schema}.{UninstallProcedure}");

        [Fact]
        public void MustEmitObjectIdGuardForInstallProcedure()
            => Create().ToString().Should().Contain($"OBJECT_ID (''{Schema}.{InstallProcedure}'', ''P'')");

        [Fact]
        public void MustEmitDropProcedureForInstallProcedure()
            => Create().ToString().Should().Contain($"DROP PROCEDURE {Schema}.{InstallProcedure}");

        [Fact]
        public void MustEmitSelfDropProcedureForUninstallProcedure()
            => Create().ToString().Should().Contain($"DROP PROCEDURE {Schema}.{UninstallProcedure}");

        [Fact]
        public void MustEmitNestedScriptsWithDoubledQuotes()
            => Create().ToString().Should().Contain($"OBJECT_ID (''{Schema}.{Trigger}'', ''TR'')");
    }
}
