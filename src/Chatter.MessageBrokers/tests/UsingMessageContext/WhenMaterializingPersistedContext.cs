using FluentAssertions;
using System;
using System.Collections.Generic;
using System.Text.Json;
using Xunit;

namespace Chatter.MessageBrokers.Tests.UsingMessageContext
{
    // INVARIANT: MessageContext.MaterializePersistedContext is the single construction point that
    // every System.Text.Json deserialize seam (outbox replay, SQL Service Broker envelope unwrap)
    // routes through, so no downstream typed reader observes a raw JsonElement. These tests pin the
    // CLR-type restoration parity contract: the materialized values must carry the exact CLR types
    // Newtonsoft's untyped IDictionary<string, object> read would have produced. A regression in the
    // materializer (e.g. a "helpful" Guid coercion, or numbers materializing as int/double instead of
    // long) must fail the build here.
    public class WhenMaterializingPersistedContext : Testing.Core.Context
    {
        // The writers (InMemory/EF SendToOutbox, SSB envelope) serialize an IDictionary<string, object>
        // with ChatterJson.Options; this builds the persisted JSON the same way so the round-trip pins
        // the real wire format.
        private static string SerializePersistedContext(IDictionary<string, object> context)
            => JsonSerializer.Serialize(context, ChatterJson.Options);

        private static IDictionary<string, object> MaterializeFrom(IDictionary<string, object> context)
            => MessageContext.MaterializePersistedContext(SerializePersistedContext(context));

        [Fact]
        public void MustRestoreStringAsString()
        {
            var materialized = MaterializeFrom(new Dictionary<string, object> { ["key"] = "a-string-value" });

            materialized["key"].Should().BeOfType<string>().And.Be("a-string-value");
        }

        // INVARIANT: a JSON integer materializes to Int64 (long), matching Newtonsoft's untyped read.
        // NOT int, NOT double — downstream readers (e.g. ReceiveAttempts) depend on the boxed long.
        [Fact]
        public void MustRestoreJsonIntegerAsLong()
        {
            var materialized = MaterializeFrom(new Dictionary<string, object> { ["attempts"] = 3 });

            materialized["attempts"].Should().BeOfType<long>().And.Be(3L);
        }

        // INVARIANT: a JSON non-integer number materializes to double, matching Newtonsoft.
        [Fact]
        public void MustRestoreJsonNonIntegerNumberAsDouble()
        {
            var materialized = MaterializeFrom(new Dictionary<string, object> { ["ratio"] = 1.5d });

            materialized["ratio"].Should().BeOfType<double>().And.Be(1.5d);
        }

        [Fact]
        public void MustRestoreBoolAsBool()
        {
            var materialized = MaterializeFrom(new Dictionary<string, object> { ["flag"] = true });

            materialized["flag"].Should().BeOfType<bool>().And.Be(true);
        }

        // INVARIANT: a strict ISO-8601 datetime string materializes to DateTime, matching Newtonsoft's
        // default DateParseHandling.
        [Fact]
        public void MustRestoreIso8601DateTimeStringAsDateTime()
        {
            var instant = new DateTime(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc);

            var materialized = MaterializeFrom(new Dictionary<string, object> { ["enqueueAt"] = instant });

            materialized["enqueueAt"].Should().BeOfType<DateTime>();
            ((DateTime)materialized["enqueueAt"]).ToUniversalTime().Should().Be(instant);
        }

        [Fact]
        public void MustRestoreNullAsNull()
        {
            var materialized = MaterializeFrom(new Dictionary<string, object> { ["absent"] = null });

            materialized.Should().ContainKey("absent");
            materialized["absent"].Should().BeNull();
        }

