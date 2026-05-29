using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Moq;
using System;
using System.Collections.Generic;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Sending.UsingOutboundBrokeredMessage
{
    public class WhenConstructing : Testing.Core.Context
    {
        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
        private readonly byte[] _body = new byte[] { 1, 2, 3 };

        public WhenConstructing()
            => _bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");

        private OutboundBrokeredMessage CreateSut(
            string messageId = "message-id",
            byte[] body = null,
            IDictionary<string, object> messageContext = null,
            string destination = "destination",
            IBrokeredMessageBodyConverter bodyConverter = null)
            => new OutboundBrokeredMessage(
                messageId,
                body ?? _body,
                messageContext,
                destination,
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
        public void MustMapDestination()
            => CreateSut(destination: "queue/path").Destination.Should().Be("queue/path");

        [Fact]
        public void MustExposeProvidedMessageContextInstance()
        {
            var context = new Dictionary<string, object>();
            CreateSut(messageContext: context).MessageContext.Should().BeSameAs(context);
        }

        [Fact]
        public void MustCreateMessageContextWhenNullProvided()
            => CreateSut(messageContext: null).MessageContext.Should().NotBeNull();

        [Fact]
        public void MustStampContentTypeFromBodyConverter()
            => CreateSut().MessageContext[MessageContext.ContentType].Should().Be("application/json");

        [Fact]
        public void MustExposeContentTypeFromBodyConverter()
            => CreateSut().ContentType.Should().Be("application/json");

        [Fact]
        public void MustGenerateCorrelationIdWhenNoneProvided()
        {
            var sut = CreateSut(messageContext: new Dictionary<string, object>());
            sut.CorrelationId.Should().NotBeNullOrWhiteSpace();
            Guid.TryParse(sut.CorrelationId, out _).Should().BeTrue();
        }

        [Fact]
        public void MustPreserveCorrelationIdWhenProvided()
        {
            var context = new Dictionary<string, object> { [MessageContext.CorrelationId] = "corr-1" };
            CreateSut(messageContext: context).CorrelationId.Should().Be("corr-1");
        }

        [Fact]
        public void MustThrowArgumentNullWhenBodyIsNull()
            => FluentActions.Invoking(() =>
                    new OutboundBrokeredMessage("message-id", (byte[])null, new Dictionary<string, object>(), "destination", _bodyConverter.Object))
                .Should().Throw<ArgumentNullException>();

        [Fact]
        public void MustThrowArgumentExceptionWhenDestinationIsNullOrWhitespace()
            => FluentActions.Invoking(() => CreateSut(destination: "  "))
                .Should().Throw<ArgumentException>();

        [Fact]
        public void MustThrowArgumentNullWhenBodyConverterIsNull()
            => FluentActions.Invoking(() =>
                    new OutboundBrokeredMessage("message-id", _body, new Dictionary<string, object>(), "destination", (IBrokeredMessageBodyConverter)null))
                .Should().Throw<ArgumentNullException>();
    }
}
