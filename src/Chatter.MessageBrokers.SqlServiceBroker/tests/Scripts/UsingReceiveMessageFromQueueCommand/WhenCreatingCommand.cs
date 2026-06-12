using Chatter.MessageBrokers.SqlServiceBroker.Scripts;
using FluentAssertions;
using System;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Scripts.UsingReceiveMessageFromQueueCommand
{
    // Behavior-pinning tests: characterize the SQL emitted by ReceiveMessageFromQueueCommand.Create()
    // AS-IS, including the raw (non-bracket-escaped) queue-name interpolation. Create() only calls
    // _connection.CreateCommand(), sets properties, and builds CommandText; it never opens the
    // connection, so an unopened SqlConnection() is a valid argument.
    public class WhenCreatingCommand : Testing.Core.Context
    {
        // INVARIANT: this is the exact base query for the default branch (Guid.Empty handle, timeout<=0).
        // The queue name is interpolated raw with no bracket escaping.
        private const string BaseQueryForDefaults =
            "WAITFOR (RECEIVE TOP(1) " +
            "conversation_group_id, conversation_handle, " +
            "message_sequence_number, service_name, service_contract_name, " +
            "message_type_name, " +
            "CASE WHEN SUBSTRING(message_body, 1, 2) = 0x1F8B " +
            "THEN CAST(decompress(message_body) AS VARBINARY(MAX)) " +
            "ELSE message_body END as message_body " +
            "FROM TestQueue)";

        private static SqlCommand CreateCommandWith(int timeout = -1, Guid conversationHandle = default)
            => new ReceiveMessageFromQueueCommand(new SqlConnection(), "TestQueue", timeout, conversationHandle).Create();

        [Fact]
        public void MustEmitExactBaseQueryForDefaultHandleAndDefaultTimeout()
            => CreateCommandWith().CommandText.Should().Be(BaseQueryForDefaults);

        [Fact]
        public void MustSetCommandTimeoutToZero()
            => CreateCommandWith().CommandTimeout.Should().Be(0);

        [Fact]
        public void MustSetCommandTypeToText()
            => CreateCommandWith().CommandType.Should().Be(CommandType.Text);

        [Fact]
        public void MustInterpolateQueueNameRawWithoutBracketEscaping()
            => CreateCommandWith().CommandText.Should().EndWith("FROM TestQueue)");

        [Fact]
        public void MustNotEmitWhereClauseForDefaultConversationHandle()
            => CreateCommandWith().CommandText.Should().NotContain("WHERE conversation_handle");

        [Fact]
        public void MustNotAddConversationHandleParameterForDefaultConversationHandle()
            => CreateCommandWith().Parameters.Cast<SqlParameter>()
                .Should().NotContain(p => p.ParameterName == "@conversationHandle");

        [Fact]
        public void MustEmitWhereClauseForNonDefaultConversationHandle()
            => CreateCommandWith(conversationHandle: Guid.NewGuid()).CommandText
                .Should().Contain(" WHERE conversation_handle = @conversationHandle");

        [Fact]
        public void MustAddConversationHandleParameterForNonDefaultConversationHandle()
            => CreateCommandWith(conversationHandle: Guid.NewGuid()).Parameters.Cast<SqlParameter>()
                .Should().Contain(p => p.ParameterName == "@conversationHandle");

        [Fact]
        public void MustNotEmitTimeoutClauseForDefaultTimeout()
            => CreateCommandWith().CommandText.Should().NotContain("TIMEOUT");

        [Fact]
        public void MustNotEmitTimeoutClauseForZeroTimeout()
            => CreateCommandWith(timeout: 0).CommandText.Should().NotContain("TIMEOUT");

        [Fact]
        public void MustNotAddTimeoutParameterForDefaultTimeout()
            => CreateCommandWith().Parameters.Cast<SqlParameter>()
                .Should().NotContain(p => p.ParameterName == "@timeoutInMilliseconds");

        [Fact]
        public void MustEmitTimeoutClauseAfterClosingParenForPositiveTimeout()
            => CreateCommandWith(timeout: 5).CommandText
                .Should().Contain("FROM TestQueue), TIMEOUT @timeoutInMilliseconds");

        [Fact]
        public void MustAddTimeoutParameterForPositiveTimeout()
            => CreateCommandWith(timeout: 5).Parameters.Cast<SqlParameter>()
                .Should().Contain(p => p.ParameterName == "@timeoutInMilliseconds");

        [Fact]
        public void MustEmitWhereClauseBeforeTimeoutClauseWhenBothPresent()
            => CreateCommandWith(timeout: 5, conversationHandle: Guid.NewGuid()).CommandText
                .Should().Contain(" WHERE conversation_handle = @conversationHandle), TIMEOUT @timeoutInMilliseconds");

        [Fact]
        public void MustNotContainSecondsInTimeoutParameterNameForPositiveTimeout()
        {
            var command = CreateCommandWith(timeout: 5);
            command.CommandText.Should().NotContain("Seconds");
            command.Parameters.Cast<SqlParameter>()
                .Should().Contain(p => p.ParameterName == "@timeoutInMilliseconds");
        }
    }
}
