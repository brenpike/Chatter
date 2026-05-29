using Chatter.MessageBrokers.Context;
using FluentAssertions;
using Moq;
using System.Collections.Generic;
using System.Threading;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Context.UsingMessageBrokerContext
{
    public class WhenConstructing : Testing.Core.Context
    {
        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
        private readonly byte[] _body = new byte[] { 1, 2, 3 };

        public WhenConstructing()
            => _bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");

        private MessageBrokerContext CreateSut(
            string messageId = "message-id",
            IDictionary<string, object> applicationProperties = null,
            string messageReceiverPath = "receiver-path",
            CancellationToken cancellationToken = default)
            => new MessageBrokerContext(messageId, _body, applicationProperties, messageReceiverPath, cancellationToken, _bodyConverter.Object);

        [Fact]
        public void MustWrapInboundBrokeredMessage()
        {
            var sut = CreateSut(messageId: "abc");
            sut.BrokeredMessage.Should().NotBeNull();
            sut.BrokeredMessage.MessageId.Should().Be("abc");
        }

        [Fact]
        public void MustPassReceiverPathToBrokeredMessage()
            => CreateSut(messageReceiverPath: "queue/path").BrokeredMessage.MessageReceiverPath.Should().Be("queue/path");

        [Fact]
        public void MustPassApplicationPropertiesToBrokeredMessageContext()
        {
            var props = new Dictionary<string, object> { ["key"] = "value" };
            var sut = CreateSut(applicationProperties: props);
            sut.BrokeredMessage.MessageContext.ContainsKey("key").Should().BeTrue();
        }

        [Fact]
        public void MustPropagateCancellationTokenToBaseContext()
        {
            using var cts = new CancellationTokenSource();
            CreateSut(cancellationToken: cts.Token).CancellationToken.Should().Be(cts.Token);
        }

        [Fact]
        public void MustExposeContextContainer()
            => CreateSut().Container.Should().NotBeNull();
    }
}
