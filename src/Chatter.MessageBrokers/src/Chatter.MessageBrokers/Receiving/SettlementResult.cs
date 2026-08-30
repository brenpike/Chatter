namespace Chatter.MessageBrokers.Receiving
{
    /// <summary>
    /// The result of settling a received delivery with the message broker infrastructure.
    /// </summary>
    public readonly struct SettlementResult
    {
        private const string UnrecordedReason = "no settlement reason was recorded";

        private readonly string _reason;

        private SettlementResult(SettlementOutcome outcome, string reason)
        {
            Outcome = outcome;
            _reason = reason;
        }

        /// <summary>
        /// The outcome of the settlement.
        /// </summary>
        public SettlementOutcome Outcome { get; }

        /// <summary>
        /// Whether the infrastructure settled the delivery.
        /// </summary>
        public bool IsSettled => Outcome == SettlementOutcome.Settled;

        /// <summary>
        /// Why the delivery was not settled. Carries no value when the delivery was settled.
        /// </summary>
        /// <remarks>
        /// INVARIANT: only a settled delivery yields an absent reason, so an unsettled outcome always explains
        /// itself — including <c>default(SettlementResult)</c>, whose backing reason no factory ever supplied.
        /// </remarks>
        public string Reason
            => IsSettled ? null : (string.IsNullOrWhiteSpace(_reason) ? UnrecordedReason : _reason);

        /// <summary>
        /// The infrastructure settled the delivery.
        /// </summary>
        public static SettlementResult Settled()
            => new SettlementResult(SettlementOutcome.Settled, null);

        /// <summary>
        /// There was nothing to settle.
        /// </summary>
        /// <param name="reason">Why nothing needed settling.</param>
        public static SettlementResult NotRequired(string reason)
            => new SettlementResult(SettlementOutcome.NotRequired, reason);

        /// <summary>
        /// Settlement was attempted and did not happen.
        /// </summary>
        /// <param name="reason">Why the attempted settlement did not happen.</param>
        public static SettlementResult Failed(string reason)
            => new SettlementResult(SettlementOutcome.Failed, reason);
    }
}
