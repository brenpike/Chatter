using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
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
    /// Hosts a STANDALONE change-feed outbox relay configured via <see cref="CosmosOutboxRelayOptions"/> — one Cosmos
    /// <see cref="ChangeFeedProcessor"/> over the options' explicitly-configured monitored + lease containers, independent
    /// of the <see cref="DocumentReliabilityRegistry"/>. It mirrors <see cref="CosmosOutboxRelayHostedService"/>'s
    /// change-feed-processor lifecycle and feeds every change through the same testable <see cref="CosmosOutboxRelay"/>
    /// core (filter to pending outbox documents, publish, stamp delivered+TTL), but derives its single processor descriptor
    /// from the OPTIONS rather than the registry.
    /// </summary>
    /// <remarks>
    /// The processor name is built from the SAME injective source-identity derivation the registry-driven host uses
    /// (<see cref="CosmosOutboxRelayHostedService.BuildProcessorName"/>), keyed on the caller-declared source identity when
    /// supplied (<see cref="CosmosOutboxRelayOptions.MonitoredSourceIdentity"/> / <see cref="CosmosOutboxRelayOptions.LeaseSourceIdentity"/>)
    /// else the resolved containers' ground-truth identity (account endpoint + database id + container id). Because the
    /// derivation is shared, a standalone relay coexists with the command-pipeline relay without a processor-name
    /// collision. The change-feed handler does NOT swallow a publish failure: an exception from the relay propagates so the
    /// SDK does NOT checkpoint the batch and the unpublished document re-surfaces next pass (at-least-once).
    /// </remarks>
    internal sealed class StandaloneCosmosOutboxRelayHostedService : IHostedService
    {
        private const string ProcessorNamePrefix = "chatter-cosmos-outbox-relay";

        private readonly IServiceProvider _serviceProvider;
        private readonly CosmosOutboxRelayOptions _options;
        private readonly CosmosOutboxRelay _relay;
        private ChangeFeedProcessor _processor;

        internal StandaloneCosmosOutboxRelayHostedService(IServiceProvider serviceProvider,
                                                          IMessagingInfrastructureProvider infrastructureProvider,
                                                          IBodyConverterFactory bodyConverterFactory,
                                                          CosmosOutboxRelayOptions options)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _ = infrastructureProvider ?? throw new ArgumentNullException(nameof(infrastructureProvider));
            _ = bodyConverterFactory ?? throw new ArgumentNullException(nameof(bodyConverterFactory));
            _options = options ?? throw new ArgumentNullException(nameof(options));

            // Build the relay with the options' validated drain knobs (OutboxDeliverySettings.FromOptions enforces the F2
            // invariants), including the caller's optional AdditionalPendingFilter, which the relay's ProcessChangeAsync
            // self-guard composes EXACTLY ONCE (the host pre-gate uses only the pure IsPendingOutbox identity guard — see
            // ProcessDocumentAsync — so the caller filter is never double-evaluated). The optional body resolver is NOT
            // resolved here: it is created PER PENDING (IsPendingOutbox) document from a fresh DI scope in HandleChangesAsync
            // (see ProcessDocumentAsync), so a scoped resolver never outlives the document it drains and the relay never
            // carries a resolver it might silently reuse across documents.
            _relay = new CosmosOutboxRelay(
                infrastructureProvider,
                bodyConverterFactory,
                OutboxDeliverySettings.FromOptions(options));
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            string instanceName = $"{ProcessorNamePrefix}:{Environment.MachineName}:{Guid.NewGuid()}";

            CosmosOutboxRelayHostedService.RelayProcessorDescriptor descriptor = ResolveProcessorDescriptor();
            Container monitoredContainer = descriptor.MonitoredContainer;
            Container leaseContainer = descriptor.LeaseContainer;
            IReadOnlyList<string> partitionKeyPath = descriptor.PartitionKeyPath;

            ChangeFeedProcessor processor = monitoredContainer
                .GetChangeFeedProcessorBuilder(descriptor.ProcessorName, (ChangeFeedProcessorContext context, Stream changes, CancellationToken changeCancellationToken)
                    => HandleChangesAsync(changes, monitoredContainer, partitionKeyPath, changeCancellationToken))
                .WithInstanceName(instanceName)
                .WithLeaseContainer(leaseContainer)
                // Start from the BEGINNING of the change feed (see CosmosOutboxRelayHostedService): the SDK default start
                // point is "now", which would skip any pending outbox documents written before the lease initializes.
                .WithStartTime(DateTime.MinValue.ToUniversalTime())
                .Build();

            await processor.StartAsync().ConfigureAwait(false);
            _processor = processor;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_processor is not null)
            {
                await _processor.StopAsync().ConfigureAwait(false);
                _processor = null;
            }
        }

        // Resolves the single processor descriptor from the OPTIONS: the monitored + lease containers via the configured
        // factories against the root provider, the configured partition-key path, and a processor name derived from the
        // source-identity key (declared when supplied, else resolved-container ground truth). internal so processor
        // selection is unit-testable without a live Cosmos account, mirroring how the registry host exposes
        // DistinctResolvedProcessorDescriptors().
        internal CosmosOutboxRelayHostedService.RelayProcessorDescriptor ResolveProcessorDescriptor()
        {
            Container monitoredContainer = ResolveContainer(_options.MonitoredContainerFactory, "monitored");
            Container leaseContainer = ResolveContainer(_options.LeaseContainerFactory, "lease");

            CosmosOutboxRelayHostedService.RelaySourceIdentityKey sourceIdentityKey = BuildSourceIdentityKey(monitoredContainer, leaseContainer);

            return new CosmosOutboxRelayHostedService.RelayProcessorDescriptor(
                CosmosOutboxRelayHostedService.BuildProcessorName(sourceIdentityKey),
                monitoredContainer,
                leaseContainer,
                _options.PartitionKeyPath);
        }

        // Parses the change-feed stream payload and feeds each document through the relay core, mirroring
        // CosmosOutboxRelayHostedService.HandleChangesAsync: FAIL CLOSED on an unexpected batch shape (throw so the SDK
        // does not checkpoint the batch) and let a publish failure propagate so the document re-surfaces next pass.
        // internal so the fail-closed behavior is unit-testable without the live SDK change-feed plumbing.
        internal async Task HandleChangesAsync(Stream changes, Container monitoredContainer, IReadOnlyList<string> partitionKeyPath, CancellationToken cancellationToken)
        {
            using JsonDocument payload = await JsonDocument.ParseAsync(changes, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!payload.RootElement.TryGetProperty("Documents", out JsonElement documents) || documents.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    "The Cosmos change-feed batch payload did not contain a 'Documents' array. The relay cannot determine which documents to drain from this batch, so it fails closed (the batch is not checkpointed and re-surfaces) rather than silently advancing the lease past potentially-unpublished outbox documents.");
            }

            foreach (JsonElement document in documents.EnumerateArray())
            {
                await ProcessDocumentAsync(document, monitoredContainer, partitionKeyPath, cancellationToken).ConfigureAwait(false);
            }
        }

        // Drains a single change-feed document through the relay. The monitored container is CO-RESIDENT (domain
        // aggregates, inbox markers, outbox docs, and the relay's own delivered-stamp event all surface on this feed), so a
        // PURE IDENTITY pre-gate is checked FIRST — BEFORE any DI scope is opened or the body-resolver factory is invoked.
        // The pre-gate is CosmosOutboxDocument.IsPendingOutbox ALONE (pure, total, runs NO caller code and never throws): a
        // document that is not a genuine Chatter pending outbox doc (a domain write, an inbox marker, a malformed item, an
        // already-delivered doc) is skipped with NO scope, NO factory call, and NO user DI, so a throwing
        // factory/resolver-ctor on a non-outbox write can never wedge the change feed. The caller-supplied
        // AdditionalPendingFilter is deliberately NOT evaluated here: it is composed EXACTLY ONCE inside the relay's
        // ProcessChangeAsync self-guard, so a non-idempotent or throwing caller filter cannot be double-evaluated and
        // re-open the feed-wedge class. A document that passes IsPendingOutbox but is narrowed out by the caller filter
        // opens a scope + invokes the body-resolver factory before the relay rejects it on the filter (one wasted scope; the
        // resolver is never asked to resolve; nothing is published or stamped; the scope is disposed) — an acceptable cost
        // for composing the caller delegate at a single site. When a body-resolver factory is configured, a FRESH DI scope
        // is opened PER PENDING DOCUMENT and the resolver is resolved from that scope's provider, so a scoped resolver (and
        // the scoped dependencies it pulls) never outlives the document it drains. The `using` scope WRAPS the
        // ProcessChangeAsync call so a propagating publish/resolver failure (at-least-once: the SDK does not checkpoint and
        // the document re-surfaces) still disposes the scope while the exception unwinds. With no factory configured the
        // relay's no-resolver verbatim reconstruction path is used UNCHANGED and no scope is opened.
        private async Task ProcessDocumentAsync(JsonElement document, Container monitoredContainer, IReadOnlyList<string> partitionKeyPath, CancellationToken cancellationToken)
        {
            if (!CosmosOutboxDocument.IsPendingOutbox(document))
            {
                return;
            }

            if (_options.BodyResolverFactory is null)
            {
                await _relay.ProcessChangeAsync(document, monitoredContainer, partitionKeyPath, cancellationToken).ConfigureAwait(false);
                return;
            }

            using IServiceScope scope = _serviceProvider.CreateScope();
            IOutboxBodyResolver resolver = _options.BodyResolverFactory(scope.ServiceProvider);
            await _relay.ProcessChangeAsync(document, monitoredContainer, partitionKeyPath, resolver, cancellationToken).ConfigureAwait(false);
        }

        // Builds the source-identity dedup/name key the SAME way CosmosOutboxRelayHostedService does, but sourced from the
        // options: the caller-declared identity when EITHER side is supplied (the resolved handle is not read), else the
        // resolved containers' ground truth (account endpoint + database id + container id, for both monitored and lease).
        private CosmosOutboxRelayHostedService.RelaySourceIdentityKey BuildSourceIdentityKey(Container monitoredContainer, Container leaseContainer)
        {
            if (_options.MonitoredSourceIdentity is not null || _options.LeaseSourceIdentity is not null)
            {
                return CosmosOutboxRelayHostedService.RelaySourceIdentityKey.ForDeclared(
                    _options.MonitoredSourceIdentity,
                    _options.LeaseSourceIdentity);
            }

            return CosmosOutboxRelayHostedService.RelaySourceIdentityKey.ForGroundTruth(
                CosmosOutboxRelayHostedService.NormalizeEndpoint(monitoredContainer.Database.Client.Endpoint),
                monitoredContainer.Database.Id,
                monitoredContainer.Id,
                CosmosOutboxRelayHostedService.NormalizeEndpoint(leaseContainer.Database.Client.Endpoint),
                leaseContainer.Database.Id,
                leaseContainer.Id);
        }

        private Container ResolveContainer(Func<IServiceProvider, Container> factory, string role)
            => factory(_serviceProvider)
                ?? throw new InvalidOperationException($"The configured {role}-container factory returned null.");
    }
}
