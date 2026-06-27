using Microsoft.Azure.Cosmos;
using System.Collections.Generic;
using System.Text.Json;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// The per-document context the #222 change-feed outbox relay hands to an <see cref="IOutboxBodyResolver"/> for a
    /// single admitted pending outbox document. It carries everything the resolver needs to identify the drained
    /// document without re-deriving it from the raw wire shape: the verbatim <see cref="MessageId"/>, the recovered
    /// <see cref="PartitionKey"/> (the same logical partition the delivered/TTL stamp targets), the container's declared
    /// <see cref="PartitionKeyPath"/>, and the raw <see cref="Document"/> as a <see cref="JsonElement"/> so the resolver
    /// may read any persisted field it needs.
    /// </summary>
    public readonly struct OutboxDrainContext
    {
        public OutboxDrainContext(string messageId, PartitionKey partitionKey, IReadOnlyList<string> partitionKeyPath, JsonElement document)
        {
            MessageId = messageId;
            PartitionKey = partitionKey;
            PartitionKeyPath = partitionKeyPath;
            Document = document;
        }

        /// <summary>The verbatim message id read from the drained outbox document.</summary>
        public string MessageId { get; }

        /// <summary>The partition key recovered from the document at the container's declared partition-key path.</summary>
        public PartitionKey PartitionKey { get; }

        /// <summary>The container's declared partition-key path (single or hierarchical).</summary>
        public IReadOnlyList<string> PartitionKeyPath { get; }

        /// <summary>The raw drained outbox document.</summary>
        public JsonElement Document { get; }
    }
}
