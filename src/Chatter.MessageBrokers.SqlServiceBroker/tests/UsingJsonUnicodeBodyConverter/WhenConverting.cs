using FluentAssertions;
using System;
using System.Text;
using Xunit;

namespace Chatter.MessageBrokers.SqlServiceBroker.Tests.UsingJsonUnicodeBodyConverter
{
    // Behavior-pinning tests: characterize JsonUnicodeBodyConverter AS-IS, including the
    // UTF-16 (Encoding.Unicode) encode/decode path. All members are INSTANCE methods on the
    // converter. The encoding-mismatch behavior (UTF-8 bytes fed into the Unicode decode path
    // producing garbage) is pinned deliberately as current behavior, not endorsed.
    public class WhenConverting : Testing.Core.Context
    {
        private readonly JsonUnicodeBodyConverter _sut = new JsonUnicodeBodyConverter();

        private class BodyPoco
        {
            public string Name { get; set; }
            public int Value { get; set; }
        }

        [Fact]
        public void MustExposeApplicationJsonUtf16ContentType()
            => _sut.ContentType.Should().Be("application/json; charset=utf-16");

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
        {
            var json = _sut.Stringify(new BodyPoco { Name = "abc", Value = 42 });
            json.Should().Be("{\"Name\":\"abc\",\"Value\":42}");
        }

        [Fact]
        public void MustGetBytesUsingUtf16Encode()
            => _sut.GetBytes("hello world").Should().Equal(Encoding.Unicode.GetBytes("hello world"));

        // INVARIANT: UTF-16 encodes ASCII content as 2 bytes per character.
        [Fact]
        public void MustGetBytesProduceTwoBytesPerAsciiCharacter()
            => _sut.GetBytes("abc").Should().HaveCount(6);

        [Fact]
        public void MustStringifyBytesUsingUnicodeDecode()
        {
            var bytes = Encoding.Unicode.GetBytes("hello world");
            _sut.Stringify(bytes).Should().Be("hello world");
        }

        // Pins the encoding mismatch AS-IS: UTF-8 bytes decoded through the Unicode (UTF-16) path
        // do NOT round-trip to the original ASCII string.
        [Fact]
        public void MustGarbleWhenUtf8BytesDecodedAsUnicode()
        {
            var utf8Bytes = Encoding.UTF8.GetBytes("hello world");
            _sut.Stringify(utf8Bytes).Should().NotBe("hello world");
        }

        // Convert<T> of invalid JSON surfaces the underlying System.Text.Json exception with no
        // custom wrapping.
        [Fact]
        public void MustThrowExceptionWhenConvertingInvalidJson()
        {
            var invalidJsonBytes = Encoding.Unicode.GetBytes("{ this is not valid json");

            Action act = () => _sut.Convert<BodyPoco>(invalidJsonBytes);

            act.Should().Throw<System.Text.Json.JsonException>();
        }
    }
}
