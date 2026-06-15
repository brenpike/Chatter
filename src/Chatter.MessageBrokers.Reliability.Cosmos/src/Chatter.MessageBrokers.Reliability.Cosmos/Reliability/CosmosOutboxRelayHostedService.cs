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
    /// Hosts the #222 document-tier change-feed outbox relay: one Cosmos <see cref="ChangeFeedProcessor"/> per DISTINCT
    /// <c>(Database, ContainerName, LeaseName)</c> triple from the <see cref="DocumentReliabilityRegistry"/> — NOT one
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
    /// <c>processorName</c> is STABLE per triple (a constant prefix + the triple) so every application instance sharing a
    /// lease cooperates on the same logical processor; <c>instanceName</c> is UNIQUE per host (machine + a GUID) so
    /// co-located instances do not collide on a lease.
    /// </para>
    /// </remarks>
    internal sealed class CosmosOutboxRelayHostedService : IHostedService
    {
        // Stable processorName prefix; combined with the (Database, ContainerName, LeaseName) triple it yields a
        // deterministic processor identity shared across all app instances draining the same lease.
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

            foreach (RelayProcessorDescriptor descriptor in DistinctProcessorDescriptors())
            {
                Container monitoredContainer = _containerFactory.GetDocumentContainer(descriptor.Registration);
                Container leaseContainer = _containerFactory.GetLeaseContainer(descriptor.Registration);
                IReadOnlyList<string> partitionKeyPath = descriptor.Registration.PartitionKeyPath;

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
        private async Task HandleChangesAsync(Stream changes, Container monitoredContainer, IReadOnlyList<string> partitionKeyPath, CancellationToken cancellationToken)
        {
            using JsonDocument payload = await JsonDocument.ParseAsync(changes, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!payload.RootElement.TryGetProperty("Documents", out JsonElement documents) || documents.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (JsonElement document in documents.EnumerateArray())
            {
                await _relay.ProcessChangeAsync(document, monitoredContainer, partitionKeyPath, cancellationToken).ConfigureAwait(false);
            }
        }

        // One descriptor per DISTINCT (Database, ContainerName, LeaseName) triple. Many command types may share one
        // container, so the host dedupes the triple here — the first registration seen for a triple supplies the
        // container handles and the (container-shared) partition-key path; subsequent registrations for the same triple
        // are skipped (no duplicate processor on the same lease).
        private IEnumerable<RelayProcessorDescriptor> DistinctProcessorDescriptors()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (DocumentReliabilityRegistration registration in _registry.Registrations)
            {
                string triple = registration.Database + "\0" + registration.ContainerName + "\0" + registration.LeaseName;
                if (!seen.Add(triple))
                {
                    continue;
                }

                yield return new RelayProcessorDescriptor($"{ProcessorNamePrefix}:{triple}", registration);
            }
        }

        private readonly struct RelayProcessorDescriptor
        {
            public RelayProcessorDescriptor(string processorName, DocumentReliabilityRegistration registration)
            {
                ProcessorName = processorName;
                Registration = registration;
            }

            public string ProcessorName { get; }
            public DocumentReliabilityRegistration Registration { get; }
        }
    }
}
