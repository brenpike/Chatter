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
    /// INVARIANT: the fan-out dedup key is DECLARED-OR-GROUND-TRUTH, never INFERRED from an untrusted handle (#222). The
    /// eliminated class is the inferred-key split/collapse: prior keys read SOME-BUT-NOT-ALL identity dimensions off the
    /// resolved handle, so a missing dimension could SPLIT one logical source into two processors (double-publish) or two
    /// distinct sources could COLLAPSE into one (silent non-publish of the skipped source). The key is now sourced two
    /// ways, each cannot diverge from the thing it dedupes:
    /// <list type="bullet">
    /// <item>
    /// ADVANCED PATH (registration carries a <see cref="DocumentReliabilityRegistration.DeclaredSourceIdentity"/>): the
    /// caller controls the resolved handle, so the relay does NOT read the handle's account endpoint or names — it keys on
    /// the caller-DECLARED <c>(monitored, lease)</c> identity. Same declared identity ⇒ one processor; distinct declared
    /// identities ⇒ distinct processors, even when the resolved handles look identical.
    /// </item>
    /// <item>
    /// PLAIN PATH (declared identity is <c>null</c>): the handle is provider-derived from the app-registered
    /// <see cref="CosmosClient"/>, so it is GROUND TRUTH. The key is the COMPLETE resolved identity — account ENDPOINT
    /// plus database id plus container id, for BOTH monitored and lease. Adding the account endpoint to the former
    /// four-tuple closes the cross-account collapse: identically-named containers in DIFFERENT accounts no longer share a
    /// key.
    /// </item>
    /// </list>
    /// Because the key is declared-or-ground-truth, no missing or misreported dimension can split or collapse it.
    /// </para>
    /// <para>
    /// <c>processorName</c> is STABLE per source-identity key (a constant prefix + the complete key) so every application
    /// instance sharing a source cooperates on the same logical processor and two distinct sources never share a
    /// <c>processorName</c>; <c>instanceName</c> is UNIQUE per host (machine + a GUID) so co-located instances do not
    /// collide on a lease.
    /// </para>
    /// </remarks>
    internal sealed class CosmosOutboxRelayHostedService : IHostedService
    {
        // Stable processorName prefix; combined with the COMPLETE source-identity key (declared on the advanced path,
        // ground-truth-derived on the plain path) it yields a deterministic processor identity shared across all app
        // instances draining the same source.
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

        // One descriptor per distinct change-feed SOURCE-IDENTITY key (ADR-0008). The key is DECLARED-OR-GROUND-TRUTH,
        // never INFERRED from an untrusted handle (#222):
        //   - ADVANCED PATH (registration carries a DeclaredSourceIdentity): the caller controls the resolved handle, so
        //     the relay does NOT read .Endpoint / ids off it — it keys on the caller-declared (monitored, lease) identity.
        //   - PLAIN PATH (declared identity null): the handle is provider-derived from the app CosmosClient (ground
        //     truth), so the key is the COMPLETE resolved identity: account endpoint + database id + container id, for
        //     BOTH monitored and lease. The account endpoint is added to the former four-tuple so identically-named
        //     containers in DIFFERENT accounts no longer collapse.
        // Each registration is resolved first (monitored + lease handles via the factory), then deduped on its key. The
        // first registration seen for a key wins and supplies the resolved handles + partition-key path; subsequent
        // registrations resolving to the same key are skipped (no duplicate processor on the same source). processorName
        // is derived from the SAME complete key so all app instances sharing a source cooperate on one logical processor
        // and two distinct sources never share a processorName. internal (not private) so the resolve-then-dedup is
        // unit-testable without the live SDK change-feed plumbing; the assembly exposes internals to the test project.
        internal IReadOnlyList<RelayProcessorDescriptor> DistinctResolvedProcessorDescriptors()
        {
            var descriptors = new List<RelayProcessorDescriptor>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (DocumentReliabilityRegistration registration in _registry.Registrations)
            {
                Container monitoredContainer = _containerFactory.GetDocumentContainer(registration);
                Container leaseContainer = _containerFactory.GetLeaseContainer(registration);

                string sourceIdentityKey = BuildSourceIdentityKey(registration, monitoredContainer, leaseContainer);
                if (!seen.Add(sourceIdentityKey))
                {
                    continue;
                }

                descriptors.Add(new RelayProcessorDescriptor(
                    $"{ProcessorNamePrefix}:{sourceIdentityKey}",
                    monitoredContainer,
                    leaseContainer,
                    registration.PartitionKeyPath));
            }

            return descriptors;
        }

        // Builds the COMPLETE source-identity dedup key. On the advanced path the relay MUST NOT read .Endpoint or ids off
        // the (untrusted, caller-controlled) handle — it uses the caller-declared identity. On the plain path the handle is
        // provider-derived ground truth, so the key is account endpoint + database id + container id for both monitored and
        // lease. The endpoint Uri is normalized to AbsoluteUri (ordinal) so a trailing-slash or host-case difference does
        // not spuriously split one account into two keys. Joined with "\0" and compared under StringComparer.Ordinal.
        private static string BuildSourceIdentityKey(DocumentReliabilityRegistration registration,
                                                     Container monitoredContainer,
                                                     Container leaseContainer)
        {
            if (registration.DeclaredSourceIdentity is CosmosSourceIdentity declared)
            {
                return "declared\0" + declared.Monitored + "\0" + declared.Lease;
            }

            return "ground-truth\0"
                + NormalizeEndpoint(monitoredContainer.Database.Client.Endpoint) + "\0"
                + monitoredContainer.Database.Id + "\0" + monitoredContainer.Id + "\0"
                + NormalizeEndpoint(leaseContainer.Database.Client.Endpoint) + "\0"
                + leaseContainer.Database.Id + "\0" + leaseContainer.Id;
        }

        private static string NormalizeEndpoint(Uri endpoint) => endpoint?.AbsoluteUri ?? string.Empty;

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
