using Chatter.MessageBrokers.SqlServiceBroker.Scripts;
using FluentAssertions;
using System;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Linq;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Scripts.UsingEndDialogConversationCommand
{
    // Behavior-pinning tests: characterize the SQL emitted by EndDialogConversationCommand.Create()
    // AS-IS. Create() only builds the command and never opens the connection, so an unopened
    // SqlConnection() is a valid argument.
    public class WhenCreatingCommand : Testing.Core.Context
    {
        private static SqlCommand Create(
            int errorCode = 0,
            string errorDescription = "",
            bool enableCleanup = false)
            => new EndDialogConversationCommand(
                    new SqlConnection(),
                    Guid.NewGuid(),
                    errorCode,
                    errorDescription,
                    enableCleanup)
                .Create();

        [Fact]
        public void MustEmitExactBaseQueryForDefaults()
            => Create().CommandText.Should().Be("END CONVERSATION @conversationHandle;");

        [Fact]
        public void MustSetCommandTypeToText()
            => Create().CommandType.Should().Be(CommandType.Text);

        [Fact]
        public void MustAlwaysAddConversationHandleParameter()
            => Create().Parameters.Cast<SqlParameter>()
                .Should().Contain(p => p.ParameterName == "@conversationHandle");

        [Fact]
        public void MustEmitErrorClauseWhenErrorCodeAndDescriptionProvided()
            => Create(errorCode: 5, errorDescription: "boom").CommandText
                .Should().Be("END CONVERSATION @conversationHandle WITH ERROR = @errorCode DESCRIPTION = @errorDescription;");

        [Fact]
        public void MustAddErrorCodeParameterWhenErrorClauseEmitted()
            => Create(errorCode: 5, errorDescription: "boom").Parameters.Cast<SqlParameter>()
                .Should().Contain(p => p.ParameterName == "@errorCode");

        [Fact]
        public void MustAddErrorDescriptionParameterAsNVarChar3000WhenErrorClauseEmitted()
        {
            var errorDescriptionParam = Create(errorCode: 5, errorDescription: "boom").Parameters["@errorDescription"];
            errorDescriptionParam.SqlDbType.Should().Be(SqlDbType.NVarChar);
            errorDescriptionParam.Size.Should().Be(3000);
        }

        // INVARIANT: the error-clause branch returns early, so WITH CLEANUP is unreachable once an
        // error clause is emitted, even if enableCleanup is true.
        [Fact]
        public void MustNotEmitCleanupWhenErrorClauseEmittedEvenIfCleanupEnabled()
            => Create(errorCode: 5, errorDescription: "boom", enableCleanup: true).CommandText
                .Should().NotContain("CLEANUP");

        // INVARIANT: errorCode!=0 with a blank description falls through (error clause skipped),
        // because the guard requires BOTH a non-zero code AND a non-blank description.
        [Fact]
        public void MustSkipErrorClauseWhenErrorCodeSetButDescriptionBlank()
            => Create(errorCode: 5, errorDescription: "").CommandText
                .Should().Be("END CONVERSATION @conversationHandle;");

        [Fact]
        public void MustNotAddErrorCodeParameterWhenDescriptionBlank()
            => Create(errorCode: 5, errorDescription: "").Parameters.Cast<SqlParameter>()
                .Should().NotContain(p => p.ParameterName == "@errorCode");

        [Fact]
        public void MustEmitCleanupWhenCleanupEnabledAndNoErrorClause()
            => Create(enableCleanup: true).CommandText
                .Should().Be("END CONVERSATION @conversationHandle WITH CLEANUP;");

        [Fact]
        public void MustNotEmitCleanupWhenCleanupDisabled()
            => Create(enableCleanup: false).CommandText.Should().NotContain("CLEANUP");
    }
}
