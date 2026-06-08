using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Serialization.UsingChatterJson
{
    // ====================================================================================
    // DIRECT unit tests for ChatterJson.Options — the shared System.Text.Json options added by
    // the Phase-2 Newtonsoft -> System.Text.Json port. Pins the public serializer surface every
    // Chatter.MessageBrokers serialization site routes through: PascalCase property names, compact
    // (non-indented) output, the ChatterJsonEncoder, and lossless round-tripping.
    // ====================================================================================
    public class WhenSerializing : Testing.Core.Context
    {
        private class Poco
        {
            public string Name { get; set; }
            public int Value { get; set; }
        }

        private enum BookingStatus
        {
            Pending = 0,
            Booked = 1,
            Cancelled = 2,
        }

        private class EnumPoco
        {
            public BookingStatus Status { get; set; }
        }

        [Fact]
        public void MustExposeNonNullCachedOptions()
        {
            ChatterJson.Options.Should().NotBeNull();
            ChatterJson.Options.Encoder.Should().NotBeNull();
        }

        [Fact]
        public void MustReturnTheSameCachedOptionsInstanceOnEveryAccess()
        {
            // INVARIANT: Options is a single cached instance (STJ per-call construction is a perf
            // cliff). Two reads must be reference-identical.
            ChatterJson.Options.Should().BeSameAs(ChatterJson.Options);
        }

        [Fact]
        public void MustSerializePocoWithPascalCasePropertyNamesAndCompactOutput()
        {
            // No NamingPolicy (PascalCase, matching Newtonsoft default) and WriteIndented = false
            // (compact) — confirms neither a camelCase policy nor indentation was introduced.
            var json = JsonSerializer.Serialize(new Poco { Name = "abc", Value = 42 }, ChatterJson.Options);

            json.Should().Be("{\"Name\":\"abc\",\"Value\":42}");
        }

        [Fact]
        public void MustRoundTripPocoBackToEqualValues()
        {
            var original = new Poco { Name = "abc", Value = 42 };

            var json = JsonSerializer.Serialize(original, ChatterJson.Options);
            var roundTripped = JsonSerializer.Deserialize<Poco>(json, ChatterJson.Options);

            roundTripped.Should().NotBeNull();
            roundTripped.Name.Should().Be(original.Name);
            roundTripped.Value.Should().Be(original.Value);
        }

        [Fact]
        public void MustRoundTripNonAsciiAndAstralValuesLosslessly()
        {
            // The encoder emits non-ASCII (including astral 😀 U+1F600) literally on the way out;
            // deserialization must recover the exact original string on the way back in.
            var original = new Poco { Name = "éü\U0001F600", Value = 7 };

            var json = JsonSerializer.Serialize(original, ChatterJson.Options);
            var roundTripped = JsonSerializer.Deserialize<Poco>(json, ChatterJson.Options);

            roundTripped.Name.Should().Be("éü\U0001F600");
        }

        // ====================================================================================
        // Newtonsoft read-leniency parity — deserialize-side leniencies Newtonsoft's default
        // JsonConvert tolerated but System.Text.Json tightened to throw. Each MUST succeed on read
        // without throwing while leaving serialize wire output unchanged (golden parity tests stay
        // byte-identical).
        // ====================================================================================

        // Newtonsoft coerced a quoted numeric value ({"Value":"42"}) into an int property; STJ
        // defaults strict and throws. NumberHandling.AllowReadingFromString restores the read.
        [Fact]
        public void MustReadQuotedNumberIntoNumericProperty()
        {
            var deserialized = JsonSerializer.Deserialize<Poco>("{\"Value\":\"42\"}", ChatterJson.Options);

            deserialized.Value.Should().Be(42);
        }

        // Newtonsoft tolerated a trailing comma; STJ defaults false and throws. AllowTrailingCommas
        // restores the read.
        [Fact]
        public void MustReadBodyWithTrailingComma()
        {
            var deserialized = JsonSerializer.Deserialize<Poco>("{\"Name\":\"abc\",\"Value\":42,}", ChatterJson.Options);

            deserialized.Name.Should().Be("abc");
            deserialized.Value.Should().Be(42);
        }

        // Newtonsoft tolerated // line comments; STJ defaults Disallow and throws.
        // ReadCommentHandling.Skip ignores them on read.
        [Fact]
        public void MustReadBodyWithLineComment()
        {
            var deserialized = JsonSerializer.Deserialize<Poco>("{\n// a line comment\n\"Value\":42}", ChatterJson.Options);

            deserialized.Value.Should().Be(42);
        }

        // Newtonsoft tolerated /* block */ comments; STJ defaults Disallow and throws.
        // ReadCommentHandling.Skip ignores them on read.
        [Fact]
        public void MustReadBodyWithBlockComment()
        {
            var deserialized = JsonSerializer.Deserialize<Poco>("{/* block comment */\"Value\":42}", ChatterJson.Options);

            deserialized.Value.Should().Be(42);
        }

        // Newtonsoft's default read accepted an enum NAME for an enum property ({"Status":"Booked"});
        // STJ with no enum converter reads numbers only and throws. NumericWriteStringReadEnumConverter
        // accepts the name on read.
        [Fact]
        public void MustReadEnumPropertyFromStringName()
        {
            var deserialized = JsonSerializer.Deserialize<EnumPoco>("{\"Status\":\"Booked\"}", ChatterJson.Options);

            deserialized.Status.Should().Be(BookingStatus.Booked);
        }

        // Newtonsoft parsed enum names case-insensitively; the converter restores that via
        // Enum.Parse(ignoreCase: true).
        [Fact]
        public void MustReadEnumPropertyFromStringNameCaseInsensitively()
        {
            var deserialized = JsonSerializer.Deserialize<EnumPoco>("{\"Status\":\"cancelled\"}", ChatterJson.Options);

            deserialized.Status.Should().Be(BookingStatus.Cancelled);
        }

        // The pre-converter numeric read path must keep working ({"Status":2}).
        [Fact]
        public void MustReadEnumPropertyFromNumber()
        {
            var deserialized = JsonSerializer.Deserialize<EnumPoco>("{\"Status\":2}", ChatterJson.Options);

            deserialized.Status.Should().Be(BookingStatus.Cancelled);
        }

        // WIRE PARITY: the enum converter MUST write the NUMERIC value (Newtonsoft's default and STJ's
        // default both write enums as numbers), not the name — otherwise golden byte-parity breaks.
        [Fact]
        public void MustWriteEnumPropertyAsNumberToPreserveWireParity()
        {
            var json = JsonSerializer.Serialize(new EnumPoco { Status = BookingStatus.Booked }, ChatterJson.Options);

            json.Should().Be("{\"Status\":1}");
        }

        // An unknown enum name is a genuine error, not silently coerced.
        [Fact]
        public void MustThrowOnUnknownEnumName()
        {
            var act = () => JsonSerializer.Deserialize<EnumPoco>("{\"Status\":\"NotARealStatus\"}", ChatterJson.Options);

            act.Should().Throw<JsonException>();
        }
    }
}
