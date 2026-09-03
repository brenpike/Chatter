using Chatter.MessageBrokers.Reliability.Cosmos.Diagnostics;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// Hosts the #222 document-tier change-feed outbox relay: one Cosmos <see cref="ChangeFeedProcessor"/> per distinct
    /// change-feed SOURCE IDENTITY drawn from the <see cref="DocumentReliabilityRegistry"/> — NOT one per command type
    /// (many command types may share one source, ADR-0008). Each processor monitors its container's change feed and feeds
    /// every change through the testable <see cref="CosmosOutboxRelay"/> core, which filters to outbox+pending documents,
    /// publishes them, and stamps delivered+TTL.
    /// </summary>
    /// <remarks>
    /// The SDK plumbing is kept deliberately thin (coverage targets <see cref="CosmosOutboxRelay"/>): this host derives
    /// the monitored + lease containers via <see cref="CosmosContainerFactory"/>, wires the change-feed STREAM handler so
    /// Chatter parses each change with System.Text.Json (no Cosmos-SDK Newtonsoft deserialization of the relay's reads),
    /// and starts/stops the processors. The change-feed handler does NOT swallow a publish failure: an exception thrown
    /// by the relay propagates out of the handler so the SDK does NOT checkpoint the batch — the unpublished document
    /// re-surfaces on the next pass (at-least-once) rather than the lease advancing past it.
    /// <para>
    /// INVARIANT: the fan-out dedup key is DECLARED-OR-GROUND-TRUTH, never INFERRED from an untrusted handle (#222), AND it
    /// is a TYPED, COMPONENT-WISE-EQUALITY key (<see cref="RelaySourceIdentityKey"/>) — never a flattened string. The
    /// eliminated class is twofold: (1) the inferred-key split/collapse (prior keys read SOME-BUT-NOT-ALL identity
    /// dimensions off the resolved handle, so a missing dimension could SPLIT one logical source into two processors or
    /// COLLAPSE two distinct sources into one); and (2) the SERIALIZATION-collision collapse — a flat delimiter-joined
    /// string let a delimiter byte INSIDE one component bleed across a component boundary, so distinct component tuples
    /// (e.g. monitored="a\0b",lease="c" vs monitored="a",lease="b\0c") flattened to the same string and silently collapsed.
    /// The key now compares each component SEPARATELY (ordinal); there is no boundary byte to abuse because no boundary
    /// exists. The key is sourced two ways, each cannot diverge from the thing it dedupes:
    /// <list type="bullet">
    /// <item>
    /// ADVANCED PATH (registration carries a <see cref="DocumentReliabilityRegistration.DeclaredSourceIdentity"/>): the
    /// caller controls the resolved handle, so the relay does NOT read the handle's account endpoint or names — it keys on
    /// the caller-DECLARED <c>(monitored, lease)</c> identity (<see cref="RelaySourceKind.Declared"/>). Same declared
    /// identity ⇒ one processor; distinct declared identities ⇒ distinct processors, even when the resolved handles look
    /// identical.
    /// </item>
    /// <item>
    /// PLAIN PATH (declared identity is <c>null</c>): the handle is provider-derived from the app-registered
    /// <see cref="CosmosClient"/>, so it is GROUND TRUTH (<see cref="RelaySourceKind.GroundTruth"/>). The key is the
    /// COMPLETE resolved identity — account ENDPOINT plus database id plus container id, for BOTH monitored and lease.
    /// Adding the account endpoint to the former four-tuple closes the cross-account collapse: identically-named containers
    /// in DIFFERENT accounts no longer share a key.
    /// </item>
    /// </list>
    /// Because the key is declared-or-ground-truth AND component-wise-typed, no missing/misreported dimension and no
    /// representable byte inside any single component can split or collapse it.
    /// </para>
    /// <para>
    /// <c>processorName</c> is STABLE per source-identity key so every application instance sharing a source cooperates on
    /// the same logical processor and two distinct sources never share a <c>processorName</c>. It is derived INJECTIVELY
    /// from the typed key: a canonical LENGTH-PREFIXED encoding (a discriminator tag, then each component IN FIXED ORDER as
    /// its UTF-8 byte length followed by the UTF-8 bytes — length-prefixing is injective, so distinct component tuples
    /// produce distinct byte streams with no in-band delimiter to spoof) is SHA-256 hashed and base64url-encoded, prefixed
    /// with a constant. The hash is over the DETERMINISTIC canonical bytes (not per-process-randomized GetHashCode), so the
    /// name is stable across runs/instances. The dedup HashSet keys on the TYPED key directly (not the digest), so even a
    /// hypothetical SHA-256 collision could only make two processorNames coincide (a low-stakes cooperation hint), never
    /// cause a dedup collapse. <c>instanceName</c> is UNIQUE per host (machine + a GUID) so co-located instances do not
    /// collide on a lease.
    /// </para>
    /// </remarks>
    internal sealed class CosmosOutboxRelayHostedService : IHostedService
    {
        // Stable processorName prefix; combined with an injective SHA-256/base64url digest of the typed source-identity
        // key's canonical length-prefixed encoding (declared on the advanced path, ground-truth-derived on the plain path)
        // it yields a deterministic processor identity shared across all app instances draining the same source.
        private const string ProcessorNamePrefix = "chatter-cosmos-outbox-relay";

        private readonly DocumentReliabilityRegistry _registry;
        private readonly CosmosContainerFactory _containerFactory;
        private readonly CosmosOutboxRelay _relay;
        private readonly RelayFailureNotifier _failureNotifier;
        private readonly OutboxGiveUpHandler _giveUpHandler;
        private readonly ILogger _logger;
        private readonly List<ChangeFeedProcessor> _processors = new List<ChangeFeedProcessor>();

        // logger is OPTIONAL (defaults null) so every existing direct-construction call site keeps compiling: a null
        // logger leaves the failure notifier's opt-in metric intact and its always-on log a silent no-op, and makes the
        // start-failure cleanup's stop-failure log a silent no-op too. The DI registration
        // (AddSingleton<IHostedService, CosmosOutboxRelayHostedService>) is on the CONCRETE type, so a host with logging
        // configured gets the logger injected and one without still constructs.
        public CosmosOutboxRelayHostedService(DocumentReliabilityRegistry registry,
                                              CosmosContainerFactory containerFactory,
                                              IMessagingInfrastructureProvider infrastructureProvider,
                                              IBodyConverterFactory bodyConverterFactory,
                                              ILogger<CosmosOutboxRelayHostedService> logger = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _containerFactory = containerFactory ?? throw new ArgumentNullException(nameof(containerFactory));
            _ = infrastructureProvider ?? throw new ArgumentNullException(nameof(infrastructureProvider));
            _ = bodyConverterFactory ?? throw new ArgumentNullException(nameof(bodyConverterFactory));
            // This host carries no options object, so it drains on OutboxDeliverySettings.Legacy's knobs — which now carry
            // the ALWAYS-ON published-but-unconfirmed brake. The settings instance is held so the relay's stamps and the
            // give-up handler's election read ONE give-up policy rather than two equal-valued copies.
            OutboxDeliverySettings deliverySettings = OutboxDeliverySettings.Legacy;
            _relay = new CosmosOutboxRelay(infrastructureProvider, bodyConverterFactory, deliverySettings);
            _giveUpHandler = new OutboxGiveUpHandler(deliverySettings.PoisonPolicy, _relay, logger);
            _failureNotifier = new RelayFailureNotifier(logger);
            _logger = logger;
            ProcessorFactory = BuildChangeFeedProcessor;
        }

        // Test seam over the processor build. The SDK's ChangeFeedProcessorBuilder is SEALED (and its fluent methods are
        // non-virtual), so the build chain itself is unmockable, while the ChangeFeedProcessor it yields is public
        // abstract with a public parameterless constructor and IS mockable. The default is BuildChangeFeedProcessor —
        // the real builder chain — so the built processor's configuration is identical whether or not the seam is
        // substituted; nothing about the seam alters what production builds.
        internal delegate ChangeFeedProcessor RelayProcessorFactory(RelayProcessorDescriptor descriptor,
                                                                    string instanceName,
                                                                    Container.ChangeFeedStreamHandler onChanges);

        internal RelayProcessorFactory ProcessorFactory { get; set; }

        // The processors this host currently OWNS and is therefore responsible for stopping. internal (not private) so
        // the start-failure cleanup's ownership guarantee is unit-testable; the assembly exposes internals to the test
        // project.
        internal IReadOnlyList<ChangeFeedProcessor> TrackedProcessors => _processors;

        // TWO PASSES, and the split is LOAD-BEARING. Pass 1 verifies EVERY monitored container against its ground truth;
        // only then does pass 2 build and start the processors, so a container that fails verification never has a
        // processor built for it and #362/#363 get the fast startup error they ask for. A verification failure
        // deliberately takes the host down: it is a hosted-service throw at start, matching this module's existing
        // start-time guard precedent, and there is no opt-out.
        // Pass 2 owns its OWN failure mode, because the pass split does not cover it: each processor is TRACKED BEFORE
        // its start is awaited, so a start that throws still leaves the in-flight processor referenced, and the catch
        // stops EVERY tracked processor and clears the tracking list before rethrowing the ORIGINAL failure unchanged.
        // That matters because the generic host never calls StopAsync on a hosted service whose StartAsync threw — the
        // already-started processors would otherwise run for the lifetime of the process with nothing holding them.
        // WHAT IS NOT GUARANTEED: stopping is BEST-EFFORT. The SDK throws when stopping a processor that never finished
        // starting, and those stop failures are logged at Error and swallowed so they can never mask the start failure.
        // A start that failed partway may also have acquired change-feed leases; a best-effort stop NARROWS that window
        // but cannot close it, so leases may remain partially acquired until they expire.
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<RelayProcessorDescriptor> descriptors = DistinctResolvedProcessorDescriptors();

            await VerifyMonitoredContainersAsync(descriptors, cancellationToken).ConfigureAwait(false);

            string instanceName = $"{ProcessorNamePrefix}:{Environment.MachineName}:{Guid.NewGuid()}";

            try
            {
                foreach (RelayProcessorDescriptor descriptor in descriptors)
                {
                    Container monitoredContainer = descriptor.MonitoredContainer;
                    IReadOnlyList<string> partitionKeyPath = descriptor.PartitionKeyPath;

                    ChangeFeedProcessor processor = ProcessorFactory(
                        descriptor,
                        instanceName,
                        (ChangeFeedProcessorContext context, Stream changes, CancellationToken changeCancellationToken)
                            => HandleChangesAsync(changes, monitoredContainer, partitionKeyPath, context.LeaseToken, changeCancellationToken));

                    // INVARIANT: the host OWNS the processor BEFORE it awaits the start, so a start that throws still
                    // leaves the in-flight processor tracked for cleanup rather than unreferenced.
                    _processors.Add(processor);
                    await processor.StartAsync().ConfigureAwait(false);
                }
            }
            catch (Exception startFailure)
            {
                foreach (Exception stopFailure in await StopTrackedProcessorsAsync().ConfigureAwait(false))
                {
                    _logger?.LogError(stopFailure, "The Cosmos Outbox Relay host could not stop a change-feed processor while cleaning up after a failed start; the cleanup failure is swallowed so the start failure stays the one the host reports.");
                }

                ExceptionDispatchInfo.Capture(startFailure).Throw();
            }
        }

        // The real SDK builder chain, and the ONLY implementation production uses. The configuration is exactly what it
        // was before the seam existed: instance name, lease container, start time, error notification.
        private ChangeFeedProcessor BuildChangeFeedProcessor(RelayProcessorDescriptor descriptor,
                                                             string instanceName,
                                                             Container.ChangeFeedStreamHandler onChanges)
            => descriptor.MonitoredContainer
                .GetChangeFeedProcessorBuilder(descriptor.ProcessorName, onChanges)
                .WithInstanceName(instanceName)
                .WithLeaseContainer(descriptor.LeaseContainer)
                // Start from the BEGINNING of the change feed, not the SDK default (current time). On a
                // first start or recreated/empty lease container the default start point is "now", so any
                // `pending` outbox documents already written before the hosted service initializes its leases
                // would be skipped forever — no later change is guaranteed to re-touch them — silently losing
                // those messages. DateTime.MinValue (UTC) anchors the feed at the start so the relay drains the
                // existing outbox backlog on first start. Already-delivered docs are stamped delivered+TTL and
                // re-published-then-deduped downstream (#220 inbox marker), so replaying from the start is safe
                // for this at-least-once relay.
                .WithStartTime(DateTime.MinValue.ToUniversalTime())
                // The SDK's error-notification seam is the ONLY channel carrying a lease/processor fault TOGETHER
                // with the lease token it faulted under; the relay core never sees the lease token, so without this
                // a wedged lease (a document that re-throws on every pass) is completely silent (#361).
                .WithErrorNotification(_failureNotifier.OnChangeFeedErrorAsync)
                .Build();

        // Pass 1 of the two-pass start: reconcile EVERY monitored container's declared configuration against its ground
        // truth before any processor is built. internal (not private) so the verify-all-before-start ordering stays
        // unit-testable, matching DistinctResolvedProcessorDescriptors(); the assembly exposes internals to the test
        // project.
        internal static async Task VerifyMonitoredContainersAsync(IReadOnlyList<RelayProcessorDescriptor> descriptors, CancellationToken cancellationToken)
        {
            foreach (RelayProcessorDescriptor descriptor in descriptors)
            {
                await MonitoredContainerContract.VerifyAsync(descriptor.MonitoredContainer, descriptor.PartitionKeyPath, cancellationToken).ConfigureAwait(false);
            }
        }

        // Shutdown goes through the SAME best-effort cleanup the start-failure path uses, so one processor that refuses
        // to stop never leaves the rest running. The difference is what happens to the collected failures: here they are
        // SURFACED (a shutdown that could not stop a processor is the caller's business), whereas the start-failure path
        // swallows them so they cannot mask the start failure. A lone failure is rethrown TYPE-PRESERVING; several are
        // aggregated so none is lost.
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<Exception> stopFailures = await StopTrackedProcessorsAsync().ConfigureAwait(false);

            if (stopFailures.Count == 1)
            {
                ExceptionDispatchInfo.Capture(stopFailures[0]).Throw();
            }

            if (stopFailures.Count > 1)
            {
                throw new AggregateException("The Cosmos Outbox Relay host could not stop every change-feed processor.", stopFailures);
            }
        }

        // Best-effort cleanup shared by the start-failure path and StopAsync: EVERY tracked processor is stopped even
        // when an earlier stop throws, the host then owns none, and the stop failures are RETURNED for the caller to
        // surface or swallow. It never throws itself — the SDK throws when stopping a processor that never finished
        // starting, and that must never become the failure the host reports.
        private async Task<IReadOnlyList<Exception>> StopTrackedProcessorsAsync()
        {
            var stopFailures = new List<Exception>();

            foreach (ChangeFeedProcessor processor in _processors)
            {
                try
                {
                    await processor.StopAsync().ConfigureAwait(false);
                }
                catch (Exception stopFailure)
                {
                    stopFailures.Add(stopFailure);
                }
            }

            _processors.Clear();
            return stopFailures;
        }

        // Parses the change-feed stream payload ({ "Documents": [ ... ] }) with System.Text.Json so Chatter owns the read
        // wire shape, and feeds each document through the relay core. An exception from the relay (a publish failure)
        // propagates out of this handler so the SDK does NOT checkpoint the batch and the document re-surfaces next pass.
        // internal (not private) so the fail-closed malformed-payload behavior is unit-testable without the live SDK
        // change-feed plumbing; the assembly exposes internals to the test project.
        internal async Task HandleChangesAsync(Stream changes, Container monitoredContainer, IReadOnlyList<string> partitionKeyPath, string leaseToken, CancellationToken cancellationToken)
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

            // Recorded ONCE per batch, BELOW the fail-closed throw (a payload whose size is unknown records nothing
            // rather than a fabricated one) and ABOVE the per-document loop (a mid-batch publish failure still
            // reports the batch that was attempted). An EMPTY batch is recorded too: the count measures LEASE
            // PROGRESS, so dropping it would make an idle partition indistinguishable from a stalled one.
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

        // Drains a single change-feed document through the relay and bounds the cost of one that fails on every pass. The
        // POISON arm is unavailable here — this host carries no options object, so its threshold is Legacy's zero — which
        // means a PRE-publish failure propagates forever, exactly as it always has. The POST-publish arm has no off
        // switch, so a document whose message reached the broker but whose delivered stamp keeps failing stops being
        // re-published after Legacy's cap: it is stamped published-unconfirmed, counted, logged at Error, and the loop
        // CONTINUES. Without that brake this host would publish, pay request units, and re-run downstream consumers on
        // every pass, forever.
        private async Task ProcessDocumentAsync(JsonElement document, Container monitoredContainer, IReadOnlyList<string> partitionKeyPath, string leaseToken, CancellationToken cancellationToken)
        {
            // The attempt is owned HERE, outside the relay, so the PHASE survives a relay that THROWS.
            var attempt = new OutboxDrainAttempt();

            try
            {
                await _relay.ProcessChangeAsync(document, monitoredContainer, partitionKeyPath, resolver: null, attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception drainFailure)
            {
                // CANCELLATION IS DECIDED FROM THE TOKEN, never from the exception's type: a host stop wrapped in an
                // AggregateException is still a host stop, and a spurious OperationCanceledException raised while nothing
                // was cancelled is still a deterministic defect.
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

        // One descriptor per distinct change-feed SOURCE-IDENTITY key (ADR-0008). The key is a TYPED, COMPONENT-WISE
        // RelaySourceIdentityKey that is DECLARED-OR-GROUND-TRUTH, never INFERRED from an untrusted handle (#222), and
        // never flattened to a delimiter-joined string:
        //   - ADVANCED PATH (registration carries a DeclaredSourceIdentity): the caller controls the resolved handle, so
        //     the relay does NOT read .Endpoint / ids off it — it keys on the caller-declared (monitored, lease) identity.
        //   - PLAIN PATH (declared identity null): the handle is provider-derived from the app CosmosClient (ground
        //     truth), so the key carries the COMPLETE resolved identity: account endpoint + database id + container id, for
        //     BOTH monitored and lease. The account endpoint is added to the former four-tuple so identically-named
        //     containers in DIFFERENT accounts no longer collapse.
        // Each registration is resolved first (monitored + lease handles via the factory), then deduped on its TYPED key
        // (component-wise value equality, ordinal strings — a delimiter byte inside one component cannot bleed across a
        // component boundary because no boundary byte exists). The first registration seen for a key wins and supplies the
        // resolved handles + partition-key path; subsequent registrations resolving to the same key are skipped (no
        // duplicate processor on the same source). processorName is derived INJECTIVELY from the SAME typed key so all app
        // instances sharing a source cooperate on one logical processor and two distinct sources never share a
        // processorName. internal (not private) so the resolve-then-dedup is unit-testable without the live SDK change-feed
        // plumbing; the assembly exposes internals to the test project.
        internal IReadOnlyList<RelayProcessorDescriptor> DistinctResolvedProcessorDescriptors()
        {
            var descriptors = new List<RelayProcessorDescriptor>();
            var seen = new HashSet<RelaySourceIdentityKey>();

            foreach (DocumentReliabilityRegistration registration in _registry.Registrations)
            {
                Container monitoredContainer = _containerFactory.GetDocumentContainer(registration);
                Container leaseContainer = _containerFactory.GetLeaseContainer(registration);

                RelaySourceIdentityKey sourceIdentityKey = BuildSourceIdentityKey(registration, monitoredContainer, leaseContainer);
                if (!seen.Add(sourceIdentityKey))
                {
                    continue;
                }

                descriptors.Add(new RelayProcessorDescriptor(
                    BuildProcessorName(sourceIdentityKey),
                    monitoredContainer,
                    leaseContainer,
                    registration.PartitionKeyPath));
            }

            return descriptors;
        }

        // Builds the COMPLETE, TYPED source-identity dedup key. On the advanced path the relay MUST NOT read .Endpoint or
        // ids off the (untrusted, caller-controlled) handle — it uses the caller-declared identity. On the plain path the
        // handle is provider-derived ground truth, so the key carries account endpoint + database id + container id for both
        // monitored and lease. The endpoint Uri is normalized to AbsoluteUri (ordinal) so a trailing-slash or host-case
        // difference does not spuriously split one account into two keys. The key's equality is component-wise (ordinal);
        // components are NEVER concatenated for the equality decision.
        private static RelaySourceIdentityKey BuildSourceIdentityKey(DocumentReliabilityRegistration registration,
                                                                     Container monitoredContainer,
                                                                     Container leaseContainer)
        {
            if (registration.DeclaredSourceIdentity is CosmosSourceIdentity declared)
            {
                return RelaySourceIdentityKey.ForDeclared(declared.Monitored, declared.Lease);
            }

            return RelaySourceIdentityKey.ForGroundTruth(
                NormalizeEndpoint(monitoredContainer.Database.Client.Endpoint),
                monitoredContainer.Database.Id,
                monitoredContainer.Id,
                NormalizeEndpoint(leaseContainer.Database.Client.Endpoint),
                leaseContainer.Database.Id,
                leaseContainer.Id);
        }

        // Derives a STABLE, injective processorName string from the typed key. The key's components are emitted in FIXED
        // order as a length-prefixed UTF-8 byte stream (discriminator tag, then per component: Int32 byte length + UTF-8
        // bytes). Length-prefixing is injective — distinct component tuples produce distinct byte streams, with no in-band
        // delimiter a component value could spoof. The canonical bytes are SHA-256 hashed and base64url-encoded (URL- and
        // identifier-safe), so the name is deterministic across runs/instances (unlike per-process-randomized GetHashCode).
        // internal (not private) so the standalone relay host (StandaloneCosmosOutboxRelayHostedService) reuses the SAME
        // injective source-identity-to-name derivation rather than re-implementing the #222-sensitive name machinery; the
        // assembly exposes internals to the standalone host (same assembly) and the test project. No behavior change.
        internal static string BuildProcessorName(RelaySourceIdentityKey key)
        {
            byte[] canonical = key.ToCanonicalBytes();
            byte[] digest = SHA256.HashData(canonical);
            string base64url = Convert.ToBase64String(digest)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
            return $"{ProcessorNamePrefix}:{base64url}";
        }

        // internal (not private) so the standalone relay host reuses the SAME endpoint normalization when building a
        // ground-truth source-identity key. No behavior change.
        internal static string NormalizeEndpoint(Uri endpoint) => endpoint?.AbsoluteUri ?? string.Empty;

        // Path discriminator for the typed source-identity key. Replaces the former "declared" / "ground-truth" string
        // prefix that was concatenated into the flat key.
        internal enum RelaySourceKind
        {
            Declared,
            GroundTruth
        }

        // The TYPED, COMPONENT-WISE source-identity dedup key (#222). Equality is value-based and per-component (ordinal
        // for every string via the readonly record struct's compiler-generated equality) — components are NEVER flattened
        // into one delimiter-joined string for the equality decision, so a delimiter (or any) byte inside one component
        // cannot bleed across a component boundary and collapse two distinct sources. Unused components for the active path
        // are empty string. ToCanonicalBytes() produces the deterministic length-prefixed encoding used to derive a stable,
        // injective processorName (the dedup HashSet itself keys on this typed value, never on the digest).
        internal readonly record struct RelaySourceIdentityKey
        {
            private RelaySourceIdentityKey(RelaySourceKind kind,
                                           string monitored,
                                           string lease,
                                           string monitoredEndpoint,
                                           string monitoredDb,
                                           string monitoredContainer,
                                           string leaseEndpoint,
                                           string leaseDb,
                                           string leaseContainer)
            {
                Kind = kind;
                Monitored = monitored;
                Lease = lease;
                MonitoredEndpoint = monitoredEndpoint;
                MonitoredDb = monitoredDb;
                MonitoredContainer = monitoredContainer;
                LeaseEndpoint = leaseEndpoint;
                LeaseDb = leaseDb;
                LeaseContainer = leaseContainer;
            }

            public RelaySourceKind Kind { get; }

            // Declared-path components (opaque caller-declared tokens); empty on the ground-truth path.
            public string Monitored { get; }
            public string Lease { get; }

            // Ground-truth-path components (resolved account endpoint + database id + container id, monitored and lease);
            // empty on the declared path.
            public string MonitoredEndpoint { get; }
            public string MonitoredDb { get; }
            public string MonitoredContainer { get; }
            public string LeaseEndpoint { get; }
            public string LeaseDb { get; }
            public string LeaseContainer { get; }

            public static RelaySourceIdentityKey ForDeclared(string monitored, string lease)
                => new RelaySourceIdentityKey(
                    RelaySourceKind.Declared,
                    monitored ?? string.Empty,
                    lease ?? string.Empty,
                    string.Empty, string.Empty, string.Empty,
                    string.Empty, string.Empty, string.Empty);

            public static RelaySourceIdentityKey ForGroundTruth(string monitoredEndpoint,
                                                                string monitoredDb,
                                                                string monitoredContainer,
                                                                string leaseEndpoint,
                                                                string leaseDb,
                                                                string leaseContainer)
                => new RelaySourceIdentityKey(
                    RelaySourceKind.GroundTruth,
                    string.Empty, string.Empty,
                    monitoredEndpoint ?? string.Empty,
                    monitoredDb ?? string.Empty,
                    monitoredContainer ?? string.Empty,
                    leaseEndpoint ?? string.Empty,
                    leaseDb ?? string.Empty,
                    leaseContainer ?? string.Empty);

            // Deterministic, INJECTIVE canonical encoding: a discriminator tag byte, then every component IN FIXED ORDER as
            // its UTF-8 byte length (Int32) followed by the UTF-8 bytes. Length-prefixing is injective, so distinct
            // component tuples produce distinct byte streams and there is no in-band delimiter a component value could spoof.
            public byte[] ToCanonicalBytes()
            {
                using var buffer = new MemoryStream();
                buffer.WriteByte((byte)Kind);
                WriteComponent(buffer, Monitored);
                WriteComponent(buffer, Lease);
                WriteComponent(buffer, MonitoredEndpoint);
                WriteComponent(buffer, MonitoredDb);
                WriteComponent(buffer, MonitoredContainer);
                WriteComponent(buffer, LeaseEndpoint);
                WriteComponent(buffer, LeaseDb);
                WriteComponent(buffer, LeaseContainer);
                return buffer.ToArray();
            }

            private static void WriteComponent(Stream destination, string component)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(component ?? string.Empty);
                Span<byte> length = stackalloc byte[sizeof(int)];
                BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
                destination.Write(length);
                destination.Write(bytes, 0, bytes.Length);
            }
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
