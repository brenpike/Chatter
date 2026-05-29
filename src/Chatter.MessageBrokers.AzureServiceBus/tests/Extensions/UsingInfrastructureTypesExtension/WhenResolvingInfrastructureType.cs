using Chatter.MessageBrokers;
using Chatter.MessageBrokers.AzureServiceBus;
using FluentAssertions;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Extensions.UsingInfrastructureTypesExtension
{
    public class WhenResolvingInfrastructureType : Testing.Core.Context
    {
        [Fact]
        public void MustReturnAsbInfrastructureType()
            => new InfrastructureTypes().AzureServiceBus()
                .Should().Be(ASBMessageContext.InfrastructureType);
    }
}
