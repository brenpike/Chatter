using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Serialization.UsingChatterJson
{
    // ====================================================================================
    // The lenient enum/boolean read paths reach for an integral value through the STJ reader.
    // A numeric token that is not Int64/UInt64-representable — fractional, carrying an
    // exponent, or beyond the integral range — is unconvertible, and the failure must NAME the
    // offending value, as the neighbouring failure modes in both converters already do.
    // Reaching the value through the reader's own integral accessor does not: it raises a
    // FormatException that JsonSerializer rewrites into a valueless "The JSON value could not
    // be converted to X", so the one detail a poison-body investigation needs is discarded.
    //
    // The ACCEPTED value set does not move: every numeric form that deserialized before still
    // deserializes, and every form that threw before still throws. The integer-boolean
    // acceptance cases below are pinned here, at the
    // ChatterJson.Options seam, because they exercise the very branch being rewritten; they
    // are otherwise pinned only transitively through the JsonBodyConverter seam in
    // UsingJsonBodyConverter/WhenConverting.cs (MustAcceptQuotedBooleanOnReadForBoolMember).
    // ====================================================================================
    public class WhenReadingNonIntegralNumericTokens : Testing.Core.Context
    {
        private enum BookingStatus
        {
            Pending = 0,
            Booked = 1,
            Cancelled = 2,
        }

        private enum UnsignedBookingStatus : ulong
        {
            Pending = 0,
            Booked = 1,
        }

        private class EnumPoco
        {
            public BookingStatus Status { get; set; }
        }

        private class UnsignedEnumPoco
        {
            public UnsignedBookingStatus Status { get; set; }
        }

        private class BooleanPoco
        {
            public bool Enabled { get; set; }
        }

        [Theory]
        [InlineData("{\"Enabled\":1.5}", "1.5")]
        [InlineData("{\"Enabled\":1e3}", "1e3")]
        [InlineData("{\"Enabled\":9223372036854775808}", "9223372036854775808")]
        public void MustThrowJsonExceptionForNumericBooleanThatIsNotInt64Representable(string json, string offendingValue)
        {
            var act = () => JsonSerializer.Deserialize<BooleanPoco>(json, ChatterJson.Options);

            act.Should().Throw<JsonException>().WithMessage($"*'{offendingValue}'*");
        }

        [Theory]
        [InlineData("{\"Status\":1.5}", "1.5")]
        [InlineData("{\"Status\":1e3}", "1e3")]
        [InlineData("{\"Status\":9223372036854775808}", "9223372036854775808")]
        public void MustThrowJsonExceptionForNumericEnumThatIsNotInt64Representable(string json, string offendingValue)
        {
            var act = () => JsonSerializer.Deserialize<EnumPoco>(json, ChatterJson.Options);

            act.Should().Throw<JsonException>().WithMessage($"*'{offendingValue}'*");
        }

        // A ulong-backed enum reads its numeric token as unsigned, so a negative value has no
        // representation and is rejected — as it already was, now saying which value.
        [Theory]
        [InlineData("{\"Status\":-1}", "-1")]
        [InlineData("{\"Status\":1.5}", "1.5")]
        public void MustThrowJsonExceptionForNumericEnumThatIsNotUInt64Representable(string json, string offendingValue)
        {
            var act = () => JsonSerializer.Deserialize<UnsignedEnumPoco>(json, ChatterJson.Options);

            act.Should().Throw<JsonException>().WithMessage($"*'{offendingValue}'*");
        }

        // Regression guard for the rewritten branch: the integer boolean forms Newtonsoft
        // tolerated keep deserializing, bare and quoted alike.
        [Theory]
        [InlineData("{\"Enabled\":1}", true)]
        [InlineData("{\"Enabled\":0}", false)]
        [InlineData("{\"Enabled\":\"1\"}", true)]
        [InlineData("{\"Enabled\":\"0\"}", false)]
        public void MustStillReadIntegerBooleanForms(string json, bool expected)
        {
            var deserialized = JsonSerializer.Deserialize<BooleanPoco>(json, ChatterJson.Options);

            deserialized.Enabled.Should().Be(expected);
        }
    }
}
