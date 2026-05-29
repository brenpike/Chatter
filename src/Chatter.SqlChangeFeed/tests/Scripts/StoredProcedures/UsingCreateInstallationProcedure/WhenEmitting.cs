using Chatter.SqlChangeFeed.Scripts.ServiceBroker;
using Chatter.SqlChangeFeed.Scripts.StoredProcedures;
using Chatter.SqlChangeFeed.Scripts.Triggers;
using FluentAssertions;
using System;
using Xunit;

namespace Chatter.SqlChangeFeed.Tests.Scripts.StoredProcedures.UsingCreateInstallationProcedure
{
    public class WhenEmitting : Testing.Core.Context
    {
        private const string ConnectionString = "Server=.;Database=Db;";
        private const string Database = "Db";
        private const string SetupProcedure = "MyInstallProc";
        private const string Table = "MyTable";
        private const string Schema = "dbo";
        private const string Trigger = "MyTrigger";
        private const string Service = "MyService";

        private static InstallAndConfigureSqlServiceBroker ServiceBrokerScript()
            => new InstallAndConfigureSqlServiceBroker(ConnectionString, Database, "MyQueue", Service, Schema, "MyDeadLetterQueue", "MyDeadLetterService");

        private static CreateChangeFeedTrigger TriggerScript()
            => new CreateChangeFeedTrigger(Table, Trigger, ChangeTypes.Insert, Service, Schema);

        private static CreateInstallationProcedure Create()
            => new CreateInstallationProcedure(ConnectionString, Database, SetupProcedure, ServiceBrokerScript(), TriggerScript(), Table, Schema, Trigger);

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenConnectionStringIsNullOrWhitespace(string connectionString)
            => FluentActions.Invoking(() => new CreateInstallationProcedure(connectionString, Database, SetupProcedure, ServiceBrokerScript(), TriggerScript(), Table, Schema, Trigger))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenDatabaseNameIsNullOrWhitespace(string databaseName)
            => FluentActions.Invoking(() => new CreateInstallationProcedure(ConnectionString, databaseName, SetupProcedure, ServiceBrokerScript(), TriggerScript(), Table, Schema, Trigger))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenSetupProcedureNameIsNullOrWhitespace(string setupProcedureName)
            => FluentActions.Invoking(() => new CreateInstallationProcedure(ConnectionString, Database, setupProcedureName, ServiceBrokerScript(), TriggerScript(), Table, Schema, Trigger))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenTableNameIsNullOrWhitespace(string tableName)
            => FluentActions.Invoking(() => new CreateInstallationProcedure(ConnectionString, Database, SetupProcedure, ServiceBrokerScript(), TriggerScript(), tableName, Schema, Trigger))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenSchemaNameIsNullOrWhitespace(string schemaName)
            => FluentActions.Invoking(() => new CreateInstallationProcedure(ConnectionString, Database, SetupProcedure, ServiceBrokerScript(), TriggerScript(), Table, schemaName, Trigger))
                .Should().Throw<ArgumentException>();

        [Fact]
        public void MustThrowArgumentNullExceptionWhenServiceBrokerConfigScriptIsNull()
            => FluentActions.Invoking(() => new CreateInstallationProcedure(ConnectionString, Database, SetupProcedure, null, TriggerScript(), Table, Schema, Trigger))
                .Should().Throw<ArgumentNullException>();

        [Fact]
        public void MustThrowArgumentNullExceptionWhenChangeFeedTriggerConfigScriptIsNull()
            => FluentActions.Invoking(() => new CreateInstallationProcedure(ConnectionString, Database, SetupProcedure, ServiceBrokerScript(), null, Table, Schema, Trigger))
                .Should().Throw<ArgumentNullException>();

        [Fact]
        public void MustNotGuardTriggerName()
            => FluentActions.Invoking(() => new CreateInstallationProcedure(ConnectionString, Database, SetupProcedure, ServiceBrokerScript(), TriggerScript(), Table, Schema, null))
                .Should().NotThrow();

        [Fact]
        public void MustEmitUseDatabase()
            => Create().ToString().Should().Contain($"USE [{Database}]");

        [Fact]
        public void MustEmitCreateProcedureWithSchemaAndProcedureName()
            => Create().ToString().Should().Contain($"CREATE PROCEDURE {Schema}.{SetupProcedure}");

        [Fact]
        public void MustEmitExplicitColsBitParameter()
            => Create().ToString().Should().Contain("@ExplicitCols bit = 1");

        [Fact]
        public void MustEmitNestedServiceBrokerScriptWithDoubledQuotes()
        {
            // The nested service broker script's single quotes are doubled ('' ).
            Create().ToString().Should().Contain($"name = ''{Database}''");
        }

        [Fact]
        public void MustEmitNestedTriggerScriptWithQuadrupledQuotes()
        {
            // The nested trigger script's single quotes are quadrupled ('''' ).
            Create().ToString().Should().Contain($"''''{Service}''''");
        }
    }
}
