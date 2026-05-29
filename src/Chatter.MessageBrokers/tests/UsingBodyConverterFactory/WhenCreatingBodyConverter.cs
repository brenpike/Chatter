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
        public void MustReturnFreshJsonBodyConverterForUnknownContentType()
        {
            var sut = new BodyConverterFactory(new List<IBrokeredMessageBodyConverter>());

            sut.CreateBodyConverter("application/unknown").Should().BeOfType<JsonBodyConverter>();
        }

        [Fact]
        public void MustReturnDistinctJsonBodyConverterInstancesForUnknownContentType()
        {
            var sut = new BodyConverterFactory(new List<IBrokeredMessageBodyConverter>());

            var first = sut.CreateBodyConverter("application/unknown");
            var second = sut.CreateBodyConverter("application/unknown");

            first.Should().NotBeSameAs(second);
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
