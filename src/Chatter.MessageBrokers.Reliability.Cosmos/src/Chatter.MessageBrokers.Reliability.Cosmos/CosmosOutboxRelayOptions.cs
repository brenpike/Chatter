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
        /// Optional. When supplied, binds a factory the host invokes to obtain an <see cref="IOutboxBodyResolver"/>. The
        /// host opens a fresh <see cref="IServiceScope"/> PER DRAINED DOCUMENT and resolves the resolver from that scope
        /// (disposed after the document is processed), so a resolver MAY depend on / capture scoped services in its
        /// constructor. A bound resolver owns the brokered message published for each admitted pending document instead of
        /// the relay's verbatim field reconstruction.
        /// </summary>
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
