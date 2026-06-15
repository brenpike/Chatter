using Chatter.MessageBrokers.Receiving;
using Microsoft.Azure.Cosmos;

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
}
