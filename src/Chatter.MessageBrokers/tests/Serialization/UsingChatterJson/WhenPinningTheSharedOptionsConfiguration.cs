using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Serialization.UsingChatterJson
{
    // ====================================================================================
    // GOLDEN CONFIGURATION for the shared ChatterJson.Options.
    //
    // Options is a CLOSED contract: it is sealed at construction and is not an extension
    // point. Two things must therefore hold and stay held. First, the instance really is
    // read-only, so no caller can redefine the wire contract process-wide for every message
    // sent or received. Second, the settings that MAKE UP that contract are pinned here by
    // value, so a change to any one of them has to be a deliberate edit to this file rather
    // than a silent drift in what goes on the wire.
    //
    // The supported seam for custom serialization is an IBrokeredMessageBodyConverter
    // selected by IBodyConverterFactory; a caller wanting different System.Text.Json settings
    // copy-constructs its own instance from this one (pinned below).
    // ====================================================================================
    public class WhenPinningTheSharedOptionsConfiguration : Testing.Core.Context
    {
        [Fact]
        public void MustExposeTheSharedOptionsAsReadOnly()
        {
            ChatterJson.Options.IsReadOnly.Should().BeTrue();
        }

        [Fact]
        public void MustRefuseToRegisterAConverterOnTheSharedOptions()
        {
            var addConverter = () => ChatterJson.Options.Converters.Add(new JsonStringEnumConverter());

            addConverter.Should().Throw<InvalidOperationException>();
        }

        [Fact]
        public void MustRefuseToReconfigureTheSharedOptions()
        {
            var reconfigure = () => ChatterJson.Options.WriteIndented = true;

            reconfigure.Should().Throw<InvalidOperationException>();
        }

        [Fact]
        public void MustUseTheChatterJsonEncoder()
        {
            // Wire-parity encoder: forces all non-ASCII literal, matching the prior
            // Newtonsoft.Json output byte-for-byte.
            ChatterJson.Options.Encoder.Should().BeSameAs(ChatterJsonEncoder.Shared);
        }

        [Fact]
        public void MustRegisterExactlyTheThreeParityConverters()
        {
            ChatterJson.Options.Converters.Should().HaveCount(3);
            ChatterJson.Options.Converters[0].Should().BeOfType<MaterializingObjectConverter>();
            ChatterJson.Options.Converters[1].Should().BeOfType<NumericWriteStringReadEnumConverter>();
            ChatterJson.Options.Converters[2].Should().BeOfType<NewtonsoftLenientBooleanConverter>();
        }

        [Fact]
        public void MustReadPropertyNamesCaseInsensitively()
        {
            ChatterJson.Options.PropertyNameCaseInsensitive.Should().BeTrue();
        }

        [Fact]
        public void MustIncludePublicFields()
        {
            ChatterJson.Options.IncludeFields.Should().BeTrue();
        }

        [Fact]
        public void MustPopulateAlreadyInitializedMembers()
        {
            ChatterJson.Options.PreferredObjectCreationHandling
                .Should().Be(JsonObjectCreationHandling.Populate);
        }

        [Fact]
        public void MustReadNumbersFromStrings()
        {
            ChatterJson.Options.NumberHandling.Should().Be(JsonNumberHandling.AllowReadingFromString);
        }

        [Fact]
        public void MustAllowTrailingCommasOnRead()
        {
            ChatterJson.Options.AllowTrailingCommas.Should().BeTrue();
        }

        [Fact]
        public void MustSkipCommentsOnRead()
        {
            ChatterJson.Options.ReadCommentHandling.Should().Be(JsonCommentHandling.Skip);
        }

        [Fact]
        public void MustLeaveWrittenPropertyNamesPascalCase()
        {
            // No naming policy — the declared PascalCase name is what goes on the wire.
            ChatterJson.Options.PropertyNamingPolicy.Should().BeNull();
        }

        [Fact]
        public void MustWriteCompactJson()
        {
            ChatterJson.Options.WriteIndented.Should().BeFalse();
        }

        [Fact]
        public void MustResolveContractsThroughTheParityTypeInfoResolver()
        {
            // The non-public-setter and non-public-parameterless-constructor modifiers hang off
            // this resolver; losing it drops both read-parity behaviours silently.
            ChatterJson.Options.TypeInfoResolver.Should().NotBeNull();
        }

        [Fact]
        public void MustSupportCopyConstructingAModifiableInstanceFromTheSharedOptions()
        {
            // The documented migration for a caller that used to mutate the shared options.
            var copy = new JsonSerializerOptions(ChatterJson.Options);

            copy.IsReadOnly.Should().BeFalse();
            copy.Encoder.Should().BeSameAs(ChatterJson.Options.Encoder);
            copy.Converters.Should().HaveCount(3);

            var addConverter = () => copy.Converters.Add(new JsonStringEnumConverter());

            addConverter.Should().NotThrow();
            ChatterJson.Options.Converters.Should().HaveCount(3);
        }
    }
}
