using Chatter.MessageBrokers.Reliability.Cosmos.Diagnostics;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
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
        private readonly StandaloneRelayProcessorRegistry _processorRegistry;
        private readonly RelayFailureNotifier _failureNotifier;
        private readonly OutboxGiveUpHandler _giveUpHandler;
        private readonly ILogger _logger;
        private ChangeFeedProcessor _processor;

        // processorRegistry is OPTIONAL (defaults null) so every existing direct-construction call site (and the legacy
        // 4-arg registration) keeps compiling: a null registry disables the start-time backstop (it becomes a no-op). The DI
        // registration path (AddCosmosOutboxRelay) always passes the shared registry so the guard is active in production.
        // logger is OPTIONAL for the same reason and because a null logger is a documented silent no-op for both sinks that
        // take it (RelayFailureNotifier and the #361 give-up log): observability may never be a construction prerequisite.
        internal StandaloneCosmosOutboxRelayHostedService(IServiceProvider serviceProvider,
                                                          IMessagingInfrastructureProvider infrastructureProvider,
                                                          IBodyConverterFactory bodyConverterFactory,
                                                          CosmosOutboxRelayOptions options,
                                                          StandaloneRelayProcessorRegistry processorRegistry = null,
                                                          ILogger<StandaloneCosmosOutboxRelayHostedService> logger = null)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _ = infrastructureProvider ?? throw new ArgumentNullException(nameof(infrastructureProvider));
            _ = bodyConverterFactory ?? throw new ArgumentNullException(nameof(bodyConverterFactory));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _processorRegistry = processorRegistry;
            _logger = logger;
            _failureNotifier = new RelayFailureNotifier(logger);

            // Build the relay with the options' validated drain knobs (OutboxDeliverySettings.FromOptions enforces the F2
            // invariants), including the caller's optional AdditionalPendingFilter, which the relay's ProcessChangeAsync
            // self-guard composes EXACTLY ONCE (the host pre-gate uses only the pure IsPendingOutbox identity guard — see
            // ProcessDocumentAsync — so the caller filter is never double-evaluated). The optional body resolver is NOT
            // resolved here: it is created PER PENDING (IsPendingOutbox) document from a fresh DI scope in HandleChangesAsync
            // (see ProcessDocumentAsync), so a scoped resolver never outlives the document it drains and the relay never
            // carries a resolver it might silently reuse across documents.
            // The settings instance is held only long enough to build the two things that read it, so the relay's stamps
            // and the give-up handler's election come from ONE give-up policy rather than two equal-valued copies.
            OutboxDeliverySettings deliverySettings = OutboxDeliverySettings.FromOptions(options);
            _relay = new CosmosOutboxRelay(
                infrastructureProvider,
                bodyConverterFactory,
                deliverySettings);
            _giveUpHandler = new OutboxGiveUpHandler(deliverySettings.PoisonPolicy, _relay, logger);

            ProcessorFactory = BuildChangeFeedProcessor;
        }

        // Test seam over the processor build, reusing the registry host's delegate type. The SDK's
        // ChangeFeedProcessorBuilder is SEALED (and its fluent methods are non-virtual), so the build chain itself is
        // unmockable, while the ChangeFeedProcessor it yields is public abstract with a public parameterless constructor
        // and IS mockable. The default is this host's OWN BuildChangeFeedProcessor - the real builder chain in its
        // original order - so the built processor's configuration is identical whether or not the seam is substituted;
        // nothing about the seam alters what production builds.
        internal CosmosOutboxRelayHostedService.RelayProcessorFactory ProcessorFactory { get; set; }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            string instanceName = $"{ProcessorNamePrefix}:{Environment.MachineName}:{Guid.NewGuid()}";

            CosmosOutboxRelayHostedService.RelayProcessorDescriptor descriptor = ResolveProcessorDescriptor();

            // Start-time backstop: fail fast if a ground-truth-defaulted sibling relay already resolved to this processor name
            // (one consumer group => a filtered-out document wedges). Declared relays are guarded at registration and skipped.
            RegisterStartTimeProcessorIdentity(descriptor);

            Container monitoredContainer = descriptor.MonitoredContainer;
            IReadOnlyList<string> partitionKeyPath = descriptor.PartitionKeyPath;

            // Start-time reconciliation of the monitored container's DECLARED configuration against its GROUND TRUTH: a
            // partition-key path that does not match makes every delivered stamp 404 AFTER a successful publish (#362) and a
            // purging default time-to-live deletes a still-pending document before the relay drains it (#363). Both are
            // silent at runtime, so they are rejected HERE — after the cheap in-memory collision backstop, so a collision
            // never pays for this metadata round-trip, and before any processor is built.
            await MonitoredContainerContract.VerifyAsync(monitoredContainer, partitionKeyPath, cancellationToken).ConfigureAwait(false);

            ChangeFeedProcessor processor = ProcessorFactory(
                descriptor,
                instanceName,
                (ChangeFeedProcessorContext context, Stream changes, CancellationToken changeCancellationToken)
                    => HandleChangesAsync(changes, monitoredContainer, partitionKeyPath, context.LeaseToken, changeCancellationToken));

            // INVARIANT: the host OWNS the processor BEFORE it awaits the start, so a start that throws still leaves the
            // in-flight processor referenced for cleanup rather than unreferenced with nothing able to stop it - the
            // generic host never calls StopAsync on a hosted service whose StartAsync threw.
            _processor = processor;

            try
            {
                await processor.StartAsync().ConfigureAwait(false);
            }
            catch (Exception startFailure)
            {
                // WHAT IS NOT GUARANTEED: stopping is BEST-EFFORT. The SDK throws when stopping a processor that never
                // finished starting, and that stop failure is logged at Error and swallowed so it can never mask the
                // start failure. A start that failed partway may also have acquired change-feed leases; a best-effort
                // stop NARROWS that window but cannot close it, so leases may remain partially acquired until they expire.
                Exception stopFailure = await StopTrackedProcessorAsync().ConfigureAwait(false);
                if (stopFailure is not null)
                {
                    _logger?.LogError(stopFailure, "The Standalone Cosmos Outbox Relay host could not stop its change-feed processor while cleaning up after a failed start; the cleanup failure is swallowed so the start failure stays the one the host reports.");
                }

                ExceptionDispatchInfo.Capture(startFailure).Throw();
            }
        }

        // The real SDK builder chain, and the ONLY implementation production uses. The configuration is exactly what it
        // was before the seam existed, in the SAME order: instance name, lease container, error notification, start time.
        private ChangeFeedProcessor BuildChangeFeedProcessor(CosmosOutboxRelayHostedService.RelayProcessorDescriptor descriptor,
                                                             string instanceName,
                                                             Container.ChangeFeedStreamHandler onChanges)
            => descriptor.MonitoredContainer
                .GetChangeFeedProcessorBuilder(descriptor.ProcessorName, onChanges)
                .WithInstanceName(instanceName)
                .WithLeaseContainer(descriptor.LeaseContainer)
                // The SDK's error-notification seam is the ONLY channel carrying a lease/processor fault TOGETHER with the
                // lease token it faulted under, so a stalled lease is reported rather than silent (#361).
                .WithErrorNotification(_failureNotifier.OnChangeFeedErrorAsync)
                // Start from the BEGINNING of the change feed (see CosmosOutboxRelayHostedService): the SDK default start
                // point is "now", which would skip any pending outbox documents written before the lease initializes.
                .WithStartTime(DateTime.MinValue.ToUniversalTime())
                .Build();

        // Shutdown goes through the SAME best-effort cleanup the start-failure path uses. The difference is what happens
        // to a collected stop failure: here it is SURFACED type-preserving (a shutdown that could not stop the processor
        // is the caller's business), whereas the start-failure path logs and swallows it so it cannot mask the start
        // failure. Either way the host then owns no processor, so a second shutdown is a no-op.
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            Exception stopFailure = await StopTrackedProcessorAsync().ConfigureAwait(false);

            if (stopFailure is not null)
            {
                ExceptionDispatchInfo.Capture(stopFailure).Throw();
            }
        }

        // Best-effort cleanup shared by the start-failure path and StopAsync: the tracked processor is stopped, the host
        // then owns none, and a stop failure is RETURNED (null when there was none) for the caller to surface or swallow.
        // It never throws itself - the SDK throws when stopping a processor that never finished starting, and that must
        // never become the failure the host reports.
        private async Task<Exception> StopTrackedProcessorAsync()
        {
            ChangeFeedProcessor processor = _processor;
            _processor = null;

            if (processor is null)
            {
                return null;
            }

            try
            {
                await processor.StopAsync().ConfigureAwait(false);
                return null;
            }
            catch (Exception stopFailure)
            {
                return stopFailure;
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

        // Start-time backstop for the silent consumer-group collision class: registers this host's RESOLVED processor name in
        // the shared registry so a second host that resolved to the SAME ground-truth source identity (resolved monitored+lease
        // endpoint/db/container) fails fast at start rather than silently forming one consumer group that wedges a
        // filtered-out document. Two construction-time conditions make it a no-op:
        //   - no shared registry was injected (legacy direct construction / null registry) — the backstop is disabled;
        //   - the options DECLARE a source identity (either side non-null) — declared relays are guarded at REGISTRATION, so
        //     re-registering the same declared name here would self-collide; skip them.
        // Only GROUND-TRUTH-defaulted hosts (both identities null), whose name is resolvable only after the containers are
        // resolved, are registered here. internal so the backstop is unit-testable without a live StartAsync.
        internal void RegisterStartTimeProcessorIdentity(CosmosOutboxRelayHostedService.RelayProcessorDescriptor descriptor)
        {
            if (_processorRegistry is null)
            {
                return;
            }

            if (_options.MonitoredSourceIdentity is not null || _options.LeaseSourceIdentity is not null)
            {
                return;
            }

            _processorRegistry.RegisterGroundTruthProcessorOrThrow(descriptor.ProcessorName);
        }

        // Parses the change-feed stream payload and feeds each document through the relay core, mirroring
        // CosmosOutboxRelayHostedService.HandleChangesAsync: FAIL CLOSED on an unexpected batch shape (throw so the SDK
        // does not checkpoint the batch) and let a publish failure propagate so the document re-surfaces next pass.
        // internal so the fail-closed behavior is unit-testable without the live SDK change-feed plumbing.
        internal async Task HandleChangesAsync(Stream changes, Container monitoredContainer, IReadOnlyList<string> partitionKeyPath, string leaseToken, CancellationToken cancellationToken)
        {
            using JsonDocument payload = await JsonDocument.ParseAsync(changes, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!payload.RootElement.TryGetProperty("Documents", out JsonElement documents) || documents.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    "The Cosmos change-feed batch payload did not contain a 'Documents' array. The relay cannot determine which documents to drain from this batch, so it fails closed (the batch is not checkpointed and re-surfaces) rather than silently advancing the lease past potentially-unpublished outbox documents.");
            }

            // Recorded ONCE per batch, mirroring CosmosOutboxRelayHostedService.HandleChangesAsync: BELOW the
            // fail-closed throw (a payload whose size is unknown records nothing rather than a fabricated one) and
            // ABOVE the per-document loop (a mid-batch publish failure still reports the batch that was attempted).
            // An EMPTY batch is recorded too: the count measures LEASE PROGRESS, so dropping it would make an idle
            // partition indistinguishable from a stalled one.
            // INVARIANT: ADR-0010 R1 - the document count is read INSIDE this module's own off-guard, because C#
            // evaluates arguments before the callee's guard runs.
            if (CosmosReliabilityDiagnostics.IsEnabled)
            {
                CosmosReliabilityDiagnostics.RecordDrainedBatch(leaseToken, documents.GetArrayLength());
            }

            foreach (JsonElement document in documents.EnumerateArray())
            {
                await ProcessDocumentAsync(document, monitoredContainer, partitionKeyPath, leaseToken, cancellationToken).ConfigureAwait(false);
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
        // the scoped dependencies it pulls) never outlives the document it drains. The scope is opened as an ASYNC scope
        // (`await using AsyncServiceScope` / CreateAsyncScope) so a scoped resolver or dependency that implements only
        // IAsyncDisposable is disposed via DisposeAsync rather than throwing on synchronous Dispose: a sync-dispose throw
        // after ProcessChangeAsync has already published+stamped would escape the change-feed callback and re-deliver an
        // already-delivered document. The `await using` scope WRAPS the ProcessChangeAsync call so a propagating
        // publish/resolver failure (at-least-once: the SDK does not checkpoint and the document re-surfaces) still disposes
        // the scope while the exception unwinds. With no factory configured the relay's no-resolver verbatim reconstruction
        // path is used UNCHANGED and no scope is opened.
        // A drain failure is handed to the shared OutboxGiveUpHandler, which counts per document IDENTITY — the id AND
        // the partition the document lives in — so two documents sharing a MessageId in different partitions never share
        // a counter slot. BELOW the cap governing the streak the failure PROPAGATES unchanged — fail-closed is the correct
        // answer to a TRANSIENT failure, and the document re-surfaces next pass. AT the cap the document is stamped with
        // the matching non-pending status, counted, logged at Error, and the loop CONTINUES to the next document so the
        // batch checkpoints and the head-of-line block on that lease clears. The give-up stamp's OWN failure is never
        // swallowed — a misconfigured partition-key path (#362) makes it fail exactly as the delivered stamp would, and
        // that must surface rather than be laundered into "give up on everything".
        private async Task ProcessDocumentAsync(JsonElement document, Container monitoredContainer, IReadOnlyList<string> partitionKeyPath, string leaseToken, CancellationToken cancellationToken)
        {
            if (!CosmosOutboxDocument.IsPendingOutbox(document))
            {
                // Counted HERE and only here: a document the pre-gate rejects never reaches the relay's own admission
                // gate, and one that clears the pre-gate is counted there instead — so each drained document is
                // counted exactly once across the two layers.
                // INVARIANT: ADR-0010 R1 - the outcome value is resolved INSIDE this module's own off-guard, because
                // C# evaluates arguments before the callee's guard runs.
                if (CosmosReliabilityDiagnostics.IsEnabled)
                {
                    CosmosReliabilityDiagnostics.RecordDrainedDocument(CosmosReliabilityDiagnostics.DrainOutcomes.Skipped);
                }

                return;
            }

            // The attempt is owned HERE, OUTSIDE the drain's own DI scope, so the PHASE survives both a relay that THROWS
            // and a scope disposal that throws: a scope-disposal failure AFTER a publish that returned is still a
            // POST-publish failure.
            var attempt = new OutboxDrainAttempt();

            try
            {
                await DrainDocumentAsync(document, monitoredContainer, partitionKeyPath, attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception drainFailure)
            {
                // CANCELLATION IS DECIDED FROM THE TOKEN, never from the exception's type: a host stop wrapped in an
                // AggregateException is still a host stop, and a resolver's spurious OperationCanceledException raised
                // while nothing was cancelled is still a deterministic defect. Counting a host stop would give up on a
                // perfectly deliverable document, so it propagates without advancing the policy.
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                if (!await _giveUpHandler.TryGiveUpAsync(document, monitoredContainer, partitionKeyPath, leaseToken, attempt.MessagePublished, drainFailure, cancellationToken).ConfigureAwait(false))
                {
                    throw;
                }

                return;
            }

            _giveUpHandler.RecordSuccessfulDrain(document, partitionKeyPath);
        }

        // The drain itself, unchanged: the no-resolver verbatim reconstruction path when no body-resolver factory is
        // configured, else a FRESH per-document async DI scope the resolver is resolved from.
        private async Task DrainDocumentAsync(JsonElement document, Container monitoredContainer, IReadOnlyList<string> partitionKeyPath, OutboxDrainAttempt attempt, CancellationToken cancellationToken)
        {
            if (_options.BodyResolverFactory is null)
            {
                await _relay.ProcessChangeAsync(document, monitoredContainer, partitionKeyPath, resolver: null, attempt, cancellationToken).ConfigureAwait(false);
                return;
            }

            await using AsyncServiceScope scope = _serviceProvider.CreateAsyncScope();
            // A configured factory MUST resolve a non-null resolver. A null return (e.g. GetService against a missing
            // registration) would otherwise be forwarded as the "no resolver" sentinel into ProcessChangeAsync, silently
            // selecting verbatim reconstruction — republishing the persisted body instead of the intended drain-time body.
            // Fail fast so the misconfiguration surfaces instead of being masked as the no-resolver path.
            IOutboxBodyResolver resolver = _options.BodyResolverFactory(scope.ServiceProvider)
                ?? throw new InvalidOperationException(
                    "The configured CosmosOutboxRelayOptions.BodyResolverFactory returned null. A configured factory must resolve a non-null IOutboxBodyResolver — null would silently select the verbatim no-resolver reconstruction path. Resolve the resolver with GetRequiredService/GetRequiredKeyedService (which throw on a missing registration) rather than GetService.");
            await _relay.ProcessChangeAsync(document, monitoredContainer, partitionKeyPath, resolver, attempt, cancellationToken).ConfigureAwait(false);
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