        // CRITICAL PARITY ORACLE: a JSON string that LOOKS like a Guid MUST materialize to string, NOT
        // Guid. JsonElement.TryGetDateTime is strict and never matches a Guid, and there is deliberately
        // no Guid coercion in the materializer — Newtonsoft's untyped read leaves a Guid-shaped string as
        // a String. This locks parity so a future "helpful" Guid coercion fails the build.
        [Fact]
        public void MustRestoreGuidShapedStringAsStringNotGuid()
        {
            const string guidShaped = "3fa85f64-5717-4562-b3fc-2c963f66afa6";

            var materialized = MaterializeFrom(new Dictionary<string, object> { ["id"] = guidShaped });

            materialized["id"].Should().BeOfType<string>().And.Be(guidShaped);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustReturnEmptyDictionaryForNullOrWhitespaceJson(string json)
        {
            var materialized = MessageContext.MaterializePersistedContext(json);

            materialized.Should().NotBeNull().And.BeEmpty();
        }

        // -----------------------------------------------------------------------
        // (IDictionary<string, object>) overload — projects an already-deserialized STJ context whose
        // object-typed values are raw JsonElements, passing through already-CLR-typed values unchanged.
        // -----------------------------------------------------------------------

        [Fact]
        public void MustMaterializeJsonElementValuesInDictionaryOverload()
        {
            using var document = JsonDocument.Parse("{\"attempts\":3,\"name\":\"a-string-value\"}");
            var context = new Dictionary<string, object>
            {
                ["attempts"] = document.RootElement.GetProperty("attempts").Clone(),
                ["name"] = document.RootElement.GetProperty("name").Clone(),
            };

            var materialized = MessageContext.MaterializePersistedContext(context);

            materialized["attempts"].Should().BeOfType<long>().And.Be(3L);
            materialized["name"].Should().BeOfType<string>().And.Be("a-string-value");
        }

        // INVARIANT: values that are already CLR-typed (not JsonElement) pass through unchanged — the
        // SSB receiver stamps native int/Guid/string headers after materialization, and a re-run of the
        // projection must not mangle them.
        [Fact]
        public void MustPassThroughAlreadyClrTypedValuesUnchangedInDictionaryOverload()
        {
            var nativeGuid = Guid.NewGuid();
            var context = new Dictionary<string, object>
            {
                ["attempts"] = 3,
                ["handle"] = nativeGuid,
                ["service"] = "test-service",
            };

            var materialized = MessageContext.MaterializePersistedContext(context);

            materialized["attempts"].Should().BeOfType<int>().And.Be(3);
            materialized["handle"].Should().BeOfType<Guid>().And.Be(nativeGuid);
            materialized["service"].Should().BeOfType<string>().And.Be("test-service");
        }

        [Fact]
        public void MustReturnEmptyDictionaryForNullSourceInDictionaryOverload()
        {
            var materialized = MessageContext.MaterializePersistedContext((IDictionary<string, object>)null);

            materialized.Should().NotBeNull().And.BeEmpty();
        }

        // -----------------------------------------------------------------------
        // Structured (object/array) attachment values — PARITY: Newtonsoft materialized
        // IDictionary<string, object> object/array entries as navigable JObject/JArray, so a
        // consumer doing slip.Attachments["payload"] = new { Id = 1 } could read structured data
        // back. The STJ materializer must recursively materialize objects -> Dictionary<string,
        // object> and arrays -> List<object> (navigable nested CLR collections) instead of
        // flattening them to a raw JSON string. Leaf values keep the per-value parity rules.
        // -----------------------------------------------------------------------

        [Fact]
        public void MustRestoreJsonObjectAsNavigableDictionaryWithTypedLeaves()
        {
            var materialized = MaterializeFrom(new Dictionary<string, object>
            {
                ["payload"] = new Dictionary<string, object> { ["id"] = 1, ["name"] = "abc" },
            });

            var payload = materialized["payload"].Should().BeAssignableTo<IDictionary<string, object>>().Subject;
            payload["id"].Should().BeOfType<long>().And.Be(1L);
            payload["name"].Should().BeOfType<string>().And.Be("abc");
        }

        [Fact]
        public void MustRestoreJsonArrayAsNavigableListWithTypedElements()
        {
            var materialized = MaterializeFrom(new Dictionary<string, object>
            {
                ["items"] = new object[] { 1, "two", true },
            });

            var items = materialized["items"].Should().BeAssignableTo<IList<object>>().Subject;
            items.Should().HaveCount(3);
            items[0].Should().BeOfType<long>().And.Be(1L);
            items[1].Should().BeOfType<string>().And.Be("two");
            items[2].Should().BeOfType<bool>().And.Be(true);
        }

        [Fact]
        public void MustRecursivelyMaterializeNestedObjectsAndArrays()
        {
            var materialized = MaterializeFrom(new Dictionary<string, object>
            {
                ["payload"] = new Dictionary<string, object>
                {
                    ["nested"] = new Dictionary<string, object> { ["count"] = 7 },
                    ["tags"] = new object[] { "a", "b" },
                },
            });

            var payload = materialized["payload"].Should().BeAssignableTo<IDictionary<string, object>>().Subject;

            var nested = payload["nested"].Should().BeAssignableTo<IDictionary<string, object>>().Subject;
            nested["count"].Should().BeOfType<long>().And.Be(7L);

            var tags = payload["tags"].Should().BeAssignableTo<IList<object>>().Subject;
            tags.Should().Equal("a", "b");
        }
    }
}
