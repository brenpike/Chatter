using System;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// Cross-call/cross-host accumulator of the standalone relays' derived <c>processorName</c>s, used to make the silent
    /// consumer-group collision class UNREPRESENTABLE: two standalone relays over the SAME monitored+lease containers (same
    /// source identity) differing ONLY in their <see cref="CosmosOutboxRelayOptions.AdditionalPendingFilter"/>/resolver would
    /// otherwise derive the SAME processor name + lease => one Cosmos consumer group => the SDK load-balances the lease
    /// ranges across BOTH relays, so a pending outbox document checkpointed by the relay whose filter/resolver REJECTS it is
    /// never seen by the relay that would ADMIT it — the document wedges. A lease cannot be keyed on a <c>Func</c> delegate,
    /// so the distinction must come from the caller's SOURCE IDENTITY; this registry fails fast on a duplicate.
    /// </summary>
    /// <remarks>
    /// INVARIANT: the declared and ground-truth processor names are DISJOINT by construction — the canonical byte stream
    /// <see cref="CosmosOutboxRelayHostedService.RelaySourceIdentityKey.ToCanonicalBytes"/> hashes begins with a
    /// <see cref="CosmosOutboxRelayHostedService.RelaySourceKind"/> discriminator byte (<c>Declared</c> vs
    /// <c>GroundTruth</c>), so a declared key and a ground-truth key can never produce the same SHA-256 digest and thus never
    /// the same name. One shared <see cref="HashSet{T}"/> therefore safely accumulates BOTH the registration-time declared
    /// names and the start-time ground-truth names without cross-kind false collisions.
    /// </remarks>
    internal sealed class StandaloneRelayProcessorRegistry
    {
        private readonly object _gate = new object();
        private readonly HashSet<string> _processorNames = new HashSet<string>(StringComparer.Ordinal);

        // Registration-time guard for DECLARED identities (both MonitoredSourceIdentity + LeaseSourceIdentity set): the
        // declared processor name is derivable at AddCosmosOutboxRelay time (it keys on the caller-declared identity, not the
        // resolved handles), so a duplicate is caught synchronously at registration.
        internal void RegisterDeclaredProcessorOrThrow(string processorName, string monitoredSourceIdentity, string leaseSourceIdentity)
        {
            lock (_gate)
            {
                if (!_processorNames.Add(processorName))
                {
                    throw new InvalidOperationException(
                        $"A standalone Cosmos outbox relay with the same declared source identity (MonitoredSourceIdentity '{monitoredSourceIdentity}', LeaseSourceIdentity '{leaseSourceIdentity}') is already registered. An identical declared source identity derives the same change-feed processor name and lease, so the two relays form ONE consumer group: the SDK load-balances the lease ranges across both, and a pending outbox document checkpointed by the relay whose AdditionalPendingFilter/resolver REJECTS it is never drained by the relay that would admit it — the document wedges. Supply DISTINCT MonitoredSourceIdentity/LeaseSourceIdentity values for distinct standalone relays over the same containers.");
                }
            }
        }

        // Start-time backstop for GROUND-TRUTH-defaulted identities (both source identities null): the ground-truth processor
        // name is resolvable only after the provider is built and the containers are resolved, so the duplicate check runs in
        // the host's StartAsync rather than at registration.
        internal void RegisterGroundTruthProcessorOrThrow(string processorName)
        {
            lock (_gate)
            {
                if (!_processorNames.Add(processorName))
                {
                    throw new InvalidOperationException(
                        $"Two standalone Cosmos outbox relays resolved to the same ground-truth source identity (the resolved monitored+lease account endpoint/database/container) and thus the same change-feed processor name '{processorName}'. The two relays form ONE consumer group: the SDK load-balances the lease ranges across both, and a pending outbox document checkpointed by the relay whose AdditionalPendingFilter/resolver REJECTS it is never drained by the relay that would admit it — the document wedges. Declare DISTINCT MonitoredSourceIdentity/LeaseSourceIdentity values for distinct standalone relays over the same containers.");
                }
            }
        }
    }
}
