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
    /// <summary>
    /// Characterizes the #361 poison stamp — the give-up the relay issues for a document it has failed to drain
    /// consecutively often enough. It is ONE patch operation: the status advanced to the configured poison value at the
    /// SAME anchored status path the delivered stamp uses, so the admission gate stops admitting the document and the
    /// change feed can advance past it. Deliberately absent: a TTL op and a delete. A given-up document is evidence of a
    /// defect, so it must stay in the container, inspectable, indefinitely.
    /// </summary>
    public class WhenStampingPoisoned
    {
        private const string InfrastructureType = "test-infra";
        private const string PoisonStatusValue = "poisoned";

        // A HIERARCHICAL partition-key path, so "the poison stamp recovers the partition key identically to the delivered
        // stamp" is exercised over a real multi-component recovery rather than a single trivial segment.
        private static readonly IReadOnlyList<string> PartitionKeyPath = Array.AsReadOnly(new[] { "/tenantId", "/region" });

        private static OutboxDeliverySettings SettingsWithPoisonPolicy()
            => new OutboxDeliverySettings(
                deliveredTtlSeconds: 86400,
                statusPatchPath: "/status",
                deliveredStatusValue: "delivered",
                additionalPendingFilter: null,
                poisonAfterConsecutiveFailures: 2,
                poisonStatusValue: PoisonStatusValue);

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

        [Fact]
        public async Task MustAdvanceTheStatusToThePoisonValueInASinglePatchKeyedOnIdAndPartitionKey()
        {
            var (container, patches) = RecordingContainer();
            var relay = new CosmosOutboxRelay(AnyProvider(), BodyConverterFactory(), SettingsWithPoisonPolicy());

            JsonElement document = OutboxDocument("msg-1", "tenant-1", "east");
            string expectedId = document.GetProperty(CosmosOutboxDocument.IdField).GetString();

            await relay.StampPoisonedAsync(document, container.Object, PartitionKeyPath, CancellationToken.None);

            (string id, PartitionKey pk, IReadOnlyList<PatchOperation> ops) = patches.Should().ContainSingle().Subject;
            id.Should().Be(expectedId, "the patch is keyed on the document id read off the change-feed item");
            pk.Should().Be(new PartitionKeyBuilder().Add("tenant-1").Add("east").Build(), "the patch targets the document's own logical partition");
            ops.Should().ContainSingle("giving up on a document is exactly one status advance — nothing else about the document changes").Which
               .Path.Should().Be("/status", "the poison stamp targets the SAME anchored status path the admission gate reads");
            ops[0].OperationType.Should().Be(PatchOperationType.Set);
            ops[0].As<PatchOperation<string>>().Value.Should().Be(PoisonStatusValue, "the configured poison status value flows to the stamp");
        }

        [Fact]
        public async Task MustNotStampATtlOrOtherwiseRemoveTheGivenUpDocument()
        {
            // A given-up document is the evidence of the defect that stalled the relay. It must remain INSPECTABLE: never
            // scheduled for self-purge, never deleted.
            var (container, patches) = RecordingContainer();
            var relay = new CosmosOutboxRelay(AnyProvider(), BodyConverterFactory(), SettingsWithPoisonPolicy());

            JsonElement document = OutboxDocument("msg-1", "tenant-1", "east");
            await relay.StampPoisonedAsync(document, container.Object, PartitionKeyPath, CancellationToken.None);

            patches.Should().ContainSingle().Which.ops.Should().NotContain(operation => operation.Path == "/" + CosmosOutboxDocument.TtlField,
                "a poisoned document carries no TTL — unlike a delivered one, it is never self-purged");
            container.Verify(c => c.PatchItemAsync<JsonElement>(
                It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<IReadOnlyList<PatchOperation>>(),
                It.IsAny<PatchItemRequestOptions>(), It.IsAny<CancellationToken>()), Times.Once);
            container.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task MustRecoverTheSamePartitionKeyTheDeliveredStampRecovers()
        {
            // The poison stamp deliberately reuses the delivered stamp's partition-key recovery. Under a MISCONFIGURED
            // partition-key path both stamps miss the same way, so a give-up can never launder a configuration error into
            // "poison everything" — the poison patch fails exactly as the delivered patch would.
            var (container, patches) = RecordingContainer();
            var relay = new CosmosOutboxRelay(AnyProvider(), BodyConverterFactory(), SettingsWithPoisonPolicy());

            JsonElement document = OutboxDocument("msg-1", "tenant-1", "east");
            await relay.ProcessChangeAsync(document, container.Object, PartitionKeyPath);
            await relay.StampPoisonedAsync(document, container.Object, PartitionKeyPath, CancellationToken.None);

            patches.Should().HaveCount(2);
            patches[1].pk.Should().Be(patches[0].pk, "both stamps recover the partition key from the same document at the same declared path");
            patches[1].id.Should().Be(patches[0].id, "both stamps key on the same document id");
        }
    }
}
