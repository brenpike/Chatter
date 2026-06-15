using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// Hosts the #222 document-tier change-feed outbox relay: one Cosmos <see cref="ChangeFeedProcessor"/> per distinct
    /// PHYSICAL (monitored, lease) container pair RESOLVED from the <see cref="DocumentReliabilityRegistry"/> — NOT one
    /// per command type (many command types may share one container, ADR-0008). Each processor monitors its container's
    /// change feed and feeds every change through the testable <see cref="CosmosOutboxRelay"/> core, which filters to
    /// outbox+pending documents, publishes them, and stamps delivered+TTL.
    /// </summary>
    /// <remarks>
    /// The SDK plumbing is kept deliberately thin (coverage targets <see cref="CosmosOutboxRelay"/>): this host derives
    /// the monitored + lease containers via <see cref="CosmosContainerFactory"/>, wires the change-feed STREAM handler so
    /// Chatter parses each change with System.Text.Json (no Cosmos-SDK Newtonsoft deserialization of the relay's reads),
    /// and starts/stops the processors. The change-feed handler does NOT swallow a publish failure: an exception thrown
    /// by the relay propagates out of the handler so the SDK does NOT checkpoint the batch — the unpublished document
    /// re-surfaces on the next pass (at-least-once) rather than the lease advancing past it.
    /// <para>
    /// INVARIANT: the fan-out dedupes on the RESOLVED physical container identity
    /// (<c>monitored.Database.Id</c>/<c>monitored.Id</c>/<c>lease.Database.Id</c>/<c>lease.Id</c>), NOT on the
    /// registration's <c>(Database, ContainerName, LeaseName)</c> triple. The advanced <c>WithCosmosDocumentReliability</c>
    /// overload SYNTHESIZES that triple from the command type while resolving the REAL containers via app-supplied
    /// factories, so two command types whose factories resolve the SAME physical document+lease containers carry DISTINCT
    /// synthetic triples; deduping on the triple would split one physical lease into two processors and double-publish
    /// every pending outbox doc (ADR-0008: one processor per physical container). Keying on the resolved identity cannot
    /// diverge from the thing it dedupes.
    /// </para>
    /// <para>
    /// <c>processorName</c> is STABLE per physical key (a constant prefix + the physical key) so every application instance
    /// sharing a lease cooperates on the same logical processor; <c>instanceName</c> is UNIQUE per host (machine + a GUID)
    /// so co-located instances do not collide on a lease.
    /// </para>
    /// </remarks>
    internal sealed class CosmosOutboxRelayHostedService : IHostedService
    {
        // Stable processorName prefix; combined with the RESOLVED physical-container key it yields a deterministic
        // processor identity shared across all app instances draining the same lease.
        private const string ProcessorNamePrefix = "chatter-cosmos-outbox-relay";

        private readonly DocumentReliabilityRegistry _registry;
        private readonly CosmosContainerFactory _containerFactory;
        private readonly CosmosOutboxRelay _relay;
        private readonly List<ChangeFeedProcessor> _processors = new List<ChangeFeedProcessor>();

        public CosmosOutboxRelayHostedService(DocumentReliabilityRegistry registry,
                                              CosmosContainerFactory containerFactory,
                                              IMessagingInfrastructureProvider infrastructureProvider,
                                              IBodyConverterFactory bodyConverterFactory)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _containerFactory = containerFactory ?? throw new ArgumentNullException(nameof(containerFactory));
            _ = infrastructureProvider ?? throw new ArgumentNullException(nameof(infrastructureProvider));
            _ = bodyConverterFactory ?? throw new ArgumentNullException(nameof(bodyConverterFactory));
            _relay = new CosmosOutboxRelay(infrastructureProvider, bodyConverterFactory);
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            string instanceName = $"{ProcessorNamePrefix}:{Environment.MachineName}:{Guid.NewGuid()}";

            foreach (RelayProcessorDescriptor descriptor in DistinctResolvedProcessorDescriptors())
            {
                Container monitoredContainer = descriptor.MonitoredContainer;
                Container leaseContainer = descriptor.LeaseContainer;
                IReadOnlyList<string> partitionKeyPath = descriptor.PartitionKeyPath;

                ChangeFeedProcessor processor = monitoredContainer
                    .GetChangeFeedProcessorBuilder(descriptor.ProcessorName, (ChangeFeedProcessorContext context, Stream changes, CancellationToken changeCancellationToken)
                        => HandleChangesAsync(changes, monitoredContainer, partitionKeyPath, changeCancellationToken))
                    .WithInstanceName(instanceName)
                    .WithLeaseContainer(leaseContainer)
                    .Build();

                await processor.StartAsync().ConfigureAwait(false);
                _processors.Add(processor);
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            foreach (ChangeFeedProcessor processor in _processors)
            {
                await processor.StopAsync().ConfigureAwait(false);
            }

            _processors.Clear();
        }

        // Parses the change-feed stream payload ({ "Documents": [ ... ] }) with System.Text.Json so Chatter owns the read
        // wire shape, and feeds each document through the relay core. An exception from the relay (a publish failure)
        // propagates out of this handler so the SDK does NOT checkpoint the batch and the document re-surfaces next pass.
        // internal (not private) so the fail-closed malformed-payload behavior is unit-testable without the live SDK
        // change-feed plumbing; the assembly exposes internals to the test project.
        internal async Task HandleChangesAsync(Stream changes, Container monitoredContainer, IReadOnlyList<string> partitionKeyPath, CancellationToken cancellationToken)
        {
            using JsonDocument payload = await JsonDocument.ParseAsync(changes, cancellationToken: cancellationToken).ConfigureAwait(false);

            // FAIL CLOSED on an unexpected batch shape. Normal handler completion is the SDK's checkpoint signal, so a
            // silent return on a missing/non-array "Documents" property would advance the lease PAST every change in the
            // batch — losing any pending outbox doc inside a payload whose shape the relay could not parse (SDK version
            // skew, a wire-contract change, or a corrupt batch). For an at-least-once relay the correct bias is to throw
            // so the batch is NOT checkpointed and re-surfaces next pass, exactly as a publish failure does.
            if (!payload.RootElement.TryGetProperty("Documents", out JsonElement documents) || documents.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    "The Cosmos change-feed batch payload did not contain a 'Documents' array. The relay cannot determine which documents to drain from this batch, so it fails closed (the batch is not checkpointed and re-surfaces) rather than silently advancing the lease past potentially-unpublished outbox documents.");
            }

            foreach (JsonElement document in documents.EnumerateArray())
            {
                await _relay.ProcessChangeAsync(document, monitoredContainer, partitionKeyPath, cancellationToken).ConfigureAwait(false);
            }
        }

        // One descriptor per distinct PHYSICAL (monitored, lease) container pair (ADR-0008). Each registration is RESOLVED
        // FIRST — the monitored and lease container handles are derived via the factory — and deduped on the resolved
        // physical identity (monitored.Database.Id / monitored.Id / lease.Database.Id / lease.Id), NOT on the
        // registration's (Database, ContainerName, LeaseName) triple. The advanced overload synthesizes that triple from
        // the command type while resolving the same physical containers, so deduping on the triple would split one
        // physical lease into two processors and double-publish every pending outbox doc. The first registration seen for
        // a physical key wins and supplies the resolved handles + partition-key path; subsequent registrations resolving
        // to the same physical key are skipped (no duplicate processor on the same lease). processorName is derived from
        // the SAME physical key so all app instances sharing a physical lease cooperate on one logical processor.
        // internal (not private) so the resolve-then-dedup is unit-testable without the live SDK change-feed plumbing; the
        // assembly exposes internals to the test project.
        internal IReadOnlyList<RelayProcessorDescriptor> DistinctResolvedProcessorDescriptors()
        {
            var descriptors = new List<RelayProcessorDescriptor>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (DocumentReliabilityRegistration registration in _registry.Registrations)
            {
                Container monitoredContainer = _containerFactory.GetDocumentContainer(registration);
                Container leaseContainer = _containerFactory.GetLeaseContainer(registration);

                string physicalKey = monitoredContainer.Database.Id + "\0" + monitoredContainer.Id + "\0"
                    + leaseContainer.Database.Id + "\0" + leaseContainer.Id;
                if (!seen.Add(physicalKey))
                {
                    continue;
                }

                descriptors.Add(new RelayProcessorDescriptor(
                    $"{ProcessorNamePrefix}:{physicalKey}",
                    monitoredContainer,
                    leaseContainer,
                    registration.PartitionKeyPath));
            }

            return descriptors;
        }

        internal readonly struct RelayProcessorDescriptor
        {
            public RelayProcessorDescriptor(string processorName,
                                            Container monitoredContainer,
                                            Container leaseContainer,
                                            IReadOnlyList<string> partitionKeyPath)
            {
                ProcessorName = processorName;
                MonitoredContainer = monitoredContainer;
                LeaseContainer = leaseContainer;
                PartitionKeyPath = partitionKeyPath;
            }

            public string ProcessorName { get; }
            public Container MonitoredContainer { get; }
            public Container LeaseContainer { get; }
            public IReadOnlyList<string> PartitionKeyPath { get; }
        }
    }
}
