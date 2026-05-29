using FluentAssertions;
using System;
using System.Text;
using Xunit;

namespace Chatter.MessageBrokers.Tests.UsingTextPlainBodyConverter
{
    public class WhenConverting : Testing.Core.Context
    {
        private const string _conversionFailureMessage = "A strongly typed body is required. Consider using a content type like application/json.";
        private readonly TextPlainBodyConverter _sut = new TextPlainBodyConverter();

        private class BodyPoco
        {
            public string Name { get; set; }
            public int Value { get; set; }
        }

        [Fact]
        public void MustExposeTextPlainContentType()
            => _sut.ContentType.Should().Be("text/plain");

        [Fact]
        public void MustRoundTripObjectThroughBytes()
        {
            var original = new BodyPoco { Name = "abc", Value = 42 };

            var bytes = _sut.Convert(original);
            var result = _sut.Convert<BodyPoco>(bytes);

            result.Name.Should().Be("abc");
            result.Value.Should().Be(42);
        }

        [Fact]
        public void MustStringifyBytesUsingUtf8Decode()
        {
            var bytes = Encoding.UTF8.GetBytes("hello world");
            _sut.Stringify(bytes).Should().Be("hello world");
        }

        [Fact]
        public void MustGetBytesUsingUtf8Encode()
        {
            _sut.GetBytes("hello world").Should().Equal(Encoding.UTF8.GetBytes("hello world"));
        }

        [Fact]
        public void MustWrapDeserializationFailureInBareExceptionWithInnerPreserved()
        {
            var invalidJson = Encoding.UTF8.GetBytes("this is not json");

            var act = FluentActions.Invoking(() => _sut.Convert<BodyPoco>(invalidJson));

            act.Should().Throw<Exception>()
                .WithMessage(_conversionFailureMessage)
                .WithInnerException<Exception>();
        }
    }
}
