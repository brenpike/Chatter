using Chatter.MessageBrokers.Routing.Slips;
using FluentAssertions;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Routing.Slips.UsingRoutingStepBuilder
{
    public class WhenBuilding : Testing.Core.Context
    {
        [Fact]
        public void MustSetDestinationPathOnRoutingStepFromWithStep()
        {
            var step = RoutingStepBuilder.WithStep("destination").Build();
            step.DestinationPath.Should().Be("destination");
        }

        [Fact]
        public void MustRoundTripNullDestinationPathWithoutValidation()
        {
            var step = RoutingStepBuilder.WithStep(null).Build();
            step.DestinationPath.Should().BeNull();
        }

        [Fact]
        public void MustRoundTripEmptyDestinationPathWithoutValidation()
        {
            var step = RoutingStepBuilder.WithStep(string.Empty).Build();
            step.DestinationPath.Should().Be(string.Empty);
        }

        [Fact]
        public void MustProduceDistinctRoutingStepInstancesPerBuild()
        {
            var builder = RoutingStepBuilder.WithStep("destination");
            builder.Build().Should().NotBeSameAs(builder.Build());
        }
    }
}
