using Chatter.SqlChangeFeed.Scripts.Triggers;
using FluentAssertions;
using System;
using Xunit;

namespace Chatter.SqlChangeFeed.Tests.Scripts.Triggers.UsingDeleteChangeFeedTrigger
{
    public class WhenEmitting : Testing.Core.Context
    {
        private const string Trigger = "MyTrigger";
        private const string Schema = "dbo";

        private static DeleteChangeFeedTrigger Create()
            => new DeleteChangeFeedTrigger(Trigger, Schema);

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenChangeFeedTriggerNameIsNullOrWhitespace(string triggerName)
            => FluentActions.Invoking(() => new DeleteChangeFeedTrigger(triggerName, Schema))
                .Should().Throw<ArgumentException>();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowWhenSchemaNameIsNullOrWhitespace(string schemaName)
            => FluentActions.Invoking(() => new DeleteChangeFeedTrigger(Trigger, schemaName))
                .Should().Throw<ArgumentException>();

        [Fact]
        public void MustEmitObjectIdGuardForTrigger()
            => Create().ToString().Should().Contain($"OBJECT_ID ('{Schema}.{Trigger}', 'TR')");

        [Fact]
        public void MustEmitDropTriggerStatement()
            => Create().ToString().Should().Contain($"DROP TRIGGER {Schema}.[{Trigger}]");
    }
}
