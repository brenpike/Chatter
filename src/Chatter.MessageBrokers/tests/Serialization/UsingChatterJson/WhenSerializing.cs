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
    }
}
