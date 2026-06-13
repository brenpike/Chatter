using Chatter.MessageBrokers;
using Chatter.MessageBrokers.RabbitMQ;
using FluentAssertions;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Extensions.UsingInfrastructureTypesExtension
{
    // Pins InfrastructureTypesExtension.RabbitMq AS-IS: it ignores its receiver and returns the
    // RabbitMqMessageContext.InfrastructureType constant, so it resolves on a freshly constructed
    // InfrastructureTypes().
    public class WhenResolvingRabbitMq : Testing.Core.Context
    {
        [Fact]
        public void MustReturnRabbitMqInfrastructureTypeLiteral()
            => new InfrastructureTypes().RabbitMq().Should().Be("Chatter.Infrastructure.RabbitMQ");

        [Fact]
        public void MustReturnSameValueAsRabbitMqMessageContextInfrastructureType()
            => new InfrastructureTypes().RabbitMq().Should().Be(RabbitMqMessageContext.InfrastructureType);

        // INVARIANT: the result does not depend on receiver state; a new instance resolves the constant.
        [Fact]
        public void MustResolveOnNewlyConstructedInfrastructureTypes()
            => new InfrastructureTypes().RabbitMq().Should().NotBeNullOrEmpty();
    }
}
