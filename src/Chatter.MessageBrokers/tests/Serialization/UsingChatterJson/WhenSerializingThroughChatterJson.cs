using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Serialization.UsingChatterJson
{
    // ====================================================================================
    // The PUBLIC serialization capability of Chatter.MessageBrokers.
    //
    // ChatterJson publishes two generic forwarders and nothing else; the JsonSerializerOptions
    // that back them stay internal to the assembly. The closed-by-construction guard at the
    // bottom of this file is what keeps it that way — a configuration object reachable from
    // outside the assembly is what forced sibling packages to depend on the options instance in
    // the first place.
    // ====================================================================================
    public class WhenSerializingThroughChatterJson : Testing.Core.Context
    {
        private class Poco
        {
            public string Name { get; set; }
            public int Value { get; set; }
        }

        private sealed class DetailedPoco : Poco
        {
            public string Detail { get; set; }
        }

        private enum BookingStatus
        {
            Pending = 0,
            Booked = 1,
        }

        private sealed class LenientPoco
        {
            public bool Enabled { get; set; }
            public BookingStatus Status { get; set; }
        }

        [Fact]
        public void MustWriteAnObjectTypedPocoExactlyAsTheSharedOptionsDo()
        {
            object body = new Poco { Name = "abc", Value = 42 };

            ChatterJson.Serialize(body).Should().Be(JsonSerializer.Serialize(body, ChatterJson.Options));
        }

        [Fact]
        public void MustWriteAMessageContextExactlyAsTheSharedOptionsDo()
        {
            // The shape the outbox persists a brokered message's context as.
            IDictionary<string, object> messageContext = new Dictionary<string, object>
            {
                ["MessageId"] = "abc",
                ["Attempts"] = 3,
                ["Enabled"] = true,
            };

            ChatterJson.Serialize(messageContext).Should().Be(JsonSerializer.Serialize(messageContext, ChatterJson.Options));
        }

        [Fact]
        public void MustWriteABaseTypedRootByItsDeclaredType()
        {
            // Serialize forwards the caller's DECLARED type, so a base-typed root writes the base's
            // members only. A non-generic Serialize(object) would widen the root to its runtime type,
            // adding derived members and reordering properties.
            Poco body = new DetailedPoco { Name = "abc", Value = 42, Detail = "derived" };

            ChatterJson.Serialize(body).Should().Be("{\"Name\":\"abc\",\"Value\":42}");
        }

        [Fact]
        public void MustReadBackABodyItWrote()
        {
            var original = new Poco { Name = "abc", Value = 42 };

            var roundTripped = ChatterJson.Deserialize<Poco>(ChatterJson.Serialize(original));

            roundTripped.Name.Should().Be("abc");
            roundTripped.Value.Should().Be(42);
        }

        [Fact]
        public void MustReadAQuotedBoolean()
        {
            // Newtonsoft read-leniency parity reaches the caller through the facade, not only through
            // the options instance.
            var deserialized = ChatterJson.Deserialize<LenientPoco>("{\"Enabled\":\"true\"}");

            deserialized.Enabled.Should().BeTrue();
        }

        [Fact]
        public void MustReadAnEnumName()
        {
            var deserialized = ChatterJson.Deserialize<LenientPoco>("{\"Status\":\"Booked\"}");

            deserialized.Status.Should().Be(BookingStatus.Booked);
        }

        [Fact]
        public void MustPublishTheSerializationCapability()
        {
            typeof(ChatterJson).IsPublic.Should().BeTrue();
        }

        [Fact]
        public void MustKeepTheSerializerConfigurationOffThePublicSurface()
        {
            var membersExposingOptions = typeof(ChatterJson)
                .GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
                .Where(MentionsSerializerOptions)
                .Select(member => member.Name);

            membersExposingOptions.Should().BeEmpty();
        }

        private static bool MentionsSerializerOptions(MemberInfo member)
            => SignatureTypesOf(member)
                .SelectMany(FlattenType)
                .Any(type => type == typeof(JsonSerializerOptions));

        private static IEnumerable<Type> SignatureTypesOf(MemberInfo member)
        {
            switch (member)
            {
                case MethodBase method:
                    foreach (var parameter in method.GetParameters())
                    {
                        yield return parameter.ParameterType;
                    }

                    if (method is MethodInfo methodInfo)
                    {
                        yield return methodInfo.ReturnType;
                    }

                    break;

                case FieldInfo field:
                    yield return field.FieldType;
                    break;

                case PropertyInfo property:
                    yield return property.PropertyType;

                    foreach (var indexParameter in property.GetIndexParameters())
                    {
                        yield return indexParameter.ParameterType;
                    }

                    break;

                case Type nestedType:
                    yield return nestedType;
                    break;
            }
        }

        // A signature mentions the options type by ref, by array, or as a generic argument just as
        // surely as it does directly, so flatten before comparing.
        private static IEnumerable<Type> FlattenType(Type type)
        {
            yield return type;

            if (type.HasElementType)
            {
                foreach (var elementType in FlattenType(type.GetElementType()))
                {
                    yield return elementType;
                }
            }

            foreach (var genericArgument in type.GetGenericArguments())
            {
                foreach (var flattenedArgument in FlattenType(genericArgument))
                {
                    yield return flattenedArgument;
                }
            }
        }
    }
}
