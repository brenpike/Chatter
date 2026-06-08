using FluentAssertions;
using System;
using System.Collections.Generic;
using System.Text.Json;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Serialization.UsingMaterializingObjectConverter
{
    // ====================================================================================
    // DIRECT CONTRACT of the global MaterializingObjectConverter registered on
    // ChatterJson.Options. This is the one converter that restores Newtonsoft's untyped-read
    // CLR-type fidelity at EVERY object-typed read position (message-context values,
    // RoutingSlip.Attachments values, any DTO `object` member). It is exercised here through
    // ChatterJson.Options by serializing/deserializing an `object`-typed member, which is the
    // only public surface the converter participates in.
    //
    // The contract has three faces:
    //   A. READ  — JsonElement -> CLR type (long/double/string/DateTime/bool/null, nested
    //              Dictionary<string, object> / List<object>), recursively at every depth.
    //   B. WRITE — non-reentrant runtime-typed pass-through; byte-identical to a plain STJ
    //              serialize of the same runtime values; NEVER a stack overflow.
    //   C. ROUND-TRIP — CLR values survive serialize -> deserialize (documenting the untyped
    //              widening: an int CLR value writes a JSON number and reads back as long).
    //   E. COMPOSITION — composes with the other ChatterJson.Options modifiers/converters with
    //              no interference.
    //
    // GUID PARITY ORACLE: a Guid-shaped string materializes to STRING, not Guid, matching
    // Newtonsoft's untyped read. There is deliberately no Guid coercion in the materializer.
    // ====================================================================================
    public class WhenConverting : Testing.Core.Context
    {
        // The only public path the converter participates in: an `object`-typed member on a DTO
        // serialized/deserialized through the shared ChatterJson.Options.
        private class ObjectMemberPoco
        {
            public object Value { get; set; }
        }

        // Deserialize a single JSON value at an object position and return the materialized CLR value.
        private static object MaterializeObjectMember(string innerJson)
        {
            var poco = JsonSerializer.Deserialize<ObjectMemberPoco>(
                "{\"Value\":" + innerJson + "}", ChatterJson.Options);
            return poco.Value;
        }

        // Serialize a CLR value at an object position and return the produced inner JSON for "Value".
        private static string SerializeObjectMember(object value)
            => JsonSerializer.Serialize(new ObjectMemberPoco { Value = value }, ChatterJson.Options);

        // ------------------------------------------------------------------------------------
        // A. READ — per-kind CLR-type fidelity (type AND value asserted)
        // ------------------------------------------------------------------------------------

        // INVARIANT: a JSON integer materializes to Int64 (long), not int/double, matching Newtonsoft.
        [Fact]
        public void MustMaterializeJsonIntegerAsLong()
        {
            var value = MaterializeObjectMember("3");

            value.Should().BeOfType<long>().And.Be(3L);
        }

        // BOUNDARY: a value larger than int.MaxValue must stay a long (no int overflow / no double).
        [Fact]
        public void MustMaterializeIntegerAboveInt32MaxAsLong()
        {
            long aboveInt32 = (long)int.MaxValue + 1;

            var value = MaterializeObjectMember(aboveInt32.ToString());

            value.Should().BeOfType<long>().And.Be(aboveInt32);
        }

        // BOUNDARY: a very large Int64 stays a long (exercises TryGetInt64's upper edge).
        [Fact]
        public void MustMaterializeVeryLargeLongAsLong()
        {
            var value = MaterializeObjectMember(long.MaxValue.ToString());

            value.Should().BeOfType<long>().And.Be(long.MaxValue);
        }

        // INVARIANT: a JSON non-integer number materializes to double, matching Newtonsoft.
        [Fact]
        public void MustMaterializeNonIntegerNumberAsDouble()
        {
            var value = MaterializeObjectMember("3.5");

            value.Should().BeOfType<double>().And.Be(3.5d);
        }

        [Fact]
        public void MustMaterializePlainStringAsString()
        {
            var value = MaterializeObjectMember("\"plain-text\"");

            value.Should().BeOfType<string>().And.Be("plain-text");
        }

        // INVARIANT: a strict ISO-8601 datetime string materializes to DateTime (TryGetDateTime),
        // matching Newtonsoft's default DateParseHandling.
        [Fact]
        public void MustMaterializeIso8601StringAsDateTime()
        {
            var value = MaterializeObjectMember("\"2026-06-07T12:00:00Z\"");

            value.Should().BeOfType<DateTime>();
            ((DateTime)value).ToUniversalTime()
                .Should().Be(new DateTime(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc));
        }

        // A non-ISO date-like string (TryGetDateTime is strict) stays a string — not over-coerced.
        [Fact]
        public void MustMaterializeNonIsoDateLikeStringAsString()
        {
            var value = MaterializeObjectMember("\"06/07/2026\"");

            value.Should().BeOfType<string>().And.Be("06/07/2026");
        }

        // GUID PARITY ORACLE: a Guid-shaped string stays a STRING, never a Guid (no coercion).
        [Fact]
        public void MustMaterializeGuidShapedStringAsStringNotGuid()
        {
            const string guidShaped = "3fa85f64-5717-4562-b3fc-2c963f66afa6";

            var value = MaterializeObjectMember("\"" + guidShaped + "\"");

            value.Should().BeOfType<string>().And.Be(guidShaped);
        }

        [Fact]
        public void MustMaterializeTrueAsBool()
        {
            var value = MaterializeObjectMember("true");

            value.Should().BeOfType<bool>().And.Be(true);
        }

        [Fact]
        public void MustMaterializeFalseAsBool()
        {
            var value = MaterializeObjectMember("false");

            value.Should().BeOfType<bool>().And.Be(false);
        }

        [Fact]
        public void MustMaterializeNullAsNull()
        {
            var value = MaterializeObjectMember("null");

            value.Should().BeNull();
        }

        // INVARIANT: a JSON object materializes to a navigable Dictionary<string, object> whose leaves
        // are recursively materialized (nested int -> long, nested ISO -> DateTime).
        [Fact]
        public void MustMaterializeJsonObjectAsNavigableDictionaryWithRecursivelyMaterializedLeaves()
        {
            var value = MaterializeObjectMember(
                "{\"count\":7,\"name\":\"abc\",\"at\":\"2026-06-07T12:00:00Z\"}");

            var dictionary = value.Should().BeAssignableTo<IDictionary<string, object>>().Subject;
            dictionary["count"].Should().BeOfType<long>().And.Be(7L);
            dictionary["name"].Should().BeOfType<string>().And.Be("abc");
            dictionary["at"].Should().BeOfType<DateTime>();
        }

        // INVARIANT: a nested JSON object materializes to a nested Dictionary (object -> Dictionary at depth).
        [Fact]
        public void MustMaterializeNestedJsonObjectAsNestedDictionary()
        {
            var value = MaterializeObjectMember("{\"outer\":{\"inner\":1}}");

            var outer = value.Should().BeAssignableTo<IDictionary<string, object>>().Subject;
            var inner = outer["outer"].Should().BeAssignableTo<IDictionary<string, object>>().Subject;
            inner["inner"].Should().BeOfType<long>().And.Be(1L);
        }

        // INVARIANT: a JSON array materializes to a navigable List<object> with recursively-materialized
        // elements (mixed primitives).
        [Fact]
        public void MustMaterializeJsonArrayAsNavigableListWithMaterializedElements()
        {
            var value = MaterializeObjectMember("[1,\"two\",true,3.5]");

            var list = value.Should().BeAssignableTo<IList<object>>().Subject;
            list.Should().HaveCount(4);
            list[0].Should().BeOfType<long>().And.Be(1L);
            list[1].Should().BeOfType<string>().And.Be("two");
            list[2].Should().BeOfType<bool>().And.Be(true);
            list[3].Should().BeOfType<double>().And.Be(3.5d);
        }

        // An array of objects: each element materializes to a Dictionary.
        [Fact]
        public void MustMaterializeArrayOfObjectsAsListOfDictionaries()
        {
            var value = MaterializeObjectMember("[{\"id\":1},{\"id\":2}]");

            var list = value.Should().BeAssignableTo<IList<object>>().Subject;
            list.Should().HaveCount(2);
            list[0].Should().BeAssignableTo<IDictionary<string, object>>()
                .Which["id"].Should().BeOfType<long>().And.Be(1L);
            list[1].Should().BeAssignableTo<IDictionary<string, object>>()
                .Which["id"].Should().BeOfType<long>().And.Be(2L);
        }

        // Nested arrays materialize to nested Lists.
        [Fact]
        public void MustMaterializeNestedArraysAsNestedLists()
        {
            var value = MaterializeObjectMember("[[1,2],[3,4]]");

            var outer = value.Should().BeAssignableTo<IList<object>>().Subject;
            outer.Should().HaveCount(2);
            var first = outer[0].Should().BeAssignableTo<IList<object>>().Subject;
            first[0].Should().BeOfType<long>().And.Be(1L);
            first[1].Should().BeOfType<long>().And.Be(2L);
        }

        // DEEP nesting: object -> array -> object -> primitives. Full recursive materialization at depth.
        [Fact]
        public void MustRecursivelyMaterializeDeeplyNestedStructure()
        {
            var value = MaterializeObjectMember(
                "{\"rows\":[{\"cells\":[{\"v\":9,\"at\":\"2026-06-07T12:00:00Z\"}]}]}");

            var root = value.Should().BeAssignableTo<IDictionary<string, object>>().Subject;
            var rows = root["rows"].Should().BeAssignableTo<IList<object>>().Subject;
            var firstRow = rows[0].Should().BeAssignableTo<IDictionary<string, object>>().Subject;
            var cells = firstRow["cells"].Should().BeAssignableTo<IList<object>>().Subject;
            var firstCell = cells[0].Should().BeAssignableTo<IDictionary<string, object>>().Subject;

            firstCell["v"].Should().BeOfType<long>().And.Be(9L);
            firstCell["at"].Should().BeOfType<DateTime>();
        }

        [Fact]
        public void MustMaterializeEmptyObjectAsEmptyDictionary()
        {
            var value = MaterializeObjectMember("{}");

            value.Should().BeAssignableTo<IDictionary<string, object>>().Which.Should().BeEmpty();
        }

        [Fact]
        public void MustMaterializeEmptyArrayAsEmptyList()
        {
            var value = MaterializeObjectMember("[]");

            value.Should().BeAssignableTo<IList<object>>().Which.Should().BeEmpty();
        }

        // ------------------------------------------------------------------------------------
        // B. WRITE — byte-parity, non-reentrant pass-through, NO stack overflow
        // ------------------------------------------------------------------------------------
        //
        // Each case asserts the produced JSON equals what STJ produces for the same RUNTIME value
        // when serialized WITHOUT the object-typed indirection (i.e. typed directly), proving the
        // converter's Write path is a transparent runtime-typed pass-through and never re-enters
        // itself (which would StackOverflow).

        [Fact]
        public void MustWriteStringObjectMemberByteIdenticalToTypedSerialize()
        {
            var json = SerializeObjectMember("hello");

            json.Should().Be("{\"Value\":\"hello\"}");
        }

        [Fact]
        public void MustWriteLongObjectMemberByteIdenticalToTypedSerialize()
        {
            var json = SerializeObjectMember(42L);

            json.Should().Be("{\"Value\":42}");
        }

        [Fact]
        public void MustWriteDoubleObjectMemberByteIdenticalToTypedSerialize()
        {
            var json = SerializeObjectMember(3.5d);

            json.Should().Be("{\"Value\":3.5}");
        }

        [Fact]
        public void MustWriteBoolObjectMemberByteIdenticalToTypedSerialize()
        {
            var json = SerializeObjectMember(true);

            json.Should().Be("{\"Value\":true}");
        }

        [Fact]
        public void MustWriteDictionaryObjectMemberByteIdenticalToTypedSerialize()
        {
            var dictionary = new Dictionary<string, object> { ["a"] = 1L, ["b"] = "x" };

            var json = SerializeObjectMember(dictionary);

            // Equals a direct serialize of the same Dictionary runtime value through the same options.
            json.Should().Be("{\"Value\":" + JsonSerializer.Serialize(dictionary, ChatterJson.Options) + "}");
            json.Should().Be("{\"Value\":{\"a\":1,\"b\":\"x\"}}");
        }

        [Fact]
        public void MustWriteListObjectMemberByteIdenticalToTypedSerialize()
        {
            var list = new List<object> { 1L, "x", true };

            var json = SerializeObjectMember(list);

            json.Should().Be("{\"Value\":" + JsonSerializer.Serialize(list, ChatterJson.Options) + "}");
            json.Should().Be("{\"Value\":[1,\"x\",true]}");
        }

        [Fact]
        public void MustWriteDateTimeObjectMemberByteIdenticalToTypedSerialize()
        {
            var instant = new DateTime(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc);

            var json = SerializeObjectMember(instant);

            // The DateTime runtime value serializes identically whether at an object position or typed.
            json.Should().Be("{\"Value\":" + JsonSerializer.Serialize(instant, ChatterJson.Options) + "}");
        }

        // A bare System.Object instance at an object position serializes to "{}" — the explicit
        // empty-object branch that prevents runtime-type dispatch re-entering the converter (overflow).
        [Fact]
        public void MustWriteBareObjectAsEmptyObjectWithoutStackOverflow()
        {
            var json = SerializeObjectMember(new object());

            json.Should().Be("{\"Value\":{}}");
        }

        // A null object member serializes to JSON null.
        [Fact]
        public void MustWriteNullObjectMemberAsJsonNull()
        {
            var json = SerializeObjectMember(null);

            json.Should().Be("{\"Value\":null}");
        }

        // A risky-character string value rides through the object member and is still encoded by the
        // ChatterJsonEncoder exactly as a typed string would be (astral emoji stays literal UTF-8) —
        // byte-identical to the golden encoder behavior, since Write dispatches on the runtime string
        // type through the same options. Guards against the converter altering the encoder path.
        [Fact]
        public void MustWriteRiskyStringObjectMemberByteIdenticalToTypedSerialize()
        {
            const string risky = "+<>&'/\"\\éü\U0001F600";

            var json = SerializeObjectMember(risky);

            json.Should().Be("{\"Value\":" + JsonSerializer.Serialize(risky, ChatterJson.Options) + "}");
            json.Should().Contain("\U0001F600");
            json.Should().NotContain("\\u");
        }

        // ------------------------------------------------------------------------------------
        // C. ROUND-TRIP fidelity — CLR values survive serialize -> deserialize
        // ------------------------------------------------------------------------------------

        // A Dictionary<string, object> with mixed CLR values round-trips: types survive per the
        // untyped-read parity rules. NOTE: an int CLR value writes a JSON number and reads back as
        // long (documented expected Newtonsoft-parity untyped widening), so the dictionary seeds a
        // long for the integer case to assert the post-round-trip CLR type directly.
        [Fact]
        public void MustRoundTripMixedDictionaryPreservingClrTypes()
        {
            var instant = new DateTime(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc);
            var original = new Dictionary<string, object>
            {
                ["str"] = "abc",
                ["num"] = 5L,
                ["when"] = instant,
                ["flag"] = true,
                ["nested"] = new Dictionary<string, object> { ["k"] = "v" },
                ["list"] = new List<object> { 1L, 2L },
            };

            var json = JsonSerializer.Serialize(original, ChatterJson.Options);
            var roundTripped = JsonSerializer.Deserialize<Dictionary<string, object>>(json, ChatterJson.Options);

            roundTripped["str"].Should().BeOfType<string>().And.Be("abc");
            roundTripped["num"].Should().BeOfType<long>().And.Be(5L);
            roundTripped["when"].Should().BeOfType<DateTime>();
            ((DateTime)roundTripped["when"]).ToUniversalTime().Should().Be(instant);
            roundTripped["flag"].Should().BeOfType<bool>().And.Be(true);

            var nested = roundTripped["nested"].Should().BeAssignableTo<IDictionary<string, object>>().Subject;
            nested["k"].Should().BeOfType<string>().And.Be("v");

            var list = roundTripped["list"].Should().BeAssignableTo<IList<object>>().Subject;
            list.Should().Equal(1L, 2L);
        }

        // DOCUMENTED PARITY: serializing an int CLR value at an object position writes a JSON number,
        // which reads back as long (Int64). This is the expected untyped Newtonsoft-parity widening —
        // an untyped read has no original-type memory, so the integer materializes to long.
        [Fact]
        public void MustRoundTripIntClrValueBackAsLong()
        {
            var json = SerializeObjectMember(7); // boxed int -> JSON number "7"

            var roundTripped = MaterializeObjectMember(
                JsonSerializer.SerializeToElement(7).GetRawText());

            // direct: the serialize wrote a plain number, and the read materialized it to long.
            json.Should().Be("{\"Value\":7}");
            roundTripped.Should().BeOfType<long>().And.Be(7L);
        }

        // ------------------------------------------------------------------------------------
        // E. COMPOSITION with the other ChatterJson.Options modifiers/converters
        // ------------------------------------------------------------------------------------

        // A DTO with a PRIVATE-setter member AND an object member: EnableNonPublicSetters and the
        // converter compose — both the private-setter string and the materialized object member bind.
        private class PrivateSetterWithObjectMemberPoco
        {
            public string Name { get; private set; }
            public object Value { get; private set; }
        }

        [Fact]
        public void MustComposePrivateSetterModifierWithObjectConverter()
        {
            var poco = JsonSerializer.Deserialize<PrivateSetterWithObjectMemberPoco>(
                "{\"Name\":\"abc\",\"Value\":{\"id\":1}}", ChatterJson.Options);

            poco.Name.Should().Be("abc");
            var value = poco.Value.Should().BeAssignableTo<IDictionary<string, object>>().Subject;
            value["id"].Should().BeOfType<long>().And.Be(1L);
        }

        // A getter-only collection of `object` (Populate) populates with materialized elements:
        // PreferredObjectCreationHandling.Populate and the converter compose.
        private class GetterOnlyObjectCollectionPoco
        {
            public List<object> Items { get; } = new();
        }

        [Fact]
        public void MustComposePopulateHandlingWithObjectConverterForGetterOnlyCollection()
        {
            var poco = JsonSerializer.Deserialize<GetterOnlyObjectCollectionPoco>(
                "{\"Items\":[1,\"two\",{\"k\":\"v\"}]}", ChatterJson.Options);

            poco.Items.Should().HaveCount(3);
            poco.Items[0].Should().BeOfType<long>().And.Be(1L);
            poco.Items[1].Should().BeOfType<string>().And.Be("two");
            poco.Items[2].Should().BeAssignableTo<IDictionary<string, object>>()
                .Which["k"].Should().BeOfType<string>().And.Be("v");
        }

        // A QUOTED number ("3") at an object position materializes as STRING "3" — the converter sees
        // JsonTokenType.String and never coerces it to a number. This matches Newtonsoft's untyped read
        // (a quoted value is a string). Guards against any test expecting a long here.
        [Fact]
        public void MustMaterializeQuotedNumberAtObjectPositionAsString()
        {
            var value = MaterializeObjectMember("\"3\"");

            value.Should().BeOfType<string>().And.Be("3");
        }
    }
}
