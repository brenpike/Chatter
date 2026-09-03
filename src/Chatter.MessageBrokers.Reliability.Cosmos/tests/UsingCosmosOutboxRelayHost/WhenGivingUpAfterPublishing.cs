using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Cosmos;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingCosmosOutboxRelayHost
{
    /// <summary>
    /// Characterizes the ALWAYS-ON post-publish brake on the REGISTRY-driven relay host. This host carries no options
    /// object, so it drains on the legacy defaults — which means the OPT-IN poison policy is unavailable to it and a
    /// PRE-publish failure still propagates forever, unchanged. The post-publish brake is a different thing entirely: it
    /// has no off switch, because a document whose message reached the broker but whose delivered stamp keeps failing is
    /// re-published on every single pass, and that cost is already being paid. Covering only the standalone host would
    /// leave this one storming.
    /// </summary>
    public class WhenGivingUpAfterPublishing
    {
        private const string InfrastructureType = "test-infra";
        private const string Destination = "orders";
        private const string TenantId = "tenant-1";
        private const int LegacyUnconfirmedPublishCap = 5;

        private static readonly IReadOnlyList<string> PartitionKeyPath = Array.AsReadOnly(new[] { "/tenantId" });

        private static JsonElement Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        private static JsonElement JsonValue(string raw) => Parse(JsonSerializer.Serialize(raw));

        private static Stream StreamOf(params JsonElement[] documents)
            => new MemoryStream(Encoding.UTF8.GetBytes($"{{\"Documents\":[{string.Join(",", Array.ConvertAll(documents, document => document.GetRawText()))}]}}"));

        // A genuinely-admitted pending Outbox Document that reconstructs and publishes cleanly, so every failure it
        // suffers is a POST-publish one.
        private static JsonElement PendingOutboxDocument(string messageId)
        {
            var outbound = new OutboundBrokeredMessage(messageId, new { OrderId = 7 }, MessageContextWithInfrastructure(), Destination, new JsonBodyConverter());
            JsonObject rendered = CosmosOutboxDocument.From(outbound).ToJsonObject(PartitionKeyPath, new List<JsonElement> { JsonValue(TenantId) });
            return Parse(rendered.ToJsonString());
        }

        // An Outbox Document that clears every admission gate yet is UNDELIVERABLE: it carries no content type, so
        // reconstruction throws BEFORE any publish on every pass.
        private static JsonElement UndeliverablePendingOutboxDocument(string messageId)
        {
            var document = new CosmosOutboxDocument(
                id: CosmosItemId.ForOutbox(messageId),
                messageId: messageId,
                destination: Destination,
                messageBody: "{}",
                messageContentType: null,
                serializedMessageContext: JsonSerializer.Serialize(MessageContextWithInfrastructure(), ChatterJson.Options));
            JsonObject rendered = document.ToJsonObject(PartitionKeyPath, new List<JsonElement> { JsonValue(TenantId) });
            return Parse(rendered.ToJsonString());
        }

        private static Dictionary<string, object> MessageContextWithInfrastructure()
            => new Dictionary<string, object> { [MessageContext.InfrastructureType] = InfrastructureType };

        private static string OutboxIdOf(JsonElement document) => document.GetProperty(CosmosOutboxDocument.IdField).GetString();

        private static bool IsDeliveredStamp(IReadOnlyList<PatchOperation> operations)
            => operations.Any(operation => operation.Path == "/" + CosmosOutboxDocument.TtlField);

        // A container recording every attempted patch, whose DELIVERED stamp (the two-op status+ttl patch) always faults
        // while a one-op status stamp still lands — the shape of the real defect this brake exists for.
        private static (Mock<Container> container, List<(string id, PartitionKey partitionKey, IReadOnlyList<PatchOperation> ops)> patches) RecordingContainer()
        {
            var patches = new List<(string, PartitionKey, IReadOnlyList<PatchOperation>)>();
            var failure = new CosmosException("the delivered stamp could not be applied", HttpStatusCode.BadRequest, subStatusCode: 0, activityId: "activity-1", requestCharge: 0);
            var container = new Mock<Container>();

            container.Setup(c => c.PatchItemAsync<JsonElement>(
                        It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<IReadOnlyList<PatchOperation>>(),
                        It.IsAny<PatchItemRequestOptions>(), It.IsAny<CancellationToken>()))
                     .Returns<string, PartitionKey, IReadOnlyList<PatchOperation>, PatchItemRequestOptions, CancellationToken>(
                        (id, partitionKey, ops, _, __) =>
                        {
                            patches.Add((id, partitionKey, ops));
                            return IsDeliveredStamp(ops)
                                ? Task.FromException<ItemResponse<JsonElement>>(failure)
                                : Task.FromResult(Mock.Of<ItemResponse<JsonElement>>());
                        });

            return (container, patches);
        }

        private static IBodyConverterFactory BodyConverterFactory()
        {
            var factory = new Mock<IBodyConverterFactory>();
            factory.Setup(f => f.CreateBodyConverter(It.IsAny<string>())).Returns(new JsonBodyConverter());
            return factory.Object;
        }

        private static IMessagingInfrastructureProvider ProviderRecording(List<string> publishedMessageIds)
        {
            var dispatcher = new Mock<IMessagingInfrastructureDispatcher>();
            dispatcher.Setup(d => d.Dispatch(It.IsAny<OutboundBrokeredMessage>(), It.IsAny<TransactionContext>()))
                      .Callback<OutboundBrokeredMessage, TransactionContext>((message, _) => publishedMessageIds.Add(message.MessageId))
                      .Returns(Task.CompletedTask);

            var provider = new Mock<IMessagingInfrastructureProvider>();
            provider.Setup(p => p.GetDispatcher(It.IsAny<string>())).Returns(dispatcher.Object);
            return provider.Object;
        }

        private static CosmosOutboxRelayHostedService Host(IMessagingInfrastructureProvider infrastructureProvider)
            => new CosmosOutboxRelayHostedService(
                new DocumentReliabilityRegistry(),
                new CosmosContainerFactory(Mock.Of<IServiceProvider>()),
                infrastructureProvider,
                BodyConverterFactory());

        private static async Task DrainOnePassAsync<TFailure>(CosmosOutboxRelayHostedService host, Container monitored, JsonElement document, string because)
            where TFailure : Exception
        {
            using Stream pass = StreamOf(document);
            Func<Task> drain = () => host.HandleChangesAsync(pass, monitored, PartitionKeyPath, "lease-0", CancellationToken.None);
            await drain.Should().ThrowAsync<TFailure>(because);
        }

        [Fact]
        public async Task MustStampPublishedUnconfirmedOnTheCappedPass()
        {
            var published = new List<string>();
            var (monitored, patches) = RecordingContainer();
            CosmosOutboxRelayHostedService host = Host(ProviderRecording(published));
            JsonElement document = PendingOutboxDocument("msg-1");

            foreach (int _ in Enumerable.Range(1, LegacyUnconfirmedPublishCap - 1))
            {
                await DrainOnePassAsync<CosmosException>(host, monitored.Object, document, "below the cap the relay stays fail-closed");
            }

            using Stream cappedPass = StreamOf(document);
            Func<Task> drain = () => host.HandleChangesAsync(cappedPass, monitored.Object, PartitionKeyPath, "lease-0", CancellationToken.None);
            await drain.Should().NotThrowAsync("at the cap the registry host stops re-publishing, so the batch checkpoints");

            (string stampedId, PartitionKey stampedPartitionKey, IReadOnlyList<PatchOperation> giveUpOps) =
                patches.Should().ContainSingle(patch => !IsDeliveredStamp(patch.ops), "exactly one give-up stamp is issued, on the capped pass").Subject;
            stampedId.Should().Be(OutboxIdOf(document), "the give-up stamp is keyed on the document's own id");
            stampedPartitionKey.Should().Be(new PartitionKeyBuilder().Add(TenantId).Build(), "and on the logical partition the document lives in");
            giveUpOps.Should().ContainSingle().Which.Path.Should().Be("/" + CosmosOutboxDocument.StatusField);
            giveUpOps[0].As<PatchOperation<string>>().Value.Should().Be(CosmosOutboxDocument.StatusUnconfirmed,
                "the message DID reach the broker, so it must never be recorded as one that was never delivered");
            published.Should().HaveCount(LegacyUnconfirmedPublishCap, "the cap is what stops an unbounded number of publishes");
        }

        [Fact]
        public async Task MustPropagateAPrePublishFailureForever()
        {
            // The poison policy is not available on this host, so a document that fails BEFORE its publish stays
            // fail-closed indefinitely: nothing went out, so nothing is being re-paid on every pass.
            var published = new List<string>();
            var (monitored, patches) = RecordingContainer();
            CosmosOutboxRelayHostedService host = Host(ProviderRecording(published));
            JsonElement undeliverable = UndeliverablePendingOutboxDocument("msg-1");

            foreach (int _ in Enumerable.Range(1, LegacyUnconfirmedPublishCap * 2))
            {
                await DrainOnePassAsync<InvalidOperationException>(host, monitored.Object, undeliverable,
                    "a pre-publish failure on the registry host propagates on every pass, however many there have been");
            }

            patches.Should().BeEmpty("nothing was published and nothing was given up on, so no stamp was ever issued");
            published.Should().BeEmpty("the document never reached its publish");
        }
    }
}
