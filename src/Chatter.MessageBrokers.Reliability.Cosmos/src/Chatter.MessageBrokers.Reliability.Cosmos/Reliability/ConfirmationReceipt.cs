using Microsoft.Azure.Cosmos;
using System.Text.Json;

namespace Chatter.MessageBrokers.Reliability.Cosmos
{
    /// <summary>
    /// Evidence that the Outbox Relay's status write LANDED on the monitored container: the receipt one stamp returns,
    /// and — combined with <see cref="Or"/> across a batch's documents — the whole batch's evidence. It is what
    /// <see cref="OutboxDrainGate.RecordConfirmationSuccess"/> requires before it lifts a Drain Suspension.
    /// </summary>
    /// <remarks>
    /// WHY A VALUE RATHER THAN A CONTROL-FLOW INFERENCE. A drain loop that simply RAN TO COMPLETION proves only that
    /// nothing threw, and absence of failure is not presence of success: an EMPTY batch and a batch every document's
    /// pending-outbox pre-gate rejected both reach loop-end having performed NO confirming write at all — and on a
    /// CO-RESIDENT monitored container (domain writes, inbox markers, already-delivered documents) the second is the
    /// ordinary batch, not an exotic one. Lifting a suspension on either would evict the lease's entry, which resets
    /// the consecutive count as well, so the bound the suspension exists to hold degrades from one republish per
    /// window to a full fresh threshold of them.
    /// INVARIANT: a PRESENT receipt is derivable ONLY from an object a status write RETURNED. The constructor is
    /// private and <see cref="ForStamp"/> is the sole mint, so <see langword="default"/> — no receipt — is the only
    /// value any other code can produce. A gate transition inferred from control-flow arrival rather than from the
    /// signal it measures is therefore not expressible at any call site.
    /// It is minted at the ONE write both stamps share rather than at the stamp call sites, so a future stamp path
    /// mints its receipt by construction instead of re-deciding which arrival counts as evidence.
    /// </remarks>
    internal readonly struct ConfirmationReceipt
    {
        private readonly bool _stampLanded;

        private ConfirmationReceipt(ItemResponse<JsonElement> stampResponse) => _stampLanded = stampResponse is not null;

        /// <summary>
        /// Whether a status write landed. <see langword="false"/> on <see langword="default"/>, which is what a drain
        /// that performed no confirming write carries.
        /// </summary>
        internal bool IsPresent => _stampLanded;

        /// <summary>
        /// The SOLE mint: the receipt for one status write, taken from the response that write returned. A
        /// <see langword="null"/> <paramref name="stampResponse"/> yields <see langword="default"/> — no receipt.
        /// </summary>
        internal static ConfirmationReceipt ForStamp(ItemResponse<JsonElement> stampResponse) => new ConfirmationReceipt(stampResponse);

        /// <summary>
        /// The DISJUNCTION of this receipt and <paramref name="other"/>, which is how a batch accumulates the evidence
        /// its documents produced: one landed stamp anywhere in the batch is evidence the write path is up.
        /// </summary>
        internal ConfirmationReceipt Or(ConfirmationReceipt other) => _stampLanded ? this : other;
    }
}
