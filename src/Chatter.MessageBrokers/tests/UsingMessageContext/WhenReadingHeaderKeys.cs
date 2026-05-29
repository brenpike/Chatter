using FluentAssertions;
using Xunit;

namespace Chatter.MessageBrokers.Tests.UsingMessageContext
{
    // INVARIANT: these header-key strings are the wire format; pinning their exact
    // values guards against an accidental rename silently breaking message interop.
    public class WhenReadingHeaderKeys : Testing.Core.Context
    {
        [Fact]
        public void MustPinChatterBaseHeader()
            => MessageContext.ChatterBaseHeader.Should().Be("Chatter");

        [Fact]
        public void MustPinVia()
            => MessageContext.Via.Should().Be("Chatter.Via");

        [Fact]
        public void MustPinFailureDetails()
            => MessageContext.FailureDetails.Should().Be("Chatter.FailureDetails");

        [Fact]
        public void MustPinFailureDescription()
            => MessageContext.FailureDescription.Should().Be("Chatter.FailureDescription");

        [Fact]
        public void MustPinGroupId()
            => MessageContext.GroupId.Should().Be("Chatter.GroupId");

        [Fact]
        public void MustPinSubject()
            => MessageContext.Subject.Should().Be("Chatter.Subject");

        [Fact]
        public void MustPinContentType()
            => MessageContext.ContentType.Should().Be("Chatter.ContentType");

        [Fact]
        public void MustPinCorrelationId()
            => MessageContext.CorrelationId.Should().Be("Chatter.CorrelationId");

        [Fact]
        public void MustPinTimeToLive()
            => MessageContext.TimeToLive.Should().Be("Chatter.TimeToLive");

        [Fact]
        public void MustPinExpiryTimeUtc()
            => MessageContext.ExpiryTimeUtc.Should().Be("Chatter.ExpiryTimeUtc");

        [Fact]
        public void MustPinIsError()
            => MessageContext.IsError.Should().Be("Chatter.IsError");

        [Fact]
        public void MustPinRoutingSlip()
            => MessageContext.RoutingSlip.Should().Be("Chatter.Routing.Slip");

        [Fact]
        public void MustPinRouteToSelfPath()
            => MessageContext.RouteToSelfPath.Should().Be("Chatter.Routing.RouteToSelfPath");

        [Fact]
        public void MustPinReplyToAddress()
            => MessageContext.ReplyToAddress.Should().Be("Chatter.Routing.ReplyTo");

        [Fact]
        public void MustPinReplyToGroupId()
            => MessageContext.ReplyToGroupId.Should().Be("Chatter.Routing.ReplyToGroupId");

        [Fact]
        public void MustPinInfrastructureType()
            => MessageContext.InfrastructureType.Should().Be("Chatter.Infrastructure.Type");

        [Fact]
        public void MustPinReceiveAttempts()
            => MessageContext.ReceiveAttempts.Should().Be("Chatter.Infrastructure.ReceiveAttempts");
    }
}
