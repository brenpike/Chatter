using System;
using System.Collections.Generic;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    // The single hardened validator for a container's partition-key path, reused by BOTH the command-pipeline
    // registration (Extensions.WithCosmosDocumentReliability) and the standalone relay registration
    // (CosmosOutboxRelayServiceCollectionExtensions.AddCosmosOutboxRelay). It rejects an empty path and any
    // null/whitespace segment AND returns an independent read-only snapshot, so post-registration mutation of the
    // caller-owned collection cannot corrupt the stored path.
    internal static class PartitionKeyPathValidator
    {
        // Validates the supplied partition-key path and returns an independent read-only snapshot. Each segment is read
        // ONCE into a local, validated, and stored from that SAME local — never re-read from the source collection — so
        // the validated bytes are the stored bytes and a concurrently-mutating caller collection cannot slip a bad
        // segment past the check (TOCTOU-free). Throws <see cref="ArgumentException"/> (carrying <paramref name="paramName"/>)
        // when the path is null/empty or any segment is null/whitespace.
        internal static IReadOnlyList<string> ValidateAndSnapshot(IReadOnlyList<string> partitionKeyPath, string paramName)
        {
            if (partitionKeyPath is null || partitionKeyPath.Count == 0)
            {
                throw new ArgumentException("A container partition-key path is required.", paramName);
            }

            var snapshot = new string[partitionKeyPath.Count];
            for (var i = 0; i < snapshot.Length; i++)
            {
                var segment = partitionKeyPath[i];
                if (string.IsNullOrWhiteSpace(segment))
                {
                    throw new ArgumentException("Every container partition-key path segment must be non-null and non-whitespace.", paramName);
                }

                snapshot[i] = segment;
            }

            return Array.AsReadOnly(snapshot);
        }
    }
}
