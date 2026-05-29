using Chatter.MessageBrokers.Routing.Slips;
using FluentAssertions;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Routing.Slips.UsingRoutingSlip
{
    public class WhenRoutingToNextStep : Testing.Core.Context
    {
        [Fact]
        public void MustReturnNullWhenRouteIsEmpty()
        {
            var slip = RoutingSlipBuilder.NewRoutingSlip(System.Guid.NewGuid()).Build();
            slip.RouteToNextStep().Should().BeNull();
        }

        [Fact]
        public void MustNotAccumulateVisitedWhenRouteIsEmpty()
        {
            var slip = RoutingSlipBuilder.NewRoutingSlip(System.Guid.NewGuid()).Build();
            slip.RouteToNextStep();
            slip.Visited.Should().BeEmpty();
        }

        [Fact]
        public void MustReturnHeadDestinationPath()
        {
            var slip = RoutingSlipBuilder.NewRoutingSlip(System.Guid.NewGuid())
                .WithRoute("first")
                .WithRoute("second")
                .Build();

            slip.RouteToNextStep().Should().Be("first");
        }

        [Fact]
        public void MustRemoveHeadFromRouteInPlace()
        {
            var slip = RoutingSlipBuilder.NewRoutingSlip(System.Guid.NewGuid())
                .WithRoute("first")
                .WithRoute("second")
                .Build();

            slip.RouteToNextStep();

            slip.Route.Should().HaveCount(1);
            slip.Route[0].DestinationPath.Should().Be("second");
        }

        [Fact]
        public void MustAppendHeadToVisited()
        {
            var slip = RoutingSlipBuilder.NewRoutingSlip(System.Guid.NewGuid())
                .WithRoute("first")
                .WithRoute("second")
                .Build();

            slip.RouteToNextStep();

            slip.Visited.Should().HaveCount(1);
            slip.Visited[0].DestinationPath.Should().Be("first");
        }

        [Fact]
        public void MustAccumulateVisitedInOrderAcrossRepeatedCallsToExhaustion()
        {
            var slip = RoutingSlipBuilder.NewRoutingSlip(System.Guid.NewGuid())
                .WithRoute("first")
                .WithRoute("second")
                .WithRoute("third")
                .Build();

            slip.RouteToNextStep().Should().Be("first");
            slip.RouteToNextStep().Should().Be("second");
            slip.RouteToNextStep().Should().Be("third");

            slip.Route.Should().BeEmpty();
            slip.Visited.Should().HaveCount(3);
            slip.Visited[0].DestinationPath.Should().Be("first");
            slip.Visited[1].DestinationPath.Should().Be("second");
            slip.Visited[2].DestinationPath.Should().Be("third");
        }

        [Fact]
        public void MustReturnNullOnceRouteIsExhausted()
        {
            var slip = RoutingSlipBuilder.NewRoutingSlip(System.Guid.NewGuid())
                .WithRoute("only")
                .Build();

            slip.RouteToNextStep();
            slip.RouteToNextStep().Should().BeNull();
            slip.Visited.Should().HaveCount(1);
        }
    }
}
