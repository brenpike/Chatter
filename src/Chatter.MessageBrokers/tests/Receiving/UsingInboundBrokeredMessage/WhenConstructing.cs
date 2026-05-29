using Chatter.MessageBrokers.Receiving;
using FluentAssertions;
using Moq;
using System;
using System.Collections.Generic;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Receiving.UsingInboundBrokeredMessage
{
    public class WhenConstructing : Testing.Core.Context
    {
        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();

        public WhenConstructing()
            => _bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");

        private InboundBrokeredMessage CreateSut(
            string messageId = "message-id",
            byte[] body = null,
            IDictionary<string, object> messageContext = null,
            string messageReceiverPath = "receiver-path",
            IBrokeredMessageBodyConverter bodyConverter = null)
            => new InboundBrokeredMessage(
                messageId,
                body ?? new byte[] { 1, 2, 3 },
                messageContext,
                messageReceiverPath,
                bodyConverter ?? _bodyConverter.Object);

        [Fact]
        public void MustMapMessageId()
            => CreateSut(messageId: "abc").MessageId.Should().Be("abc");

        [Fact]
        public void MustMapBody()
        {
            var body = new byte[] { 9, 8, 7 };
            CreateSut(body: body).Body.Should().BeSameAs(body);
        }

        [Fact]
        public void MustMapMessageReceiverPath()
            => CreateSut(messageReceiverPath: "queue/path").MessageReceiverPath.Should().Be("queue/path");

        [Fact]
        public void MustExposeProvidedMessageContextAsReadOnly()
        {
            var context = new Dictionary<string, object> { ["key"] = "value" };
            var sut = CreateSut(messageContext: context);
            sut.MessageContext.ContainsKey("key").Should().BeTrue();
            sut.MessageContext["key"].Should().Be("value");
        }

        [Fact]
        public void MustStampContentTypeFromBodyConverterIntoMessageContext()
        {
            var sut = CreateSut();
            sut.MessageContext[MessageContext.ContentType].Should().Be("application/json");
        }

        [Fact]
        public void MustCreateEmptyMessageContextWhenNullProvided()
        {
            var sut = CreateSut(messageContext: null);
            // INVARIANT: a null application-property dictionary is replaced with an empty one,
            // then the content type key is stamped in, so exactly one key is present.
            sut.MessageContext.Should().ContainSingle()
                .Which.Key.Should().Be(MessageContext.ContentType);
        }

        [Fact]
        public void MustReadCorrelationIdFromMessageContext()
        {
            var context = new Dictionary<string, object> { [MessageContext.CorrelationId] = "corr-1" };
            CreateSut(messageContext: context).CorrelationId.Should().Be("corr-1");
        }

        [Fact]
        public void MustReturnNullCorrelationIdWhenMissing()
            => CreateSut().CorrelationId.Should().BeNull();

        [Fact]
        public void MustThrowNullReferenceWhenBodyConverterIsNull()
        {
            // INVARIANT: although the constructor defaults BodyConverter to a JsonBodyConverter when null,
            // it then dereferences the original (still-null) parameter to read ContentType, throwing NRE.
            FluentActions.Invoking(() =>
                    new InboundBrokeredMessage("message-id", new byte[] { 1 }, new Dictionary<string, object>(), "receiver-path", null))
                .Should().Throw<NullReferenceException>();
        }
    }
}
