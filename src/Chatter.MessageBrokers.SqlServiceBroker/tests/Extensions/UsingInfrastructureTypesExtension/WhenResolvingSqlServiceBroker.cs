using FluentAssertions;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.Extensions.UsingInfrastructureTypesExtension
{
    // Behavior-pinning tests: characterize InfrastructureTypesExtension.SqlServiceBroker AS-IS.
    // The extension ignores its receiver entirely and returns the SSBMessageContext.InfrastructureType
    // constant, so it works on a freshly constructed InfrastructureTypes().
    public class WhenResolvingSqlServiceBroker : Testing.Core.Context
    {
        [Fact]
        public void MustReturnSqlServiceBrokerInfrastructureTypeLiteral()
            => new InfrastructureTypes().SqlServiceBroker().Should().Be("Chatter.Infrastructure.SqlServiceBroker");

        [Fact]
        public void MustReturnSameValueAsSsbMessageContextInfrastructureType()
            => new InfrastructureTypes().SqlServiceBroker().Should().Be(SSBMessageContext.InfrastructureType);

        // INVARIANT: the result does not depend on receiver state; a new instance resolves the constant.
        [Fact]
        public void MustResolveOnNewlyConstructedInfrastructureTypes()
            => new InfrastructureTypes().SqlServiceBroker().Should().NotBeNullOrEmpty();
    }
}
