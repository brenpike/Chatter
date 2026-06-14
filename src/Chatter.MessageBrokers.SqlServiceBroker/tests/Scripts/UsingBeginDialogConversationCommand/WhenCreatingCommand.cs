using Chatter.MessageBrokers.SqlServiceBroker.Scripts;
using FluentAssertions;
using System;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Linq;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Scripts.UsingBeginDialogConversationCommand
{
    // Behavior-pinning tests: characterize the SQL emitted by BeginDialogConversationCommand.Create()
    // AS-IS. Create() only builds the command (CreateCommand + properties + CommandText) and never
    // opens the connection, so an unopened SqlConnection() is a valid argument.
    // INVARIANT: Create() mutates _targetServiceName (bracket-strip) in place and so is NOT idempotent;
    // every test constructs a fresh instance and calls Create() exactly once.
    public class WhenCreatingCommand : Testing.Core.Context
    {
        private static SqlCommand Create(
            string targetServiceName,
            string initiatorServiceName = "",
            string serviceContractName = "",
            int lifetime = 0,
            bool encryption = false,
            Guid relatedConversationGroupId = default,
            Guid relatedConversationId = default)
            => new BeginDialogConversationCommand(
                    new SqlConnection(),
                    targetServiceName,
                    initiatorServiceName,
                    serviceContractName,
                    lifetime,
                    encryption,
                    relatedConversationGroupId,
                    relatedConversationId)
                .Create();

        [Fact]
        public void MustEmitExactCanonicalQueryForDefaults()
            => Create("TargetSvc").CommandText
                .Should().Be("BEGIN DIALOG @conversationHandle FROM SERVICE [TargetSvc] TO SERVICE @targetService WITH ENCRYPTION = OFF;");

        [Fact]
        public void MustSetCommandTypeToText()
            => Create("TargetSvc").CommandType.Should().Be(CommandType.Text);

        [Fact]
        public void MustDefaultInitiatorToTargetWhenInitiatorBlank()
            => Create("TargetSvc").CommandText.Should().Contain("FROM SERVICE [TargetSvc] ");

        [Fact]
        public void MustBracketWrapInitiatorServiceName()
            => Create("TargetSvc", initiatorServiceName: "MyInitiator").CommandText
                .Should().Contain("FROM SERVICE [MyInitiator] ");

        [Fact]
        public void MustNotDoubleBracketInitiatorThatAlreadyStartsWithBracket()
            => Create("TargetSvc", initiatorServiceName: "[Already]").CommandText
                .Should().Contain("FROM SERVICE [Already] ");

        [Fact]
        public void MustStripBracketsFromTargetServiceNameParameterValue()
            => Create("[Target]Svc").Parameters["@targetService"].Value
                .Should().Be("TargetSvc");

        [Fact]
        public void MustAlwaysAddTargetServiceParameter()
            => Create("TargetSvc").Parameters.Cast<SqlParameter>()
                .Should().Contain(p => p.ParameterName == "@targetService");

        [Fact]
        public void MustAddConversationHandleOutputParameter()
        {
            var handleParam = Create("TargetSvc").Parameters["@conversationHandle"];
            handleParam.Direction.Should().Be(ParameterDirection.Output);
            handleParam.SqlDbType.Should().Be(SqlDbType.UniqueIdentifier);
        }

        [Fact]
        public void MustEmitEncryptionOffWhenEncryptionDisabled()
            => Create("TargetSvc", encryption: false).CommandText
                .Should().Contain("WITH ENCRYPTION = OFF");

        [Fact]
        public void MustEmitEncryptionOnWhenEncryptionEnabled()
            => Create("TargetSvc", encryption: true).CommandText
                .Should().Contain("WITH ENCRYPTION = ON");

        [Fact]
        public void MustNotEmitContractClauseWhenContractBlank()
            => Create("TargetSvc").CommandText.Should().NotContain("ON CONTRACT");

        [Fact]
        public void MustNotAddContractParameterWhenContractBlank()
            => Create("TargetSvc").Parameters.Cast<SqlParameter>()
                .Should().NotContain(p => p.ParameterName == "@contractName");

        [Fact]
        public void MustEmitContractClauseWhenContractProvided()
            => Create("TargetSvc", serviceContractName: "MyContract").CommandText
                .Should().Contain(" ON CONTRACT @contractName");

        [Fact]
        public void MustAddContractParameterWhenContractProvided()
            => Create("TargetSvc", serviceContractName: "MyContract").Parameters.Cast<SqlParameter>()
                .Should().Contain(p => p.ParameterName == "@contractName");

        [Fact]
        public void MustEmitRelatedConversationWhenConversationIdSet()
        {
            var command = Create("TargetSvc", relatedConversationId: Guid.NewGuid());
            command.CommandText.Should().Contain(" , RELATED_CONVERSATION = @conversationId");
            command.Parameters.Cast<SqlParameter>().Should().Contain(p => p.ParameterName == "@conversationId");
        }

        [Fact]
        public void MustEmitRelatedConversationGroupWhenOnlyGroupIdSet()
        {
            var command = Create("TargetSvc", relatedConversationGroupId: Guid.NewGuid());
            command.CommandText.Should().Contain(" , RELATED_CONVERSATION_GROUP = @conversationGroupId");
            command.Parameters.Cast<SqlParameter>().Should().Contain(p => p.ParameterName == "@conversationGroupId");
        }

        // INVARIANT: when BOTH relatedConversationId and relatedConversationGroupId are set, the
        // conversationId branch wins and the group branch is skipped entirely.
        [Fact]
        public void MustPreferConversationIdOverGroupWhenBothSet()
        {
            var command = Create("TargetSvc",
                relatedConversationGroupId: Guid.NewGuid(),
                relatedConversationId: Guid.NewGuid());
            command.CommandText.Should().Contain(" , RELATED_CONVERSATION = @conversationId");
            command.CommandText.Should().NotContain("RELATED_CONVERSATION_GROUP");
            command.Parameters.Cast<SqlParameter>().Should().NotContain(p => p.ParameterName == "@conversationGroupId");
        }

        [Fact]
        public void MustNotEmitLifetimeClauseForDefaultLifetime()
            => Create("TargetSvc").CommandText.Should().NotContain("LIFETIME");

        [Fact]
        public void MustNotEmitLifetimeClauseForZeroLifetime()
            => Create("TargetSvc", lifetime: 0).CommandText.Should().NotContain("LIFETIME");

        [Fact]
        public void MustEmitLifetimeClauseForPositiveLifetime()
        {
            var command = Create("TargetSvc", lifetime: 60);
            command.CommandText.Should().Contain(" , LIFETIME = @lifetime");
            command.Parameters.Cast<SqlParameter>().Should().Contain(p => p.ParameterName == "@lifetime");
        }

        [Fact]
        public void MustEndQueryWithSemicolon()
            => Create("TargetSvc").CommandText.Should().EndWith(";");
    }
}
