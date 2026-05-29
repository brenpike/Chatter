using Chatter.MessageBrokers.AzureServiceBus;
using FluentAssertions;
using Microsoft.Azure.ServiceBus;
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

        [Fact]
        public void MustReturnSubscriptionPathWhenSendingAndReceiverDiffer()
            => _sut.GetMessageReceivingPath("topic", "subscriber")
                   .Should().Be(EntityNameHelper.FormatSubscriptionPath("topic", "subscriber"));

        [Fact]
        public void MustReturnRulePathFromEntityNameHelper()
            => _sut.GetMessageReceivingRulePath("topic", "subscriber", "rule")
                   .Should().Be(EntityNameHelper.FormatRulePath("topic", "subscriber", "rule"));

        [Fact]
        public void MustReturnSendingPathIdentity()
            => _sut.GetMessageSendingPath("sending-path").Should().Be("sending-path");
    }
}
