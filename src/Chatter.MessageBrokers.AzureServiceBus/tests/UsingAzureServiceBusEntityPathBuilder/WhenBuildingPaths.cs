using Chatter.MessageBrokers.AzureServiceBus;
using FluentAssertions;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.UsingAzureServiceBusEntityPathBuilder
{
    // AzureServiceBusEntityPathBuilder is internal and implements IBrokeredMessagePathBuilder
    // via explicit interface members; the SUT is referenced through the interface.
    public class WhenBuildingPaths : Testing.Core.Context
    {
        private readonly IBrokeredMessagePathBuilder _sut = new AzureServiceBusEntityPathBuilder();

        [Fact]
        public void MustReturnNullWhenReceiverPathIsNull()
            => _sut.GetMessageReceivingPath("sending", null).Should().BeNull();

        [Fact]
        public void MustReturnNullWhenReceiverPathIsWhitespace()
            => _sut.GetMessageReceivingPath("sending", "   ").Should().BeNull();

        [Fact]
        public void MustReturnSendingPathWhenSendingEqualsReceiver()
            => _sut.GetMessageReceivingPath("same", "same").Should().Be("same");

        [Fact]
        public void MustReturnReceiverPathWhenSendingIsBlankAndReceiverIsNotBlank()
            => _sut.GetMessageReceivingPath("", "receiver").Should().Be("receiver");

        // Expected path shapes reproduce Azure Service Bus' canonical subscription/rule path format
        // (topic/Subscriptions/<sub> and .../Rules/<rule>). The SDK's EntityNameFormatter is internal
        // in Azure.Messaging.ServiceBus 7.20.1, so the literals are pinned directly.
        [Fact]
        public void MustReturnSubscriptionPathWhenSendingAndReceiverDiffer()
            => _sut.GetMessageReceivingPath("topic", "subscriber")
                   .Should().Be("topic/Subscriptions/subscriber");

        [Fact]
        public void MustReturnRulePathInSubscriptionRuleFormat()
            => _sut.GetMessageReceivingRulePath("topic", "subscriber", "rule")
                   .Should().Be("topic/Subscriptions/subscriber/Rules/rule");

        [Fact]
        public void MustReturnSendingPathIdentity()
            => _sut.GetMessageSendingPath("sending-path").Should().Be("sending-path");
    }
}
