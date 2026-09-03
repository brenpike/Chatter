using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Cosmos;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingCosmosOutboxRelay
{
    public class WhenDrainingOutbox : Testing.Core.Context
    {
        private const string InfrastructureType = "test-infra";
        private static readonly IReadOnlyList<string> PartitionKeyPath = Array.AsReadOnly(new[] { "/tenantId" });

        // Builds the exact outbox wire document the Document-Tier Batch-Lifecycle Behavior writes, as a JsonElement the
        // relay reads. The MessageContext is serialized through ChatterJson.Options (EF parity) so the relay's
        // MaterializePersistedContext round-trips it faithfully.
        private static JsonElement OutboxDocument(string messageId, string destination, object body, string tenantId)
        {
            var converter = new JsonBodyConverter();
            var messageContext = new Dictionary<string, object>
            {
                [MessageContext.InfrastructureType] = InfrastructureType,
            };
            var outbound = new OutboundBrokeredMessage(messageId, body, messageContext, destination, converter);
            CosmosOutboxDocument document = CosmosOutboxDocument.From(outbound);

            var partitionKeyValues = new List<JsonElement> { JsonValue(tenantId) };
            JsonObject rendered = document.ToJsonObject(PartitionKeyPath, partitionKeyValues);
            return Parse(rendered.ToJsonString());
        }

        // A non-outbox / non-pending document for the filter tests, rendered with the same partition-key stamping so it
        // is wire-faithful save for the discriminator/status under test.
        private static JsonElement DocumentWith(string chatterType, string status, string tenantId)
        {
            var node = new JsonObject
            {
                [CosmosOutboxDocument.IdField] = "doc-1",
                [CosmosOutboxDocument.MessageIdField] = "msg-1",
                [CosmosOutboxDocument.DestinationField] = "dest",
                [CosmosOutboxDocument.MessageBodyField] = "{}",
                [CosmosOutboxDocument.MessageContentTypeField] = "application/json",
                [CosmosOutboxDocument.MessageContextField] = "{}",
                ["tenantId"] = tenantId,
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

        // A document that carries the outbox discriminator + pending status but whose physical id is NOT the deterministic
        // outbox id Chatter mints for its MessageId — i.e. an app/domain document forged through a raw Cosmos write that no
        // staging guard closes. The relay must NOT publish or patch it (id-consistency is the publish-side close).
        private static JsonElement OutboxShapedDocumentWithForeignId(string id, string messageId, string tenantId)
        {
            var node = new JsonObject
            {
                [CosmosOutboxDocument.IdField] = id,
                [CosmosOutboxDocument.DiscriminatorField] = CosmosItemId.OutboxKind,
                [CosmosOutboxDocument.StatusField] = CosmosOutboxDocument.StatusPending,
                [CosmosOutboxDocument.MessageIdField] = messageId,
                [CosmosOutboxDocument.DestinationField] = "dest",
                [CosmosOutboxDocument.MessageBodyField] = "{}",
                [CosmosOutboxDocument.MessageContentTypeField] = "application/json",
                [CosmosOutboxDocument.MessageContextField] = "{}",
                ["tenantId"] = tenantId,
            };
            return Parse(node.ToJsonString());
        }

        // An admitted outbox document whose own MessageContentType is EMPTY, carrying the content type in its persisted
        // MessageContext instead — the fallback the reconstruction has always resolved through.
        private static JsonElement DocumentWithContentTypeOnlyInTheMessageContext(string messageId, string destination, string tenantId)
        {
            var messageContext = new JsonObject
            {
                [MessageContext.ContentType] = "application/json",
                [MessageContext.InfrastructureType] = InfrastructureType,
            };
            var node = new JsonObject
            {
                [CosmosOutboxDocument.IdField] = CosmosItemId.ForOutbox(messageId),
                [CosmosOutboxDocument.DiscriminatorField] = CosmosItemId.OutboxKind,
                [CosmosOutboxDocument.StatusField] = CosmosOutboxDocument.StatusPending,
                [CosmosOutboxDocument.MessageIdField] = messageId,
                [CosmosOutboxDocument.DestinationField] = destination,
                [CosmosOutboxDocument.MessageBodyField] = "{}",
                [CosmosOutboxDocument.MessageContentTypeField] = string.Empty,
                [CosmosOutboxDocument.MessageContextField] = messageContext.ToJsonString(),
                ["tenantId"] = tenantId,
            };
            return Parse(node.ToJsonString());
        }

        private static JsonElement JsonValue(string raw) => Parse(JsonSerializer.Serialize(raw));

        private static JsonElement Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        private static IBodyConverterFactory BodyConverterFactory()
        {
            var factory = new Mock<IBodyConverterFactory>();
            factory.Setup(f => f.CreateBodyConverter(It.IsAny<string>())).Returns(new JsonBodyConverter());
            return factory.Object;
        }

        // A provider whose dispatcher records every dispatched message; the recorded list is the publish ledger.
        private static (IMessagingInfrastructureProvider provider, List<OutboundBrokeredMessage> published) RecordingProvider()
        {
            var published = new List<OutboundBrokeredMessage>();
            var dispatcher = new Mock<IMessagingInfrastructureDispatcher>();
            dispatcher.Setup(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), It.IsAny<TransactionContext>()))
                      .Callback<OutboundBrokeredMessage, TransactionContext>((m, _) => published.Add(m))
                      .Returns(Task.CompletedTask);

            var provider = new Mock<IMessagingInfrastructureProvider>();
            provider.Setup(p => p.GetDispatcher(It.IsAny<string>())).Returns(dispatcher.Object);
            return (provider.Object, published);
        }

        // A provider whose dispatcher throws on publish — the publish-failure path.
        private static IMessagingInfrastructureProvider ThrowingProvider(Exception toThrow)
        {
            var dispatcher = new Mock<IMessagingInfrastructureDispatcher>();
            dispatcher.Setup(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), It.IsAny<TransactionContext>()))
                      .ThrowsAsync(toThrow);

            var provider = new Mock<IMessagingInfrastructureProvider>();
            provider.Setup(p => p.GetDispatcher(It.IsAny<string>())).Returns(dispatcher.Object);
            return provider.Object;
        }

        // A container that records each PatchItemAsync call (id, partition key, ops) and returns a benign response.
        private static (Mock<Container> container, List<(string id, PartitionKey pk, IReadOnlyList<PatchOperation> ops)> patches) RecordingContainer()
        {
            var patches = new List<(string, PartitionKey, IReadOnlyList<PatchOperation>)>();
            var container = new Mock<Container>();
            container.Setup(c => c.PatchItemAsync<JsonElement>(
                        It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<IReadOnlyList<PatchOperation>>(),
                        It.IsAny<PatchItemRequestOptions>(), It.IsAny<CancellationToken>()))
                     .Callback<string, PartitionKey, IReadOnlyList<PatchOperation>, PatchItemRequestOptions, CancellationToken>(
                        (id, pk, ops, _, __) => patches.Add((id, pk, ops)))
                     .ReturnsAsync(Mock.Of<ItemResponse<JsonElement>>());
            return (container, patches);
        }

        [Fact]
        public async Task MustPublishOutboxPendingDocument()
        {
            var (provider, published) = RecordingProvider();
            var (container, patches) = RecordingContainer();
            var relay = new CosmosOutboxRelay(provider, BodyConverterFactory());

            JsonElement document = OutboxDocument("msg-1", "orders", new { OrderId = 7 }, "tenant-1");
            await relay.ProcessChangeAsync(document, container.Object, PartitionKeyPath);

            published.Should().ContainSingle();
            patches.Should().ContainSingle();
        }

        [Theory]
        [InlineData(CosmosItemId.InboxKind, CosmosOutboxDocument.StatusPending)] // an inbox marker is never published
        [InlineData("aggregate", CosmosOutboxDocument.StatusPending)]            // a domain document is never published
        [InlineData(null, CosmosOutboxDocument.StatusPending)]                   // a doc with no discriminator is skipped
        [InlineData(CosmosItemId.OutboxKind, CosmosOutboxDocument.StatusDelivered)] // already-delivered (the relay's own update event)
        [InlineData(CosmosItemId.OutboxKind, null)]                             // malformed outbox doc: missing status
        [InlineData(CosmosItemId.OutboxKind, "")]                               // malformed outbox doc: empty status
        public async Task MustNotPublishNonOutboxOrNonPendingDocument(string chatterType, string status)
        {
            var (provider, published) = RecordingProvider();
            var (container, patches) = RecordingContainer();
            var relay = new CosmosOutboxRelay(provider, BodyConverterFactory());

            JsonElement document = DocumentWith(chatterType, status, "tenant-1");
            await relay.ProcessChangeAsync(document, container.Object, PartitionKeyPath);

            published.Should().BeEmpty("only a pending outbox document is published");
            patches.Should().BeEmpty("a skipped document is never patched delivered");
        }

        [Fact]
        public async Task MustNotPublishOutboxShapedDocumentWhoseIdIsNotChatterMinted()
        {
            // The app owns the container and can author a document carrying _chatterType="outbox" + status="pending"
            // through a raw Cosmos write. Such a domain/app document is NOT one Chatter minted: its id is not
            // CosmosItemId.ForOutbox(MessageId). The relay must skip it — publishing it would be a forbidden domain-doc
            // leak, and patching it status=delivered+ttl would mutate app data.
            var (provider, published) = RecordingProvider();
            var (container, patches) = RecordingContainer();
            var relay = new CosmosOutboxRelay(provider, BodyConverterFactory());

            JsonElement forged = OutboxShapedDocumentWithForeignId("app-authored-id", "msg-1", "tenant-1");
            await relay.ProcessChangeAsync(forged, container.Object, PartitionKeyPath);

            published.Should().BeEmpty("an outbox-discriminated, pending document whose id is not ForOutbox(MessageId) is not Chatter-minted and must not be published");
            patches.Should().BeEmpty("a skipped document is never patched delivered — its app data is not mutated");
        }

        [Fact]
        public async Task MustNotPublishOutboxShapedDocumentWhoseIdMatchesADifferentMessageId()
        {
            // Even a reserved-namespace outbox: id is rejected when it is ForOutbox of a DIFFERENT message id than the
            // document's own MessageId — the id must be the deterministic outbox id of THIS document's MessageId.
            var (provider, published) = RecordingProvider();
            var (container, patches) = RecordingContainer();
            var relay = new CosmosOutboxRelay(provider, BodyConverterFactory());

            string foreignButReservedId = CosmosItemId.ForOutbox("some-other-message");
            JsonElement forged = OutboxShapedDocumentWithForeignId(foreignButReservedId, "msg-1", "tenant-1");
            await relay.ProcessChangeAsync(forged, container.Object, PartitionKeyPath);

            published.Should().BeEmpty("the id must be ForOutbox of THIS document's MessageId, not of a different message id");
            patches.Should().BeEmpty();
        }

        [Fact]
        public async Task MustReconstructDispatchedMessageFaithfully()
        {
            var (provider, published) = RecordingProvider();
            var (container, _) = RecordingContainer();
            var relay = new CosmosOutboxRelay(provider, BodyConverterFactory());

            var body = new { OrderId = 7, Sku = "abc" };
            JsonElement document = OutboxDocument("msg-42", "orders", body, "tenant-1");
            await relay.ProcessChangeAsync(document, container.Object, PartitionKeyPath);

            OutboundBrokeredMessage dispatched = published.Should().ContainSingle().Subject;
            dispatched.MessageId.Should().Be("msg-42");
            dispatched.Destination.Should().Be("orders");
            // Content type round-trips onto the reconstructed message context (set by the OutboundBrokeredMessage ctor
            // from the converter), and the infrastructure type survives MaterializePersistedContext.
            dispatched.MessageContext[MessageContext.ContentType].Should().Be("application/json");
            dispatched.MessageContext[MessageContext.InfrastructureType].Should().Be(InfrastructureType);
            // The body bytes round-trip to the same JSON the original message stringified.
            var converter = new JsonBodyConverter();
            converter.Stringify(dispatched.Body).Should().Be(converter.Stringify(body));
        }

        [Fact]
        public async Task MustSerializeWithTheContentTypeResolvedFromThePersistedMessageContext()
        {
            // The document carries no content type of its own, so the one the persisted message context carries is what
            // the body converter is asked for. The reconstruction takes it from the Outbox Document Contract's verified
            // descriptor rather than re-reading the context, which is what keeps a non-string persisted content type a
            // named violation instead of a cast that faults the whole batch.
            var (provider, published) = RecordingProvider();
            var (container, _) = RecordingContainer();
            var requestedContentTypes = new List<string>();
            var factory = new Mock<IBodyConverterFactory>();
            factory.Setup(f => f.CreateBodyConverter(It.IsAny<string>()))
                   .Callback<string>(contentType => requestedContentTypes.Add(contentType))
                   .Returns(new JsonBodyConverter());
            var relay = new CosmosOutboxRelay(provider, factory.Object);

            JsonElement document = DocumentWithContentTypeOnlyInTheMessageContext("msg-1", "orders", "tenant-1");
            await relay.ProcessChangeAsync(document, container.Object, PartitionKeyPath);

            requestedContentTypes.Should().ContainSingle().Which.Should().Be("application/json");
            published.Should().ContainSingle().Which.Destination.Should().Be("orders");
        }

        [Fact]
        public async Task MustSuppressRepublishOfDeliveredDocument()
        {
            // The relay's OWN delivered/TTL update produces a change-feed event for a now-delivered document. That doc is
            // no longer pending, so it must NOT be republished — publish-once by construction.
            var (provider, published) = RecordingProvider();
            var (container, patches) = RecordingContainer();
            var relay = new CosmosOutboxRelay(provider, BodyConverterFactory());

            JsonElement deliveredDocument = DocumentWith(CosmosItemId.OutboxKind, CosmosOutboxDocument.StatusDelivered, "tenant-1");
            await relay.ProcessChangeAsync(deliveredDocument, container.Object, PartitionKeyPath);

            published.Should().BeEmpty();
            patches.Should().BeEmpty();
        }

        [Fact]
        public async Task MustStampDeliveredAndTtlInASinglePatchKeyedOnIdAndPartitionKey()
        {
            var (provider, _) = RecordingProvider();
            var (container, patches) = RecordingContainer();
            var relay = new CosmosOutboxRelay(provider, BodyConverterFactory());

            JsonElement document = OutboxDocument("msg-1", "orders", new { OrderId = 7 }, "tenant-1");
            string expectedId = document.GetProperty(CosmosOutboxDocument.IdField).GetString();

            await relay.ProcessChangeAsync(document, container.Object, PartitionKeyPath);

            (string id, PartitionKey pk, IReadOnlyList<PatchOperation> ops) = patches.Should().ContainSingle().Subject;
            id.Should().Be(expectedId, "the patch is keyed on the document id read off the change-feed item");
            pk.Should().Be(new PartitionKey("tenant-1"), "the patch targets the document's own logical partition");
            ops.Should().HaveCount(2, "a single patch sets both status=delivered and ttl");
            container.Verify(c => c.PatchItemAsync<JsonElement>(
                It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<IReadOnlyList<PatchOperation>>(),
                It.IsAny<PatchItemRequestOptions>(), It.IsAny<CancellationToken>()), Times.Once,
                "delivered + ttl are stamped in exactly one PatchItemAsync");
        }

        [Fact]
        public async Task MustNotPatchAndMustPropagateWhenPublishThrows()
        {
            // Publish throws -> no patch issued, the exception propagates so the host does not checkpoint the change-feed
            // batch and the doc stays pending (at-least-once).
            var publishFailure = new InvalidOperationException("broker unavailable");
            var provider = ThrowingProvider(publishFailure);
            var (container, patches) = RecordingContainer();
            var relay = new CosmosOutboxRelay(provider, BodyConverterFactory());

            JsonElement document = OutboxDocument("msg-1", "orders", new { OrderId = 7 }, "tenant-1");
            Func<Task> act = () => relay.ProcessChangeAsync(document, container.Object, PartitionKeyPath);

            (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(publishFailure);
            patches.Should().BeEmpty("a publish failure must leave the document pending — no delivered/ttl patch");
        }
    }
}
