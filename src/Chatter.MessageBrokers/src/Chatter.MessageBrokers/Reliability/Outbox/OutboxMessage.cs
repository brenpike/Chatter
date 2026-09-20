using System;

namespace Chatter.MessageBrokers.Reliability.Outbox
{
    public sealed class OutboxMessage
    {
        public int Id { get; set; }
        public string MessageId { get; set; }
        public string Destination { get; set; }
        public string MessageContext { get; set; }
        public string MessageBody { get; set; }
        public string MessageContentType { get; set; }
        public DateTime SentToOutboxAtUtc { get; set; }
        public DateTime? ProcessedFromOutboxAtUtc { get; set; }
        public Guid BatchId { get; set; }

        /// <summary>
        /// How many times dispatch of this message has been attempted and did not succeed. Zero - the value a staged
        /// message carries - means never attempted.
        /// </summary>
        /// <remarks>
        /// NOTE: this is the whole schedule. The wait before the next attempt is derived from this count by
        /// <see cref="Chatter.MessageBrokers.Reliability.Configuration.ReliabilityOptions"/>, so lengthening the wait
        /// needs no column of its own beyond the instant below.
        /// </remarks>
        public int DispatchAttempts { get; set; }

        /// <summary>
        /// The instant from which this message may be attempted again. Null - the value a staged message carries -
        /// means DUE NOW, so a row written before a store knew this property existed is taken by a poll rather than
        /// held back for good.
        /// </summary>
        /// <remarks>
        /// INVARIANT: null is the due-now value rather than the never-due one. Oracle:
        /// <c>WhenResolvingReliabilityStores.OutboxMessage_IsNeverAttemptedAndDueNowWhenStaged</c>; giving this
        /// property an initializer of <see cref="DateTime.UtcNow"/> reddens it and nothing else.
        /// <para>
        /// NO POLL READS THIS YET. Selection is still <c>ProcessedFromOutboxAtUtc IS NULL</c> alone; the store that
        /// gates a poll on this instant is a later step, so no test in this repository pins the due gate itself.
        /// </para>
        /// </remarks>
        public DateTime? NextAttemptAtUtc { get; set; }
    }
}
