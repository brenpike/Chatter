using Chatter.MessageBrokers.Context;

namespace Chatter.MessageBrokers.Receiving
{
    /// <summary>
    /// An OPTIONAL capability a messaging-infrastructure receiver MAY implement alongside
    /// <see cref="IMessagingInfrastructureReceiver"/> to learn when the worker handling a delivery is finished with
    /// it. The receiver discovers the capability by a TYPE CHECK on the infrastructure receiver it was given.
    /// </summary>
    /// <remarks>
    /// DELIBERATELY NOT A MEMBER OF <see cref="IMessagingInfrastructureReceiver"/>. That port carries the settlement
    /// contract, and ADR-0010 D7 requires every implementation of it to be contract-tested against all three
    /// settlement outcomes; widening it would impose that obligation on every broker for a capability only one of
    /// them needs. An implementation that does not declare this interface is never called and sees no change at all.
    /// </remarks>
    public interface IDeliveryReleaseSignal
    {
        /// <summary>
        /// Signals that the worker handling <paramref name="context"/>'s delivery is finished with it, whether or not
        /// the delivery was settled.
        /// </summary>
        /// <param name="context">The delivery the worker has finished handling.</param>
        /// <remarks>
        /// Called EXACTLY ONCE per delivered message, AFTER the settlement answer for that delivery (acknowledge,
        /// acknowledge-failure, negative acknowledge, dead-letter and poison alike) and BEFORE the receiver returns
        /// the concurrency slot the delivery occupied.
        /// It MUST NOT throw. A throw is logged and swallowed; the slot is still returned.
        /// </remarks>
        void DeliveryReleased(MessageBrokerContext context);
    }
}
