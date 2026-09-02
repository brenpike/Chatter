using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Cosmos;
using Chatter.MessageBrokers.Sending;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.UsingStandaloneCosmosOutboxRelayHost
{
    /// <summary>
    /// Characterizes the STANDALONE relay host's #361 poison arm: the opt-in escape from an Outbox Document that fails
    /// on EVERY pass and therefore wedges its Lease Token forever. Below the configured threshold a drain failure still
    /// PROPAGATES (fail-closed, unchanged — the correct response to a transient failure); at the threshold the document
    /// is stamped with the poison status, counted, logged, and the batch CONTINUES so head-of-line blocking clears. With
    /// the policy off — the default — the host behaves exactly as it did before the policy existed.
    /// </summary>
    public class WhenApplyingThePoisonPolicy
    {
        private const string InfrastructureType = "test-infra";
        private const string PoisonStatusValue = "poisoned";
        private const string Destination = "orders";
        private const string TenantId = "tenant-1";

        private static readonly IReadOnlyList<string> PartitionKeyPath = Array.AsReadOnly(new[] { "/tenantId" });

        private static JsonElement Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        private static JsonElement JsonValue(string raw) => Parse(JsonSerializer.Serialize(raw));

        private static Stream StreamOf(params JsonElement[] documents)
            => new MemoryStream(Encoding.UTF8.GetBytes($"{{\"Documents\":[{string.Join(",", Array.ConvertAll(documents, document => document.GetRawText()))}]}}"));

        // A genuinely-admitted pending Outbox Document, wire-faithful (id == CosmosItemId.ForOutbox(MessageId), status
        // == pending, _chatterType == outbox), that reconstructs and publishes cleanly.
        private static JsonElement PendingOutboxDocument(string messageId)
        {
            var outbound = new OutboundBrokeredMessage(messageId, new { OrderId = 7 }, MessageContextWithInfrastructure(), Destination, new JsonBodyConverter());
            return Render(CosmosOutboxDocument.From(outbound));
        }

        // An Outbox Document that clears EVERY admission gate yet is UNDELIVERABLE: it carries no content type in the
        // document nor in its persisted message context, so reconstruction throws on every pass. IsPendingOutbox proves
        // ADMISSION, not publishability — which is the whole premise of the poison policy.
        private static JsonElement UndeliverablePendingOutboxDocument(string messageId)
            => Render(new CosmosOutboxDocument(
                id: CosmosItemId.ForOutbox(messageId),
                messageId: messageId,
                destination: Destination,
                messageBody: "{}",
                messageContentType: null,
                serializedMessageContext: JsonSerializer.Serialize(MessageContextWithInfrastructure(), ChatterJson.Options)));

        private static Dictionary<string, object> MessageContextWithInfrastructure()
            => new Dictionary<string, object> { [MessageContext.InfrastructureType] = InfrastructureType };

        private static JsonElement Render(CosmosOutboxDocument document)
        {
            JsonObject rendered = document.ToJsonObject(PartitionKeyPath, new List<JsonElement> { JsonValue(TenantId) });
            return Parse(rendered.ToJsonString());
        }

        private static string OutboxIdOf(JsonElement document) => document.GetProperty(CosmosOutboxDocument.IdField).GetString();

        // A container recording each PatchItemAsync call, optionally faulting it — the poison stamp's OWN failure must
        // propagate rather than be laundered into "gave up on everything".
        private static (Mock<Container> container, List<(string id, IReadOnlyList<PatchOperation> ops)> patches) RecordingContainer(Exception patchFailure = null)
        {
            var patches = new List<(string, IReadOnlyList<PatchOperation>)>();
            var container = new Mock<Container>();

            if (patchFailure is null)
            {
                container.Setup(c => c.PatchItemAsync<JsonElement>(
                            It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<IReadOnlyList<PatchOperation>>(),
                            It.IsAny<PatchItemRequestOptions>(), It.IsAny<CancellationToken>()))
                         .Callback<string, PartitionKey, IReadOnlyList<PatchOperation>, PatchItemRequestOptions, CancellationToken>(
                            (id, _, ops, __, ___) => patches.Add((id, ops)))
                         .ReturnsAsync(Mock.Of<ItemResponse<JsonElement>>());
            }
            else
            {
                container.Setup(c => c.PatchItemAsync<JsonElement>(
                            It.IsAny<string>(), It.IsAny<PartitionKey>(), It.IsAny<IReadOnlyList<PatchOperation>>(),
                            It.IsAny<PatchItemRequestOptions>(), It.IsAny<CancellationToken>()))
                         .Callback<string, PartitionKey, IReadOnlyList<PatchOperation>, PatchItemRequestOptions, CancellationToken>(
                            (id, _, ops, __, ___) => patches.Add((id, ops)))
                         .ThrowsAsync(patchFailure);
            }

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

        private static StandaloneCosmosOutboxRelayHostedService StandaloneHost(Container monitored,
                                                                              IMessagingInfrastructureProvider infrastructureProvider,
                                                                              int poisonAfterConsecutiveFailures,
                                                                              Func<IServiceProvider, IOutboxBodyResolver> bodyResolverFactory = null)
            => new StandaloneCosmosOutboxRelayHostedService(
                new ServiceCollection().BuildServiceProvider(),
                infrastructureProvider,
                BodyConverterFactory(),
                new CosmosOutboxRelayOptions
                {
                    MonitoredContainerFactory = _ => monitored,
                    LeaseContainerFactory = _ => monitored,
                    PartitionKeyPath = PartitionKeyPath,
                    PoisonAfterConsecutiveFailures = poisonAfterConsecutiveFailures,
                    PoisonStatusValue = PoisonStatusValue,
                    BodyResolverFactory = bodyResolverFactory,
                });

        // #361's own proposed case with the policy OFF (the default): the batch fails on its first, undeliverable
        // document and NEITHER document is stamped — an old relay behaves exactly as it always did, end to end.
        [Fact]
        public async Task MustFailTheWholeBatchAndStampNothingWhenThePolicyIsOff()
        {
            var published = new List<string>();
            var (monitored, patches) = RecordingContainer();
            StandaloneCosmosOutboxRelayHostedService host = StandaloneHost(monitored.Object, ProviderRecording(published), poisonAfterConsecutiveFailures: 0);

            using Stream batch = StreamOf(UndeliverablePendingOutboxDocument("msg-1"), PendingOutboxDocument("msg-2"));

            Func<Task> drain = () => host.HandleChangesAsync(batch, monitored.Object, PartitionKeyPath, "lease-0", CancellationToken.None);

            (await drain.Should().ThrowAsync<InvalidOperationException>(
                "with no poison policy the undeliverable document's failure propagates, so the SDK never checkpoints the batch"))
                .Which.Message.Should().Contain("content type");
            patches.Should().BeEmpty("neither document is stamped: the first failed and the second was never reached");
            published.Should().BeEmpty("the batch fails before the second document is processed");
        }

        // #361's own proposed case at threshold 1: the undeliverable document is given up on — ONE status op, no ttl —
        // and the batch CONTINUES, so the document behind it drains and stamps delivered instead of starving forever.
        [Fact]
        public async Task MustStampTheUndeliverableDocumentPoisonedAndDrainTheRestOfTheBatch()
        {
            var published = new List<string>();
            var (monitored, patches) = RecordingContainer();
            StandaloneCosmosOutboxRelayHostedService host = StandaloneHost(monitored.Object, ProviderRecording(published), poisonAfterConsecutiveFailures: 1);

            JsonElement undeliverable = UndeliverablePendingOutboxDocument("msg-1");
            JsonElement deliverable = PendingOutboxDocument("msg-2");
            using Stream batch = StreamOf(undeliverable, deliverable);

            Func<Task> drain = () => host.HandleChangesAsync(batch, monitored.Object, PartitionKeyPath, "lease-0", CancellationToken.None);

            await drain.Should().NotThrowAsync("the given-up document no longer blocks the batch, so the lease can advance");

            patches.Should().HaveCount(2);
            (string poisonedId, IReadOnlyList<PatchOperation> poisonOps) = patches[0];
            poisonedId.Should().Be(OutboxIdOf(undeliverable), "the poison stamp is keyed on the failing document's own id");
            poisonOps.Should().ContainSingle("giving up on a document is exactly one status advance — a given-up document is evidence and stays inspectable").Which
                     .Path.Should().Be("/" + CosmosOutboxDocument.StatusField);
            poisonOps[0].As<PatchOperation<string>>().Value.Should().Be(PoisonStatusValue);
            poisonOps.Should().NotContain(operation => operation.Path == "/" + CosmosOutboxDocument.TtlField,
                "a given-up document is never scheduled for self-purge");

            (string deliveredId, IReadOnlyList<PatchOperation> deliveredOps) = patches[1];
            deliveredId.Should().Be(OutboxIdOf(deliverable), "the document behind the given-up one drains normally");
            deliveredOps.Should().HaveCount(2, "a delivered document is stamped status + ttl");
            published.Should().ContainSingle().Which.Should().Be("msg-2", "only the deliverable document is published");
        }

        // A TRANSIENT failure must stay fail-closed: below the threshold the drain failure propagates untouched and NO
        // poison patch is issued, so the document re-surfaces on the next pass.
        [Fact]
        public async Task MustPropagateAndStampNothingWhileBelowTheThreshold()
        {
            var published = new List<string>();
            var (monitored, patches) = RecordingContainer();
            StandaloneCosmosOutboxRelayHostedService host = StandaloneHost(monitored.Object, ProviderRecording(published), poisonAfterConsecutiveFailures: 2);

            using Stream batch = StreamOf(UndeliverablePendingOutboxDocument("msg-1"));

            Func<Task> drain = () => host.HandleChangesAsync(batch, monitored.Object, PartitionKeyPath, "lease-0", CancellationToken.None);

            await drain.Should().ThrowAsync<InvalidOperationException>(
                "a single failure is below the threshold, so the fail-closed behavior is unchanged");
            patches.Should().BeEmpty("a document below the threshold is not given up on");
        }

        // The poison patch's OWN failure must PROPAGATE. A #362 partition-key mismatch makes the poison patch 404 just
        // as the delivered patch would, and that must surface rather than be laundered into "poison everything".
        [Fact]
        public async Task MustPropagateWhenThePoisonStampItselfFails()
        {
            var patchFailure = new CosmosException("the poison patch did not find the document", HttpStatusCode.NotFound, subStatusCode: 0, activityId: "activity-1", requestCharge: 0);
            var published = new List<string>();
            var (monitored, patches) = RecordingContainer(patchFailure);
            StandaloneCosmosOutboxRelayHostedService host = StandaloneHost(monitored.Object, ProviderRecording(published), poisonAfterConsecutiveFailures: 1);

            using Stream batch = StreamOf(UndeliverablePendingOutboxDocument("msg-1"));

            Func<Task> drain = () => host.HandleChangesAsync(batch, monitored.Object, PartitionKeyPath, "lease-0", CancellationToken.None);

            (await drain.Should().ThrowAsync<CosmosException>(
                "a failing poison stamp is a configuration error and must surface, not be silently absorbed"))
                .Which.Should().BeSameAs(patchFailure);
        }

        // The reset the policy implements: an INTERMITTENT failure can never accumulate across successful drains into a
        // give-up. Failure, success, failure at threshold 2 leaves the document pending and unstamped.
        [Fact]
        public async Task MustResetTheFailureCountAfterASuccessfulDrain()
        {
            var published = new List<string>();
            var (monitored, patches) = RecordingContainer();

            var drainAttempts = 0;
            var resolver = new Mock<IOutboxBodyResolver>();
            resolver.Setup(r => r.ResolveAsync(It.IsAny<OutboxDrainContext>(), It.IsAny<CancellationToken>()))
                    .Returns<OutboxDrainContext, CancellationToken>((_, __) =>
                    {
                        drainAttempts++;
                        return drainAttempts == 2
                            ? Task.FromResult<OutboundBrokeredMessage>(null)
                            : Task.FromException<OutboundBrokeredMessage>(new InvalidOperationException("intermittent resolver failure"));
                    });

            StandaloneCosmosOutboxRelayHostedService host = StandaloneHost(
                monitored.Object, ProviderRecording(published), poisonAfterConsecutiveFailures: 2, bodyResolverFactory: _ => resolver.Object);

            JsonElement document = PendingOutboxDocument("msg-1");

            using (Stream firstPass = StreamOf(document))
            {
                Func<Task> drain = () => host.HandleChangesAsync(firstPass, monitored.Object, PartitionKeyPath, "lease-0", CancellationToken.None);
                await drain.Should().ThrowAsync<InvalidOperationException>("the first failure is below the threshold");
            }

            using (Stream secondPass = StreamOf(document))
            {
                await host.HandleChangesAsync(secondPass, monitored.Object, PartitionKeyPath, "lease-0", CancellationToken.None);
            }

            using (Stream thirdPass = StreamOf(document))
            {
                Func<Task> drain = () => host.HandleChangesAsync(thirdPass, monitored.Object, PartitionKeyPath, "lease-0", CancellationToken.None);
                await drain.Should().ThrowAsync<InvalidOperationException>(
                    "the successful drain reset the count, so this failure is the first one again — still below the threshold");
            }

            patches.Should().ContainSingle("only the successful pass stamped the document")
                   .Which.ops.Should().HaveCount(2, "that stamp is the delivered status + ttl, never a poison stamp");
        }

        // A cancelled drain is NOT a deterministic defect: counting a shutdown cancellation toward the threshold would
        // let a host stop give up on a perfectly deliverable document.
        [Fact]
        public async Task MustNotCountACancelledDrainTowardTheThreshold()
        {
            var published = new List<string>();
            var (monitored, patches) = RecordingContainer();

            var resolver = new Mock<IOutboxBodyResolver>();
            resolver.Setup(r => r.ResolveAsync(It.IsAny<OutboxDrainContext>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new OperationCanceledException());

            StandaloneCosmosOutboxRelayHostedService host = StandaloneHost(
                monitored.Object, ProviderRecording(published), poisonAfterConsecutiveFailures: 1, bodyResolverFactory: _ => resolver.Object);

            using Stream batch = StreamOf(PendingOutboxDocument("msg-1"));

            Func<Task> drain = () => host.HandleChangesAsync(batch, monitored.Object, PartitionKeyPath, "lease-0", CancellationToken.None);

            await drain.Should().ThrowAsync<OperationCanceledException>("a cancellation propagates unchanged");
            patches.Should().BeEmpty("a cancelled drain never gives up on the document");
        }

        // The give-up is what lets the feed advance: a poisoned document is no longer admitted, so neither the host
        // pre-gate nor the relay's own admission gate drains it again.
        [Fact]
        public async Task MustNotReDrainADocumentAlreadyStampedPoisoned()
        {
            JsonElement poisoned = Parse(PendingOutboxDocument("msg-1").GetRawText()
                .Replace($"\"{CosmosOutboxDocument.StatusField}\":\"{CosmosOutboxDocument.StatusPending}\"",
                         $"\"{CosmosOutboxDocument.StatusField}\":\"{PoisonStatusValue}\""));

            CosmosOutboxDocument.IsPendingOutbox(poisoned).Should().BeFalse(
                "a poisoned document is out of pending, so the change feed can advance past it");

            var published = new List<string>();
            var (monitored, patches) = RecordingContainer();
            StandaloneCosmosOutboxRelayHostedService host = StandaloneHost(monitored.Object, ProviderRecording(published), poisonAfterConsecutiveFailures: 1);

            using Stream batch = StreamOf(poisoned);
            await host.HandleChangesAsync(batch, monitored.Object, PartitionKeyPath, "lease-0", CancellationToken.None);

            published.Should().BeEmpty("a poisoned document is never re-published");
            patches.Should().BeEmpty("a poisoned document is never re-stamped");
        }
    }
}
