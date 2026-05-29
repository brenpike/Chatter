using Chatter.MessageBrokers.Receiving;
using FluentAssertions;
using Moq;
using System.Collections.Generic;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Receiving.UsingInboundBrokeredMessage
{
    public class WhenReadingContextProperties : Testing.Core.Context
    {
        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();

        public WhenReadingContextProperties()
            => _bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");

        private InboundBrokeredMessage CreateSut(IDictionary<string, object> messageContext)
            => new InboundBrokeredMessage("message-id", new byte[] { 1 }, messageContext, "receiver-path", _bodyConverter.Object);

        [Fact]
        public void MustReportIsErrorFalseWhenKeyMissing()
            => CreateSut(new Dictionary<string, object>()).IsError.Should().BeFalse();

        [Fact]
        public void MustReportIsErrorTrueWhenKeySetTrue()
        {
            var context = new Dictionary<string, object> { [MessageContext.IsError] = true };
            CreateSut(context).IsError.Should().BeTrue();
        }

        [Fact]
        public void MustReportIsSuccessAsNegationOfIsError()
        {
            var errored = new Dictionary<string, object> { [MessageContext.IsError] = true };
            CreateSut(errored).IsSuccess.Should().BeFalse();
            CreateSut(new Dictionary<string, object>()).IsSuccess.Should().BeTrue();
        }

        [Fact]
        public void MustReadViaFromMessageContext()
        {
            var context = new Dictionary<string, object> { [MessageContext.Via] = "receiver-a" };
            CreateSut(context).Via.Should().Be("receiver-a");
        }

        [Fact]
        public void MustReturnNullViaWhenMissing()
            => CreateSut(new Dictionary<string, object>()).Via.Should().BeNull();
    }
}
