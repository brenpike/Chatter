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
    // Create() only builds the command (CreateCommand + properties + CommandText) and never
    // opens the connection, so an unopened SqlConnection() is a valid argument.
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
        public void MustEscapeClosingBracketInInitiatorServiceName()
            => Create("TargetSvc", initiatorServiceName: "x]; DROP TABLE t; --").CommandText
                .Should().Contain("FROM SERVICE [x]]; DROP TABLE t; --] ");

        [Fact]
        public void MustEscapeInitiatorThatIsNotAWellFormedQuotedIdentifier()
            => Create("TargetSvc", initiatorServiceName: "[x] TO SERVICE y").CommandText
                .Should().Contain("FROM SERVICE [[x]] TO SERVICE y] ");

        [Fact]
        public void MustQuoteDottedInitiatorServiceNameAsASingleIdentifier()
            => Create("TargetSvc", initiatorServiceName: "//company.com/service").CommandText
                .Should().Contain("FROM SERVICE [//company.com/service] ");

        [Fact]
        public void MustUnquoteWellFormedQuotedTargetServiceNameParameterValue()
            => Create("[Target]").Parameters["@targetService"].Value
                .Should().Be("Target");

        [Fact]
        public void MustPassThroughTargetServiceNameThatIsNotAWellFormedQuotedIdentifier()
            => Create("[Target]Svc").Parameters["@targetService"].Value
                .Should().Be("[Target]Svc");

        [Fact]
        public void MustPreserveClosingBracketInsideTargetServiceNameParameterValue()
            => Create("my]service").Parameters["@targetService"].Value
                .Should().Be("my]service");

        [Fact]
        public void MustPassThroughNullTargetServiceNameParameterValue()
            => Create(null, initiatorServiceName: "MyInitiator").Parameters["@targetService"].Value
                .Should().BeNull();

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
        public void MustEmitTheSameCommandWhenCreateIsCalledTwiceOnOneInstance()
        {
            var command = new BeginDialogConversationCommand(new SqlConnection(), "[Target]", "[Already]");

            var first = command.Create();
            var second = command.Create();

            second.CommandText.Should().Be(first.CommandText);
            second.Parameters["@targetService"].Value.Should().Be(first.Parameters["@targetService"].Value);
        }

        [Fact]
        public void MustLeaveTargetServiceNameFieldUntouchedByCreate()
        {
            var command = new BeginDialogConversationCommand(new SqlConnection(), "[Target]");

            command.Create();

            command._targetServiceName.Should().Be("[Target]");
        }

        [Fact]
        public void MustEndQueryWithSemicolon()
            => Create("TargetSvc").CommandText.Should().EndWith(";");
    }
}
