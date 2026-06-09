using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Routing.Context;
using FluentAssertions;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Routing.Context.UsingCompensationRoutingContext
{
    public class WhenConstructing : Testing.Core.Context
    {
        [Fact]
        public void MustMapDestinationPathFromShortConstructor()
            => new CompensationRoutingContext("destination").DestinationPath.Should().Be("destination");

        [Fact]
        public void MustCreateContainerFromShortConstructor()
            => new CompensationRoutingContext("destination").Container.Should().NotBeNull();

        [Fact]
        public void MustDefaultCompensateDetailsAndDescriptionToEmptyFromShortConstructor()
        {
            var sut = new CompensationRoutingContext("destination");
            sut.CompensateDetails.Should().BeEmpty();
            sut.CompensateDescription.Should().BeEmpty();
        }

        [Fact]
        public void MustMapDestinationPathFromFullConstructor()
            => new CompensationRoutingContext("destination", "details", "description").DestinationPath.Should().Be("destination");

        [Fact]
        public void MustMapDetailsAndDescriptionFromFullConstructor()
        {
            var sut = new CompensationRoutingContext("destination", "details", "description");
            sut.CompensateDetails.Should().Be("details");
            sut.CompensateDescription.Should().Be("description");
        }

        [Fact]
        public void MustCreateContainerInheritingFromProvidedContext()
        {
            var inheritedContext = new ContextContainer();
            inheritedContext.Include("inherited-value");

            var sut = new CompensationRoutingContext("destination", "details", "description", inheritedContext);

            sut.Container.TryGet<string>(out var inheritedValue).Should().BeTrue();
            inheritedValue.Should().Be("inherited-value");
        }
    }
}
