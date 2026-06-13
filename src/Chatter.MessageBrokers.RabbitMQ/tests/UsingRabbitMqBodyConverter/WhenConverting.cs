using Chatter.MessageBrokers.RabbitMQ;
using FluentAssertions;
using System;
using System.Text;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.UsingRabbitMqBodyConverter
{
    // Pins RabbitMqBodyConverter AS-IS: a UTF-8 JSON converter (unlike SSB's UTF-16 JsonUnicodeBodyConverter).
    // ContentType is application/json; charset=utf-8 and GetBytes/Stringify(byte[]) use Encoding.UTF8.
    public class WhenConverting : Testing.Core.Context
    {
        private readonly RabbitMqBodyConverter _sut = new RabbitMqBodyConverter();

        private class BodyPoco
        {
            public string Name { get; set; }
            public int Value { get; set; }
        }

        [Fact]
        public void MustExposeApplicationJsonUtf8ContentType()
            => _sut.ContentType.Should().Be("application/json; charset=utf-8");

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
        public void MustStringifyObjectAsJson()
            => _sut.Stringify(new BodyPoco { Name = "abc", Value = 42 })
                   .Should().Be("{\"Name\":\"abc\",\"Value\":42}");

        [Fact]
        public void MustGetBytesUsingUtf8Encode()
            => _sut.GetBytes("hello world").Should().Equal(Encoding.UTF8.GetBytes("hello world"));

        // INVARIANT: UTF-8 encodes ASCII content as 1 byte per character (the SSB UTF-16 converter uses 2).
        [Fact]
        public void MustGetBytesProduceOneBytePerAsciiCharacter()
            => _sut.GetBytes("abc").Should().HaveCount(3);

        [Fact]
        public void MustStringifyBytesUsingUtf8Decode()
        {
            var bytes = Encoding.UTF8.GetBytes("hello world");
            _sut.Stringify(bytes).Should().Be("hello world");
        }

        // INVARIANT: a non-ASCII payload survives the UTF-8 GetBytes -> Stringify(byte[]) round-trip intact.
        [Fact]
        public void MustRoundTripNonAsciiThroughUtf8()
        {
            const string unicode = "héllo wörld ✨";
            _sut.Stringify(_sut.GetBytes(unicode)).Should().Be(unicode);
        }

        // Convert<T> of invalid JSON surfaces the underlying System.Text.Json exception with no custom wrapping.
        [Fact]
        public void MustThrowExceptionWhenConvertingInvalidJson()
        {
            var invalidJsonBytes = Encoding.UTF8.GetBytes("{ this is not valid json");

            Action act = () => _sut.Convert<BodyPoco>(invalidJsonBytes);

            act.Should().Throw<System.Text.Json.JsonException>();
        }
    }
}
