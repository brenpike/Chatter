namespace Chatter.MessageBrokers.Receiving
{
    /// <summary>
    /// The outcome of settling a received delivery with the message broker infrastructure.
    /// </summary>
    public enum SettlementOutcome : byte
    {
        /// <summary>
        /// There was nothing to settle, e.g. Azure Service Bus <c>ReceiveAndDelete</c>; RabbitMQ at-most-once.
        /// </summary>
        /// <remarks>
        /// INVARIANT: this is the zero value, so <c>default(SettlementResult)</c> — the value an unconfigured
        /// <see cref="IMessagingInfrastructureReceiver"/> test double yields — never reads as
        /// <see cref="Settled"/>, and never claims a settlement was attempted when none was.
        /// </remarks>
        NotRequired = 0,
        /// <summary>
        /// The infrastructure settled the delivery, e.g. a PeekLock ack succeeded.
        /// </summary>
        Settled = 1,
        /// <summary>
        /// Settlement was attempted and did not happen, e.g. a PeekLock ack could not locate the message.
        /// </summary>
        Failed = 2,
    }
}
