using Chatter.MessageBrokers.Reliability.Cosmos;
using FluentAssertions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingCosmosOutboxDocument
{
    public class WhenFilteringPendingOutbox : Testing.Core.Context
    {
        // Builds a wire-faithful outbox JsonElement the public predicate reads. By default it is a genuine Chatter
        // outbox doc: _chatterType="outbox", status="pending", and id = CosmosItemId.ForOutbox(MessageId). Each parameter
        // lets a single field diverge so a test isolates exactly one failing predicate clause; a null chatterType/status
        // omits that property entirely (the missing-field cases). When no explicit id is given the id is the deterministic
        // outbox id for the supplied MessageId, so the id-consistency clause passes and only the field under test varies.
        private static JsonElement Document(
            string chatterType = CosmosItemId.OutboxKind,
            string status = CosmosOutboxDocument.StatusPending,
            string messageId = "msg-1",
            string id = null)
        {
            var node = new JsonObject
            {
                [CosmosOutboxDocument.IdField] = id ?? CosmosItemId.ForOutbox(messageId),
                [CosmosOutboxDocument.MessageIdField] = messageId,
                [CosmosOutboxDocument.DestinationField] = "dest",
                [CosmosOutboxDocument.MessageBodyField] = "{}",
                [CosmosOutboxDocument.MessageContentTypeField] = "application/json",
                [CosmosOutboxDocument.MessageContextField] = "{}",
            };
            if (chatterType is not null)
            {
                node[CosmosOutboxDocument.DiscriminatorField] = chatterType;
            }
            if (status is not null)
            {
                node[CosmosOutboxDocument.StatusField] = status;
            }
            return Parse(node.ToJsonString());
        }

        private static JsonElement Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        [Fact]
        public void MustReturnTrueForOutboxPendingDocumentWhoseIdIsChatterMinted()
        {
            CosmosOutboxDocument.IsPendingOutbox(Document()).Should().BeTrue(
                "an outbox-discriminated, pending document whose id == ForOutbox(MessageId) is one Chatter minted and is pending");
        }

        [Theory]
        [InlineData(CosmosItemId.InboxKind)] // an inbox marker is not an outbox document
        [InlineData("aggregate")]            // a domain document is not an outbox document
        [InlineData("other")]                // any foreign discriminator is not an outbox document
        [InlineData(null)]                   // a document with no discriminator is not an outbox document
        public void MustReturnFalseWhenDiscriminatorIsNotOutbox(string chatterType)
        {
            CosmosOutboxDocument.IsPendingOutbox(Document(chatterType: chatterType)).Should().BeFalse(
                "only a document whose _chatterType discriminator equals the outbox kind is an outbox document");
        }

        [Fact]
        public void MustReturnFalseWhenStatusIsDelivered()
        {
            CosmosOutboxDocument.IsPendingOutbox(Document(status: CosmosOutboxDocument.StatusDelivered)).Should().BeFalse(
                "an already-delivered outbox document is not pending");
        }

        [Theory]
        [InlineData(null)] // missing status
        [InlineData("")]   // empty status
        public void MustReturnFalseWhenStatusIsMissingOrEmpty(string status)
        {
            CosmosOutboxDocument.IsPendingOutbox(Document(status: status)).Should().BeFalse(
                "a malformed outbox document with a missing or empty status is not pending");
        }

        [Fact]
        public void MustReturnFalseWhenIdIsForeign()
        {
            CosmosOutboxDocument.IsPendingOutbox(Document(id: "app-authored-id")).Should().BeFalse(
                "an outbox-shaped document whose id is not ForOutbox(MessageId) is not Chatter-minted");
        }

        [Fact]
        public void MustReturnFalseWhenIdIsForoutboxOfADifferentMessageId()
        {
            string foreignButReservedId = CosmosItemId.ForOutbox("some-other-message");

            CosmosOutboxDocument.IsPendingOutbox(Document(messageId: "msg-1", id: foreignButReservedId)).Should().BeFalse(
                "the id must be ForOutbox of THIS document's MessageId, not of a different message id");
        }
    }
}
