using System;
using System.Text;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// Builds Cosmos-id-safe document ids for the co-resident outbox doc (<c>outbox:{encoded(MessageId)}</c>) and the
    /// inbox marker (<c>inbox:{encoded(MessageId)}</c>, #220). The raw <c>OutboundBrokeredMessage.MessageId</c> may be
    /// caller-supplied and can contain characters Cosmos rejects in item ids (<c>/</c>, <c>\</c>, <c>?</c>, <c>#</c>),
    /// so the id segment is a deterministic encoding of the message id; the raw id is stored verbatim in a separate
    /// document field. #220 reuses this for the inbox-marker id so both tiers encode identically.
    /// </summary>
    public static class CosmosItemId
    {
        /// <summary>The reserved discriminator value and id prefix for outbox documents.</summary>
        public const string OutboxKind = "outbox";

        /// <summary>The reserved discriminator value and id prefix for inbox markers (#220 consumes this).</summary>
        public const string InboxKind = "inbox";

        /// <summary>
        /// The Chatter-reserved item-id prefixes (<c>"{kind}:"</c>), derived from the kind constants so there is a single
        /// ground truth — adding a kind constant extends this set, never a parallel literal list. An app document whose
        /// item id starts with one of these prefixes is forbidden on the public atomic-write surface (see
        /// <see cref="GuardNotReserved"/>).
        /// </summary>
        private static readonly string[] _reservedIdPrefixes = { OutboxKind + ":", InboxKind + ":" };

        /// <summary>
        /// True when <paramref name="id"/> begins with any Chatter-reserved id prefix (<c>"outbox:"</c> /
        /// <c>"inbox:"</c>). The match is an ordinal PREFIX test (not a substring search): only an id that actually leads
        /// with a reserved <c>"{kind}:"</c> is reserved.
        /// </summary>
        /// <remarks>
        /// This predicate backs the reserved-namespace guard on the public staging surface, which is useful
        /// DEFENSE-IN-DEPTH against an app accidentally authoring a reserved-prefix id through a staging method. It is NO
        /// LONGER the soundness basis for the inbox marker-409 → confirmed-duplicate decision: the application OWNS the
        /// container (it registers the <c>CosmosClient</c> the container is derived from, and the atomic-write handle
        /// exposes the raw container), so an app can author a reserved-prefix id through a non-staging path no staging
        /// guard can close. Soundness now comes from CONFIRMING the conflicting doc at execute time
        /// (<c>DocumentTierBatchLifecycleBehavior.InspectBatchResponseAsync</c> point-reads the conflicting marker and
        /// treats a 409 as a duplicate only when the doc is a genuine Chatter inbox marker for that message id),
        /// NOT from inferring duplicate from a bare marker-409.
        /// </remarks>
        public static bool IsReserved(string id)
        {
            if (id is null)
            {
                return false;
            }

            foreach (string reservedPrefix in _reservedIdPrefixes)
            {
                if (id.StartsWith(reservedPrefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Throws <see cref="ArgumentException"/> when <paramref name="id"/> is in the Chatter-reserved id namespace
        /// (<see cref="IsReserved"/>), naming the reserved namespace and the offending id. This is the staging-time guard
        /// the public atomic-write surface applies so an app caller cannot author a doc carrying a reserved
        /// <c>inbox:</c>/<c>outbox:</c> id; framework reserved-id construction flows through <see cref="Build"/> and the
        /// internal reserved-write path, which are NOT subject to this guard.
        /// </summary>
        /// <remarks>
        /// This guard is DEFENSE-IN-DEPTH on the public staging surface (see <see cref="IsReserved"/>) — it rejects at
        /// staging time, consistent with the existing length and forbidden-character guards on <see cref="Build"/>. It
        /// is NO LONGER the soundness basis for the inbox marker-409 → confirmed-duplicate decision: that soundness now
        /// comes from CONFIRMING the conflicting doc at execute time, because the app owns the container and can author
        /// a reserved-prefix id through a non-staging path this guard cannot close.
        /// </remarks>
        public static void GuardNotReserved(string id, string paramName)
        {
            if (IsReserved(id))
            {
                throw new ArgumentException(
                    $"The item id '{id}' is in the Chatter-reserved id namespace ('{OutboxKind}:'/'{InboxKind}:'). " +
                    "Application documents must not author reserved-prefix ids through the public staging surface; the " +
                    "framework owns this namespace (defense-in-depth — the marker-409 duplicate decision is confirmed " +
                    "against the conflicting doc, not inferred from the reserved namespace).", paramName);
            }
        }

        /// <summary>
        /// Builds the outbox document id <c>outbox:{encoded(messageId)}</c>.
        /// </summary>
        public static string ForOutbox(string messageId) => Build(OutboxKind, messageId);

        /// <summary>
        /// Builds the inbox marker id <c>inbox:{encoded(messageId)}</c> (#220 consumes this).
        /// </summary>
        public static string ForInbox(string messageId) => Build(InboxKind, messageId);

        /// <summary>The Cosmos-forbidden item-id characters (<c>/</c>, <c>\</c>, <c>?</c>, <c>#</c>).</summary>
        private static readonly char[] _forbiddenIdChars = { '/', '\\', '?', '#' };

        /// <summary>
        /// Cosmos DB's maximum item-id length, in characters. An id over this limit is rejected by the service when the
        /// batch op executes, so it is rejected here at staging time instead.
        /// </summary>
        public const int MaxItemIdLength = 1023;

        /// <summary>
        /// Composes <c>{kind}:{encoded(messageId)}</c>. <paramref name="kind"/> is a Chatter-reserved, id-safe literal.
        /// </summary>
        /// <remarks>
        /// INVARIANT: every public path through <see cref="Build"/> produces only Cosmos-safe ids. The
        /// <paramref name="messageId"/> segment is made id-safe by <see cref="Encode"/>; <paramref name="kind"/> is
        /// emitted verbatim as the id prefix, so it is validated here against the same Cosmos-forbidden character set —
        /// a caller-supplied kind carrying <c>/</c>, <c>\</c>, <c>?</c>, or <c>#</c> would otherwise yield an invalid
        /// item id. This closes the unsafe-kind class for the public surface, not just the reserved constants.
        ///
        /// The composed id is also bounded to <see cref="MaxItemIdLength"/>: a caller-supplied message id long enough to
        /// push the encoded id over Cosmos DB's id-length limit would otherwise fail at batch-execution time and trigger
        /// inbound-message redelivery without committing the batch, so it is rejected here at staging time instead. The
        /// raw message id is stored verbatim in a separate document field, so the physical item id is safe to bound.
        /// </remarks>
        public static string Build(string kind, string messageId)
        {
            if (string.IsNullOrWhiteSpace(kind))
            {
                throw new ArgumentException("A document-id kind is required.", nameof(kind));
            }

            if (kind.IndexOfAny(_forbiddenIdChars) >= 0)
            {
                throw new ArgumentException(
                    "A document-id kind must not contain the Cosmos-forbidden id characters '/', '\\\\', '?', or '#'.", nameof(kind));
            }

            if (messageId is null)
            {
                throw new ArgumentNullException(nameof(messageId));
            }

            var id = $"{kind}:{Encode(messageId)}";

            if (id.Length > MaxItemIdLength)
            {
                throw new ArgumentException(
                    $"The composed Cosmos item id is {id.Length} characters, which exceeds Cosmos DB's {MaxItemIdLength}-character id limit. " +
                    "Supply a shorter message id.", nameof(messageId));
            }

            return id;
        }

        /// <summary>
        /// Deterministic, Cosmos-id-safe encoding of <paramref name="messageId"/>: URL-safe Base64 of the UTF-8 bytes.
        /// </summary>
        /// <remarks>
        /// INVARIANT: the output contains only characters Cosmos permits in an item id. URL-safe Base64 (RFC 4648 §5)
        /// replaces the forbidden <c>+</c>/<c>/</c> with <c>-</c>/<c>_</c> and drops <c>=</c> padding, so the result
        /// avoids every Cosmos-forbidden id character (<c>/</c>, <c>\</c>, <c>?</c>, <c>#</c>). The encoding is
        /// deterministic for a given message id so the same message always maps to the same physical item id.
        /// </remarks>
        public static string Encode(string messageId)
        {
            if (messageId is null)
            {
                throw new ArgumentNullException(nameof(messageId));
            }

            var bytes = Encoding.UTF8.GetBytes(messageId);
            return Convert.ToBase64String(bytes)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
        }
    }
}
