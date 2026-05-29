using Chatter.MessageBrokers.Routing.Slips;
using FluentAssertions;
using System;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Routing.Slips.UsingRoutingSlipBuilder
{
    public class WhenBuilding : Testing.Core.Context
    {
        private readonly Guid _id = Guid.NewGuid();

        [Fact]
        public void MustSetRoutingSlipIdFromNewRoutingSlip()
        {
            var slip = RoutingSlipBuilder.NewRoutingSlip(_id).Build();
            slip.Id.Should().Be(_id);
        }

        [Fact]
        public void MustProduceEmptyRouteWhenNoRouteAdded()
        {
            var slip = RoutingSlipBuilder.NewRoutingSlip(_id).Build();
            slip.Route.Should().BeEmpty();
        }

        [Fact]
        public void MustAccumulateRouteFromStringInInsertionOrder()
        {
            var slip = RoutingSlipBuilder.NewRoutingSlip(_id)
                .WithRoute("first")
                .WithRoute("second")
                .WithRoute("third")
                .Build();

            slip.Route.Should().HaveCount(3);
            slip.Route[0].DestinationPath.Should().Be("first");
            slip.Route[1].DestinationPath.Should().Be("second");
            slip.Route[2].DestinationPath.Should().Be("third");
        }

        [Fact]
        public void MustAccumulateRouteFromRoutingStepInInsertionOrder()
        {
            var firstStep = RoutingStepBuilder.WithStep("first").Build();
            var secondStep = RoutingStepBuilder.WithStep("second").Build();

            var slip = RoutingSlipBuilder.NewRoutingSlip(_id)
                .WithRoute(firstStep)
                .WithRoute(secondStep)
                .Build();

            slip.Route.Should().HaveCount(2);
            slip.Route[0].Should().BeSameAs(firstStep);
            slip.Route[1].Should().BeSameAs(secondStep);
        }

        [Fact]
        public void MustAccumulateRouteFromRoutingStepBuilderInInsertionOrder()
        {
            var slip = RoutingSlipBuilder.NewRoutingSlip(_id)
                .WithRoute(RoutingStepBuilder.WithStep("first"))
                .WithRoute(RoutingStepBuilder.WithStep("second"))
                .Build();

            slip.Route.Should().HaveCount(2);
            slip.Route[0].DestinationPath.Should().Be("first");
            slip.Route[1].DestinationPath.Should().Be("second");
        }

        [Fact]
        public void MustAccumulateRouteFromMixedOverloadsInInsertionOrder()
        {
            var explicitStep = RoutingStepBuilder.WithStep("middle").Build();

            var slip = RoutingSlipBuilder.NewRoutingSlip(_id)
                .WithRoute("first")
                .WithRoute(explicitStep)
                .WithRoute(RoutingStepBuilder.WithStep("last"))
                .Build();

            slip.Route.Should().HaveCount(3);
            slip.Route[0].DestinationPath.Should().Be("first");
            slip.Route[1].Should().BeSameAs(explicitStep);
            slip.Route[2].DestinationPath.Should().Be("last");
        }

        [Fact]
        public void MustReturnSameBuilderInstanceFromWithRouteForChaining()
        {
            var builder = RoutingSlipBuilder.NewRoutingSlip(_id);
            builder.WithRoute("step").Should().BeSameAs(builder);
        }
    }
}
