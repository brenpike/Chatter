using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Cosmos;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Moq;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingCosmosOutboxRelay
{
    /// <summary>
    /// Characterizes the POST-PUBLISH give-up — the stamp the relay issues for a document whose brokered message ALREADY
    /// reached the broker but whose delivered stamp could not be confirmed. It is ONE patch operation: the status advanced
    /// to the configured unconfirmed value at the SAME anchored status path the delivered and poison stamps use, so the
    /// admission gate stops admitting the document and the change feed can advance past it. Deliberately absent: a TTL op
    /// and a delete. Like a given-up document, a published-unconfirmed one is evidence, so it stays in the container,
    /// inspectable, indefinitely.
    /// </summary>
    public class WhenStampingUnconfirmed
    {
        private const string InfrastructureType = "test-infra";
        private const string UnconfirmedStatusValue = "published-unconfirmed";

        // A HIERARCHICAL partition-key path, so "the unconfirmed stamp recovers the partition key identically to the
        // delivered stamp" is exercised over a real multi-component recovery rather than a single trivial segment.
        private static readonly IReadOnlyList<string> PartitionKeyPath = Array.AsReadOnly(new[] { "/tenantId", "/region" });

        private static OutboxDeliverySettings SettingsWithUnconfirmedStatus()
            => new OutboxDeliverySettings(
                deliveredTtlSeconds: 86400,
                statusPatchPath: "/status",
                deliveredStatusValue: "delivered",
                additionalPendingFilter: null,
                unconfirmedStatusValue: UnconfirmedStatusValue);

        // The exact outbox wire document the relay reads, stamped at both declared partition-key segments.
        private static JsonElement OutboxDocument(string messageId, string tenantId, string region)
        {
            var converter = new JsonBodyConverter();
            var messageContext = new Dictionary<string, object>
            {
                [MessageContext.InfrastructureType] = InfrastructureType,
            };
            var outbound = new OutboundBrokeredMessage(messageId, new { OrderId = 7 }, messageContext, "orders", converter);
            CosmosOutboxDocument document = CosmosOutboxDocument.From(outbound);

            var partitionKeyValues = new List<JsonElement> { JsonValue(tenantId), JsonValue(region) };
            JsonObject rendered = document.ToJsonObject(PartitionKeyPath, partitionKeyValues);
            return Parse(rendered.ToJsonString());
        }

        // An outbox-shaped document carrying no physical id at all — the stamp has nothing to key on.
        private static JsonElement DocumentWithoutId(string tenantId, string region)
        {
            var node = new JsonObject
            {
                [CosmosOutboxDocument.DiscriminatorField] = CosmosItemId.OutboxKind,
                [CosmosOutboxDocument.StatusField] = CosmosOutboxDocument.StatusPending,
                [CosmosOutboxDocument.MessageIdField] = "msg-1",
                [CosmosOutboxDocument.DestinationField] = "orders",
                [CosmosOutboxDocument.MessageBodyField] = "{}",
                [CosmosOutboxDocument.MessageContentTypeField] = "application/json",
                [CosmosOutboxDocument.MessageContextField] = "{}",
                ["tenantId"] = tenantId,
                ["region"] = region,
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

        private static IMessagingInfrastructureProvider AnyProvider()
        {
            var dispatcher = new Mock<IMessagingInfrastructureDispatcher>();
            dispatcher.Setup(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), It.IsAny<TransactionContext>()))
                      .Returns(Task.CompletedTask);

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

        // A container whose patch always faults, so the unconfirmed stamp's OWN failure is observable.
        private static Mock<Container> FailingContainer(Exception toThrow)
        {
            var container = new Mock<Container>();
            container.Setup(c => c.PatchItemAsync<JsonElement>(
                        It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<IReadOnlyList<PatchOperation>>(),
                        It.IsAny<PatchItemRequestOptions>(), It.IsAny<CancellationToken>()))
                     .ThrowsAsync(toThrow);
            return container;
        }

        [Fact]
        public async Task MustAdvanceTheStatusToTheUnconfirmedValueInASinglePatchKeyedOnIdAndPartitionKey()
        {
            var (container, patches) = RecordingContainer();
            var relay = new CosmosOutboxRelay(AnyProvider(), BodyConverterFactory(), SettingsWithUnconfirmedStatus());

            JsonElement document = OutboxDocument("msg-1", "tenant-1", "east");
            string expectedId = document.GetProperty(CosmosOutboxDocument.IdField).GetString();

            await relay.StampUnconfirmedAsync(document, container.Object, PartitionKeyPath, CancellationToken.None);

            (string id, PartitionKey pk, IReadOnlyList<PatchOperation> ops) = patches.Should().ContainSingle().Subject;
            id.Should().Be(expectedId, "the patch is keyed on the document id read off the change-feed item");
            pk.Should().Be(new PartitionKeyBuilder().Add("tenant-1").Add("east").Build(), "the patch targets the document's own logical partition");
            ops.Should().ContainSingle("a published-unconfirmed document is exactly one status advance — nothing else about the document changes").Which
               .Path.Should().Be("/status", "the unconfirmed stamp targets the SAME anchored status path the admission gate reads");
            ops[0].OperationType.Should().Be(PatchOperationType.Set);
            ops[0].As<PatchOperation<string>>().Value.Should().Be(UnconfirmedStatusValue, "the configured unconfirmed status value flows to the stamp");
        }

        [Fact]
        public async Task MustNotStampATtlOrOtherwiseRemoveThePublishedUnconfirmedDocument()
        {
            // The message already reached the broker; what could not be confirmed is the delivered stamp. That document is
            // the evidence of an unconfirmed delivery, so it must remain INSPECTABLE: never scheduled for self-purge,
            // never deleted.
            var (container, patches) = RecordingContainer();
            var relay = new CosmosOutboxRelay(AnyProvider(), BodyConverterFactory(), SettingsWithUnconfirmedStatus());

            JsonElement document = OutboxDocument("msg-1", "tenant-1", "east");
            await relay.StampUnconfirmedAsync(document, container.Object, PartitionKeyPath, CancellationToken.None);

            patches.Should().ContainSingle().Which.ops.Should().NotContain(operation => operation.Path == "/" + CosmosOutboxDocument.TtlField,
                "a published-unconfirmed document carries no TTL — unlike a delivered one, it is never self-purged");
            container.Verify(c => c.PatchItemAsync<JsonElement>(
                It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<IReadOnlyList<PatchOperation>>(),
                It.IsAny<PatchItemRequestOptions>(), It.IsAny<CancellationToken>()), Times.Once);
            container.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task MustPropagateWhenTheUnconfirmedStampItselfFails()
        {
            // The unconfirmed stamp's OWN failure must surface. A partition-key mismatch makes it 404 exactly as the
            // delivered patch would, and that is a configuration error — never something to launder into silence.
            var patchFailure = new CosmosException("the unconfirmed patch did not find the document", HttpStatusCode.NotFound, subStatusCode: 0, activityId: "activity-1", requestCharge: 0);
            Mock<Container> container = FailingContainer(patchFailure);
            var relay = new CosmosOutboxRelay(AnyProvider(), BodyConverterFactory(), SettingsWithUnconfirmedStatus());

            JsonElement document = OutboxDocument("msg-1", "tenant-1", "east");
            Func<Task> stamp = () => relay.StampUnconfirmedAsync(document, container.Object, PartitionKeyPath, CancellationToken.None);

            (await stamp.Should().ThrowAsync<CosmosException>(
                "a failing unconfirmed stamp is a configuration error and must surface, not be silently absorbed"))
                .Which.Should().BeSameAs(patchFailure);
        }

        [Fact]
        public async Task MustNameTheStampKindWhenTheDocumentIsMissingItsId()
        {
            var (container, patches) = RecordingContainer();
            var relay = new CosmosOutboxRelay(AnyProvider(), BodyConverterFactory(), SettingsWithUnconfirmedStatus());

            JsonElement document = DocumentWithoutId("tenant-1", "east");
            Func<Task> stamp = () => relay.StampUnconfirmedAsync(document, container.Object, PartitionKeyPath, CancellationToken.None);

            (await stamp.Should().ThrowAsync<InvalidOperationException>())
                .Which.Message.Should().Contain("unconfirmed", "the failure names the stamp that could not be keyed");
            patches.Should().BeEmpty("a stamp with nothing to key on issues no patch");
        }
    }
}
