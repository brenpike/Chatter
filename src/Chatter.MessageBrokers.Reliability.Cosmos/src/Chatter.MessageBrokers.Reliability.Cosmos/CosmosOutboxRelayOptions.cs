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
    /// delivered TTL, <c>/status</c> -&gt; <c>delivered</c>, <c>/ttl</c>, and the <see cref="CosmosOutboxDocument.IsPendingOutbox"/>
    /// pending predicate), so a relay configured with only the required factories + partition-key path behaves identically
    /// to the command-pipeline relay over the same source.
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
        /// Optional. When supplied, resolves an <see cref="IOutboxBodyResolver"/> from the root service provider once at
        /// host start. A bound resolver owns the brokered message published for each admitted pending document instead of
        /// the relay's verbatim field reconstruction.
        /// </summary>
        public Func<IServiceProvider, IOutboxBodyResolver>? BodyResolverFactory { get; set; }

        /// <summary>The post-delivery retention window stamped on a delivered document, in seconds. Defaults to one day.</summary>
        public int DeliveredTtlSeconds { get; set; } = 86400;

        /// <summary>The Cosmos document-patch path for the delivery status field. Defaults to <c>/status</c>.</summary>
        public string StatusPatchPath { get; set; } = "/status";

        /// <summary>The status value a delivered document is advanced to. Defaults to <c>delivered</c>.</summary>
        public string DeliveredStatusValue { get; set; } = "delivered";

        /// <summary>The Cosmos document-patch path for the system TTL property. Defaults to <c>/ttl</c>.</summary>
        public string TtlPatchPath { get; set; } = "/ttl";

        /// <summary>
        /// The predicate admitting a change-feed document as a pending outbox document to drain. Defaults to
        /// <see cref="CosmosOutboxDocument.IsPendingOutbox"/>.
        /// </summary>
        public Func<JsonElement, bool> PendingFilter { get; set; } = CosmosOutboxDocument.IsPendingOutbox;

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
