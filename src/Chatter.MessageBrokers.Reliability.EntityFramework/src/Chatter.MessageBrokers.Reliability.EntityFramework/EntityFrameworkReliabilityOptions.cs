using System;

namespace Chatter.MessageBrokers.Reliability.EntityFramework
{
    /// <summary>
    /// Retention settings for the relational inbox and outbox tables.
    /// </summary>
    /// <remarks>
    /// INVARIANT: both retention windows default to null, which DISABLES them. A finite default would change the
    /// behaviour of a host that is already running: a late redelivery its inbox suppresses today would start being
    /// handled again the moment its marker aged out. This deliberately diverges from the in-memory inbox, whose
    /// Deduplication Window is mandatory and at least one minute - that store's window is its only reclamation rule,
    /// while a relational marker that is never purged only costs a row.
    /// </remarks>
    public sealed class EntityFrameworkReliabilityOptions
    {
        /// <summary>
        /// How long an inbox marker suppresses a redelivery of its message id. Null - the default - disables inbox
        /// retention, which keeps every marker for good. A non-positive window is refused at registration.
        /// </summary>
        public TimeSpan? InboxDeduplicationWindow { get; set; }

        /// <summary>
        /// How long an outbox row is kept after the drain stamped it processed. Null - the default - disables outbox
        /// retention, which keeps every processed row for good. A non-positive retention is refused at registration.
        /// </summary>
        public TimeSpan? ProcessedOutboxRetention { get; set; }

        /// <summary>
        /// How long the purge waits between passes. Default value is 5 minutes; a non-positive interval is refused at
        /// registration because it names a loop that never waits.
        /// </summary>
        public TimeSpan PurgeInterval { get; set; } = TimeSpan.FromMinutes(5);
    }
}
