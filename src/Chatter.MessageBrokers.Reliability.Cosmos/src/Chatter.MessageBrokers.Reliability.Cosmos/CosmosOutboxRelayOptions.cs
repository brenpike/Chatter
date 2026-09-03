#nullable enable annotations
using Chatter.MessageBrokers.Reliability.Cosmos;
using Microsoft.Azure.Cosmos;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>
    /// Configures a STANDALONE Cosmos change-feed outbox relay registered via
    /// <see cref="CosmosOutboxRelayServiceCollectionExtensions.AddCosmosOutboxRelay"/>. Unlike the command-pipeline relay
    /// (registered by <c>WithCosmosDocumentReliability&lt;TCommand&gt;</c>, which derives its processors from the
    /// <c>DocumentReliabilityRegistry</c>), a standalone relay drains a SINGLE explicitly-configured monitored container's
    /// change feed against an explicitly-configured lease container — independent of any command-type participation. Use it
    /// to relay an outbox container the application owns directly.
    /// </summary>
    /// <remarks>
    /// The drain knobs default to the same behavior the registry-driven relay's legacy settings reproduce (a one-day
    /// delivered TTL stamped at the reserved <c>/ttl</c> path, <c>/status</c> -&gt; <c>delivered</c>, and no additional
    /// pending filter — the relay ALWAYS applies the <see cref="CosmosOutboxDocument.IsPendingOutbox"/> id-guard), so a
    /// relay configured with only the required factories + partition-key path behaves identically to the command-pipeline
    /// relay over the same source. The three stamp knobs are validated when the options are mapped into the relay's
    /// settings: the delivered status value must differ from <c>pending</c>, the delivered TTL must be positive, and the
    /// status patch path is anchored to the <c>status</c> field. The delivered TTL's patch path is NOT a knob — it is
    /// hard-wired to the Cosmos-reserved <c>/ttl</c> property (the only field Cosmos self-purges on), so a non-purging
    /// delivered stamp is unrepresentable.
    /// </remarks>
    public sealed class CosmosOutboxRelayOptions
    {
        /// <summary>
        /// REQUIRED. Resolves the monitored (document/outbox) <see cref="Container"/> whose change feed the relay drains,
        /// from the root service provider. The application owns the container's existence and the <see cref="CosmosClient"/>
        /// lifecycle (the relay derives, never provisions).
        /// </summary>
        public Func<IServiceProvider, Container> MonitoredContainerFactory { get; set; }

        /// <summary>
        /// REQUIRED. Resolves the lease <see cref="Container"/> backing the change-feed processor, from the root service
        /// provider.
        /// </summary>
        public Func<IServiceProvider, Container> LeaseContainerFactory { get; set; }

        /// <summary>
        /// REQUIRED, non-empty. The monitored container's partition-key path (single or hierarchical, e.g.
        /// <c>["/tenantId"]</c>). The relay recovers each drained document's partition key at these segments to target the
        /// delivered/TTL patch at the document's own logical partition.
        /// </summary>
        public IReadOnlyList<string> PartitionKeyPath { get; set; }

        /// <summary>
        /// Optional RAW escape hatch. When supplied, binds a factory the host invokes to obtain an
        /// <see cref="IOutboxBodyResolver"/>. A bound resolver owns the brokered message published for each admitted pending
        /// document instead of the relay's verbatim field reconstruction.
        /// </summary>
        /// <remarks>
        /// LIFETIME CONTRACT (per-admitted-document scope). The host opens a fresh <see cref="IServiceScope"/> and invokes
        /// this factory with THAT scope's <see cref="IServiceProvider"/> ONLY for an ADMITTED pending outbox document — NOT
        /// per raw drained document. The monitored container is co-resident (domain aggregates, inbox markers, and the
        /// relay's own delivered-stamp event also surface on its change feed); a non-admitted document is skipped with NO
        /// scope opened and this factory NOT invoked, so a non-outbox write never constructs a resolver. The scope is
        /// disposed after the admitted document is processed. The factory MUST resolve a FRESH resolver from the supplied
        /// provider on every call and MUST NOT cache or capture the resolver (or its scoped dependencies) across documents —
        /// a captured resolver would outlive the per-document scope it was bound to. Because resolution happens inside the
        /// per-document scope, a resolver MAY depend on / capture scoped services in its constructor.
        /// <para>
        /// SAFE-BY-DEFAULT PATH: prefer the typed
        /// <see cref="CosmosOutboxRelayServiceCollectionExtensions.AddCosmosOutboxRelay{TResolver}(IServiceCollection, System.Action{CosmosOutboxRelayOptions})"/>
        /// overload (or the keyed
        /// <see cref="CosmosOutboxRelayServiceCollectionExtensions.AddCosmosOutboxRelay{TResolver}(IServiceCollection, object, System.Action{CosmosOutboxRelayOptions})"/>
        /// overload for multiple monitored containers), which registers the resolver scoped and wires this factory correctly
        /// for you. Set this property directly only when you need full control over how the resolver is obtained.
        /// </para>
        /// </remarks>
        public Func<IServiceProvider, IOutboxBodyResolver>? BodyResolverFactory { get; set; }

        /// <summary>
        /// The post-delivery retention window stamped on a delivered document, in seconds. Defaults to one day. Must be
        /// positive — a delivered document must be scheduled for self-purge (a non-positive value is rejected at construction).
        /// </summary>
        public int DeliveredTtlSeconds { get; set; } = 86400;

        /// <summary>
        /// The Cosmos document-patch path for the delivery status field. Defaults to <c>/status</c> and is anchored there:
        /// it must equal <c>/</c> + the <c>status</c> field the always-applied pending gate reads, so a delivered stamp
        /// always moves the document out of pending (any other path is rejected at construction).
        /// </summary>
        public string StatusPatchPath { get; set; } = "/status";

        /// <summary>
        /// The status value a delivered document is advanced to. Defaults to <c>delivered</c>. Must be non-empty and must
        /// differ from <c>pending</c> (an equal-to-pending value is rejected at construction).
        /// </summary>
        public string DeliveredStatusValue { get; set; } = "delivered";

        /// <summary>
        /// OPT-IN (#361). The number of CONSECUTIVE failed drains of the SAME document IDENTITY — the (id, recovered
        /// partition key) pair the poison stamp itself patches, so two documents sharing a message id in different logical
        /// partitions never share a count — after which the relay gives up on that document, stamping it
        /// <see cref="PoisonStatusValue"/> so the change feed can advance past it. Defaults to <c>0</c> — OFF — which keeps
        /// the fail-closed behavior: every failure re-throws, nothing is checkpointed, and the document stays pending. Must
        /// not be negative.
        /// </summary>
        /// <remarks>
        /// Enable this only when head-of-line blocking is the greater risk. Throw-so-no-checkpoint is CORRECT for a
        /// TRANSIENT publish failure — the gap it closes is that there is otherwise no escape from a DETERMINISTIC one,
        /// where one undeliverable document stalls every later document in its partition range indefinitely.
        /// <para>
        /// THE RELAY DOES NOT CLASSIFY THE FAILURE. It counts THAT a drain failed, not WHAT KIND of failure it was: every
        /// failure other than a drain cancelled by host shutdown advances the count, INCLUDING a downstream broker, auth, or
        /// throttling outage. An enabled policy riding out a sustained outage therefore gives up on a lease's backlog
        /// SERIALLY — the head document fails once per change-feed pass, so it is given up on after this many passes and the
        /// next document becomes the head.
        /// </para>
        /// <para>
        /// THE COUNT IS PROCESS-LOCAL. It lives in memory in the relay instance that observed the failures, so it resets on
        /// restart and fragments across lease rebalancing and across hosts sharing one change-feed source identity; a give-up
        /// takes this many consecutive failures within ONE host's lifetime.
        /// </para>
        /// A given-up document is NEVER deleted and carries NO TTL: it stays in the container, inspectable, at its poison
        /// status, until an operator re-drives it (see <see cref="PoisonStatusValue"/>). So an over-eager give-up costs
        /// delivery until that intervention, never the document.
        /// </remarks>
        public int PoisonAfterConsecutiveFailures { get; set; }

        /// <summary>
        /// The status value a given-up document is stamped with once <see cref="PoisonAfterConsecutiveFailures"/> is
        /// reached. Defaults to <c>poisoned</c>. Read only while the policy is enabled, and then it must be non-empty, must
        /// differ from <c>pending</c> (which would re-surface the document forever), and must differ from
        /// <see cref="DeliveredStatusValue"/> (which would make a give-up indistinguishable from a delivery) — all rejected
        /// at construction.
        /// </summary>
        /// <remarks>
        /// A document carrying this status is NEVER deleted and carries NO TTL — nothing re-publishes it, and re-driving it
        /// by patching its status back to pending, so the change feed surfaces it again, is an OPERATOR action. That matters
        /// because <see cref="PoisonAfterConsecutiveFailures"/> elects on an unclassified, process-local failure count, so a
        /// document may be stamped with this status for an outage rather than for a defect of its own.
        /// </remarks>
        public string PoisonStatusValue { get; set; } = "poisoned";

        /// <summary>
        /// Optional. An ADDITIONAL predicate that can only further NARROW which documents the relay admits. The relay
        /// ALWAYS applies the built-in <see cref="CosmosOutboxDocument.IsPendingOutbox"/> id-guard first; this predicate
        /// runs only on documents that already passed it (logical AND) and therefore cannot replace or weaken the #222
        /// id-guard. Defaults to <c>null</c> (no additional narrowing).
        /// </summary>
        public Func<JsonElement, bool>? AdditionalPendingFilter { get; set; }

        /// <summary>
        /// Optional caller-declared monitored-side change-feed source identity. When supplied (with
        /// <see cref="LeaseSourceIdentity"/>), the relay's processor name is derived from the declared pair rather than the
        /// resolved containers' ground-truth identity — so two relays declaring the same source cooperate on one logical
        /// processor and two distinct declarations never collide. Leave both null to key on the resolved containers' ground
        /// truth (account endpoint + database id + container id).
        /// </summary>
        public string? MonitoredSourceIdentity { get; set; }

        /// <summary>Optional caller-declared lease-side change-feed source identity. See <see cref="MonitoredSourceIdentity"/>.</summary>
        public string? LeaseSourceIdentity { get; set; }
    }
}
