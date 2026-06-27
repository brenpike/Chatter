using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Reliability.Cosmos;
using Chatter.MessageBrokers.Sending;
using Chatter.Testing.Core.Integration;
using FluentAssertions;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chatter.MessageBrokers.Reliability.Cosmos.Tests.Integration
{
    // STANDALONE relay + IOutboxBodyResolver drain-time seam (STEP-004) driven through the REAL
    // StandaloneCosmosOutboxRelayHostedService (registered via AddCosmosOutboxRelay) against the emulator:
    //   - a THIN trigger doc (status=pending, _chatterType=outbox, id == ForOutbox(MessageId), a /pk value, and NO
    //     MessageBody / MessageContentType / MessageContext) is staged directly into the monitored container. The relay's
    //     verbatim Reconstruct path would THROW "no content type" on this doc — it carries no body/content-type/context —
    //     so the only way it can be published is through a bound resolver that RESOLVES the body at drain.
    //   - an IOutboxBodyResolver bound on the standalone relay reads the MessageId + the /pk partition value off the
    //     OutboxDrainContext and returns a real OutboundBrokeredMessage (content type + body + destination) built at drain.
    //   - ASSERT: the resolver-produced message reaches the capturing broker sink (where verbatim Reconstruct would have
    //     thrown), the resolver observed the staged MessageId + partition (proving the body was resolved from the context,
    //     not the doc), and the thin doc is then stamped status=delivered + a positive ttl so it self-purges.
    [Trait("Category", "Integration")]
    [Collection(CosmosEmulatorCollection.Name)]
    public class WhenResolvingThinTriggerOutbox
    {
        private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(120);
        private static readonly TimeSpan DeliveredTimeout = TimeSpan.FromSeconds(120);

        private readonly CosmosEmulatorFixture _emulator;

        public WhenResolvingThinTriggerOutbox(CosmosEmulatorFixture emulator) => _emulator = emulator;

        [RequiresDockerFact]
        public async Task ResolvesBodyAtDrainForThinTriggerThenStampsDelivered()
        {
            await using CosmosTestClient testClient = await CosmosTestClient.CreateAsync(_emulator.GetEmulatorEndpoint(), CosmosEmulatorFixture.WellKnownEmulatorKey);
            Container container = testClient.Client.GetContainer(CosmosTestClient.DatabaseName, CosmosTestClient.DocumentContainerName);

            string partition = "pk-" + Guid.NewGuid().ToString("N");
            string messageId = "thin-" + Guid.NewGuid().ToString("N");
            string thinId = CosmosItemId.ForOutbox(messageId);
            // A UNIQUE per-message destination prefix isolates THIS test's publication from other tests' docs the shared
            // change-feed relay also drains, and the resolver keys each published destination off the drained doc's OWN
            // MessageId so a stray pending doc surfacing on the shared container cannot pollute this doc's sink.
            string destinationPrefix = "thin-dest-" + Guid.NewGuid().ToString("N") + "-";
            string destination = destinationPrefix + messageId;

            var resolver = new ThinTriggerBodyResolver(destinationPrefix);

            await using CosmosReliabilityHarness harness = CosmosReliabilityHarness.Build(
                testClient.Client,
                // The standalone relay does NOT consult the command-pipeline DocumentReliabilityRegistry, so no
                // WithCosmosDocumentReliability<TCommand> registration is layered: a no-op pipeline keeps the relay under
                // test the ONLY hosted service the harness starts.
                pipeline => { },
                services => services.AddCosmosOutboxRelay(options =>
                {
                    options.MonitoredContainerFactory = serviceProvider => serviceProvider.GetRequiredService<CosmosClient>()
                        .GetContainer(CosmosTestClient.DatabaseName, CosmosTestClient.DocumentContainerName);
                    options.LeaseContainerFactory = serviceProvider => serviceProvider.GetRequiredService<CosmosClient>()
                        .GetContainer(CosmosTestClient.DatabaseName, CosmosTestClient.LeaseContainerName);
                    options.PartitionKeyPath = new[] { CosmosTestClient.PartitionKeyPath };
                    // Bind the resolver: it owns the brokered message for each admitted pending doc INSTEAD of the relay's
                    // verbatim field reconstruction (which would throw on the thin trigger's missing content type).
                    options.BodyResolverFactory = _ => resolver;
                }));

            // Stage the THIN trigger directly into the monitored container BEFORE start; the processor's begin-of-feed
            // start drains the backlog. The doc passes IsPendingOutbox (outbox + pending + id == ForOutbox(MessageId)) yet
            // carries NO MessageBody / MessageContentType / MessageContext — verbatim Reconstruct would throw "no content type".
            await StageThinTriggerAsync(container, messageId, partition);

            await harness.StartAsync();

            // The resolver-produced message reaches the capturing broker sink — proof the relay published via the resolver
            // on a doc the verbatim reconstruction path could not (it would have thrown "no content type").
            await WaitForPublishedToDestinationAsync(harness.Capture, destination, PublishTimeout);
            harness.Capture.Published.Count(m => m.Destination == destination)
                .Should().Be(1, "the standalone relay must publish the resolver-produced message exactly once for the thin trigger");

            // The resolver read the MessageId + partition off the OutboxDrainContext — the body was RESOLVED at drain from
            // the context, not lifted from the (absent) persisted body/content-type fields.
            resolver.ObservedPartitions.Should().ContainKey(messageId);
            resolver.ObservedPartitions[messageId].Should().Be(partition,
                "the resolver must read the partition value the thin trigger carries at the container's /pk path off the drain context");

            // The thin doc is stamped delivered + a positive ttl so Cosmos self-purges it (the container has DefaultTimeToLive
            // enabled). Located by its known id + partition (the thin doc carries no Destination field to query on).
            await WaitForDeliveredWithTtlAsync(container, thinId, partition);
        }

        // Stages the THIN trigger document at the test edge: only the fields IsPendingOutbox requires (id == ForOutbox(MessageId),
        // _chatterType=outbox, status=pending, the verbatim MessageId) plus the partition value at the container's /pk path.
        // It deliberately omits MessageBody / MessageContentType / MessageContext so the verbatim reconstruction path cannot
        // publish it — only a bound resolver can.
        private static async Task StageThinTriggerAsync(Container container, string messageId, string partition)
        {
            using Stream stream = ThinTriggerDocument(messageId, partition);
            using ResponseMessage response = await container.CreateItemStreamAsync(stream, new PartitionKey(partition));
            response.IsSuccessStatusCode.Should().BeTrue("the thin trigger document must be created so the standalone relay can drain it");
        }

        private static Stream ThinTriggerDocument(string messageId, string partition)
        {
            var document = new JsonObject
            {
                [CosmosOutboxDocument.IdField] = CosmosItemId.ForOutbox(messageId),
                [CosmosOutboxDocument.DiscriminatorField] = CosmosItemId.OutboxKind,
                [CosmosOutboxDocument.StatusField] = CosmosOutboxDocument.StatusPending,
                [CosmosOutboxDocument.MessageIdField] = messageId,
                // The partition value at the container's declared single-segment PK path (CosmosTestClient.PartitionKeyPath == "/pk").
                [AggregateDocument.PartitionField] = partition,
            };

            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document);
            return new MemoryStream(bytes, writable: false);
        }

        // Bounded poll until at least one message to the given destination is captured. Returns when seen, else throws
        // (a never-published publication fails fast rather than hanging CI).
        private static async Task WaitForPublishedToDestinationAsync(CapturingInfrastructure capture, string destination, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            do
            {
                if (capture.Published.Any(m => m.Destination == destination))
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
            while (DateTime.UtcNow < deadline);

            throw new Xunit.Sdk.XunitException($"No publication to destination '{destination}' was captured within {timeout}.");
        }

        // Bounded poll until the thin doc is status=delivered with a positive ttl (edge point-read, not a fixed sleep). The
        // doc is located by its known id + partition — it carries no Destination field, so the WhenRelayingOutbox query-by-
        // Destination read does not apply; a direct point-read is the attributable read for a thin trigger.
        private static async Task WaitForDeliveredWithTtlAsync(Container container, string id, string partition)
        {
            DateTime deadline = DateTime.UtcNow + DeliveredTimeout;
            string lastStatus = null;
            do
            {
                (string status, bool positiveTtl) = await ReadStatusAndTtlAsync(container, id, partition);
                lastStatus = status;
                if (string.Equals(status, CosmosOutboxDocument.StatusDelivered, StringComparison.Ordinal) && positiveTtl)
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
            while (DateTime.UtcNow < deadline);

            throw new Xunit.Sdk.XunitException(
                $"The thin trigger document was not stamped delivered with a positive ttl within {DeliveredTimeout} (last observed status '{lastStatus}').");
        }

        // Point-reads the thin doc by id + partition as a stream the test parses with System.Text.Json (the stream bytes
        // are fully test-owned, avoiding the disposed-JsonElement hazard of GetItemQueryIterator<JsonElement>).
        private static async Task<(string status, bool positiveTtl)> ReadStatusAndTtlAsync(Container container, string id, string partition)
        {
            using ResponseMessage read = await container.ReadItemStreamAsync(id, new PartitionKey(partition));
            if (read.StatusCode != HttpStatusCode.OK)
            {
                return (null, false);
            }

            using JsonDocument document = await JsonDocument.ParseAsync(read.Content);
            JsonElement root = document.RootElement;
            string status = root.TryGetProperty(CosmosOutboxDocument.StatusField, out JsonElement statusElement) && statusElement.ValueKind == JsonValueKind.String
                ? statusElement.GetString()
                : null;
            bool positiveTtl = root.TryGetProperty("ttl", out JsonElement ttlElement)
                && ttlElement.ValueKind == JsonValueKind.Number
                && ttlElement.GetInt32() > 0;
            return (status, positiveTtl);
        }

        // An IOutboxBodyResolver that RESOLVES the brokered message at drain from the OutboxDrainContext: it reads the
        // verbatim MessageId and the /pk partition value off the context (the thin trigger carries no body/content-type/
        // context to reconstruct from), builds a fresh json body keyed to those values, and publishes to a per-MessageId
        // destination so a stray pending doc cannot pollute another doc's sink. It records the partition it observed per
        // MessageId so the test can prove the body was resolved from the context rather than lifted from the doc.
        private sealed class ThinTriggerBodyResolver : IOutboxBodyResolver
        {
            private readonly string _destinationPrefix;
            private readonly JsonBodyConverter _converter = new JsonBodyConverter();
            private readonly ConcurrentDictionary<string, string> _observedPartitions = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

            public ThinTriggerBodyResolver(string destinationPrefix)
                => _destinationPrefix = destinationPrefix ?? throw new ArgumentNullException(nameof(destinationPrefix));

            // MessageId -> the partition value the resolver read off the drain context for that doc.
            public IReadOnlyDictionary<string, string> ObservedPartitions => _observedPartitions;

            public Task<OutboundBrokeredMessage> ResolveAsync(OutboxDrainContext context, CancellationToken cancellationToken = default)
            {
                string messageId = context.MessageId;
                string partition = context.Document.TryGetProperty(AggregateDocument.PartitionField, out JsonElement partitionElement)
                    && partitionElement.ValueKind == JsonValueKind.String
                        ? partitionElement.GetString()
                        : null;
                _observedPartitions[messageId] = partition;

                // The infrastructure type stamped on the context so the relay's GetDispatcher resolves the capture sink (it
                // is also the only registered infrastructure, so the default path lands there too).
                var messageContext = new Dictionary<string, object>
                {
                    [MessageContext.InfrastructureType] = CapturingInfrastructure.InfrastructureType,
                };

                // The body is RESOLVED at drain from the context values — nothing here comes off the persisted doc body.
                byte[] body = _converter.Convert(new Dictionary<string, string>
                {
                    ["messageId"] = messageId,
                    ["partition"] = partition,
                });

                var message = new OutboundBrokeredMessage(messageId, body, messageContext, _destinationPrefix + messageId, _converter);
                return Task.FromResult(message);
            }
        }
    }
}
