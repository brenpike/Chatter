using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.Pipeline;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing.Options;
using Chatter.MessageBrokers.Routing.Slips;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Routing.Slips.UsingRoutingSlipBehavior
{
    public class WhenHandling : Testing.Core.Context
    {
        private readonly Mock<IBrokeredMessageBodyConverter> _bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
        private readonly Mock<IBrokeredMessageDispatcher> _dispatcher = new Mock<IBrokeredMessageDispatcher>();
        private readonly RoutingSlipBehavior<FakeMessage> _sut
            = new RoutingSlipBehavior<FakeMessage>(NullLogger<RoutingSlipBehavior<FakeMessage>>.Instance);
        private readonly FakeMessage _message = new FakeMessage();

        public WhenHandling()
            => _bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");

        // The behavior reads the slip via a FRESH deserialize from the message context, so the slip
        // that gets advanced and sent is NOT the builder instance. Seed the serialized slip into the
        // message-context dictionary through the production WithRoutingSlip serialize seam.
        private MessageBrokerContext CreateBrokerContextWithSerializedSlip(RoutingSlip slip)
        {
            var messageContext = new Dictionary<string, object>();
            var context = CreateBrokerContext(messageContext);
            context.BrokeredMessage.WithRoutingSlip(slip);
            return context;
        }

        private MessageBrokerContext CreateBrokerContext(IDictionary<string, object> messageContext)
            => new MessageBrokerContext("message-id", new byte[] { 1 }, messageContext, "receiver-path", CancellationToken.None, _bodyConverter.Object);

        private void IncludeDispatcher(MessageBrokerContext context)
            => context.Container.Include<IExternalDispatcher>(_dispatcher.Object);

        [Fact]
        public async Task MustInvokeNextOnceWithoutSendingWhenContextIsNotMessageBrokerContext()
        {
            var nonBrokerContext = new Mock<IMessageHandlerContext>().Object;
            var nextCount = 0;
            CommandHandlerDelegate next = () => { nextCount++; return Task.CompletedTask; };

            await _sut.Handle(_message, nonBrokerContext, next);

            nextCount.Should().Be(1);
            _dispatcher.Invocations.Should().BeEmpty();
        }

        [Fact]
        public async Task MustInvokeNextOnceWithoutSendingWhenNoRoutingSlipPresent()
        {
            var context = CreateBrokerContext(new Dictionary<string, object>());
            IncludeDispatcher(context);
            var nextCount = 0;
            CommandHandlerDelegate next = () => { nextCount++; return Task.CompletedTask; };

            await _sut.Handle(_message, context, next);

            nextCount.Should().Be(1);
            _dispatcher.Invocations.Should().BeEmpty();
        }

        [Fact]
        public async Task MustInvokeNextOnceAndIncludeSlipInContainerWithoutSendingWhenRouteIsEmpty()
        {
            var slip = RoutingSlipBuilder.NewRoutingSlip(System.Guid.NewGuid()).Build();
            var context = CreateBrokerContextWithSerializedSlip(slip);
            IncludeDispatcher(context);
            var nextCount = 0;
            CommandHandlerDelegate next = () => { nextCount++; return Task.CompletedTask; };

            await _sut.Handle(_message, context, next);

            nextCount.Should().Be(1);
            context.Container.TryGet<RoutingSlip>(out _).Should().BeTrue();
            _dispatcher.Invocations.Should().BeEmpty();
        }

        [Fact]
        public async Task MustInvokeNextOnceAndForwardMessageToNextDestinationWhenRouteIsNonEmpty()
        {
            var slip = RoutingSlipBuilder.NewRoutingSlip(System.Guid.NewGuid())
                .WithRoute("first")
                .WithRoute("second")
                .Build();
            var context = CreateBrokerContextWithSerializedSlip(slip);
            IncludeDispatcher(context);
            var nextCount = 0;
            CommandHandlerDelegate next = () => { nextCount++; return Task.CompletedTask; };

            await _sut.Handle(_message, context, next);

            nextCount.Should().Be(1);
            // The slip path routes via the IBrokeredMessageDispatcher.Send(TMessage, string, TransactionContext, SendOptions)
            // overload to the head destination. Verify THAT specific overload (4 overloads exist).
            _dispatcher.Verify(
                d => d.Send(_message, "first", It.IsAny<TransactionContext>(), It.IsAny<SendOptions>()),
                Times.Once);
        }

        private class FakeMessage : ICommand { }
    }
}
