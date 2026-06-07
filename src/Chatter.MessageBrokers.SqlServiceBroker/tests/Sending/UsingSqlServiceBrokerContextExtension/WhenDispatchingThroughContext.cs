using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.Events;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Routing.Options;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Moq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Sending.UsingSqlServiceBrokerContextExtension
{
    // Characterization tests pinning the CURRENT behavior of the SqlServiceBroker() extension on
    // IMessageHandlerContext BEFORE the dispatcher type is collapsed (refactor #126 / STEP-005).
    // Locals are bound via var / IMessageBrokerContext so they survive the upcoming return-type
    // change with at most a trivial edit; nothing here binds to ISqlServiceBrokerContextDispatcher.
    public class WhenDispatchingThroughContext : Testing.Core.Context
    {
        private static MessageBrokerContext CreateRealContext()
        {
            var bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
            bodyConverter.SetupGet(c => c.ContentType).Returns("application/json");
            return new MessageBrokerContext("message-id", new byte[] { 1 }, new Dictionary<string, object>(), "receiver-path", CancellationToken.None, bodyConverter.Object);
        }

        [Fact]
        public void MustStampSqlServiceBrokerInfrastructureTypeOnRealContext()
        {
            IMessageBrokerContext context = CreateRealContext();

            context.SqlServiceBroker();

            context.BrokeredMessage.MessageContext[MessageContext.InfrastructureType]
                .Should().Be(SSBMessageContext.InfrastructureType);
        }

        [Fact]
        public void MustReturnNullWhenContextIsNotMessageBrokerContext()
        {
            ((IMessageHandlerContext)Mock.Of<IMessageHandlerContext>()).SqlServiceBroker()
                .Should().BeNull();
        }

        [Fact]
        public async Task MustForwardSendWithDestinationToContext()
        {
            var mock = new Mock<IMessageBrokerContext>();
            mock.SetupGet(c => c.BrokeredMessage).Returns(CreateRealContext().BrokeredMessage);
            var command = Mock.Of<ICommand>();
            var options = new SendOptions();
            mock.Setup(c => c.Send(command, "destination-path", options)).Returns(Task.CompletedTask);

            await mock.Object.SqlServiceBroker().Send(command, "destination-path", options);

            mock.Verify(c => c.Send(command, "destination-path", options), Times.Once);
        }

        [Fact]
        public async Task MustForwardSendWithoutDestinationToContext()
        {
            var mock = new Mock<IMessageBrokerContext>();
            mock.SetupGet(c => c.BrokeredMessage).Returns(CreateRealContext().BrokeredMessage);
            var command = Mock.Of<ICommand>();
            var options = new SendOptions();
            mock.Setup(c => c.Send(command, options)).Returns(Task.CompletedTask);

            await mock.Object.SqlServiceBroker().Send(command, options);

            mock.Verify(c => c.Send(command, options), Times.Once);
        }

        [Fact]
        public async Task MustForwardPublishWithDestinationToContext()
        {
            var mock = new Mock<IMessageBrokerContext>();
            mock.SetupGet(c => c.BrokeredMessage).Returns(CreateRealContext().BrokeredMessage);
            var @event = Mock.Of<IEvent>();
            var options = new PublishOptions();
            mock.Setup(c => c.Publish(@event, "destination-path", options)).Returns(Task.CompletedTask);

            await mock.Object.SqlServiceBroker().Publish(@event, "destination-path", options);

            mock.Verify(c => c.Publish(@event, "destination-path", options), Times.Once);
        }

        [Fact]
        public async Task MustForwardPublishWithoutDestinationToContext()
        {
            var mock = new Mock<IMessageBrokerContext>();
            mock.SetupGet(c => c.BrokeredMessage).Returns(CreateRealContext().BrokeredMessage);
            var @event = Mock.Of<IEvent>();
            var options = new PublishOptions();
            mock.Setup(c => c.Publish(@event, options)).Returns(Task.CompletedTask);

            await mock.Object.SqlServiceBroker().Publish(@event, options);

            mock.Verify(c => c.Publish(@event, options), Times.Once);
        }

        [Fact]
        public async Task MustForwardPublishBatchToContext()
        {
            var mock = new Mock<IMessageBrokerContext>();
            mock.SetupGet(c => c.BrokeredMessage).Returns(CreateRealContext().BrokeredMessage);
            var events = new[] { Mock.Of<IEvent>() };
            var options = new PublishOptions();
            mock.Setup(c => c.Publish(events, options)).Returns(Task.CompletedTask);

            await mock.Object.SqlServiceBroker().Publish(events, options);

            mock.Verify(c => c.Publish(events, options), Times.Once);
        }

        [Fact]
        public async Task MustForwardForwardToContext()
        {
            var mock = new Mock<IMessageBrokerContext>();
            mock.SetupGet(c => c.BrokeredMessage).Returns(CreateRealContext().BrokeredMessage);
            mock.Setup(c => c.Forward("forward-destination")).Returns(Task.CompletedTask);

            await mock.Object.SqlServiceBroker().Forward("forward-destination");

            mock.Verify(c => c.Forward("forward-destination"), Times.Once);
        }
    }
}
