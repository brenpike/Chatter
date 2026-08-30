using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Sending;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Routing
{
    /// <summary>
    /// Routes a brokered message to a receiver
    /// </summary>
    public interface IRouteBrokeredMessages
    {
        /// <summary>
        /// Routes a brokered message to a receiver
        /// </summary>
        /// <param name="outboundBrokeredMessage">The outbound brokered message to be routed to a receiver</param>
        /// <param name="transactionContext">The transactional information to used while routing</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Route(OutboundBrokeredMessage outboundBrokeredMessage, TransactionContext transactionContext);

        /// <summary>
        /// Routes a batch of brokered messages
        /// </summary>
        /// <param name="outboundBrokeredMessages">The batch of outbound brokered messages to be routed to their receivers. Enumerate exactly once.</param>
        /// <param name="transactionContext">The transactional information to used while routing</param>
        /// <param name="infrastructureType">The messaging infrastructure the batch is routed through; empty selects the default infrastructure</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        /// <remarks>
        /// <paramref name="outboundBrokeredMessages"/> is a lazy sequence and an implementation MUST enumerate it
        /// EXACTLY ONCE, and must hand it on to the messaging infrastructure in a way that preserves that guarantee.
        /// Its iterator carries per-yield side effects — message id generation, message body conversion and W3C
        /// trace context propagation — and it also supplies the send span's batch count. A second pass therefore
        /// repeats every side effect, discards the results of the first pass, corrupts the reported batch count,
        /// and breaks a caller that supplied a one-shot sequence.
        /// Do not call Count(), Any(), ToList() or any other LINQ operator that walks the sequence before or in
        /// addition to the real enumeration; when a count is needed, accumulate it during the single walk.
        /// </remarks>
        Task Route(IEnumerable<OutboundBrokeredMessage> outboundBrokeredMessages, TransactionContext transactionContext, string infrastructureType = "");
    }
}
