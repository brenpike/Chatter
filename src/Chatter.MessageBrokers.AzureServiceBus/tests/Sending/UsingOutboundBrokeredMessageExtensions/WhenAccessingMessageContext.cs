using Chatter.MessageBrokers.AzureServiceBus.Sending;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Sending.UsingOutboundBrokeredMessageExtensions
{
    public class WhenAccessingMessageContext : Testing.Core.Context
    {
        private readonly byte[] _body = new byte[] { 1, 2, 3 };
        private readonly JsonBodyConverter _converter = new JsonBodyConverter();

        private OutboundBrokeredMessage CreateSut()
            => new OutboundBrokeredMessage("message-id", _body, new Dictionary<string, object>(), "destination", _converter);

        // Mirrors MessageContext.MaterializePersistedContextValue (internal to Chatter.MessageBrokers,
        // NOT visible to this Chatter.MessageBrokers.AzureServiceBus.Tests assembly) and the projection
        // OutboxProcessor.Process performs on replay: deserialize each persisted value to a JsonElement and
        // restore the CLR type Newtonsoft's untyped read produced. The production materializer is pinned
        // directly in Chatter.MessageBrokers.Tests (WhenSerializingRiskyCharacters); here it is reproduced
        // so this test can construct the SAME boxed-long / DateTime context the outbox hands back, then drive
        // the REAL Azure Service Bus typed readers against it.
        // INVARIANT: must stay byte-equivalent to MessageContext.MaterializePersistedContextValue.
        private static object MaterializePersistedContextValue(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Number:
                    return element.TryGetInt64(out var asLong) ? asLong : element.GetDouble();
                case JsonValueKind.String:
                    return element.TryGetDateTime(out var asDateTime) ? asDateTime : (object)element.GetString();
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return element.GetBoolean();
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return null;
                default:
                    return element.GetRawText();
            }
        }

        [Fact]
        public void MustRoundTripTo()
        {
            var sut = CreateSut();
            sut.WithTo("the-to").Should().BeSameAs(sut);
            sut.GetToAddress().Should().Be("the-to");
        }

        [Fact]
        public void MustRoundTripViaPartitionKey()
        {
            var sut = CreateSut();
            sut.WithViaPartitionKey("via").Should().BeSameAs(sut);
            sut.GetViaPartitionKey().Should().Be("via");
        }

        [Fact]
        public void MustRoundTripPartitionKey()
        {
            var sut = CreateSut();
            sut.WithPartitionKey("pk").Should().BeSameAs(sut);
            sut.GetPartitionKey().Should().Be("pk");
        }

        [Fact]
        public void MustRoundTripScheduledEnqueueTimeUtc()
        {
            var when = new DateTime(2026, 5, 29, 12, 0, 0, DateTimeKind.Utc);
            var sut = CreateSut();
            sut.WithScheduledEnqueueTimeUtc(when).Should().BeSameAs(sut);
            sut.GetScheduledEnqueueTimeUtc().Should().Be(when);
        }

        [Fact]
        public void MustReturnNullScheduledEnqueueTimeUtcWhenAbsent()
            => CreateSut().GetScheduledEnqueueTimeUtc().Should().BeNull();

        [Fact]
        public void MustReturnNullToAddressWhenAbsent()
            => CreateSut().GetToAddress().Should().BeNull();

        [Fact]
        public void MustReturnApplicationPropertyWhenPresent()
        {
            var sut = CreateSut().WithTo("the-to");
            sut.GetApplicationPropertyByKey(ASBMessageContext.To).Should().Be("the-to");
        }

        [Fact]
        public void MustReturnNullApplicationPropertyWhenAbsent()
            => CreateSut().GetApplicationPropertyByKey("missing").Should().BeNull();

        // OUTBOX-REPLAY TYPED-READER GATE (the HIGH finding): proves typed MessageContext values survive
        // the full serialize -> persist -> materialize -> typed-read round-trip WITHOUT a mock that bypasses
        // the casts. Before the materializer fix the replayed context held the RAW persisted shapes
        // (JsonElement / string / boxed long), so GetScheduledEnqueueTimeUtc()'s (DateTime?) cast,
        // GetTimeToLive()'s TimeSpan handling, and ReceiveAttempts' (int) unbox each threw on replay. This
        // test reconstructs the SAME materialized context the outbox hands back and asserts the REAL readers
        // return the correct typed values and AsAzureServiceBusMessage() maps them onto the SDK Message.
        [Fact]
        public void MustReadTypedContextValuesAfterOutboxSerializeMaterializeRoundTrip()
        {
            var scheduledEnqueueTimeUtc = new DateTime(2026, 5, 29, 12, 0, 0, DateTimeKind.Utc);
            var timeToLive = TimeSpan.FromMinutes(5);
            const int receiveAttempts = 3;

            // Build the live MessageContext exactly as the producing side does, using the REAL key
            // constants and native CLR types (DateTime, TimeSpan, int).
            var liveContext = new Dictionary<string, object>
            {
                [ASBMessageContext.ScheduledEnqueueTimeUtc] = scheduledEnqueueTimeUtc,
                [MessageContext.TimeToLive] = timeToLive,
                [MessageContext.ReceiveAttempts] = receiveAttempts
            };

            // PERSIST seam: serialize with ChatterJson.Options exactly as the InMemory/EF outbox writers do.
            var persisted = JsonSerializer.Serialize(liveContext, ChatterJson.Options);

            // MATERIALIZE seam: deserialize to Dictionary<string, JsonElement> and project each value through
            // the materializer (mirrored above) — identical to OutboxProcessor.Process on replay.
            var headers = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(persisted, ChatterJson.Options);
            IDictionary<string, object> materializedContext =
                headers.ToDictionary(kvp => kvp.Key, kvp => MaterializePersistedContextValue(kvp.Value));

            // Confirm the replayed context holds the post-materialize SHAPE that previously broke the
            // reader: the numeric ReceiveAttempts comes back as a boxed numeric that is NOT a native int
            // (the materializer reproduces Newtonsoft's untyped read), so a raw (int) unbox would have
            // thrown InvalidCastException without ReceiveAttempts' Convert.ToInt32 tolerance.
            materializedContext[MessageContext.ReceiveAttempts].Should().NotBeOfType<int>();

            // Construct the OutboundBrokeredMessage from the materialized dictionary the way
            // OutboxProcessor does, then drive the REAL typed readers.
            var replayed = new OutboundBrokeredMessage("message-id", _body, materializedContext, "destination", _converter);

            replayed.GetScheduledEnqueueTimeUtc().Should().Be(scheduledEnqueueTimeUtc);
            replayed.GetTimeToLive().Should().Be(timeToLive);
            replayed.ReceiveAttempts.Should().Be(receiveAttempts);

            // The ASB mapping must read the same typed values onto the SDK Message without throwing.
            var asbMessage = replayed.AsAzureServiceBusMessage();
            asbMessage.ScheduledEnqueueTimeUtc.Should().Be(scheduledEnqueueTimeUtc);
            asbMessage.TimeToLive.Should().Be(timeToLive);
        }
    }
}
