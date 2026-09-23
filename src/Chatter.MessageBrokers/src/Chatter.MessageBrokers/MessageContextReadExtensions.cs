using System.Collections.Generic;

namespace Chatter.MessageBrokers
{
    /// <summary>
    /// The Message Context Read for a reader that holds only the Message Context dictionary rather than an
    /// <see cref="Sending.OutboundBrokeredMessage"/> (ADR-0036).
    /// </summary>
    internal static class MessageContextReadExtensions
    {
        /// <summary>
        /// Reads the Message Context value stored under <paramref name="key"/> when that value is a <typeparamref name="TValue"/>.
        /// </summary>
        /// <param name="context">The Message Context to read.</param>
        /// <param name="key">The Message Context key to read.</param>
        /// <param name="value">The stored value when found; otherwise <see langword="default"/>.</param>
        /// <returns>
        /// <see langword="true"/> when <paramref name="key"/> is present and its value is a <typeparamref name="TValue"/>;
        /// otherwise <see langword="false"/>, for an absent key, a value of another kind and a stored <see langword="null"/>
        /// alike. No conversion is attempted, so a stored <see cref="long"/> is not found as a <see cref="string"/>.
        /// </returns>
        internal static bool TryReadMessageContext<TValue>(this IDictionary<string, object> context, string key, out TValue value)
        {
            // INVARIANT: this is the same kind test as OutboundBrokeredMessage.TryGetMessageContextByKey, for a Message
            // Context held only as a dictionary. That dictionary may be one copied verbatim from a delivery's
            // application properties, so a value may be of any kind the wire carries. Oracles:
            // MustReadAMismatchedKindAsNotFoundThroughTheDictionaryTryRead and
            // MustReadAStoredNullAsNotFoundThroughTheDictionaryTryRead; replacing the `stored is TValue typed` kind
            // test with a `(TValue)stored` cast reddens both.
            if (context.TryGetValue(key, out var stored) && stored is TValue typed)
            {
                value = typed;
                return true;
            }

            value = default;
            return false;
        }
    }
}
