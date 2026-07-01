using Microsoft.Azure.Cosmos;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// The shared co-resident partition-key stamping primitive (ADR-0007). Both the outbox document
    /// (<see cref="CosmosOutboxDocument"/>) and the inbox marker (<see cref="CosmosInboxMarker"/>, #220) carry their
    /// resolved partition-key value(s) at the container's ACTUAL partition-key path — single or hierarchical — rather
    /// than at a fixed field named <c>partitionKey</c>, so each document lands in the same logical partition the
    /// framework-owned batch was opened on. The minting logic lives here ONCE; each caller passes its OWN
    /// reserved-root-field collision-guard set so the two document shapes share no field-name knowledge.
    /// </summary>
    internal static class CosmosPartitionKeyStamping
    {
        /// <summary>
        /// Stamps every resolved partition-key value at its declared container path segment into
        /// <paramref name="document"/>. <paramref name="partitionKeyPath"/> and <paramref name="partitionKeyValues"/>
        /// map positionally: a hierarchical path (e.g. <c>["/tenant/id", "/region"]</c>) stamps one value per path,
        /// nesting intermediate objects so each value lands at its real container path. Each call mints a fresh
        /// <see cref="JsonNode"/> from the caller-supplied <see cref="JsonElement"/> values so multiple documents may be
        /// built from the same value set without cross-document node re-parenting. <paramref name="reservedRootFields"/>
        /// is the caller's own collision-guard set: a path whose ROOT segment matches one of these would overwrite a
        /// required Chatter field, so stamping fails loudly rather than silently corrupting the document.
        /// </summary>
        // INVARIANT: the partition-key value is placed at the container's REAL declared path, never at a fixed field
        // named "partitionKey"; a hierarchical container stamps one leaf per path segment. The stamped value preserves
        // its JSON value kind (string/number/bool/null) so the document lands in the SAME logical partition the batch
        // was opened on — a non-string partition value must NOT be coerced to a JSON string.
        public static void Stamp(JsonObject document,
                                 IReadOnlyList<string> partitionKeyPath,
                                 IReadOnlyList<JsonElement> partitionKeyValues,
                                 ISet<string> reservedRootFields)
        {
            _ = document ?? throw new ArgumentNullException(nameof(document));
            _ = reservedRootFields ?? throw new ArgumentNullException(nameof(reservedRootFields));

            if (partitionKeyPath is null || partitionKeyPath.Count == 0)
            {
                throw new ArgumentException("A container partition-key path is required to stamp the document.", nameof(partitionKeyPath));
            }

            _ = partitionKeyValues ?? throw new ArgumentNullException(nameof(partitionKeyValues));
            if (partitionKeyValues.Count != partitionKeyPath.Count)
            {
                throw new ArgumentException(
                    $"Expected '{partitionKeyPath.Count}' partition-key value(s) to match the path segment count but got '{partitionKeyValues.Count}'.",
                    nameof(partitionKeyValues));
            }

            for (var i = 0; i < partitionKeyPath.Count; i++)
            {
                StampPartitionKeySegment(document, partitionKeyPath[i], partitionKeyValues[i], reservedRootFields);
            }
        }

        /// <summary>
        /// Recovers the scalar partition-key value(s) from a resolved <see cref="PartitionKey"/> via its public
        /// JSON-array form (e.g. <c>["tenant-1"]</c> for a single string PK, <c>[42]</c> for a numeric PK,
        /// <c>["a","b"]</c> for a hierarchical PK), mapped positionally onto <paramref name="partitionKeyPath"/>. Each
        /// value is returned as a cloned <see cref="JsonElement"/> — a self-contained value type detached from its
        /// backing <see cref="JsonDocument"/> — preserving its JSON value kind (string/number/bool/null) so a stamped
        /// document lands in the SAME logical partition the framework-owned batch was opened on. Callers may pass the
        /// same list to <see cref="Stamp"/> for multiple documents; each call mints a fresh node from the element. Both
        /// the outbox doc and the inbox marker recover values this way so the two share one carriage contract.
        /// </summary>
        public static IReadOnlyList<JsonElement> RecoverPartitionKeyValues(PartitionKey partitionKey, IReadOnlyList<string> partitionKeyPath)
        {
            _ = partitionKeyPath ?? throw new ArgumentNullException(nameof(partitionKeyPath));

            using var doc = JsonDocument.Parse(partitionKey.ToString());
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException($"Unexpected partition-key serialization '{partitionKey}'; expected a JSON array.");
            }

            if (root.GetArrayLength() != partitionKeyPath.Count)
            {
                throw new InvalidOperationException(
                    $"The resolved partition key has '{root.GetArrayLength()}' value(s) but the container partition-key path declares '{partitionKeyPath.Count}' segment(s).");
            }

            var values = new List<JsonElement>(partitionKeyPath.Count);
            foreach (JsonElement element in root.EnumerateArray())
            {
                // Clone detaches the element from the backing JsonDocument so the value remains valid after the
                // using-scoped doc is disposed and can be reused across multiple Stamp calls.
                values.Add(element.Clone());
            }

            return values;
        }

        // Stamps a single partition-key path (e.g. "/tenant/id") into the document, creating intermediate objects for
        // each non-leaf segment so the value lands at the real container path rather than a flattened fixed field. A
        // fresh JsonNode is minted from the JsonElement on every call so this document never receives a node that is
        // already parented to another document — cross-document reparenting is structurally impossible.
        // Extracts the ROOT (first) property-name segment of a container partition-key path (e.g. "/tenant/id" -> "tenant").
        // Shared by render-time collision-guard stamping and registration-time fail-loud validation so both derive the
        // partition path's root segment through ONE primitive rather than duplicating the split.
        internal static string ExtractRootSegment(string partitionKeyPath)
        {
            var segments = partitionKeyPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                throw new ArgumentException("A partition-key path segment must contain at least one property name.", nameof(partitionKeyPath));
            }

            return segments[0];
        }

        private static void StampPartitionKeySegment(JsonObject root, string partitionKeyPath, JsonElement partitionKeyValue, ISet<string> reservedRootFields)
        {
            var segments = partitionKeyPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string rootSegment = ExtractRootSegment(partitionKeyPath);

            // COLLISION GUARD: a partition-key path whose ROOT segment is a Chatter-reserved field would overwrite a
            // required Chatter value (e.g. /id replaces the deterministic document id, colliding every doc in the
            // partition). Fail loudly rather than silently corrupt the document. This eliminates the
            // reserved-field-overwrite class by construction. The reserved set belongs to the caller's document shape.
            if (reservedRootFields.Contains(rootSegment))
            {
                throw new InvalidOperationException(
                    $"The container partition-key path '{partitionKeyPath}' targets the Chatter-reserved field '{rootSegment}'. " +
                    "A co-resident document cannot be stamped on a partition-key path whose root segment is one of " +
                    $"[{string.Join(", ", reservedRootFields)}] without overwriting a required field. Use a non-reserved partition-key path for the container.");
            }

            JsonObject current = root;
            for (var i = 0; i < segments.Length - 1; i++)
            {
                var segment = segments[i];
                if (current[segment] is JsonObject existing)
                {
                    current = existing;
                }
                else
                {
                    var nested = new JsonObject();
                    current[segment] = nested;
                    current = nested;
                }
            }

            // Mint a fresh JsonNode from the detached JsonElement so this leaf has no prior parent and each document
            // gets its own independent node. JsonNode.Parse returns null for a JSON null literal, which is the correct
            // JSON-null leaf for a null partition-key component.
            current[segments[^1]] = JsonNode.Parse(partitionKeyValue.GetRawText());
        }
    }
}
