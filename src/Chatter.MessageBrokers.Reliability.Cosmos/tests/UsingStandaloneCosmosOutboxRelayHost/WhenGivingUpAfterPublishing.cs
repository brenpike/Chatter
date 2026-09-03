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
using System.Linq;
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
    /// Characterizes the ALWAYS-ON post-publish brake on the standalone relay host, with the opt-in poison policy OFF.
    /// Sending is two steps — publish the brokered message, then stamp the document delivered — so a document whose
    /// publish SUCCEEDS but whose delivered stamp FAILS stays pending and is published AGAIN on the next pass. A
    /// permanently-failing stamp repeats that forever: a broker publish plus request units plus downstream consumer work
    /// on every pass, without limit. At the cap the relay stops: the document is advanced to the published-unconfirmed
    /// status — never to poisoned, which would claim a message that DID go out was never delivered — the batch continues,
    /// and nothing publishes it again.
    /// </summary>
    public class WhenGivingUpAfterPublishing
    {
        private const string InfrastructureType = "test-infra";
        private const string PoisonStatusValue = "poisoned";
        private const string Destination = "orders";
        private const string TenantId = "tenant-1";
        private const int UnconfirmedPublishCap = 5;

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
        // suffers here is a POST-publish one.
        private static JsonElement PendingOutboxDocument(string messageId, string tenantId = TenantId)
        {
            var outbound = new OutboundBrokeredMessage(messageId, new { OrderId = 7 }, MessageContextWithInfrastructure(), Destination, new JsonBodyConverter());
            JsonObject rendered = CosmosOutboxDocument.From(outbound).ToJsonObject(PartitionKeyPath, new List<JsonElement> { JsonValue(tenantId) });
            return Parse(rendered.ToJsonString());
        }

        private static Dictionary<string, object> MessageContextWithInfrastructure()
            => new Dictionary<string, object> { [MessageContext.InfrastructureType] = InfrastructureType };

        private static string OutboxIdOf(JsonElement document) => document.GetProperty(CosmosOutboxDocument.IdField).GetString();

        private static bool IsDeliveredStamp(IReadOnlyList<PatchOperation> operations)
            => operations.Any(operation => operation.Path == "/" + CosmosOutboxDocument.TtlField);

        // A container recording every attempted patch. The DELIVERED stamp of <paramref name="unconfirmableId"/> — the
        // two-op status+ttl patch — always faults, which is exactly the shape of the real defect: a container whose
        // partition-key path is "/ttl" rejects the delivered stamp permanently while a one-op status stamp still lands.
        // Every other patch succeeds unless <paramref name="failEveryPatch"/> is set.
        private static (Mock<Container> container, List<(string id, PartitionKey partitionKey, IReadOnlyList<PatchOperation> ops)> patches, CosmosException failure) RecordingContainer(
            string unconfirmableId,
            bool failEveryPatch = false)
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
                            bool faults = failEveryPatch || (id == unconfirmableId && IsDeliveredStamp(ops));
                            return faults
                                ? Task.FromException<ItemResponse<JsonElement>>(failure)
                                : Task.FromResult(Mock.Of<ItemResponse<JsonElement>>());
                        });

            return (container, patches, failure);
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

        // The poison policy is OFF on every host here: the brake under test is the one that cannot be turned off.
        private static StandaloneCosmosOutboxRelayHostedService StandaloneHost(Container monitored, IMessagingInfrastructureProvider infrastructureProvider)
            => new StandaloneCosmosOutboxRelayHostedService(
                new ServiceCollection().BuildServiceProvider(),
                infrastructureProvider,
                BodyConverterFactory(),
                new CosmosOutboxRelayOptions
                {
                    MonitoredContainerFactory = _ => monitored,
                    LeaseContainerFactory = _ => monitored,
                    PartitionKeyPath = PartitionKeyPath,
                    PoisonAfterConsecutiveFailures = 0,
                    PoisonStatusValue = PoisonStatusValue,
                    GiveUpAfterUnconfirmedPublishes = UnconfirmedPublishCap,
                });

        private static async Task DrainOneFailingPassAsync(StandaloneCosmosOutboxRelayHostedService host, Container monitored, params JsonElement[] documents)
        {
            using Stream pass = StreamOf(documents);
            Func<Task> drain = () => host.HandleChangesAsync(pass, monitored, PartitionKeyPath, "lease-0", CancellationToken.None);
            await drain.Should().ThrowAsync<CosmosException>("a document whose delivered stamp keeps failing is fail-closed until the cap");
        }

        [Fact]
        public void MustDefaultTheCapToAPositiveNumberOfPublishes()
        {
            // The brake is on for a consumer who configured nothing at all, and there is no value of this knob that turns
            // it off — that is what makes the republish storm unconstructable.
            new CosmosOutboxRelayOptions().GiveUpAfterUnconfirmedPublishes.Should().BePositive("the post-publish brake has no off switch");
        }

        [Fact]
        public async Task MustPropagateAndStampNothingWhileBelowTheCap()
        {
            // Fail-closed stays the answer below the cap: the delivered stamp may yet succeed on the next pass, and a
            // document given up on early is one nobody will ever see confirmed.
            var published = new List<string>();
            JsonElement document = PendingOutboxDocument("msg-1");
            var (monitored, patches, _) = RecordingContainer(OutboxIdOf(document));
            StandaloneCosmosOutboxRelayHostedService host = StandaloneHost(monitored.Object, ProviderRecording(published));

            foreach (int pass in Enumerable.Range(1, UnconfirmedPublishCap - 1))
            {
                await DrainOneFailingPassAsync(host, monitored.Object, document);
                patches.Should().OnlyContain(patch => IsDeliveredStamp(patch.ops),
                    $"pass {pass} is below the cap, so the only patch attempted is the delivered stamp that failed — no give-up stamp is issued");
            }
        }

        [Fact]
        public async Task MustStampPublishedUnconfirmedOnTheCappedPass()
        {
            var published = new List<string>();
            JsonElement document = PendingOutboxDocument("msg-1");
            var (monitored, patches, _) = RecordingContainer(OutboxIdOf(document));
            StandaloneCosmosOutboxRelayHostedService host = StandaloneHost(monitored.Object, ProviderRecording(published));

            foreach (int _ in Enumerable.Range(1, UnconfirmedPublishCap - 1))
            {
                await DrainOneFailingPassAsync(host, monitored.Object, document);
            }

            using Stream cappedPass = StreamOf(document);
            Func<Task> drain = () => host.HandleChangesAsync(cappedPass, monitored.Object, PartitionKeyPath, "lease-0", CancellationToken.None);
            await drain.Should().NotThrowAsync("at the cap the relay stops re-publishing, so the batch checkpoints");

            (string stampedId, PartitionKey stampedPartitionKey, IReadOnlyList<PatchOperation> giveUpOps) =
                patches.Should().ContainSingle(patch => !IsDeliveredStamp(patch.ops), "exactly one give-up stamp is issued, on the capped pass").Subject;
            stampedId.Should().Be(OutboxIdOf(document), "the give-up stamp is keyed on the document's own id");
            stampedPartitionKey.Should().Be(new PartitionKeyBuilder().Add(TenantId).Build(), "and on the logical partition the document lives in");
            giveUpOps.Should().ContainSingle("giving up is exactly one status advance — a published-unconfirmed document is evidence and stays inspectable").Which
                     .Path.Should().Be("/" + CosmosOutboxDocument.StatusField);
            giveUpOps[0].As<PatchOperation<string>>().Value.Should().Be(CosmosOutboxDocument.StatusUnconfirmed,
                "the message DID reach the broker, so it must never be recorded as one that was never delivered");
            giveUpOps[0].As<PatchOperation<string>>().Value.Should().NotBe(PoisonStatusValue, "'poisoned' would be a lie about a message that went out");
            giveUpOps.Should().NotContain(operation => operation.Path == "/" + CosmosOutboxDocument.TtlField,
                "a document nobody could confirm is never scheduled for self-purge");
        }

        [Fact]
        public async Task MustPublishExactlyOncePerPassAndNeverAgainAfterTheCap()
        {
            var published = new List<string>();
            JsonElement document = PendingOutboxDocument("msg-1");
            var (monitored, patches, _) = RecordingContainer(OutboxIdOf(document));
            StandaloneCosmosOutboxRelayHostedService host = StandaloneHost(monitored.Object, ProviderRecording(published));

            foreach (int _ in Enumerable.Range(1, UnconfirmedPublishCap - 1))
            {
                await DrainOneFailingPassAsync(host, monitored.Object, document);
            }

            using Stream cappedPass = StreamOf(document);
            await host.HandleChangesAsync(cappedPass, monitored.Object, PartitionKeyPath, "lease-0", CancellationToken.None);

            published.Should().HaveCount(UnconfirmedPublishCap, "the message went out once per pass, and the cap is what stops an unbounded number of them");

            // There is no pass M+1: the stamp advanced the document out of pending, so the admission gate no longer
            // admits it and nothing publishes it again.
            JsonElement stamped = Parse(document.GetRawText()
                .Replace($"\"{CosmosOutboxDocument.StatusField}\":\"{CosmosOutboxDocument.StatusPending}\"",
                         $"\"{CosmosOutboxDocument.StatusField}\":\"{CosmosOutboxDocument.StatusUnconfirmed}\""));
            CosmosOutboxDocument.IsPendingOutbox(stamped).Should().BeFalse("a published-unconfirmed document is out of pending, so the change feed advances past it");
        }

        [Fact]
        public async Task MustContinueTheBatchOnTheCappedPassSoLaterDocumentsDrain()
        {
            var published = new List<string>();
            JsonElement unconfirmable = PendingOutboxDocument("msg-1");
            JsonElement healthy = PendingOutboxDocument("msg-2");
            var (monitored, patches, _) = RecordingContainer(OutboxIdOf(unconfirmable));
            StandaloneCosmosOutboxRelayHostedService host = StandaloneHost(monitored.Object, ProviderRecording(published));

            foreach (int _ in Enumerable.Range(1, UnconfirmedPublishCap - 1))
            {
                await DrainOneFailingPassAsync(host, monitored.Object, unconfirmable, healthy);
            }

            using Stream cappedPass = StreamOf(unconfirmable, healthy);
            await host.HandleChangesAsync(cappedPass, monitored.Object, PartitionKeyPath, "lease-0", CancellationToken.None);

            patches.Should().Contain(patch => patch.id == OutboxIdOf(healthy) && IsDeliveredStamp(patch.ops),
                "the document behind the given-up one drains and stamps delivered instead of starving forever");
            published.Should().Contain("msg-2", "the batch continues past the give-up");
        }

        [Fact]
        public async Task MustPropagateWhenTheUnconfirmedStampItselfFails()
        {
            // The give-up stamp's OWN failure is never swallowed: it fails for the same reasons the delivered stamp does,
            // and a configuration error must not be laundered into "give up on everything".
            var published = new List<string>();
            JsonElement document = PendingOutboxDocument("msg-1");
            var (monitored, patches, patchFailure) = RecordingContainer(OutboxIdOf(document), failEveryPatch: true);
            StandaloneCosmosOutboxRelayHostedService host = StandaloneHost(monitored.Object, ProviderRecording(published));

            foreach (int _ in Enumerable.Range(1, UnconfirmedPublishCap - 1))
            {
                await DrainOneFailingPassAsync(host, monitored.Object, document);
            }

            using Stream cappedPass = StreamOf(document);
            Func<Task> drain = () => host.HandleChangesAsync(cappedPass, monitored.Object, PartitionKeyPath, "lease-0", CancellationToken.None);

            (await drain.Should().ThrowAsync<CosmosException>("a failing give-up stamp must surface, not be silently absorbed"))
                .Which.Should().BeSameAs(patchFailure);
            patches.Should().Contain(patch => !IsDeliveredStamp(patch.ops), "the give-up stamp was attempted before it faulted");
        }
    }
}
