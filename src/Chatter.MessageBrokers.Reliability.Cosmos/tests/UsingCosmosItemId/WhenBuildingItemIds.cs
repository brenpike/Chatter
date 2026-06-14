using Chatter.MessageBrokers.Reliability.Cosmos;
using FluentAssertions;
using System;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingCosmosItemId
{
    public class WhenBuildingItemIds : Testing.Core.Context
    {
        [Theory]
        [InlineData("a/b")]
        [InlineData("a\\b")]
        [InlineData("a?b")]
        [InlineData("a#b")]
        public void MustRejectKindContainingCosmosForbiddenCharacters(string unsafeKind)
        {
            // The kind is emitted verbatim as the id prefix; a forbidden character would yield an invalid Cosmos id, so
            // Build must reject it rather than compose an unsafe item id.
            Action act = () => CosmosItemId.Build(unsafeKind, "message-id");

            act.Should().Throw<ArgumentException>();
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MustRejectNullOrWhitespaceKind(string kind)
        {
            Action act = () => CosmosItemId.Build(kind, "message-id");

            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void MustRejectNullMessageId()
        {
            Action act = () => CosmosItemId.Build(CosmosItemId.OutboxKind, null);

            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void MustEncodeForbiddenMessageIdCharactersToIdSafeForm()
        {
            var id = CosmosItemId.ForOutbox("msg/with?reserved#and\\chars");

            id.Should().StartWith("outbox:");
            id.Should().NotContainAny("/", "\\", "?", "#");
        }

        [Fact]
        public void MustEncodeDeterministically()
        {
            CosmosItemId.ForOutbox("same-id").Should().Be(CosmosItemId.ForOutbox("same-id"));
            CosmosItemId.ForOutbox("a").Should().NotBe(CosmosItemId.ForOutbox("b"));
        }
    }
}
