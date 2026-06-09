using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing.Options;
using Chatter.MessageBrokers.Routing.Slips;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Moq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Routing.Slips.UsingMessageHandlerContextExtensions
{
    public class WhenSendingAndForwarding : Testing.Core.Context
    {
        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
        private readonly Mock<IBrokeredMessageDispatcher> _dispatcher = new Mock<IBrokeredMessageDispatcher>();
        private readonly FakeMessage _message = new FakeMessage();

        public WhenSendingAndForwarding()
            => _bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");

        private MessageBrokerContext CreateContext()
            => new MessageBrokerContext("message-id", new byte[] { 1 }, new Dictionary<string, object>(), "receiver-path", CancellationToken.None, _bodyConverter.Object);

        private void IncludeDispatcher(MessageBrokerContext context)
            => context.Container.Include<IExternalDispatcher>(_dispatcher.Object);

        private static RoutingSlip CreateSlip()
            => RoutingSlipBuilder.NewRoutingSlip(System.Guid.NewGuid())
                .WithRoute("first")
                .WithRoute("second")
                .Build();

        [Fact]
        public async Task MustDelegateSendToDispatcherWhenPresent()
        {
            var context = CreateContext();
            IncludeDispatcher(context);
            var slip = CreateSlip();

            await context.Send(_message, slip);

            _dispatcher.Verify(
                d => d.Send(_message, "first", It.IsAny<TransactionContext>(), It.IsAny<SendOptions>()),
                Times.Once);
        }

        [Fact]
        public async Task MustReturnCompletedTaskWithoutDispatchingSendWhenNoDispatcherPresent()
        {
            var context = CreateContext();
            var slip = CreateSlip();

            await context.Send(_message, slip);

            _dispatcher.Invocations.Should().BeEmpty();
        }

        [Fact]
        public async Task MustForwardThroughDispatcherWhenPresentAndHeadIsNonEmpty()
        {
            var context = CreateContext();
            IncludeDispatcher(context);
            var slip = CreateSlip();

            await context.Forward(slip);

            _dispatcher.Verify(
                d => d.Forward(It.IsAny<InboundBrokeredMessage>(), "first", It.IsAny<TransactionContext>()),
                Times.Once);
        }

        [Fact]
        public async Task MustNotForwardWhenRouteIsEmpty()
        {
            var context = CreateContext();
            IncludeDispatcher(context);
            var slip = RoutingSlipBuilder.NewRoutingSlip(System.Guid.NewGuid()).Build();

            await context.Forward(slip);

            _dispatcher.Invocations.Should().BeEmpty();
        }

        [Fact]
        public async Task MustNotForwardWhenNoDispatcherPresent()
        {
            var context = CreateContext();
            var slip = CreateSlip();

            await context.Forward(slip);

            _dispatcher.Invocations.Should().BeEmpty();
        }

        private class FakeMessage : ICommand { }
    }
}
