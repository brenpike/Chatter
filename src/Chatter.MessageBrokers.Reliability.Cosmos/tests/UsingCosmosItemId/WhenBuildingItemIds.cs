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

        [Fact]
        public void MustRejectMessageIdThatExceedsCosmosIdLengthLimit()
        {
            // A message id long enough to push the encoded id over Cosmos's id-length limit would fail at batch-execution
            // time and trigger redelivery without committing, so Build must reject it at staging time instead.
            var overLimitMessageId = new string('a', CosmosItemId.MaxItemIdLength);

            Action act = () => CosmosItemId.ForOutbox(overLimitMessageId);

            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void MustAcceptMessageIdAtCosmosIdLengthLimit()
        {
            // base64 of N bytes is 4*ceil(N/3) chars (minus stripped padding); choose a raw length whose composed id
            // ("outbox:" + encoded) lands at or under the limit to prove the boundary is inclusive, not off-by-one.
            var prefixLength = CosmosItemId.OutboxKind.Length + 1;
            var encodedBudget = CosmosItemId.MaxItemIdLength - prefixLength;
            var rawBytes = (encodedBudget / 4) * 3;
            var atLimitMessageId = new string('a', rawBytes);

            var id = CosmosItemId.ForOutbox(atLimitMessageId);

            id.Length.Should().BeLessThanOrEqualTo(CosmosItemId.MaxItemIdLength);
        }

        [Theory]
        [InlineData("inbox:abc")]
        [InlineData("outbox:abc")]
        public void MustFlagReservedPrefixIds(string reservedId)
        {
            // A reserved inbox:/outbox: prefix is the enforced Chatter id-namespace; IsReserved must flag it so the
            // public atomic-write surface can reject it (the marker-409 duplicate inference depends on this exclusivity).
            CosmosItemId.IsReserved(reservedId).Should().BeTrue();
        }

        [Theory]
        [InlineData("order:abc")]
        [InlineData("app-id")]
        [InlineData("")]
        [InlineData(null)]
        public void MustNotFlagNonReservedAppId(string appId)
        {
            CosmosItemId.IsReserved(appId).Should().BeFalse();
        }

        [Theory]
        [InlineData("Inbox:abc")]
        [InlineData("OUTBOX:abc")]
        public void MustMatchReservedPrefixOrdinallyWithNoCaseNormalization(string differentCaseId)
        {
            // The reserved kinds are exact lowercase constants; the match is ordinal with no case folding, so a
            // different-case prefix is NOT reserved.
            CosmosItemId.IsReserved(differentCaseId).Should().BeFalse();
        }

        [Theory]
        [InlineData("xinbox:abc")]
        [InlineData("my-outbox:abc")]
        [InlineData("data-inbox:value")]
        public void MustMatchReservedPrefixNotSubstring(string substringId)
        {
            // Reserved is a PREFIX test on "{kind}:", not a substring search: an id that merely CONTAINS inbox:/outbox:
            // later in the string is not reserved.
            CosmosItemId.IsReserved(substringId).Should().BeFalse();
        }

        [Fact]
        public void MustDeriveReservedPrefixesFromKindConstants()
        {
            // The reserved-prefix set is derived from the kind constants (single ground truth), so a composed "{kind}:"
            // for each kind is flagged — there is no parallel literal list to drift.
            CosmosItemId.IsReserved(CosmosItemId.InboxKind + ":anything").Should().BeTrue();
            CosmosItemId.IsReserved(CosmosItemId.OutboxKind + ":anything").Should().BeTrue();
        }

        [Fact]
        public void MustHaveReservedForInboxAndForOutboxOutputs()
        {
            CosmosItemId.IsReserved(CosmosItemId.ForInbox("msg-1")).Should().BeTrue();
            CosmosItemId.IsReserved(CosmosItemId.ForOutbox("msg-1")).Should().BeTrue();
        }

        [Theory]
        [InlineData("inbox:abc")]
        [InlineData("outbox:abc")]
        public void MustThrowFromGuardNotReservedForReservedId(string reservedId)
        {
            Action act = () => CosmosItemId.GuardNotReserved(reservedId, "id");

            // The exception names the reserved namespace and the offending id.
            act.Should().Throw<ArgumentException>()
                .Which.Message.Should().Contain(reservedId);
        }

        [Theory]
        [InlineData("order:abc")]
        [InlineData("app-id")]
        [InlineData(null)]
        public void MustNotThrowFromGuardNotReservedForNonReservedId(string appId)
        {
            Action act = () => CosmosItemId.GuardNotReserved(appId, "id");

            act.Should().NotThrow();
        }
    }
}
