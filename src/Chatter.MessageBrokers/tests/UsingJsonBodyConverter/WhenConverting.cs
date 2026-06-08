using FluentAssertions;
using System.Text;
using Xunit;

namespace Chatter.MessageBrokers.Tests.UsingJsonBodyConverter
{
    public class WhenConverting : Testing.Core.Context
    {
        private readonly JsonBodyConverter _sut = new JsonBodyConverter();

        private class BodyPoco
        {
            public string Name { get; set; }
            public int Value { get; set; }
        }

        // Immutable command/event-style DTO: members exposed with PRIVATE setters only.
        // Newtonsoft populated private setters by default; the STJ port must too (read-path).
        private class PrivateSetterBodyPoco
        {
            public string Name { get; private set; }
            public int Value { get; private set; }
        }

        [Fact]
        public void MustExposeApplicationJsonContentType()
            => _sut.ContentType.Should().Be("application/json");

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
        public void MustStringifyObjectAsJson()
        {
            var json = _sut.Stringify(new BodyPoco { Name = "abc", Value = 42 });
            json.Should().Be("{\"Name\":\"abc\",\"Value\":42}");
        }

        // PARITY: Newtonsoft's JsonConvert.SerializeObject(null) produced the literal JSON "null".
        // The STJ port must not dereference body.GetType() on a null body (NullReferenceException);
        // a null body serializes to the literal JSON null, matching JsonUnicodeBodyConverter.
        [Fact]
        public void MustStringifyNullObjectAsJsonNullWithoutThrowing()
        {
            object body = null;

            _sut.Stringify(body).Should().Be("null");
        }

        // A null body round-trips: serialize -> "null" bytes; deserialize -> default(TBody).
        [Fact]
        public void MustRoundTripNullObjectThroughBytes()
        {
            object body = null;

            var bytes = _sut.Convert(body);
            var result = _sut.Convert<BodyPoco>(bytes);

            result.Should().BeNull();
        }

        // PARITY: Newtonsoft populated NON-PUBLIC property setters by default; STJ binds only
        // public setters / ctor params. The EnableNonPublicSetters contract modifier on the
        // shared ChatterJson.Options restores Newtonsoft's private-setter binding, so immutable
        // command/event DTOs deserialize their members instead of silently leaving them at
        // default (null / 0).
        [Fact]
        public void MustPopulatePrivateSettersOnDeserialize()
        {
            var bytes = _sut.GetBytes("{\"Name\":\"abc\",\"Value\":42}");

            var result = _sut.Convert<PrivateSetterBodyPoco>(bytes);

            result.Name.Should().Be("abc");
            result.Value.Should().Be(42);
        }
    }
}
