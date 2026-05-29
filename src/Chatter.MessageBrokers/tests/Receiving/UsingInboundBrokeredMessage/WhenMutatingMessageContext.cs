using Chatter.MessageBrokers.Receiving;
using FluentAssertions;
using Moq;
using System.Collections.Generic;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Receiving.UsingInboundBrokeredMessage
{
    public class WhenMutatingMessageContext : Testing.Core.Context
    {
        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
        private readonly byte[] _body = new byte[] { 1, 2, 3 };

        public WhenMutatingMessageContext()
            => _bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");

        private InboundBrokeredMessage CreateSut(IDictionary<string, object> messageContext = null)
            => new InboundBrokeredMessage("message-id", _body, messageContext, "receiver-path", _bodyConverter.Object);

        [Fact]
        public void MustConvertBodyUsingBodyConverter()
        {
            _bodyConverter.Setup(c => c.Convert<string>(_body)).Returns("converted");
            CreateSut().GetMessageFromBody<string>().Should().Be("converted");
            _bodyConverter.Verify(c => c.Convert<string>(_body), Times.Once);
        }

        [Fact]
        public void MustStampInfrastructureTypeViaSelector()
        {
            var sut = CreateSut();
            var returned = sut.UseMessagingInfrastructure(types => "my-infrastructure");
            returned.Should().BeSameAs(sut);
            sut.MessageContext[MessageContext.InfrastructureType].Should().Be("my-infrastructure");
        }

        [Fact]
        public void MustSetViaWhenKeyAbsent()
        {
            var sut = CreateSut();
            sut.UpdateVia("receiver-a");
            sut.Via.Should().Be("receiver-a");
        }

        [Fact]
        public void MustAppendViaWhenKeyAlreadyPresent()
        {
            var context = new Dictionary<string, object> { [MessageContext.Via] = "receiver-a" };
            var sut = CreateSut(context);
            sut.UpdateVia("receiver-b");
            sut.Via.Should().Be("receiver-a,receiver-b");
        }

        [Fact]
        public void MustNotAppendViaWhenAlreadyPresentAndNewViaIsWhitespace()
        {
            var context = new Dictionary<string, object> { [MessageContext.Via] = "receiver-a" };
            var sut = CreateSut(context);
            sut.UpdateVia("   ");
            sut.Via.Should().Be("receiver-a");
        }

        [Fact]
        public void MustStampFailureDetails()
        {
            var sut = CreateSut();
            sut.WithFailureDetails("detail");
            sut.MessageContext[MessageContext.FailureDetails].Should().Be("detail");
        }

        [Fact]
        public void MustStampFailureDescription()
        {
            var sut = CreateSut();
            sut.WithFailureDescription("description");
            sut.MessageContext[MessageContext.FailureDescription].Should().Be("description");
        }

        [Fact]
        public void MustRemoveReplyToProperties()
        {
            var context = new Dictionary<string, object>
            {
                [MessageContext.ReplyToAddress] = "address",
                [MessageContext.ReplyToGroupId] = "group"
            };
            var sut = CreateSut(context);
            sut.ClearReplyToProperties();
            sut.MessageContext.ContainsKey(MessageContext.ReplyToAddress).Should().BeFalse();
            sut.MessageContext.ContainsKey(MessageContext.ReplyToGroupId).Should().BeFalse();
        }

        [Fact]
        public void MustStampRouteToSelfPath()
        {
            var sut = CreateSut();
            sut.WithRouteToSelfPath("self-path");
            sut.MessageContext[MessageContext.RouteToSelfPath].Should().Be("self-path");
        }
    }
}
