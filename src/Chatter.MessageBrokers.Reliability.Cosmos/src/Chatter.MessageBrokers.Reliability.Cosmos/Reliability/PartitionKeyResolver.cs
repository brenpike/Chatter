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
    /// <remarks>
    /// The resolver is Try/nullable-shaped: a <c>null</c> return means "no resolvable partition for this message", in
    /// which case the Document-Tier Batch-Lifecycle Behavior opens no batch and bare-passes-through to <c>next()</c>.
    /// The resolver is only ever invoked for REGISTERED participants (a command type with a registration in the
    /// Document Reliability Registry), so non-participant commands never reach it. Because
    /// <see cref="MessageHandlerContextExtensions.GetInboundBrokeredMessage"/> returns <c>null</c> for any in-process
    /// command not received from a broker, a resolver MUST tolerate a <c>null</c> inbound message (returning
    /// <c>null</c> for it is the idiomatic "no partition" answer).
    /// </remarks>
    /// <param name="inboundBrokeredMessage">The message currently being handled; may be <c>null</c> for in-process commands.</param>
    /// <returns>The resolved Cosmos <see cref="PartitionKey"/> for the message's aggregate partition, or <c>null</c> when none is resolvable.</returns>
    public delegate PartitionKey? ResolvePartitionKey(InboundBrokeredMessage inboundBrokeredMessage);

    /// <summary>
    /// Holder pairing the app-supplied <see cref="ResolvePartitionKey"/> delegate (Try/nullable) with the container's
    /// partition-key path. In the per-command registry model the resolver and path are carried per registration on
    /// <see cref="DocumentReliabilityRegistration"/>; this holder remains the canonical (delegate, path) pairing the
    /// registration is built from. The resolver is only ever invoked for registered participants and must tolerate a
    /// <c>null</c> inbound message (returning <c>null</c> = "no resolvable partition for this message").
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
