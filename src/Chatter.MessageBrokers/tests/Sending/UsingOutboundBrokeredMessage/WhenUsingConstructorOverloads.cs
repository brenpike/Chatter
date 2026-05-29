using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Moq;
using System.Collections.Generic;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Sending.UsingOutboundBrokeredMessage
{
    public class WhenUsingConstructorOverloads : Testing.Core.Context
    {
        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
        private readonly Mock<IMessageIdGenerator> _idGenerator = new Mock<IMessageIdGenerator>();
        private readonly byte[] _body = new byte[] { 1, 2, 3 };

        public WhenUsingConstructorOverloads()
            => _bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");

        [Fact]
        public void MustGenerateMessageIdFromGeneratorOverBody()
        {
            var generatedId = System.Guid.NewGuid();
            _idGenerator.Setup(g => g.GenerateId(_body)).Returns(generatedId);

            var sut = new OutboundBrokeredMessage(_idGenerator.Object, _body, new Dictionary<string, object>(), "destination", _bodyConverter.Object);

            sut.MessageId.Should().Be(generatedId.ToString());
            _idGenerator.Verify(g => g.GenerateId(_body), Times.Once);
        }

        [Fact]
        public void MustConvertObjectBodyUsingBodyConverter()
        {
            var poco = new { Name = "abc" };
            _bodyConverter.Setup(c => c.Convert(poco)).Returns(_body);

            var sut = new OutboundBrokeredMessage("message-id", (object)poco, new Dictionary<string, object>(), "destination", _bodyConverter.Object);

            sut.Body.Should().BeSameAs(_body);
            _bodyConverter.Verify(c => c.Convert(poco), Times.Once);
        }

        [Fact]
        public void MustConvertObjectBodyAndGenerateIdFromConvertedBytes()
        {
            var poco = new { Name = "abc" };
            var generatedId = System.Guid.NewGuid();
            _bodyConverter.Setup(c => c.Convert(poco)).Returns(_body);
            _idGenerator.Setup(g => g.GenerateId(_body)).Returns(generatedId);

            var sut = new OutboundBrokeredMessage(_idGenerator.Object, (object)poco, new Dictionary<string, object>(), "destination", _bodyConverter.Object);

            sut.MessageId.Should().Be(generatedId.ToString());
            sut.Body.Should().BeSameAs(_body);
        }
    }
}
