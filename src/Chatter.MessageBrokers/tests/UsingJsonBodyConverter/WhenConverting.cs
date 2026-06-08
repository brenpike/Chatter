using FluentAssertions;
using System;
using System.Collections.Generic;
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

        // Body DTO carrying object / Dictionary<string, object> / List<object> members. STJ would
        // surface raw JsonElement at each object-typed read position; the global
        // MaterializingObjectConverter on ChatterJson.Options must materialize them to CLR types so a
        // downstream consumer reading Payload / Tags / Items does not hit a JsonElement cast failure.
        // This is the body-converter face of the converter — the open Codex P2 (JsonBodyConverter.cs:11).
        private class ObjectMembersBodyPoco
        {
            public object Payload { get; set; }
            public Dictionary<string, object> Tags { get; set; }
            public List<object> Items { get; set; }
        }

        // Immutable command/event-style DTO: members exposed with PRIVATE setters only.
        // Newtonsoft populated private setters by default; the STJ port must too (read-path).
        private class PrivateSetterBodyPoco
        {
            public string Name { get; private set; }
            public int Value { get; private set; }
        }

        // Message contract exposing an INITIALIZED getter-only collection. Newtonsoft populated
        // the existing collection through the getter; the STJ port must too (read-path).
        private class GetterOnlyCollectionBodyPoco
        {
            public List<string> Items { get; } = new();
        }

        // Immutable command/event-style DTO whose ONLY default constructor is PRIVATE. Newtonsoft
        // instantiated the type via the non-public ctor and then populated the private setters; STJ
        // cannot create the instance (no public parameterless ctor, no [JsonConstructor]) unless the
        // EnableNonPublicParameterlessConstructor contract modifier wires CreateObject. Members are
        // private-set, so EnableNonPublicSetters then populates them once the instance exists.
        private class PrivateCtorDto
        {
            private PrivateCtorDto() { }
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

        // PARITY: Newtonsoft populated an EXISTING initialized getter-only collection through its
        // getter (add into the already-constructed instance). STJ defaults object-creation handling
        // to Replace and EnableNonPublicSetters skips a setter-less member, so without
        // PreferredObjectCreationHandling = Populate on the shared ChatterJson.Options the DTO would
        // arrive with an EMPTY Items collection. Populate restores the inbound array values.
        [Fact]
        public void MustPopulateGetterOnlyCollectionsOnDeserialize()
        {
            var bytes = _sut.GetBytes("{\"Items\":[\"a\",\"b\"]}");

            var result = _sut.Convert<GetterOnlyCollectionBodyPoco>(bytes);

            result.Items.Should().Equal("a", "b");
        }

        // PARITY: Newtonsoft instantiated a DTO whose ONLY default constructor is non-public and then
        // populated its (private) setters. STJ's JsonSerializer.Deserialize<T> cannot create such an
        // instance, so EnableNonPublicSetters alone never gets a chance to run — the
        // EnableNonPublicParameterlessConstructor contract modifier on the shared ChatterJson.Options
        // must wire JsonTypeInfo.CreateObject to the non-public parameterless ctor. The instance is
        // then created and its private Name/Value populated (without the modifier this deserialize
        // fails: STJ cannot construct the type).
        [Fact]
        public void MustActivateNonPublicParameterlessConstructorOnDeserialize()
        {
            var bytes = _sut.GetBytes("{\"Name\":\"abc\",\"Value\":42}");

            var result = _sut.Convert<PrivateCtorDto>(bytes);

            result.Should().NotBeNull();
            result.Name.Should().Be("abc");
            result.Value.Should().Be(42);
        }

        // UNAFFECTED: a type with a PUBLIC parameterless ctor already has a CreateObject, so the
        // non-public-ctor modifier must skip it and leave normal construction untouched.
        [Fact]
        public void MustLeavePublicConstructorTypesUnaffected()
        {
            var bytes = _sut.GetBytes("{\"Name\":\"abc\",\"Value\":42}");

            var result = _sut.Convert<BodyPoco>(bytes);

            result.Name.Should().Be("abc");
            result.Value.Should().Be(42);
        }

        // OPEN CODEX P2 RESOLUTION (JsonBodyConverter.cs:11): a body DTO whose members are object-typed
        // must arrive with CLR-typed (materialized) values, NOT raw JsonElement, after Convert<TBody>.
        // The Payload object member, the Dictionary<string, object> member, and the List<object> member
        // each route through the global MaterializingObjectConverter on the shared ChatterJson.Options,
        // so a consumer reading them downstream gets long/string/DateTime/nested-collections rather than
        // a JsonElement cast failure — restoring Newtonsoft's untyped-read fidelity over UTF-8 bodies.
        [Fact]
        public void MustMaterializeObjectTypedBodyMembersToClrTypes()
        {
            var bytes = _sut.GetBytes(
                "{\"Payload\":{\"id\":1,\"when\":\"2026-06-07T12:00:00Z\"}," +
                "\"Tags\":{\"k\":\"v\",\"n\":7}," +
                "\"Items\":[1,\"two\",true]}");

            var result = _sut.Convert<ObjectMembersBodyPoco>(bytes);

            // object Payload -> navigable Dictionary with recursively-materialized leaves.
            var payload = result.Payload.Should().BeAssignableTo<IDictionary<string, object>>().Subject;
            payload["id"].Should().BeOfType<long>().And.Be(1L);
            payload["when"].Should().BeOfType<DateTime>();

            // Dictionary<string, object> member -> values materialized to CLR types (NOT JsonElement).
            result.Tags["k"].Should().BeOfType<string>().And.Be("v");
            result.Tags["n"].Should().BeOfType<long>().And.Be(7L);

            // List<object> member -> elements materialized to CLR types (NOT JsonElement).
            result.Items[0].Should().BeOfType<long>().And.Be(1L);
            result.Items[1].Should().BeOfType<string>().And.Be("two");
            result.Items[2].Should().BeOfType<bool>().And.Be(true);
        }

        // A body that IS a Dictionary<string, object> (the whole payload, not a member): every value
        // materializes to its CLR type through the converter over the UTF-8 JsonBodyConverter path.
        [Fact]
        public void MustMaterializeBodyThatIsDictionaryOfObjectToClrTypes()
        {
            var bytes = _sut.GetBytes("{\"count\":3,\"name\":\"abc\",\"flag\":true}");

            var result = _sut.Convert<Dictionary<string, object>>(bytes);

            result["count"].Should().BeOfType<long>().And.Be(3L);
            result["name"].Should().BeOfType<string>().And.Be("abc");
            result["flag"].Should().BeOfType<bool>().And.Be(true);
        }
    }
}
