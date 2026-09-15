using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Serialization.UsingChatterJson
{
    // ====================================================================================
    // CHARACTERIZATION tests for the READ-leniency edges of the shared ChatterJson.Options —
    // the corners of NumericWriteStringReadEnumConverter and NewtonsoftLenientBooleanConverter
    // that the happy-path coverage in WhenSerializing.cs does not reach. Every value below is
    // accepted TODAY; the leniency is deliberate Newtonsoft read-parity, so that a body written
    // by one version of a message handler still deserializes in another during a rolling deploy.
    // These are a guard against a later narrowing of the read paths, not a specification of
    // desired behavior — nothing here should be "fixed" by tightening a converter.
    //
    // NOT repeated here (already pinned elsewhere): valid enum name, case-insensitive enum name,
    // numeric enum member, numeric write-parity and unknown-enum-name-throws in WhenSerializing.cs;
    // the common quoted/numeric boolean forms ("True"/"FALSE"/"1"/"0" and a bare 1/0) in
    // UsingJsonBodyConverter/WhenConverting.cs (MustAcceptQuotedBooleanOnReadForBoolMember).
    // ====================================================================================
    public class WhenLenientlyReadingEnumsAndBooleans : Testing.Core.Context
    {
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

        private class BooleanPoco
        {
            public bool Enabled { get; set; }
        }

        // The numeric read path calls Enum.ToObject, which does NOT check membership. A value that
        // names no declared member deserializes into the enum-typed property as-is.
        [Fact]
        public void MustReadUndefinedNumericEnumValue()
        {
            var deserialized = JsonSerializer.Deserialize<EnumPoco>("{\"Status\":999}", ChatterJson.Options);

            ((int)deserialized.Status).Should().Be(999);
        }

        // Enum.Parse accepts a numeric value expressed as a STRING, and applies no membership check
        // to it either — so the quoted form is lenient in exactly the same way as the bare one.
        [Fact]
        public void MustReadUndefinedNumericEnumValueWrittenAsString()
        {
            var deserialized = JsonSerializer.Deserialize<EnumPoco>("{\"Status\":\"999\"}", ChatterJson.Options);

            ((int)deserialized.Status).Should().Be(999);
        }

        // A negative numeric string is accepted for a signed-backed enum, again without membership
        // validation.
        [Fact]
        public void MustReadNegativeNumericEnumValueWrittenAsString()
        {
            var deserialized = JsonSerializer.Deserialize<EnumPoco>("{\"Status\":\"-1\"}", ChatterJson.Options);

            ((int)deserialized.Status).Should().Be(-1);
        }

        // Enum.Parse tolerates Newtonsoft's comma-separated combination syntax and ORs the members
        // together, whether or not the enum carries [Flags].
        [Fact]
        public void MustReadCommaSeparatedEnumNamesAsCombinedValue()
        {
            var deserialized = JsonSerializer.Deserialize<EnumPoco>("{\"Status\":\"Booked,Cancelled\"}", ChatterJson.Options);

            ((int)deserialized.Status).Should().Be((int)BookingStatus.Booked | (int)BookingStatus.Cancelled);
        }

        // bool.TryParse trims surrounding whitespace, so a padded quoted boolean is accepted too.
        [Fact]
        public void MustReadQuotedBooleanWithSurroundingWhitespace()
        {
            var deserialized = JsonSerializer.Deserialize<BooleanPoco>("{\"Enabled\":\" true \"}", ChatterJson.Options);

            deserialized.Enabled.Should().BeTrue();
        }
    }
}
