using Microsoft.Azure.Cosmos;
using System;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// An immutable, per-command-type document-tier reliability registration. Participation in the document tier is
    /// having a registration: the <see cref="DocumentReliabilityRegistry"/> is a positive allowlist keyed by
    /// <see cref="CommandType"/>, so a command type with a registration participates and one without bypasses. Each
    /// registration selects the container the command's aggregate/outbox/inbox-marker are co-resident in (by
    /// <see cref="Database"/> + <see cref="ContainerName"/>), the change-feed <see cref="LeaseName"/> the #222 relay
    /// drains, the Try/nullable partition-key <see cref="Resolver"/>, and the container's <see cref="PartitionKeyPath"/>.
    /// </summary>
    /// <remarks>
    /// Multiple single-partition containers are supported (ADR-0008): many command types MAY map to one container, but
    /// exactly ONE registration exists per command type. The container handle is DERIVED (never provisioned) by the
    /// <see cref="CosmosContainerFactory"/>: by default via <c>client.GetContainer(<see cref="Database"/>,
    /// <see cref="ContainerName"/>)</c> on the app-registered <see cref="CosmosClient"/>, or — when the optional
    /// <see cref="DocumentContainerFactory"/> / <see cref="LeaseContainerFactory"/> are supplied — by invoking those
    /// app-provided factories. ADR-0007's single-aggregate/single-partition hard constraint is unaffected: each write is
    /// still single-partition; the registry adds container selection, not cross-partition atomicity.
    /// </remarks>
    public sealed class DocumentReliabilityRegistration
    {
        public DocumentReliabilityRegistration(Type commandType,
                                               string database,
                                               string containerName,
                                               string leaseName,
                                               ResolvePartitionKey resolver,
                                               IReadOnlyList<string> partitionKeyPath,
                                               Func<IServiceProvider, Container> documentContainerFactory = null,
                                               Func<IServiceProvider, Container> leaseContainerFactory = null,
                                               CosmosSourceIdentity? declaredSourceIdentity = null)
        {
            CommandType = commandType ?? throw new ArgumentNullException(nameof(commandType));
            Database = database ?? throw new ArgumentNullException(nameof(database));
            ContainerName = containerName ?? throw new ArgumentNullException(nameof(containerName));
            LeaseName = leaseName ?? throw new ArgumentNullException(nameof(leaseName));
            Resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            PartitionKeyPath = partitionKeyPath ?? throw new ArgumentNullException(nameof(partitionKeyPath));
            DocumentContainerFactory = documentContainerFactory;
            LeaseContainerFactory = leaseContainerFactory;
            DeclaredSourceIdentity = declaredSourceIdentity;
        }

        /// <summary>The command type this registration participates for (the registry key).</summary>
        public Type CommandType { get; }

        /// <summary>The Cosmos database the document container lives in.</summary>
        public string Database { get; }

        /// <summary>The document (aggregate) container the aggregate, outbox doc, and inbox marker are co-resident in.</summary>
        public string ContainerName { get; }

        /// <summary>The change-feed lease container name — consumed by the #222 relay's per-registration fan-out.</summary>
        public string LeaseName { get; }

        /// <summary>The Try/nullable partition-key resolver (a <c>null</c> return = no resolvable partition for the message).</summary>
        public ResolvePartitionKey Resolver { get; }

        /// <summary>The container's partition-key path (e.g. <c>"/tenantId"</c>; hierarchical containers carry multiple segments).</summary>
        public IReadOnlyList<string> PartitionKeyPath { get; }

        /// <summary>
        /// Optional explicit document-container factory. When non-null the <see cref="CosmosContainerFactory"/> invokes
        /// it instead of deriving the handle via <c>client.GetContainer(Database, ContainerName)</c>.
        /// </summary>
        public Func<IServiceProvider, Container> DocumentContainerFactory { get; }

        /// <summary>
        /// Optional explicit lease-container factory. When non-null the <see cref="CosmosContainerFactory"/> invokes it
        /// instead of deriving the handle via <c>client.GetContainer(Database, LeaseName)</c>.
        /// </summary>
        public Func<IServiceProvider, Container> LeaseContainerFactory { get; }

        /// <summary>
        /// Optional caller-DECLARED change-feed source identity for the monitored + lease containers. It is the relay's
        /// dedup/processor key, declared-or-derived (#222 root remediation):
        /// <list type="bullet">
        /// <item>
        /// FACTORY PATH (<c>non-null</c>): when the advanced overload supplies <see cref="DocumentContainerFactory"/> /
        /// <see cref="LeaseContainerFactory"/>, the caller fully controls the resolved <see cref="Container"/> handles, so
        /// the relay CANNOT trust those handles to identify the physical change-feed source. The caller therefore DECLARES
        /// the identity here, and THIS declared identity IS the relay dedup key — the relay never inspects the untrusted
        /// handle's account endpoint or names to key on the factory path.
        /// </item>
        /// <item>
        /// PLAIN PATH (<c>null</c>): the handle is derived by the provider from the app-registered
        /// <see cref="CosmosClient"/>, so it is ground truth. The relay derives the dedup key from the resolved handle
        /// (account endpoint + database id + container id, for both monitored and lease) rather than from any declared
        /// value.
        /// </item>
        /// </list>
        /// INVARIANT: the relay dedup key is NEVER inferred from an untrusted handle — it is the DECLARED identity on the
        /// factory path and GROUND-TRUTH-derived on the plain path. This dissolves the split/collapse class the prior
        /// inferred-from-handle keys admitted (#222).
        /// </summary>
        public CosmosSourceIdentity? DeclaredSourceIdentity { get; }
    }

    /// <summary>
    /// A caller-declared, opaque-but-stable change-feed source identity for the advanced/factory registration path. It
    /// carries one stable token per source container — <see cref="Monitored"/> for the monitored (document) container and
    /// <see cref="Lease"/> for the lease container — so two registrations that resolve the SAME physical change-feed
    /// source declare the SAME pair (and collapse to one relay processor) while two distinct sources declare distinct
    /// pairs (and never share a processor). The tokens are opaque to the relay: it only compares them for equality under
    /// ordinal string comparison; their meaning is the application's.
    /// </summary>
    /// <remarks>
    /// This type exists ONLY for the factory path, where the resolved <see cref="Container"/> handle is caller-controlled
    /// and therefore untrusted as a dedup key. On the plain path the key is ground-truth-derived from the resolved handle
    /// and this declared identity is <c>null</c> (see <see cref="DocumentReliabilityRegistration.DeclaredSourceIdentity"/>).
    /// </remarks>
    public readonly struct CosmosSourceIdentity
    {
        public CosmosSourceIdentity(string monitored, string lease)
        {
            Monitored = monitored;
            Lease = lease;
        }

        /// <summary>The stable token identifying the monitored (document) change-feed source container.</summary>
        public string Monitored { get; }

        /// <summary>The stable token identifying the lease container backing the change-feed processor.</summary>
        public string Lease { get; }
    }
}
