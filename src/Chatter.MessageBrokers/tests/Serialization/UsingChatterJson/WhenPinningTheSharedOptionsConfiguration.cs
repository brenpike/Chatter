using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Serialization.UsingChatterJson
{
    // ====================================================================================
    // GOLDEN CONFIGURATION for the module-internal ChatterJson.Options.
    //
    // The settings that make up the wire contract are pinned here by value, so a change to
    // any one of them has to be a deliberate edit to this file rather than a silent drift in
    // what goes on the wire.
    //
    // The read-only assertions below guard the same drift from the other direction: a
    // serialization site inside this trust boundary cannot reconfigure the shared instance at
    // runtime. Custom serialization is done with an IBrokeredMessageBodyConverter selected by
    // IBodyConverterFactory, which supplies its own options.
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
    }
}
