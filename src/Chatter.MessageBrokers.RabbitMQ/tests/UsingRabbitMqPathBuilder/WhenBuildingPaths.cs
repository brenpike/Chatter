using Chatter.MessageBrokers;
using FluentAssertions;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.UsingRabbitMqPathBuilder
{
    // Pins the RabbitMqPathBuilder default-exchange convention: RabbitMQ addresses receivers by queue name
    // (routing key = Destination = queue name), so every path is the receiver/queue name verbatim — there is no
    // topic/subscription or rule entity to compose into the path. The three methods are EXPLICIT interface
    // implementations on an internal type, so the test instantiates the concrete builder and casts to
    // IBrokeredMessagePathBuilder to reach them. GetMessageSendingPath returns its input; the receiving and
    // receiving-rule paths return the receiver path verbatim (sending path and rule name ignored).
    public class WhenBuildingPaths : Testing.Core.Context
    {
        private static IBrokeredMessagePathBuilder NewBuilder()
            => new RabbitMqPathBuilder();

        [Fact]
        public void MustReturnSendingPathVerbatim()
        {
            var builder = NewBuilder();

            builder.GetMessageSendingPath("orders-queue").Should().Be("orders-queue",
                "RabbitMQ sends to the queue name verbatim under the default-exchange convention");
        }

        [Fact]
        public void MustReturnReceiverPathForReceivingPathIgnoringSendingPath()
        {
            var builder = NewBuilder();

            builder.GetMessageReceivingPath("orders-sending", "orders-receiver").Should().Be("orders-receiver",
                "the receiving path is the receiver/queue name verbatim; the sending path is not composed in");
        }

        [Fact]
        public void MustReturnReceiverPathForReceivingRulePathIgnoringSendingPathAndRule()
        {
            var builder = NewBuilder();

            builder.GetMessageReceivingRulePath("orders-sending", "orders-receiver", "some-rule").Should().Be("orders-receiver",
                "there is no rule entity in RabbitMQ addressing; the rule name and sending path are ignored");
        }
    }
}
