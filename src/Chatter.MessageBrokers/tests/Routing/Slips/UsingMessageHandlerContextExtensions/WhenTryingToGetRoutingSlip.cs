using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing.Slips;
using FluentAssertions;
using Moq;
using System.Collections.Generic;
using System.Threading;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Routing.Slips.UsingMessageHandlerContextExtensions
{
    public class WhenTryingToGetRoutingSlip : Testing.Core.Context
    {
        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();

        public WhenTryingToGetRoutingSlip()
            => _bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");

        private MessageBrokerContext CreateContext(IDictionary<string, object> messageContext)
            => new MessageBrokerContext("message-id", new byte[] { 1 }, messageContext, "receiver-path", CancellationToken.None, _bodyConverter.Object);

        [Fact]
        public void MustReturnTrueAndSlipWhenBrokerContextCarriesSerializedSlip()
        {
            var slip = RoutingSlipBuilder.NewRoutingSlip(System.Guid.NewGuid())
                .WithRoute("first")
                .Build();
            var context = CreateContext(new Dictionary<string, object>());
            context.BrokeredMessage.WithRoutingSlip(slip);

            IMessageHandlerContext asHandlerContext = context;
            var found = asHandlerContext.TryGetRoutingSlip(out var foundSlip);

            found.Should().BeTrue();
            foundSlip.Should().NotBeNull();
            foundSlip.Route[0].DestinationPath.Should().Be("first");
        }

        [Fact]
        public void MustReturnFalseWhenContextIsNotBrokerContext()
        {
            var nonBrokerContext = new Mock<IMessageHandlerContext>().Object;

            var found = nonBrokerContext.TryGetRoutingSlip(out var foundSlip);

            found.Should().BeFalse();
            foundSlip.Should().BeNull();
        }

        [Fact]
        public void MustReturnFalseWhenNoSlipPresent()
        {
            var context = CreateContext(new Dictionary<string, object>());

            IMessageHandlerContext asHandlerContext = context;
            var found = asHandlerContext.TryGetRoutingSlip(out var foundSlip);

            found.Should().BeFalse();
            foundSlip.Should().BeNull();
        }

        [Fact]
        public void MustReturnFalseInsteadOfThrowingOnMalformedSlipValue()
        {
            // INVARIANT: TryGetRoutingSlip swallows deserialize exceptions. A non-string value under the
            // RoutingSlip key makes the (string) cast / deserialize throw, which must surface as false.
            var messageContext = new Dictionary<string, object>
            {
                [MessageContext.RoutingSlip] = 12345
            };
            var context = CreateContext(messageContext);

            IMessageHandlerContext asHandlerContext = context;
            var found = asHandlerContext.TryGetRoutingSlip(out var foundSlip);

            found.Should().BeFalse();
            foundSlip.Should().BeNull();
        }
    }
}
