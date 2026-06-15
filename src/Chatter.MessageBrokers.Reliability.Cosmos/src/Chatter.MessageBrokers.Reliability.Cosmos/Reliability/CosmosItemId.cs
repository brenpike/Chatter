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
