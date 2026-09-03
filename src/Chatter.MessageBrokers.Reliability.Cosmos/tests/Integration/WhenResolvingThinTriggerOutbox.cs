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
    //     verbatim path would stamp this doc UNDELIVERABLE — it carries no body/content-type/context, which the Outbox
    //     Document Contract proves from its own bytes — so the only way it can be published is through a bound resolver
    //     that RESOLVES the body at drain (the contract is evaluated ONLY on the no-resolver path).
    //   - an IOutboxBodyResolver bound on the standalone relay reads the MessageId + the /pk partition value off the
    //     OutboxDrainContext and returns a real OutboundBrokeredMessage (content type + body + destination) built at drain.
    //   - the resolver is bound through a SCOPED service registration: BodyResolverFactory resolves the resolver from the
    //     provider it is handed, and the resolver constructor-captures a SCOPED dependency (ScopedDrainProbe). Because the
    //     standalone host opens a FRESH DI scope PER DRAINED DOCUMENT and hands that scope's provider to BodyResolverFactory,
    //     the scoped dependency resolves and is then DISPOSED when the host disposes the per-document scope. This proves the
    //     F3 closed-by-construction guarantee: a resolver MAY depend on scoped services without opening its own scope.
    //   - ASSERT: the resolver-produced message reaches the capturing broker sink (where the verbatim path would have
    //     stamped the doc undeliverable), the resolver observed the staged MessageId + partition (proving the body was
    //     resolved from the context, not the doc), the thin doc is then stamped status=delivered + a positive ttl so it
    //     self-purges, AND the resolver's scoped dependency was both CONSTRUCTED from the per-document scope and
    //     DISPOSED when the host disposed that scope
    //     (a scoped dependency resolved from the root provider would not be disposed until harness teardown).
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

            // The test-held shared sink the scoped resolver + scoped probe record into; it is registered as a SINGLETON so
            // the exact instance the test asserts on is the one the per-document scope injects.
            var observations = new ThinTriggerObservations();

            await using CosmosReliabilityHarness harness = CosmosReliabilityHarness.Build(
                testClient.Client,
                // The standalone relay does NOT consult the command-pipeline DocumentReliabilityRegistry, so no
                // WithCosmosDocumentReliability<TCommand> registration is layered: a no-op pipeline keeps the relay under
                // test the ONLY hosted service the harness starts.
                pipeline => { },
                services =>
                {
                    // The shared observation sink (singleton) the scoped probe + scoped resolver write into.
                    services.AddSingleton(observations);
                    // A SCOPED dependency the resolver constructor-captures: it records its construction and disposal into
                    // the sink. The standalone host opens a fresh scope PER DRAINED DOCUMENT, so this probe is constructed
                    // from that per-document scope and DISPOSED when the host disposes the scope after the document drains.
                    services.AddScoped(serviceProvider => new ScopedDrainProbe(serviceProvider.GetRequiredService<ThinTriggerObservations>()));
                    // The resolver itself is SCOPED and depends on the scoped probe: it owns the brokered message for each
                    // admitted pending doc INSTEAD of the relay's verbatim field reconstruction (which would throw on the
                    // thin trigger's missing content type), and demonstrates a resolver MAY depend on scoped services.
                    services.AddScoped(serviceProvider => new ThinTriggerBodyResolver(destinationPrefix, serviceProvider.GetRequiredService<ScopedDrainProbe>()));
                    services.AddCosmosOutboxRelay(options =>
                    {
                        options.MonitoredContainerFactory = serviceProvider => serviceProvider.GetRequiredService<CosmosClient>()
                            .GetContainer(CosmosTestClient.DatabaseName, CosmosTestClient.DocumentContainerName);
                        options.LeaseContainerFactory = serviceProvider => serviceProvider.GetRequiredService<CosmosClient>()
                            .GetContainer(CosmosTestClient.DatabaseName, CosmosTestClient.LeaseContainerName);
                        options.PartitionKeyPath = new[] { CosmosTestClient.PartitionKeyPath };
                        // Resolve the resolver from the provider the host HANDS the factory. R-STEP-002 hands the
                        // per-document SCOPE's provider here, so the scoped resolver (and its scoped probe) resolve from a
                        // genuine child scope rather than a pre-built instance closed over by the test.
                        options.BodyResolverFactory = serviceProvider => serviceProvider.GetRequiredService<ThinTriggerBodyResolver>();
                    });
                });

            // Stage the THIN trigger directly into the monitored container BEFORE start; the processor's begin-of-feed
            // start drains the backlog. The doc passes IsPendingOutbox (outbox + pending + id == ForOutbox(MessageId)) yet
            // carries NO MessageBody / MessageContentType / MessageContext — the verbatim path would stamp it undeliverable.
            await StageThinTriggerAsync(container, messageId, partition);

            await harness.StartAsync();

            // The resolver-produced message reaches the capturing broker sink — proof the relay published via the resolver
            // on a doc the verbatim reconstruction path could not (it would have stamped it undeliverable).
            await WaitForPublishedToDestinationAsync(harness.Capture, destination, PublishTimeout);
            harness.Capture.Published.Count(m => m.Destination == destination)
                .Should().Be(1, "the standalone relay must publish the resolver-produced message exactly once for the thin trigger");

            // The resolver read the MessageId + partition off the OutboxDrainContext — the body was RESOLVED at drain from
            // the context, not lifted from the (absent) persisted body/content-type fields.
            observations.ObservedPartitions.Should().ContainKey(messageId);
            observations.ObservedPartitions[messageId].Should().Be(partition,
                "the resolver must read the partition value the thin trigger carries at the container's /pk path off the drain context");

            // The thin doc is stamped delivered + a positive ttl so Cosmos self-purges it (the container has DefaultTimeToLive
            // enabled). Located by its known id + partition (the thin doc carries no Destination field to query on).
            await WaitForDeliveredWithTtlAsync(container, thinId, partition);

            // F3 closed-by-construction: the resolver's SCOPED dependency was constructed from the provider BodyResolverFactory
            // was handed — i.e. the per-document scope the host opened — so a resolver may depend on scoped services without
            // opening its own scope. (>= 1: the shared container may surface other tests' admitted pending docs, each draining
            // through its own per-document scope; this test's drain is one of them.)
            observations.ScopeConstructedCount.Should().BeGreaterThanOrEqualTo(1,
                "BodyResolverFactory must resolve the scoped resolver + its scoped probe from the per-document scope the host hands it");

            // The host disposes the per-document scope after the document drains (the `using IServiceScope` wraps the drain),
            // so the scoped probe is DISPOSED while the harness is still alive. A scoped dependency resolved from the ROOT
            // provider would only be disposed at harness teardown — observing disposal here proves the factory received a
            // genuine per-document child scope, not the root/singleton provider.
            await WaitForScopeDisposedAsync(observations);
            observations.ScopeDisposedCount.Should().BeGreaterThanOrEqualTo(1,
                "the host must dispose the per-document scope after draining, disposing the resolver's scoped dependency");
        }

        // Bounded poll until the host has disposed at least one per-document scope (the scoped probe's Dispose recorded into
        // the sink), else throws — a never-disposed scope fails fast rather than hanging CI.
        private static async Task WaitForScopeDisposedAsync(ThinTriggerObservations observations)
        {
            DateTime deadline = DateTime.UtcNow + DeliveredTimeout;
            do
            {
                if (observations.ScopeDisposedCount >= 1)
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
            while (DateTime.UtcNow < deadline);

            throw new Xunit.Sdk.XunitException(
                $"The host did not dispose a per-document scope within {DeliveredTimeout} (constructed {observations.ScopeConstructedCount}, disposed {observations.ScopeDisposedCount}).");
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

        // The test-held shared sink the scoped resolver + scoped probe record into. Registered as a SINGLETON so the exact
        // instance the test asserts on is injected into every per-document scope. Records (a) the partition each resolver
        // observed off the drain context (proving the body was resolved from the context, not lifted from the doc) and
        // (b) how many per-document scopes the host constructed + disposed the probe in.
        private sealed class ThinTriggerObservations
        {
            private readonly ConcurrentDictionary<string, string> _observedPartitions = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
            private int _scopeConstructedCount;
            private int _scopeDisposedCount;

            // MessageId -> the partition value a resolver read off the drain context for that doc.
            public IReadOnlyDictionary<string, string> ObservedPartitions => _observedPartitions;

            // The number of per-document scopes the scoped probe was constructed in / disposed in.
            public int ScopeConstructedCount => Volatile.Read(ref _scopeConstructedCount);
            public int ScopeDisposedCount => Volatile.Read(ref _scopeDisposedCount);

            public void RecordObservedPartition(string messageId, string partition) => _observedPartitions[messageId] = partition;
            public void RecordScopeConstructed() => Interlocked.Increment(ref _scopeConstructedCount);
            public void RecordScopeDisposed() => Interlocked.Increment(ref _scopeDisposedCount);
        }

        // A SCOPED, IDisposable dependency the resolver constructor-captures. The standalone host opens a fresh DI scope per
        // drained document and hands that scope's provider to BodyResolverFactory, so this probe is constructed from the
        // per-document scope and DISPOSED when the host disposes the scope after the document drains. Its construction +
        // disposal counts are the attributable evidence that BodyResolverFactory received a genuine per-document child scope
        // (a scoped service resolved from the root provider would not be disposed until harness teardown).
        private sealed class ScopedDrainProbe : IDisposable
        {
            private readonly ThinTriggerObservations _observations;

            public ScopedDrainProbe(ThinTriggerObservations observations)
            {
                _observations = observations ?? throw new ArgumentNullException(nameof(observations));
                _observations.RecordScopeConstructed();
            }

            // The shared sink the resolver records its observed partition into — reached THROUGH the scoped probe so the
            // resolver genuinely depends on the scoped dependency it captured.
            public ThinTriggerObservations Observations => _observations;

            public void Dispose() => _observations.RecordScopeDisposed();
        }

        // An IOutboxBodyResolver that RESOLVES the brokered message at drain from the OutboxDrainContext: it reads the
        // verbatim MessageId and the /pk partition value off the context (the thin trigger carries no body/content-type/
        // context to reconstruct from), builds a fresh json body keyed to those values, and publishes to a per-MessageId
        // destination so a stray pending doc cannot pollute another doc's sink. It is registered SCOPED and constructor-
        // captures a SCOPED ScopedDrainProbe, recording the observed partition into the shared sink reached through the
        // probe — so the test can prove the body was resolved from the context AND that a resolver may depend on scoped
        // services without opening its own scope.
        private sealed class ThinTriggerBodyResolver : IOutboxBodyResolver
        {
            private readonly string _destinationPrefix;
            private readonly ScopedDrainProbe _probe;
            private readonly JsonBodyConverter _converter = new JsonBodyConverter();

            public ThinTriggerBodyResolver(string destinationPrefix, ScopedDrainProbe probe)
            {
                _destinationPrefix = destinationPrefix ?? throw new ArgumentNullException(nameof(destinationPrefix));
                _probe = probe ?? throw new ArgumentNullException(nameof(probe));
            }

            public Task<OutboundBrokeredMessage> ResolveAsync(OutboxDrainContext context, CancellationToken cancellationToken = default)
            {
                string messageId = context.MessageId;
                string partition = context.Document.TryGetProperty(AggregateDocument.PartitionField, out JsonElement partitionElement)
                    && partitionElement.ValueKind == JsonValueKind.String
                        ? partitionElement.GetString()
                        : null;
                // Record through the scoped probe's sink — proving the resolver used the scoped dependency it depends on.
                _probe.Observations.RecordObservedPartition(messageId, partition);

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
