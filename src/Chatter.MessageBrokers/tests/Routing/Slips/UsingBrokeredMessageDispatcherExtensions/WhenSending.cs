using Chatter.CQRS.Commands;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing.Options;
using Chatter.MessageBrokers.Routing.Slips;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Moq;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Routing.Slips.UsingBrokeredMessageDispatcherExtensions
{
    public class WhenSending : Testing.Core.Context
    {
        private readonly Mock<IBrokeredMessageDispatcher> _dispatcher = new Mock<IBrokeredMessageDispatcher>();
        private readonly FakeMessage _message = new FakeMessage();

        public WhenSending()
            => _dispatcher
                .Setup(d => d.Send(It.IsAny<FakeMessage>(), It.IsAny<string>(), It.IsAny<TransactionContext>(), It.IsAny<SendOptions>()))
                .Returns(Task.CompletedTask);

        private static RoutingSlip CreateSlip()
            => RoutingSlipBuilder.NewRoutingSlip(System.Guid.NewGuid())
                .WithRoute("first")
                .WithRoute("second")
                .Build();

        [Fact]
        public async Task MustRouteSlipToNextStepShrinkingRouteAndGrowingVisited()
        {
            var slip = CreateSlip();

            await _dispatcher.Object.Send(_message, slip);

            slip.Route.Should().HaveCount(1);
            slip.Route[0].DestinationPath.Should().Be("second");
            slip.Visited.Should().HaveCount(1);
            slip.Visited[0].DestinationPath.Should().Be("first");
        }

        [Fact]
        public async Task MustDispatchToHeadDestination()
        {
            var slip = CreateSlip();

            await _dispatcher.Object.Send(_message, slip);

            _dispatcher.Verify(
                d => d.Send(_message, "first", It.IsAny<TransactionContext>(), It.IsAny<SendOptions>()),
                Times.Once);
        }

        [Fact]
        public async Task MustAttachSlipToSendOptionsMessageContext()
        {
            var slip = CreateSlip();
            SendOptions capturedOptions = null;
            _dispatcher
                .Setup(d => d.Send(_message, It.IsAny<string>(), It.IsAny<TransactionContext>(), It.IsAny<SendOptions>()))
                .Callback<FakeMessage, string, TransactionContext, SendOptions>((_, _, _, options) => capturedOptions = options)
                .Returns(Task.CompletedTask);

            await _dispatcher.Object.Send(_message, slip);

            capturedOptions.Should().NotBeNull();
            capturedOptions.MessageContext.Should().ContainKey(MessageContext.RoutingSlip);
        }

        [Fact]
        public async Task MustCreateDefaultSendOptionsWhenNullPassed()
        {
            var slip = CreateSlip();
            SendOptions capturedOptions = null;
            _dispatcher
                .Setup(d => d.Send(_message, It.IsAny<string>(), It.IsAny<TransactionContext>(), It.IsAny<SendOptions>()))
                .Callback<FakeMessage, string, TransactionContext, SendOptions>((_, _, _, options) => capturedOptions = options)
                .Returns(Task.CompletedTask);

            await _dispatcher.Object.Send(_message, slip, options: null);

            capturedOptions.Should().NotBeNull();
        }

        private class FakeMessage : ICommand { }
    }
}
