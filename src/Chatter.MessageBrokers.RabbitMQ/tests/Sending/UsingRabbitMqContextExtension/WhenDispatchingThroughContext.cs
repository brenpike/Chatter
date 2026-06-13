using Chatter.CQRS.Context;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.RabbitMQ;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Moq;
using System.Collections.Generic;
using System.Threading;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Sending.UsingRabbitMqContextExtension
{
    // Pins the RabbitMQ MessageHandlerContextExtensions: RabbitMq() stamps the RabbitMQ InfrastructureType on
    // the outbound message (and returns null for a non-IMessageBrokerContext), and WithRabbitMqRouting stamps
    // TargetExchange + RoutingKey onto the outbound message context for the sender to read at dispatch time.
    public class WhenDispatchingThroughContext : Testing.Core.Context
    {
        private const string Destination = "receiver-path";

        private static MessageBrokerContext CreateRealContext()
        {
            var bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
            bodyConverter.SetupGet(c => c.ContentType).Returns("application/json; charset=utf-8");
            return new MessageBrokerContext("message-id", new byte[] { 1 }, new Dictionary<string, object>(), Destination, CancellationToken.None, bodyConverter.Object);
        }

        private static OutboundBrokeredMessage CreateOutbound()
        {
            var bodyConverter = new Mock<IBrokeredMessageBodyConverter>();
            bodyConverter.SetupGet(c => c.ContentType).Returns("application/json; charset=utf-8");
            return new OutboundBrokeredMessage("message-id", new byte[] { 1 }, new Dictionary<string, object>(), Destination, bodyConverter.Object);
        }

        [Fact]
        public void MustStampRabbitMqInfrastructureTypeOnRealContext()
        {
            IMessageBrokerContext context = CreateRealContext();

            context.RabbitMq();

            context.BrokeredMessage.MessageContext[MessageContext.InfrastructureType]
                .Should().Be(RabbitMqMessageContext.InfrastructureType);
        }

        [Fact]
        public void MustReturnSameContextFromRabbitMq()
        {
            IMessageBrokerContext context = CreateRealContext();
            context.RabbitMq().Should().BeSameAs(context);
        }

        [Fact]
        public void MustReturnNullWhenContextIsNotMessageBrokerContext()
            => ((IMessageHandlerContext)Mock.Of<IMessageHandlerContext>()).RabbitMq()
               .Should().BeNull();

        [Fact]
        public void MustStampTargetExchangeAndRoutingKeyFromWithRabbitMqRouting()
        {
            var outbound = CreateOutbound();

            outbound.WithRabbitMqRouting("orders-exchange", "orders.created");

            outbound.MessageContext[RabbitMqMessageContext.TargetExchange].Should().Be("orders-exchange");
            outbound.MessageContext[RabbitMqMessageContext.RoutingKey].Should().Be("orders.created");
        }

        [Fact]
        public void MustReturnSameOutboundFromWithRabbitMqRouting()
        {
            var outbound = CreateOutbound();
            outbound.WithRabbitMqRouting("ex", "rk").Should().BeSameAs(outbound);
        }
    }
}
