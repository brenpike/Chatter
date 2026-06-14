using Chatter.MessageBrokers;
using Chatter.MessageBrokers.RabbitMQ;
using FluentAssertions;
using RabbitMQ.Client;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Xunit;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.UsingRabbitMqHeaderMarshaller
{
    // Exercises the RabbitMqHeaderMarshaller public static surface directly (the type is internal static; the
    // test assembly's InternalsVisibleTo reaches it). ToHeaderTable coerces each outbound MessageContext CLR value
    // to an AMQP-field-table-legal wire type (or drops un-encodable core keys); DecodeHeaderValue is the inbound
    // type-total string decode the translator delegates to for a native-frame field sourced from its header copy.
    // No live broker, no translator — just the coercion arms.
    public class WhenCoercingHeaders : Testing.Core.Context
    {
        private static BasicProperties NewProperties() => new BasicProperties();

        // --- ToHeaderTable: CoerceOutboundValue passthrough arms (string/bool/sbyte/int/long/decimal/byte[]) ---

        [Fact]
        public void MustPassThroughFieldTableLegalTypesVerbatim()
        {
            var bytes = new byte[] { 0xDE, 0xAD };
            var context = new Dictionary<string, object>
            {
                ["a-string"] = "value",
                ["a-bool"] = true,
                ["a-sbyte"] = (sbyte)-7,
                ["an-int"] = 42,
                ["a-long"] = 9_000_000_000L,
                ["a-decimal"] = 12.5m,
                ["a-byte-array"] = bytes
            };

            var table = RabbitMqHeaderMarshaller.ToHeaderTable(context, NewProperties());

            table["a-string"].Should().Be("value");
            table["a-bool"].Should().Be(true);
            table["a-sbyte"].Should().Be((sbyte)-7);
            table["an-int"].Should().Be(42);
            table["a-long"].Should().Be(9_000_000_000L);
            table["a-decimal"].Should().Be(12.5m);
            table["a-byte-array"].Should().BeSameAs(bytes, "a byte[] is field-table-legal and passes through verbatim");
        }

        // --- ToHeaderTable: numeric-widening arms (ulong/uint/ushort/byte -> long; short -> int) ---

        [Fact]
        public void MustWidenInRangeULongToLong()
        {
            var context = new Dictionary<string, object> { ["a-ulong"] = 5UL };

            var table = RabbitMqHeaderMarshaller.ToHeaderTable(context, NewProperties());

            table["a-ulong"].Should().Be(5L, "an in-range ulong widens to long (field-table-legal)");
        }

        [Fact]
        public void MustRenderOutOfRangeULongAsInvariantString()
        {
            var context = new Dictionary<string, object> { ["a-ulong"] = ulong.MaxValue };

            var table = RabbitMqHeaderMarshaller.ToHeaderTable(context, NewProperties());

            table["a-ulong"].Should().Be(ulong.MaxValue.ToString(CultureInfo.InvariantCulture),
                "a ulong above long.MaxValue cannot widen to long, so it falls back to its invariant string form");
        }

        [Fact]
        public void MustWidenUIntUShortAndByteToLong()
        {
            var context = new Dictionary<string, object>
            {
                ["a-uint"] = (uint)123,
                ["a-ushort"] = (ushort)45,
                ["a-byte"] = (byte)7
            };

            var table = RabbitMqHeaderMarshaller.ToHeaderTable(context, NewProperties());

            table["a-uint"].Should().Be(123L);
            table["a-ushort"].Should().Be(45L);
            table["a-byte"].Should().Be(7L);
        }

        [Fact]
        public void MustWidenShortToInt()
        {
            var context = new Dictionary<string, object> { ["a-short"] = (short)-9 };

            var table = RabbitMqHeaderMarshaller.ToHeaderTable(context, NewProperties());

            table["a-short"].Should().Be(-9, "a short widens to int (field-table-legal)");
            table["a-short"].Should().BeOfType<int>();
        }

        // --- ToHeaderTable: Guid -> string and the unencodable catch-all default arm ---

        [Fact]
        public void MustRenderGuidAsString()
        {
            var guid = Guid.NewGuid();
            var context = new Dictionary<string, object> { ["a-guid"] = guid };

            var table = RabbitMqHeaderMarshaller.ToHeaderTable(context, NewProperties());

            table["a-guid"].Should().Be(guid.ToString());
        }

        [Fact]
        public void MustRenderUnencodableTypeAsInvariantStringViaDefaultArm()
        {
            // An enum is not a field-table-legal type and is not handled by an explicit arm, so it falls through to
            // the documented Convert.ToString(InvariantCulture) catch-all rather than faulting the publish.
            var context = new Dictionary<string, object> { ["an-enum"] = SampleEnum.Second };

            var table = RabbitMqHeaderMarshaller.ToHeaderTable(context, NewProperties());

            table["an-enum"].Should().Be(Convert.ToString(SampleEnum.Second, CultureInfo.InvariantCulture));
        }

        private enum SampleEnum
        {
            First,
            Second
        }

        // --- ToHeaderTable: TimeToLive dropped; ExpiryTimeUtc encode arms; null-coerced dropped; null context ---

        [Fact]
        public void MustDropTimeToLiveKey()
        {
            var context = new Dictionary<string, object>
            {
                [MessageContext.TimeToLive] = TimeSpan.FromSeconds(30),
                ["kept"] = "value"
            };

            var table = RabbitMqHeaderMarshaller.ToHeaderTable(context, NewProperties());

            table.Should().NotContainKey(MessageContext.TimeToLive,
                "TimeToLive is lifted onto the native Expiration by the translator and must never reach the field table");
            table.Should().ContainKey("kept");
        }

        [Fact]
        public void MustEncodeExpiryTimeUtcDateTimeAsIsoO()
        {
            var expiry = new DateTime(2026, 6, 13, 10, 30, 0, DateTimeKind.Utc);
            var context = new Dictionary<string, object> { [MessageContext.ExpiryTimeUtc] = expiry };

            var table = RabbitMqHeaderMarshaller.ToHeaderTable(context, NewProperties());

            table[MessageContext.ExpiryTimeUtc].Should().Be(expiry.ToString("O", CultureInfo.InvariantCulture),
                "a DateTime ExpiryTimeUtc is ISO-8601 (\"O\") encoded so it round-trips back to a DateTime");
        }

        [Fact]
        public void MustEncodeNonDateTimeExpiryTimeUtcViaOutboundFallback()
        {
            // A non-DateTime ExpiryTimeUtc value falls back to the general outbound coercion (here a uint -> long).
            var context = new Dictionary<string, object> { [MessageContext.ExpiryTimeUtc] = (uint)17 };

            var table = RabbitMqHeaderMarshaller.ToHeaderTable(context, NewProperties());

            table[MessageContext.ExpiryTimeUtc].Should().Be(17L,
                "a non-DateTime ExpiryTimeUtc falls back to the general outbound coercion rather than the ISO encode");
        }

        [Fact]
        public void MustDropNullCoercedValue()
        {
            var context = new Dictionary<string, object>
            {
                ["a-null"] = null,
                ["kept"] = "value"
            };

            var table = RabbitMqHeaderMarshaller.ToHeaderTable(context, NewProperties());

            table.Should().NotContainKey("a-null", "a null value is not field-table-legal and must be dropped");
            table["kept"].Should().Be("value");
        }

        [Fact]
        public void MustReturnEmptyTableForNullContext()
        {
            var table = RabbitMqHeaderMarshaller.ToHeaderTable(null, NewProperties());

            table.Should().NotBeNull();
            table.Should().BeEmpty();
        }

        // --- DecodeHeaderValue: byte[] -> UTF8 string; string passthrough; null; default-arm invariant string ---

        [Fact]
        public void MustDecodeByteArrayValueToUtf8String()
        {
            var bytes = Encoding.UTF8.GetBytes("corr-123");

            RabbitMqHeaderMarshaller.DecodeHeaderValue(bytes).Should().Be("corr-123",
                "a byte[] AMQP longstr decodes to its UTF-8 string");
        }

        [Fact]
        public void MustPassThroughStringValue()
        {
            RabbitMqHeaderMarshaller.DecodeHeaderValue("already-a-string").Should().Be("already-a-string");
        }

        [Fact]
        public void MustReturnNullForNullValue()
        {
            RabbitMqHeaderMarshaller.DecodeHeaderValue(null).Should().BeNull(
                "a null wire value returns null so the caller drops the key rather than stamping an empty string");
        }

        [Fact]
        public void MustRenderNonStringNonByteArrayValueAsInvariantStringViaDefaultArm()
        {
            RabbitMqHeaderMarshaller.DecodeHeaderValue(42).Should().Be(
                Convert.ToString(42, CultureInfo.InvariantCulture),
                "a non-string, non-byte[] wire value is coerced to its invariant string form rather than passed through verbatim");
            RabbitMqHeaderMarshaller.DecodeHeaderValue(true).Should().Be(
                Convert.ToString(true, CultureInfo.InvariantCulture));
        }
    }
}
