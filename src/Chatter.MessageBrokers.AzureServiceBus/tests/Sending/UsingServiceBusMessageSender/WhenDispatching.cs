using Chatter.MessageBrokers.AzureServiceBus.Options;
using Chatter.MessageBrokers.AzureServiceBus.Sending;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using System;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Sending.UsingServiceBusMessageSender
{
    // Characterization tests pinning ServiceBusMessageSender guard branches that throw BEFORE any
    // MessageSender is checked out of the pool (which would open a live connection): the constructor
    // null guard and the single-message Dispatch null/empty-destination ArgumentNullException guards.
    public class WhenDispatching : Testing.Core.Context
    {
        private static BrokeredMessageSenderPool CreatePool()
        {
            var serviceBusOptions = new ServiceBusOptions
            {
                ConnectionString = "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=key;SharedAccessKey=secret",
                TokenProvider = new NullTokenProvider(),
            };
            return new BrokeredMessageSenderPool(serviceBusOptions);
        }

        [Fact]
        public void MustThrowWhenPoolNull()
        {
            Action act = () => new ServiceBusMessageSender(null);
            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustThrowWhenBrokeredMessageNull()
        {
            var sut = new ServiceBusMessageSender(CreatePool());
            Action act = () => sut.Dispatch((OutboundBrokeredMessage)null, null);
            act.Should().Throw<ArgumentNullException>();
        }

        // NOTE (characterization finding): ServiceBusMessageSender.Dispatch contains a
        // null/empty-Destination ArgumentNullException guard, but it is unreachable through the
        // public OutboundBrokeredMessage type: that type's own ctor already rejects a null/empty
        // destination with ArgumentException before a sender can ever see one. The sender's
        // destination guard is therefore shadowed and cannot be pinned behavior-preservingly via
        // the real message type, so no test asserts it. Preserve the guard as-is during the refactor.
    }
}
