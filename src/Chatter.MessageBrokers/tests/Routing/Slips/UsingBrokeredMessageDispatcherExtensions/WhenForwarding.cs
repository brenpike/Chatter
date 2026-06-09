using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing.Slips;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Moq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Routing.Slips.UsingBrokeredMessageDispatcherExtensions
{
    public class WhenForwarding : Testing.Core.Context
    {
        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
        private readonly Mock<IBrokeredMessageDispatcher> _dispatcher = new Mock<IBrokeredMessageDispatcher>();

        public WhenForwarding()
            => _bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");

        private InboundBrokeredMessage CreateInbound()
            => new InboundBrokeredMessage("message-id", new byte[] { 1 }, new Dictionary<string, object>(), "receiver-path", _bodyConverter.Object);

        private static RoutingSlip CreateSlip()
            => RoutingSlipBuilder.NewRoutingSlip(System.Guid.NewGuid())
                .WithRoute("first")
                .WithRoute("second")
                .Build();

        [Fact]
        public async Task MustRouteSlipToNextStepShrinkingRouteAndGrowingVisited()
        {
            var slip = CreateSlip();

            await _dispatcher.Object.Forward(CreateInbound(), slip, new TransactionContext());

            slip.Route.Should().HaveCount(1);
            slip.Route[0].DestinationPath.Should().Be("second");
            slip.Visited.Should().HaveCount(1);
            slip.Visited[0].DestinationPath.Should().Be("first");
        }

        [Fact]
        public async Task MustForwardToHeadDestination()
        {
            var slip = CreateSlip();
            var inbound = CreateInbound();
            var transactionContext = new TransactionContext();

            await _dispatcher.Object.Forward(inbound, slip, transactionContext);

            _dispatcher.Verify(d => d.Forward(inbound, "first", transactionContext), Times.Once);
        }

        [Fact]
        public async Task MustAttachSlipToInboundBrokeredMessage()
        {
            var slip = CreateSlip();
            var inbound = CreateInbound();

            await _dispatcher.Object.Forward(inbound, slip, new TransactionContext());

            inbound.MessageContext.Should().ContainKey(MessageContext.RoutingSlip);
        }
    }
}
