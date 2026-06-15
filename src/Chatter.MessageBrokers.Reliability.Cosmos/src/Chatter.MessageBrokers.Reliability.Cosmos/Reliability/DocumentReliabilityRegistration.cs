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
                                               Func<IServiceProvider, Container> leaseContainerFactory = null)
        {
            CommandType = commandType ?? throw new ArgumentNullException(nameof(commandType));
            Database = database ?? throw new ArgumentNullException(nameof(database));
            ContainerName = containerName ?? throw new ArgumentNullException(nameof(containerName));
            LeaseName = leaseName ?? throw new ArgumentNullException(nameof(leaseName));
            Resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            PartitionKeyPath = partitionKeyPath ?? throw new ArgumentNullException(nameof(partitionKeyPath));
            DocumentContainerFactory = documentContainerFactory;
            LeaseContainerFactory = leaseContainerFactory;
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
    }
}
