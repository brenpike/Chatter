using Chatter.MessageBrokers.Routing.Context;
using FluentAssertions;
using System;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Routing.Context.UsingCompensationRoutingContext
{
    public class WhenSettingDetailsAndDescription : Testing.Core.Context
    {
        private static CompensationRoutingContext CreateSut()
            => new CompensationRoutingContext("destination");

        [Fact]
        public void MustUpdateCompensateDetails()
            => CreateSut().SetDetails("the-details").CompensateDetails.Should().Be("the-details");

        [Fact]
        public void MustReturnSameInstanceFromSetDetails()
        {
            var sut = CreateSut();
            sut.SetDetails("the-details").Should().BeSameAs(sut);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowArgumentExceptionWhenDetailsIsNullOrWhitespace(string details)
            => FluentActions.Invoking(() => CreateSut().SetDetails(details))
                .Should().Throw<ArgumentException>();

        [Fact]
        public void MustUpdateCompensateDescription()
            => CreateSut().SetDescription("the-description").CompensateDescription.Should().Be("the-description");

        [Fact]
        public void MustReturnSameInstanceFromSetDescription()
        {
            var sut = CreateSut();
            sut.SetDescription("the-description").Should().BeSameAs(sut);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustThrowArgumentExceptionWhenDescriptionIsNullOrWhitespace(string description)
            => FluentActions.Invoking(() => CreateSut().SetDescription(description))
                .Should().Throw<ArgumentException>();

        [Fact]
        public void MustFormatToStringAsDescriptionArrowDetails()
        {
            var sut = new CompensationRoutingContext("destination", "the-details", "the-description");
            sut.ToString().Should().Be("the-description -> the-details");
        }
    }
}
