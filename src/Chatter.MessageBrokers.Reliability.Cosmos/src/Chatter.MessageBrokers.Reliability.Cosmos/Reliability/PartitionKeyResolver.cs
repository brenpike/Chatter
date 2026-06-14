using Chatter.MessageBrokers.Receiving;
using Microsoft.Azure.Cosmos;
using System;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// An application-supplied delegate mapping the in-flight <see cref="InboundBrokeredMessage"/> to the Cosmos
    /// partition key of the aggregate the handler writes. There is no core primitive carrying a partition key (the
    /// partition is the aggregate's, which only the application knows); this resolver lives on the document-tier
    /// reliability surface and the Cosmos provider's DI registration, never as a core property.
    /// </summary>
    /// <param name="inboundBrokeredMessage">The message currently being handled.</param>
    /// <returns>The resolved Cosmos <see cref="PartitionKey"/> for the message's aggregate partition.</returns>
    public delegate PartitionKey ResolvePartitionKey(InboundBrokeredMessage inboundBrokeredMessage);

    /// <summary>
    /// Holder pairing the app-supplied <see cref="ResolvePartitionKey"/> delegate with the container's partition-key
    /// path. Registered by the Cosmos provider's DI entry point and consumed by the Document-Tier Batch-Lifecycle
    /// Behavior to open the <see cref="TransactionalBatch"/> on the correct partition; #219/#220 use the PK path to
    /// stamp the resolved value at the container's actual partition-key path.
    /// </summary>
    public sealed class PartitionKeyResolver
    {
        public PartitionKeyResolver(ResolvePartitionKey resolve, IReadOnlyList<string> partitionKeyPath)
        {
            Resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
            PartitionKeyPath = partitionKeyPath ?? throw new ArgumentNullException(nameof(partitionKeyPath));
        }

        /// <summary>
        /// The app-supplied delegate invoked by the Document-Tier Batch-Lifecycle Behavior to resolve the partition key.
        /// </summary>
        public ResolvePartitionKey Resolve { get; }

        /// <summary>
        /// The container's partition-key path (e.g. <c>"/tenantId"</c>; hierarchical containers carry multiple segments).
        /// </summary>
        public IReadOnlyList<string> PartitionKeyPath { get; }
    }
}
