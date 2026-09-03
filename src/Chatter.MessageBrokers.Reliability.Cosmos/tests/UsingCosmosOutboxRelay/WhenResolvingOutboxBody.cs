using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Cosmos;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Moq;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingCosmosOutboxRelay
{
    // Covers the #222 outbox body-resolver seam: a per-call IOutboxBodyResolver resolves the brokered message to publish
    // for each admitted document (instead of the verbatim reconstruction), the delivered/TTL stamp is driven by the
    // configurable OutboxDeliverySettings, and admission is the always-applied id-guard ANDed with an optional narrowing
    // filter. The settings' constructor enforces the F2 invariants (delivered != pending, ttl > 0, status path anchored)
    // so an unsafe stamp configuration is unconstructable rather than merely unused. The delivered TTL is always stamped
    // at the Cosmos-reserved "/ttl" path — it is hard-wired, not a configurable knob, so a non-purging delivered stamp
    // is unrepresentable.
    public class WhenResolvingOutboxBody : Testing.Core.Context
    {
        private const string InfrastructureType = "test-infra";
        private static readonly IReadOnlyList<string> PartitionKeyPath = Array.AsReadOnly(new[] { "/tenantId" });

        // Builds the exact outbox wire document the Document-Tier Batch-Lifecycle Behavior writes, as a JsonElement the
        // relay reads (parity with WhenDrainingOutbox's builder).
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

        // An already-delivered outbox document (the relay's own delivered/TTL update event), wire-faithful save for the
        // delivered status under test.
        private static JsonElement DeliveredOutboxDocument(string tenantId)
        {
            var node = new JsonObject
            {
                [CosmosOutboxDocument.IdField] = "doc-1",
                [CosmosOutboxDocument.DiscriminatorField] = CosmosItemId.OutboxKind,
                [CosmosOutboxDocument.StatusField] = CosmosOutboxDocument.StatusDelivered,
                [CosmosOutboxDocument.MessageIdField] = "msg-1",
                [CosmosOutboxDocument.DestinationField] = "dest",
                [CosmosOutboxDocument.MessageBodyField] = "{}",
                [CosmosOutboxDocument.MessageContentTypeField] = "application/json",
                [CosmosOutboxDocument.MessageContextField] = "{}",
                ["tenantId"] = tenantId,
            };
            return Parse(node.ToJsonString());
        }

        // An ADMITTED outbox document the verbatim reconstruction could never publish: it carries no Destination, no
        // MessageBody, no MessageContentType and no MessageContext — the thin-trigger shape a resolver exists for.
        private static JsonElement UnreconstructableOutboxDocument(string messageId, string tenantId)
        {
            var node = new JsonObject
            {
                [CosmosOutboxDocument.IdField] = CosmosItemId.ForOutbox(messageId),
                [CosmosOutboxDocument.DiscriminatorField] = CosmosItemId.OutboxKind,
                [CosmosOutboxDocument.StatusField] = CosmosOutboxDocument.StatusPending,
                [CosmosOutboxDocument.MessageIdField] = messageId,
                ["tenantId"] = tenantId,
            };
            return Parse(node.ToJsonString());
        }

        // An ADMITTED outbox document whose persisted context names a NON-STRING messaging system — a violation the
        // Outbox Document Contract would prove on the verbatim path, and one a resolver-owned message never faces.
        private static JsonElement OutboxDocumentWithNonStringMessagingSystem(string messageId, string tenantId)
        {
            var node = new JsonObject
            {
                [CosmosOutboxDocument.IdField] = CosmosItemId.ForOutbox(messageId),
                [CosmosOutboxDocument.DiscriminatorField] = CosmosItemId.OutboxKind,
                [CosmosOutboxDocument.StatusField] = CosmosOutboxDocument.StatusPending,
                [CosmosOutboxDocument.MessageIdField] = messageId,
                [CosmosOutboxDocument.MessageContextField] = new JsonObject { [MessageContext.InfrastructureType] = 7 }.ToJsonString(),
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

        // A provider whose dispatcher records every dispatched message and which records every messaging system it was
        // asked to resolve a dispatcher for; the recorded lists are the publish ledger and the dispatch-resolution ledger.
        private static (IMessagingInfrastructureProvider provider, List<OutboundBrokeredMessage> published, List<string> requestedMessagingSystems) RecordingProvider()
        {
            var published = new List<OutboundBrokeredMessage>();
            var dispatcher = new Mock<IMessagingInfrastructureDispatcher>();
            dispatcher.Setup(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), It.IsAny<TransactionContext>()))
                      .Callback<OutboundBrokeredMessage, TransactionContext>((m, _) => published.Add(m))
                      .Returns(Task.CompletedTask);

            var requestedMessagingSystems = new List<string>();
            var provider = new Mock<IMessagingInfrastructureProvider>();
            provider.Setup(p => p.GetDispatcher(It.IsAny<string>()))
                    .Callback<string>(requestedMessagingSystems.Add)
                    .Returns(dispatcher.Object);
            return (provider.Object, published, requestedMessagingSystems);
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

        // A resolver that records each context it is handed and returns the supplied message (which may be null).
        private static (IOutboxBodyResolver resolver, List<OutboxDrainContext> contexts) RecordingResolver(OutboundBrokeredMessage toReturn)
        {
            var contexts = new List<OutboxDrainContext>();
            var resolver = new Mock<IOutboxBodyResolver>();
            resolver.Setup(r => r.ResolveAsync(It.IsAny<OutboxDrainContext>(), It.IsAny<CancellationToken>()))
                    .Callback<OutboxDrainContext, CancellationToken>((ctx, _) => contexts.Add(ctx))
                    .ReturnsAsync(toReturn);
            return (resolver.Object, contexts);
        }

        // A resolver that throws on resolve — the resolver-failure path.
        private static IOutboxBodyResolver ThrowingResolver(Exception toThrow)
        {
            var resolver = new Mock<IOutboxBodyResolver>();
            resolver.Setup(r => r.ResolveAsync(It.IsAny<OutboxDrainContext>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(toThrow);
            return resolver.Object;
        }

        // A brokered message distinct from anything the verbatim reconstruction would yield, so a test can prove the
        // dispatched message came from the resolver and not from the document.
        private static OutboundBrokeredMessage ResolvedMessage(string messageId, string destination)
        {
            var converter = new JsonBodyConverter();
            var messageContext = new Dictionary<string, object>
            {
                [MessageContext.InfrastructureType] = InfrastructureType,
            };
            return new OutboundBrokeredMessage(messageId, new { Resolved = true }, messageContext, destination, converter);
        }

        [Fact]
        public async Task MustInvokeResolverOncePerPendingDocument()
        {
            var (provider, _, _) = RecordingProvider();
            var (container, _) = RecordingContainer();
            var (resolver, contexts) = RecordingResolver(ResolvedMessage("resolved-msg", "resolved-dest"));
            var relay = new CosmosOutboxRelay(provider, BodyConverterFactory(), OutboxDeliverySettings.Legacy);

            JsonElement document = OutboxDocument("msg-1", "orders", new { OrderId = 7 }, "tenant-1");
            await relay.ProcessChangeAsync(document, container.Object, PartitionKeyPath, resolver);

            contexts.Should().ContainSingle("the resolver is consulted exactly once for an admitted pending document");
        }

        [Fact]
        public async Task MustDispatchResolverReturnedMessageNotTheVerbatimReconstruction()
        {
            var (provider, published, _) = RecordingProvider();
            var (container, _) = RecordingContainer();
            var (resolver, _) = RecordingResolver(ResolvedMessage("resolved-msg", "resolved-dest"));
            var relay = new CosmosOutboxRelay(provider, BodyConverterFactory(), OutboxDeliverySettings.Legacy);

            // The document's own MessageId is msg-1; the resolver returns a DIFFERENT message. The dispatched message must
            // be the resolver's, proving the relay published the resolved message and not the reconstructed document body.
            JsonElement document = OutboxDocument("msg-1", "orders", new { OrderId = 7 }, "tenant-1");
            await relay.ProcessChangeAsync(document, container.Object, PartitionKeyPath, resolver);

            OutboundBrokeredMessage dispatched = published.Should().ContainSingle().Subject;
            dispatched.MessageId.Should().Be("resolved-msg");
            dispatched.Destination.Should().Be("resolved-dest");
        }

        [Fact]
        public async Task MustPublishAResolvedMessageForADocumentTheReconstructionCouldNotBuild()
        {
            // THE OUTBOX DOCUMENT CONTRACT IS EVALUATED ONLY ON THE NO-RESOLVER VERBATIM PATH. A supplied resolver OWNS
            // the message, so the document's persisted fields need not be publishable at all — evaluating the contract
            // here would mark documents undeliverable that the resolver publishes perfectly well.
            var (provider, published, _) = RecordingProvider();
            var (container, patches) = RecordingContainer();
            var (resolver, _) = RecordingResolver(ResolvedMessage("resolved-msg", "resolved-dest"));
            var relay = new CosmosOutboxRelay(provider, BodyConverterFactory(), OutboxDeliverySettings.Legacy);

            JsonElement document = UnreconstructableOutboxDocument("msg-1", "tenant-1");
            await relay.ProcessChangeAsync(document, container.Object, PartitionKeyPath, resolver);

            published.Should().ContainSingle("the resolver owns the message, so the document's own fields are never verified")
                     .Which.MessageId.Should().Be("resolved-msg");
            IReadOnlyList<PatchOperation> ops = patches.Should().ContainSingle().Subject.ops;
            ops.Should().HaveCount(2, "a resolved publish is stamped delivered+ttl, never undeliverable");
            ops[0].As<PatchOperation<string>>().Value.Should().Be(CosmosOutboxDocument.StatusDelivered);
        }

        [Fact]
        public async Task MustStampDeliveredAndDispatchNothingWhenResolverReturnsNull()
        {
            var (provider, published, _) = RecordingProvider();
            var (container, patches) = RecordingContainer();
            var (resolver, _) = RecordingResolver(toReturn: null);
            var relay = new CosmosOutboxRelay(provider, BodyConverterFactory(), OutboxDeliverySettings.Legacy);

            JsonElement document = OutboxDocument("msg-1", "orders", new { OrderId = 7 }, "tenant-1");
            await relay.ProcessChangeAsync(document, container.Object, PartitionKeyPath, resolver);

            published.Should().BeEmpty("a null resolution publishes nothing");
            patches.Should().ContainSingle("a null resolution still acknowledges the document delivered+ttl");
        }

        [Fact]
        public async Task MustUseVerbatimReconstructionWhenNoResolverBound()
        {
            var (provider, published, _) = RecordingProvider();
            var (container, _) = RecordingContainer();
            var relay = new CosmosOutboxRelay(provider, BodyConverterFactory());

            // No resolver -> the relay reconstructs and dispatches the verbatim document body (MessageId verbatim).
            JsonElement document = OutboxDocument("msg-42", "orders", new { OrderId = 7 }, "tenant-1");
            await relay.ProcessChangeAsync(document, container.Object, PartitionKeyPath);

            OutboundBrokeredMessage dispatched = published.Should().ContainSingle().Subject;
            dispatched.MessageId.Should().Be("msg-42", "with no resolver the verbatim reconstruction path is unchanged");
            dispatched.Destination.Should().Be("orders");
        }

        [Fact]
        public async Task MustStampConfiguredStatusValueAndAlwaysTtlPathWhenSafeSettingsSupplied()
        {
            var (provider, _, _) = RecordingProvider();
            var (container, patches) = RecordingContainer();
            // Non-default but SAFE configuration: the delivered status value and ttl seconds diverge from the legacy
            // defaults; the status patch path stays anchored to "/status" (the only value the F2 invariants admit). The
            // ttl patch path is NOT a knob — the delivered stamp always targets the Cosmos-reserved "/ttl". This proves
            // the non-default safe status value and ttl seconds flow through while the ttl path stays hard-wired.
            var configured = new OutboxDeliverySettings(
                deliveredTtlSeconds: 999,
                statusPatchPath: "/status",
                deliveredStatusValue: "done",
                additionalPendingFilter: null);
            var relay = new CosmosOutboxRelay(provider, BodyConverterFactory(), configured);

            JsonElement document = OutboxDocument("msg-1", "orders", new { OrderId = 7 }, "tenant-1");
            await relay.ProcessChangeAsync(document, container.Object, PartitionKeyPath);

            IReadOnlyList<PatchOperation> ops = patches.Should().ContainSingle().Subject.ops;
            ops.Should().HaveCount(2);
            ops[0].OperationType.Should().Be(PatchOperationType.Set);
            ops[0].Path.Should().Be("/status", "the status patch targets the anchored status path");
            ops[0].As<PatchOperation<string>>().Value.Should().Be("done", "the configured delivered status value flows to the stamp");
            ops[1].Path.Should().Be("/ttl", "the ttl patch always targets the Cosmos-reserved /ttl path; it is not configurable");
            ops[1].As<PatchOperation<int>>().Value.Should().Be(999, "the configured ttl seconds flows to the stamp");
        }

        [Fact]
        public async Task MustStampLegacyStatusPathValueAndTtlWhenSettingsUnset()
        {
            var (provider, _, _) = RecordingProvider();
            var (container, patches) = RecordingContainer();
            var relay = new CosmosOutboxRelay(provider, BodyConverterFactory());

            JsonElement document = OutboxDocument("msg-1", "orders", new { OrderId = 7 }, "tenant-1");
            await relay.ProcessChangeAsync(document, container.Object, PartitionKeyPath);

            IReadOnlyList<PatchOperation> ops = patches.Should().ContainSingle().Subject.ops;
            ops.Should().HaveCount(2);
            ops[0].Path.Should().Be("/status", "the legacy status patch path is unchanged");
            ops[1].Path.Should().Be("/ttl", "the legacy ttl patch path is unchanged");
        }

        [Fact]
        public async Task MustSkipDocumentNarrowedOutByAdditionalPendingFilter()
        {
            var (provider, published, _) = RecordingProvider();
            var (container, patches) = RecordingContainer();
            var (resolver, contexts) = RecordingResolver(ResolvedMessage("resolved-msg", "resolved-dest"));
            // An additional pending filter that rejects everything narrows even a genuinely-pending document out of
            // admission, so it is never resolved, dispatched, or stamped.
            var narrowToNothing = new OutboxDeliverySettings(
                deliveredTtlSeconds: 86400,
                statusPatchPath: "/status",
                deliveredStatusValue: "delivered",
                additionalPendingFilter: _ => false);
            var relay = new CosmosOutboxRelay(provider, BodyConverterFactory(), narrowToNothing);

            JsonElement document = OutboxDocument("msg-1", "orders", new { OrderId = 7 }, "tenant-1");
            await relay.ProcessChangeAsync(document, container.Object, PartitionKeyPath, resolver);

            contexts.Should().BeEmpty("a narrowed-out document is never resolved");
            published.Should().BeEmpty("a narrowed-out document is never dispatched");
            patches.Should().BeEmpty("a narrowed-out document is never stamped delivered");
        }

        [Fact]
        public async Task MustNotRedispatchAnAlreadyDeliveredDocument()
        {
            var (provider, published, _) = RecordingProvider();
            var (container, patches) = RecordingContainer();
            var (resolver, contexts) = RecordingResolver(ResolvedMessage("resolved-msg", "resolved-dest"));
            var relay = new CosmosOutboxRelay(provider, BodyConverterFactory(), OutboxDeliverySettings.Legacy);

            // A re-drain of the relay's OWN delivered/TTL update event is filtered out by the id-guard (not pending).
            JsonElement delivered = DeliveredOutboxDocument("tenant-1");
            await relay.ProcessChangeAsync(delivered, container.Object, PartitionKeyPath, resolver);

            contexts.Should().BeEmpty("an already-delivered document is not pending, so the resolver is not consulted");
            published.Should().BeEmpty();
            patches.Should().BeEmpty();
        }

        [Fact]
        public async Task MustReadTheMessagingSystemFromTheResolvedMessageAndNeverVerifyTheDocument()
        {
            // The DOCUMENT's persisted context names a non-string messaging system — a contract violation on the
            // verbatim path. A resolver OWNS the message, so its context is host-owned and is never verified: the
            // same single reader supplies the messaging system, without classification.
            var (provider, published, requestedMessagingSystems) = RecordingProvider();
            var (container, patches) = RecordingContainer();
            var (resolver, _) = RecordingResolver(ResolvedMessage("resolved-msg", "resolved-dest"));
            var relay = new CosmosOutboxRelay(provider, BodyConverterFactory(), OutboxDeliverySettings.Legacy);

            JsonElement document = OutboxDocumentWithNonStringMessagingSystem("msg-1", "tenant-1");
            await relay.ProcessChangeAsync(document, container.Object, PartitionKeyPath, resolver);

            published.Should().ContainSingle("a resolver-owned message is published whatever the document's own fields prove")
                     .Which.MessageId.Should().Be("resolved-msg");
            requestedMessagingSystems.Should().ContainSingle()
                                     .Which.Should().Be(InfrastructureType, "the messaging system comes from the resolved message's own context");
            patches.Should().ContainSingle().Which.ops.Should().HaveCount(2, "the document is stamped delivered, never undeliverable");
        }

        [Fact]
        public async Task MustHandResolverThePartitionKeyAndMessageIdFromTheDrainedDocument()
        {
            var (provider, _, _) = RecordingProvider();
            var (container, _) = RecordingContainer();
            var (resolver, contexts) = RecordingResolver(ResolvedMessage("resolved-msg", "resolved-dest"));
            var relay = new CosmosOutboxRelay(provider, BodyConverterFactory(), OutboxDeliverySettings.Legacy);

            JsonElement document = OutboxDocument("msg-77", "orders", new { OrderId = 7 }, "tenant-9");
            await relay.ProcessChangeAsync(document, container.Object, PartitionKeyPath, resolver);

            OutboxDrainContext context = contexts.Should().ContainSingle().Subject;
            context.MessageId.Should().Be("msg-77", "the context carries the drained document's verbatim message id");
            context.PartitionKey.Should().Be(new PartitionKey("tenant-9"), "the context carries the recovered partition key");
            context.PartitionKeyPath.Should().BeSameAs(PartitionKeyPath);
            context.Document.GetProperty(CosmosOutboxDocument.MessageIdField).GetString().Should().Be("msg-77");
        }

        [Fact]
        public async Task MustNotStampAndMustPropagateWhenResolverThrows()
        {
            var (provider, published, _) = RecordingProvider();
            var (container, patches) = RecordingContainer();
            var resolveFailure = new InvalidOperationException("resolver unavailable");
            IOutboxBodyResolver resolver = ThrowingResolver(resolveFailure);
            var relay = new CosmosOutboxRelay(provider, BodyConverterFactory(), OutboxDeliverySettings.Legacy);

            JsonElement document = OutboxDocument("msg-1", "orders", new { OrderId = 7 }, "tenant-1");
            Func<Task> act = () => relay.ProcessChangeAsync(document, container.Object, PartitionKeyPath, resolver);

            (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(resolveFailure);
            published.Should().BeEmpty("a resolver failure dispatches nothing");
            patches.Should().BeEmpty("a resolver failure must leave the document pending — no delivered/ttl patch");
        }

        // Constructs delivery settings with one field diverged from a known-safe baseline, so a construction-throw test
        // isolates exactly one rejected F2 invariant.
        private static Action ConstructSettings(int deliveredTtlSeconds = 86400,
                                                string statusPatchPath = "/status",
                                                string deliveredStatusValue = "delivered")
            => () => new OutboxDeliverySettings(deliveredTtlSeconds, statusPatchPath, deliveredStatusValue, additionalPendingFilter: null);

        [Fact]
        public void MustRejectDeliveredStatusEqualToPending()
        {
            ConstructSettings(deliveredStatusValue: CosmosOutboxDocument.StatusPending).Should().Throw<ArgumentException>(
                "a delivered status equal to pending would never move the document out of pending, so it must be unconstructable");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void MustRejectEmptyDeliveredStatus(string deliveredStatusValue)
        {
            ConstructSettings(deliveredStatusValue: deliveredStatusValue).Should().Throw<ArgumentException>(
                "an empty delivered status cannot advance a document out of pending");
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(-86400)]
        public void MustRejectNonPositiveDeliveredTtl(int deliveredTtlSeconds)
        {
            ConstructSettings(deliveredTtlSeconds: deliveredTtlSeconds).Should().Throw<ArgumentException>(
                "the delivered TTL must be positive; 0, -1 (retain indefinitely), and any negative are out of scope");
        }

        [Fact]
        public void MustRejectStatusPatchPathThatIsNotAnchoredToTheStatusField()
        {
            ConstructSettings(statusPatchPath: "/state").Should().Throw<ArgumentException>(
                "a status patch path that does not target the field the pending gate reads would leave the document pending forever, so it must be unconstructable");
        }

        [Theory]
        [InlineData(null)]   // missing
        [InlineData("")]     // empty
        [InlineData("status")] // no leading '/'
        [InlineData("/")]    // no non-empty segment
        public void MustRejectInvalidStatusPatchPath(string statusPatchPath)
        {
            ConstructSettings(statusPatchPath: statusPatchPath).Should().Throw<ArgumentException>(
                "the status patch path must be a valid JSON pointer");
        }

    }
}
