using Chatter.MessageBrokers.SqlServiceBroker.Scripts;
using FluentAssertions;
using System;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Linq;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Scripts.UsingSendOnConversationCommand
{
    // Behavior-pinning tests: characterize the SQL emitted by SendOnConversationCommand.Create()
    // AS-IS. Create() only builds the command and never opens the connection, so an unopened
    // SqlConnection() is a valid argument.
    public class WhenCreatingCommand : Testing.Core.Context
    {
        private static readonly byte[] Body = new byte[] { 1, 2, 3 };

        private static SqlCommand Create(bool compress = false, string messageType = "")
            => new SendOnConversationCommand(
                    new SqlConnection(),
                    Guid.NewGuid(),
                    Body,
                    transaction: null,
                    compress: compress,
                    messageType: messageType)
                .Create();

        [Fact]
        public void MustEmitExactCanonicalQueryForDefaults()
            => Create().CommandText
                .Should().Be("SEND ON CONVERSATION @conversationHandle (@message);");

        [Fact]
        public void MustSetCommandTypeToText()
            => Create().CommandType.Should().Be(CommandType.Text);

        [Fact]
        public void MustAlwaysAddConversationHandleParameter()
            => Create().Parameters.Cast<SqlParameter>()
                .Should().Contain(p => p.ParameterName == "@conversationHandle");

        [Fact]
        public void MustAddMessageParameterAsVarBinaryWithBodyValue()
        {
            var messageParam = Create().Parameters["@message"];
            messageParam.SqlDbType.Should().Be(SqlDbType.VarBinary);
            messageParam.Value.Should().BeSameAs(Body);
        }

        [Fact]
        public void MustNotEmitMessageTypeClauseWhenMessageTypeBlank()
            => Create().CommandText.Should().NotContain("MESSAGE TYPE");

        [Fact]
        public void MustNotAddMessageTypeParameterWhenMessageTypeBlank()
            => Create().Parameters.Cast<SqlParameter>()
                .Should().NotContain(p => p.ParameterName == "@messageType");

        [Fact]
        public void MustEmitMessageTypeClauseWhenMessageTypeProvided()
            => Create(messageType: "MyType").CommandText
                .Should().Be("SEND ON CONVERSATION @conversationHandle MESSAGE TYPE @messageType (@message);");

        [Fact]
        public void MustAddMessageTypeParameterWhenMessageTypeProvided()
            => Create(messageType: "MyType").Parameters.Cast<SqlParameter>()
                .Should().Contain(p => p.ParameterName == "@messageType");

        [Fact]
        public void MustEmitPlainMessageBodyWhenCompressDisabled()
            => Create(compress: false).CommandText.Should().EndWith("(@message);");

        [Fact]
        public void MustEmitCompressedMessageBodyWhenCompressEnabled()
            => Create(compress: true).CommandText
                .Should().Be("SEND ON CONVERSATION @conversationHandle (compress(@message));");
    }
}
