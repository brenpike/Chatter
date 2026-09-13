using FluentAssertions;
using Moq;
using System.Collections.Generic;
using Xunit;

namespace Chatter.MessageBrokers.Tests.UsingBodyConverterFactory
{
    public class WhenCreatingBodyConverter : Testing.Core.Context
    {
        private static Mock<IBrokeredMessageBodyConverter> ConverterFor(string contentType)
        {
            var converter = new Mock<IBrokeredMessageBodyConverter>();
            converter.SetupGet(c => c.ContentType).Returns(contentType);
            return converter;
        }

        [Fact]
        public void MustReturnRegisteredConverterForKnownContentType()
        {
            var registered = ConverterFor("application/xml");
            var sut = new BodyConverterFactory(new[] { registered.Object });

            sut.CreateBodyConverter("application/xml").Should().BeSameAs(registered.Object);
        }

        [Fact]
        public void MustReturnJsonBodyConverterForUnknownContentType()
        {
            var sut = new BodyConverterFactory(new List<IBrokeredMessageBodyConverter>());

            sut.CreateBodyConverter("application/unknown").Should().BeOfType<JsonBodyConverter>();
        }

        [Fact]
        public void MustReturnSameJsonBodyConverterInstanceForEveryUnknownContentType()
        {
            var sut = new BodyConverterFactory(new List<IBrokeredMessageBodyConverter>());

            var first = sut.CreateBodyConverter("application/unknown");
            var repeated = sut.CreateBodyConverter("application/unknown");
            var otherUnknown = sut.CreateBodyConverter("application/also-unknown");

            repeated.Should().BeSameAs(first, "the fallback is created once per factory, not once per message");
            otherUnknown.Should().BeSameAs(first, "every unknown content type shares the one fallback");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustReturnTheSharedFallbackForNullOrBlankContentType(string contentType)
        {
            var sut = new BodyConverterFactory(new List<IBrokeredMessageBodyConverter>());
            var fallback = sut.CreateBodyConverter("application/unknown");

            sut.CreateBodyConverter(contentType).Should().BeSameAs(fallback);
        }

        [Fact]
        public void MustNotSubstituteARegisteredJsonConverterForAnUnknownContentType()
        {
            var registeredJson = new JsonBodyConverter();
            var sut = new BodyConverterFactory(new IBrokeredMessageBodyConverter[] { registeredJson });

            sut.CreateBodyConverter("application/unknown").Should().NotBeSameAs(registeredJson, "the fallback belongs to the factory; a caller-registered application/json converter answers only for its own content type");
        }

        [Fact]
        public void MustHonorLastRegisteredConverterWhenContentTypesCollide()
        {
            var first = ConverterFor("application/json");
            var second = ConverterFor("application/json");
            var sut = new BodyConverterFactory(new[] { first.Object, second.Object });

            sut.CreateBodyConverter("application/json").Should().BeSameAs(second.Object);
        }
    }
}
