using Chatter.SqlChangeFeed.Scripts.Triggers;
using FluentAssertions;
using System;
using Xunit;

namespace Chatter.SqlChangeFeed.Tests.Scripts.Triggers.UsingCreateChangeFeedTrigger
{
    public class WhenEmitting : Testing.Core.Context
    {
        private const string Table = "MyTable";
        private const string Trigger = "MyTrigger";
        private const string Service = "MyService";
        private const string Schema = "dbo";

        private static CreateChangeFeedTrigger Create(ChangeTypes types = ChangeTypes.Insert)
            => new CreateChangeFeedTrigger(Table, Trigger, types, Service, Schema);

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenChangeFeedTableNameIsNullOrWhitespace(string tableName)
            => FluentActions.Invoking(() => new CreateChangeFeedTrigger(tableName, Trigger, ChangeTypes.Insert, Service, Schema))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenChangeFeedTriggerNameIsNullOrWhitespace(string triggerName)
            => FluentActions.Invoking(() => new CreateChangeFeedTrigger(Table, triggerName, ChangeTypes.Insert, Service, Schema))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenConversationServiceNameIsNullOrWhitespace(string serviceName)
            => FluentActions.Invoking(() => new CreateChangeFeedTrigger(Table, Trigger, ChangeTypes.Insert, serviceName, Schema))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenSchemaNameIsNullOrWhitespace(string schemaName)
            => FluentActions.Invoking(() => new CreateChangeFeedTrigger(Table, Trigger, ChangeTypes.Insert, Service, schemaName))
                .Should().Throw<ArgumentException>();

        [Fact]
        public void MustNotValidateTriggerRaiseByTypes()
            => FluentActions.Invoking(() => new CreateChangeFeedTrigger(Table, Trigger, ChangeTypes.None, Service, Schema))
                .Should().NotThrow();

        [Fact]
        public void MustEmitInsertOnlyAfterClauseForInsert()
            => Create(ChangeTypes.Insert).ToString().Should().Contain("AFTER INSERT");

        [Fact]
        public void MustEmitUpdateOnlyAfterClauseForUpdate()
            => Create(ChangeTypes.Update).ToString().Should().Contain("AFTER UPDATE");

        [Fact]
        public void MustEmitDeleteOnlyAfterClauseForDelete()
            => Create(ChangeTypes.Delete).ToString().Should().Contain("AFTER DELETE");

        [Fact]
        public void MustEmitInsertUpdateAfterClauseForInsertUpdate()
            => Create(ChangeTypes.Insert | ChangeTypes.Update).ToString().Should().Contain("AFTER INSERT, UPDATE");

        [Fact]
        public void MustEmitInsertUpdateDeleteAfterClauseForAll()
            => Create(ChangeTypes.Insert | ChangeTypes.Update | ChangeTypes.Delete).ToString()
                .Should().Contain("AFTER INSERT, UPDATE, DELETE");

        [Fact]
        public void MustEmitUpdateDeleteAfterClauseForUpdateDelete()
            => Create(ChangeTypes.Update | ChangeTypes.Delete).ToString().Should().Contain("AFTER UPDATE, DELETE");

        [Fact]
        public void MustDefaultToInsertAfterClauseForNone()
            => Create(ChangeTypes.None).ToString().Should().Contain("AFTER INSERT");

        [Fact]
        public void MustEmitCreateTriggerWithSchemaAndTriggerTokens()
            => Create().ToString().Should().Contain($"CREATE TRIGGER {Schema}.[{Trigger}]");

        [Fact]
        public void MustEmitOnTargetTableToken()
            => Create().ToString().Should().Contain($"ON {Schema}.[{Table}]");

        [Fact]
        public void MustEmitFromServiceToken()
            => Create().ToString().Should().Contain($"FROM SERVICE [{Service}]");

        [Fact]
        public void MustEmitOnContractWithChatterServiceContract()
            => Create().ToString().Should().Contain("ON CONTRACT [//Chatter]");

        [Fact]
        public void MustEmitSetMessageStatementPlaceholderVerbatim()
            => Create().ToString().Should().Contain("%set_message_statement%");
    }
}
