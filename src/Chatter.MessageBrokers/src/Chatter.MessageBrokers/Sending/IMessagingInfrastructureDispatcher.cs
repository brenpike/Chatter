using Chatter.MessageBrokers.Context;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Sending
{
    /// <summary>
    /// Dispatches outbound brokered messages to the messaging infrastructure
    /// </summary>
    public interface IMessagingInfrastructureDispatcher
    {
        /// <summary>
        /// Dispatches a batch of outbound brokered messages to the messaging infrastructure
        /// </summary>
        /// <param name="brokeredMessages">The outbound brokered messages to be dispatched. Enumerate exactly once.</param>
        /// <param name="transactionContext">The transactional information to be used while dispatching</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        /// <remarks>
        /// <paramref name="brokeredMessages"/> is a lazy sequence and an implementation MUST enumerate it EXACTLY ONCE.
        /// Its iterator carries per-yield side effects — message id generation, message body conversion and W3C
        /// trace context propagation — and it also supplies the send span's batch count. A second pass therefore
        /// repeats every side effect, discards the results of the first pass, corrupts the reported batch count,
        /// and breaks a caller that supplied a one-shot sequence.
        /// Do not call Count(), Any(), ToList() or any other LINQ operator that walks the sequence before or in
        /// addition to the real enumeration; when a count is needed, accumulate it during the single walk.
        /// </remarks>
        Task Dispatch(IEnumerable<OutboundBrokeredMessage> brokeredMessages, TransactionContext transactionContext);

        /// <summary>
        /// Dispatches an outbound brokered message to the messaging infrastructure
        /// </summary>
        /// <param name="brokeredMessage">The outbound brokered message to be dispatched</param>
        /// <param name="transactionContext">The transactional information to be used while dispatching</param>
        /// <returns>An awaitable <see cref="Task"/></returns>
        Task Dispatch(OutboundBrokeredMessage brokeredMessage, TransactionContext transactionContext);
    }
}
