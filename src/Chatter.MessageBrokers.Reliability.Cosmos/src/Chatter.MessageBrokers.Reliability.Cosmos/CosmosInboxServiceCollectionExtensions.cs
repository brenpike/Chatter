using Chatter.CQRS.DependencyInjection;
using Chatter.CQRS.Pipeline;
using Chatter.MessageBrokers.Reliability.Cosmos;
using Chatter.MessageBrokers.Reliability.Inbox;
using Microsoft.Azure.Cosmos;
using System;
using System.Collections.Generic;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>
    /// Registers the STANDALONE, lease-less Cosmos inbox-dedup gate (#253, ADR-0009) on the command pipeline. It replaces
    /// <see cref="IBrokeredMessageInbox"/> with <see cref="CosmosBrokeredMessageInbox"/> and adds the existing
    /// <c>InboxBehavior&lt;T&gt;</c>, and NOTHING else — no lease container, no relay host, no outbox, no router
    /// replacement, no unit-of-work behavior. It is a DISTINCT class from the module's <c>Extensions</c> (which registers
    /// the document tier) to dodge the <c>Microsoft.Extensions.DependencyInjection</c> namespace collision the 0.3.0 relay
    /// already handled with its own <see cref="CosmosOutboxRelayServiceCollectionExtensions"/>.
    /// </summary>
    /// <remarks>
    /// PREREQUISITE: the application MUST register a <see cref="CosmosClient"/> singleton — the inbox derives the
    /// idempotency container from it and owns no client. COMPOSITION: the standalone outbox relay
    /// (<see cref="CosmosOutboxRelayServiceCollectionExtensions.AddCosmosOutboxRelay"/>) and this inbox are orthogonal and
    /// fully supported together; the document tier (<c>WithCosmosDocumentReliability</c>) and this inbox are UNSUPPORTED
    /// together (ADR-0009 D3, documented not code-guarded).
    /// </remarks>
    public static class CosmosInboxServiceCollectionExtensions
    {
        /// <summary>
        /// Registers the standalone Cosmos inbox configured by <paramref name="configure"/>. Eagerly validates the
        /// options (required database/container, single-segment partition-key path, read-back budget) and derives the
        /// idempotency container lazily from the app-registered <see cref="CosmosClient"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="pipelineBuilder"/> or <paramref name="configure"/> is null.</exception>
        /// <exception cref="ArgumentException">The configured options omit a required field, declare a multi-segment
        /// partition-key path, or carry an invalid read-back budget.</exception>
        public static CommandPipelineBuilder WithCosmosInbox(this CommandPipelineBuilder pipelineBuilder, Action<CosmosInboxOptions> configure)
        {
            _ = pipelineBuilder ?? throw new ArgumentNullException(nameof(pipelineBuilder));
            _ = configure ?? throw new ArgumentNullException(nameof(configure));

            var options = new CosmosInboxOptions();
            configure(options);

            if (string.IsNullOrWhiteSpace(options.Database))
            {
                throw new ArgumentException("A non-null, non-whitespace database is required.", nameof(options.Database));
            }
            if (string.IsNullOrWhiteSpace(options.Container))
            {
                throw new ArgumentException("A non-null, non-whitespace container is required.", nameof(options.Container));
            }

            // Snapshot the partition-key path through the SAME hardened validator the document tier and the standalone
            // relay use (rejects an empty path and any null/whitespace segment, freezes an independent copy). v1 supports
            // only a single-segment path (the partition value is the inbound message id); hierarchical support is
            // deferred to backlog #254, so a multi-segment path is rejected with a clear message.
            IReadOnlyList<string> partitionKeyPath = PartitionKeyPathValidator.ValidateAndSnapshot(options.PartitionKeyPath, nameof(options.PartitionKeyPath));
            if (partitionKeyPath.Count != 1)
            {
                throw new ArgumentException(
                    "The standalone Cosmos inbox supports only a single-segment partition-key path (v1); the partition value " +
                    "is the inbound message id. Hierarchical partition-key support is deferred to backlog #254.",
                    nameof(options.PartitionKeyPath));
            }

            if (options.ReadBackMaxAttempts < 1)
            {
                throw new ArgumentException("ReadBackMaxAttempts must be at least 1 so the confirm read-back runs at least once.", nameof(options.ReadBackMaxAttempts));
            }
            if (options.ReadBackInterval < TimeSpan.Zero)
            {
                throw new ArgumentException("ReadBackInterval must be non-negative.", nameof(options.ReadBackInterval));
            }

            // A positive MarkerTimeToLive stamps the dedup-window TTL at the Cosmos-reserved `ttl` field; the marker then
            // stamps the partition value at the container's partition-key path. "ttl" is deliberately ABSENT from the
            // marker's reserved-root-field collision guard (so the document tier keeps a legal `/ttl` partition path — it
            // emits no TTL). But the STANDALONE inbox DOES emit a TTL, so a partition-key path rooted at `/ttl` here would
            // have its partition-value stamp OVERWRITE the numeric TTL — corrupting it (execute-time failure or a silently
            // defeated dedup window). Guard the one path that actually emits a TTL, fail-loud at registration BEFORE any
            // Cosmos write, rather than widening the shared reserved set. Root-segment extraction mirrors the stamping.
            if (options.MarkerTimeToLive > 0)
            {
                var partitionRootSegments = partitionKeyPath[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (partitionRootSegments.Length > 0 && string.Equals(partitionRootSegments[0], CosmosOutboxDocument.TtlField, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "A positive MarkerTimeToLive stamps the Cosmos-reserved 'ttl' field, which collides with a " +
                        "partition-key path rooted at '/ttl' (the partition-value stamp would overwrite the numeric TTL). " +
                        "Use a non-'/ttl' partition-key path (default '/idempotencyKey') or leave MarkerTimeToLive unset.",
                        nameof(options.PartitionKeyPath));
                }
            }

            string database = options.Database;
            string container = options.Container;
            int? markerTimeToLive = options.MarkerTimeToLive;
            int readBackMaxAttempts = options.ReadBackMaxAttempts;
            TimeSpan readBackInterval = options.ReadBackInterval;

            // REPLACE (not AddIfNotRegistered): AddMessageBrokers always pre-registers a default IBrokeredMessageInbox
            // (InMemoryBrokeredMessageInbox), so AddIfNotRegistered would be a silent no-op and dedup would bind the wrong
            // (in-memory) inbox. Mirror ONLY the Replace + WithBehavior of the EF WithInboxBehavior; OMIT the unit-of-work
            // behavior (the standalone inbox has no DbContext/transaction to coordinate). Scoped for EF parity (ADR-0009 D6).
            pipelineBuilder.Services.Replace<IBrokeredMessageInbox>(
                ServiceLifetime.Scoped,
                serviceProvider => new CosmosBrokeredMessageInbox(
                    serviceProvider.GetRequiredService<CosmosClient>().GetContainer(database, container),
                    partitionKeyPath,
                    markerTimeToLive,
                    readBackMaxAttempts,
                    readBackInterval));
            pipelineBuilder.WithBehavior(typeof(InboxBehavior<>));

            return pipelineBuilder;
        }
    }
}
