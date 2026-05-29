using Chatter.MessageBrokers;
using Chatter.MessageBrokers.AzureServiceBus;
using FluentAssertions;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.UsingASBMessageContext
{
    // INVARIANT: these header-key strings are the wire format; pinning their exact values
    // (each composed from MessageContext.ChatterBaseHeader) guards against an accidental
    // rename silently breaking message interop.
    public class WhenReadingHeaderKeys : Testing.Core.Context
    {
        [Fact]
        public void MustPinInfrastructureType()
            => ASBMessageContext.InfrastructureType
                .Should().Be($"{MessageContext.ChatterBaseHeader}.Infrastructure.AzureServiceBus");

        [Fact]
        public void MustPinScheduledEnqueueTimeUtc()
            => ASBMessageContext.ScheduledEnqueueTimeUtc
                .Should().Be($"{MessageContext.ChatterBaseHeader}.ScheduledEnqueueTimeUtc");

        [Fact]
        public void MustPinTo()
            => ASBMessageContext.To
                .Should().Be($"{MessageContext.ChatterBaseHeader}.To");

        [Fact]
        public void MustPinViaPartitionKey()
            => ASBMessageContext.ViaPartitionKey
                .Should().Be($"{MessageContext.ChatterBaseHeader}.ViaPartitionKey");

        [Fact]
        public void MustPinPartitionKey()
            => ASBMessageContext.PartitionKey
                .Should().Be($"{MessageContext.ChatterBaseHeader}.PartitionKey");
    }
}
