using Chatter.MessageBrokers.Context;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;

namespace Chatter.MessageBrokers.Receiving
{
    /// <summary>
    /// The message broker infrastructure used to receive messages
    /// </summary>
    /// <remarks>
    /// THE SETTLEMENT CONTRACT, declared here once and referenced by each of the three settlement members.
    /// A settlement reports one of three outcomes and an implementation MUST distinguish all three, because
    /// collapsing any two of them makes a settlement that FAILED indistinguishable from one that was never
    /// REQUIRED — which is precisely what an undeclared <c>bool</c> return could not express:
    /// <list type="bullet">
    /// <item><description><see cref="SettlementOutcome.Settled"/> — the infrastructure settled the delivery.</description></item>
    /// <item><description><see cref="SettlementOutcome.NotRequired"/> — there was nothing to settle, which is
    /// NOT a failure: Azure Service Bus <c>ReceiveAndDelete</c> and RabbitMQ at-most-once have already removed the
    /// delivery by the time it is handled, so no settlement is owed and none is reported as missing.</description></item>
    /// <item><description><see cref="SettlementOutcome.Failed"/> — the settlement was attempted and did not
    /// happen. It is TERMINAL for that delivery: the receiver logs it, reports it as a failed receive, and does
    /// NOT retry it. The delivery's fate is then whatever the infrastructure's own redelivery rules dictate.</description></item>
    /// </list>
    /// A GENUINELY TRANSIENT settlement fault MUST KEEP THROWING rather than returning
    /// <see cref="SettlementOutcome.Failed"/>. Recovery wraps the settlement call, so a thrown fault is retried
    /// and a returned one is not; converting a deterministic fault to a returned
    /// <see cref="SettlementOutcome.Failed"/> deliberately removes it from the retry path, which is correct only
    /// when retrying it could never succeed.
    /// Every implementation of this interface is contract-tested against all three outcomes (ADR-0010 D7).
    /// </remarks>
    public interface IMessagingInfrastructureReceiver : IAsyncDisposable, IDisposable
    {
        Task<MessageBrokerContext> ReceiveMessageAsync(TransactionContext transactionContext, CancellationToken cancellationToken);

        /// <summary>
        /// Starts receiving messages via the message broker infrastructure
        /// </summary>
        Task InitializeAsync(ReceiverOptions options, CancellationToken cancellationToken);

        Task StopReceiver();

        /// <summary>
        /// Acknowledges <paramref name="context"/>'s delivery, so the infrastructure removes it from the receiving path.
        /// </summary>
        /// <param name="context">The delivery to acknowledge.</param>
        /// <param name="transactionContext">The transaction the delivery is being handled under.</param>
        /// <param name="cancellationToken">The receiver's token, cancelled when the receiver is torn down.</param>
        /// <returns>
        /// <see cref="SettlementResult.Settled"/> when the infrastructure acknowledged the delivery;
        /// <see cref="SettlementResult.NotRequired"/> when there was nothing to acknowledge;
        /// <see cref="SettlementResult.Failed"/> when the acknowledgement was attempted and did not happen.
        /// </returns>
        /// <remarks>See the settlement contract on <see cref="IMessagingInfrastructureReceiver"/>.</remarks>
        Task<SettlementResult> AckMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken);

        /// <summary>
        /// Negatively acknowledges <paramref name="context"/>'s delivery, so the infrastructure returns it for redelivery.
        /// </summary>
        /// <param name="context">The delivery to negatively acknowledge.</param>
        /// <param name="transactionContext">The transaction the delivery is being handled under.</param>
        /// <param name="cancellationToken">The receiver's token, cancelled when the receiver is torn down.</param>
        /// <returns>
        /// <see cref="SettlementResult.Settled"/> when the infrastructure returned the delivery for redelivery;
        /// <see cref="SettlementResult.NotRequired"/> when there was nothing to return;
        /// <see cref="SettlementResult.Failed"/> when the negative acknowledgement was attempted and did not happen.
        /// </returns>
        /// <remarks>See the settlement contract on <see cref="IMessagingInfrastructureReceiver"/>.</remarks>
        Task<SettlementResult> NackMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken);

        /// <summary>
        /// Deadletters <paramref name="context"/>'s delivery, so the infrastructure moves it off the receiving path
        /// for inspection instead of redelivering it.
        /// </summary>
        /// <param name="context">The delivery to deadletter.</param>
        /// <param name="transactionContext">The transaction the delivery is being handled under.</param>
        /// <param name="deadLetterReason">Why the delivery is being deadlettered.</param>
        /// <param name="deadLetterErrorDescription">The detail behind <paramref name="deadLetterReason"/>.</param>
        /// <param name="cancellationToken">The receiver's token, cancelled when the receiver is torn down.</param>
        /// <returns>
        /// <see cref="SettlementResult.Settled"/> when the infrastructure deadlettered the delivery;
        /// <see cref="SettlementResult.NotRequired"/> when there was nothing to deadletter;
        /// <see cref="SettlementResult.Failed"/> when the deadlettering was attempted and did not happen.
        /// </returns>
        /// <remarks>See the settlement contract on <see cref="IMessagingInfrastructureReceiver"/>.</remarks>
        Task<SettlementResult> DeadletterMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, string deadLetterReason, string deadLetterErrorDescription, CancellationToken cancellationToken);

        /// <summary>
        /// Whether this infrastructure writes a failed delivery to the Error Queue ITSELF, so the receiver must not
        /// run its own error-recovery action for that delivery.
        /// </summary>
        /// <remarks>
        /// A SEPARATE CONCERN from the settlement outcome, and deliberately not derived from it. An infrastructure
        /// that owns the Error Queue write — RabbitMQ's error-only configuration republishes the delivery to the
        /// Error Queue as part of deadlettering it — has still SETTLED the delivery, so keying the receiver's
        /// error-recovery action on the settlement outcome would make the receiver write a SECOND copy of the same
        /// delivery to the same Error Queue. Declaring the two independently is what lets an infrastructure report
        /// a truthful settlement outcome AND suppress the duplicate write; before this member existed, an
        /// infrastructure could only suppress it by MISREPORTING the settlement as not having happened.
        /// Defaulted to <c>false</c>, which is the majority behaviour: the receiver owns the Error Queue write.
        /// </remarks>
        bool WritesToErrorQueue => false;

        /// <summary>
        /// How many times <paramref name="context"/>'s delivery has been received, read from the
        /// <see cref="MessageContext.ReceiveAttempts"/> the infrastructure stored on it.
        /// </summary>
        /// <param name="context">The delivery to count. May be null.</param>
        /// <param name="cancellationToken">The receiver's token, cancelled when the receiver is torn down.</param>
        /// <returns>
        /// The stored count when Receive Attempts is present AND is an integral value — <see cref="sbyte"/>,
        /// <see cref="byte"/>, <see cref="short"/>, <see cref="ushort"/>, <see cref="int"/>, <see cref="uint"/>,
        /// <see cref="long"/> or <see cref="ulong"/> — lying within <c>[0, int.MaxValue]</c>; otherwise
        /// <see cref="int.MaxValue"/>, the dead-letter sentinel. The sentinel is NOT a reserved value: a stored count
        /// that genuinely holds <see cref="int.MaxValue"/> is returned as itself, and nothing downstream can tell the
        /// two apart. Both sit at or above every configurable MaxReceiveAttempts, so both deadletter — see
        /// <see cref="FailureContext.DeliveryCount"/>, which is where the value reaches application code.
        /// </returns>
        /// <remarks>
        /// INVARIANT: this probe is TOTAL. It answers with the dead-letter sentinel — a count no configured
        /// MaxReceiveAttempts can sit above — for a null context, a null message, an absent Receive Attempts, a null
        /// Receive Attempts, a non-integral one (<see cref="decimal"/>, <see cref="double"/>, <see cref="float"/>, a
        /// string, a byte[]), or an integral one outside <c>[0, int.MaxValue]</c>. Any integral width IS this
        /// infrastructure's own count, because Receive Attempts is stamped as an <see cref="int"/> but arrives as a
        /// <see cref="long"/> once a delivery has been replayed from an outbox.
        /// <see cref="Sending.OutboundBrokeredMessage"/> tolerates that same replayed width when it reads the key back,
        /// but it does NOT apply this rule and must not be read as doing so: it converts through
        /// <see cref="Convert.ToInt32(object)"/>, which accepts shapes this probe refuses — a numeric string, a
        /// <see cref="bool"/>, a rounded <see cref="double"/> or <see cref="decimal"/> — and which THROWS on an
        /// unparsable or out-of-range value. The two are deliberately not one rule: only this probe is awaited inside
        /// the error ladder, where a throw is unaffordable.
        /// INVARIANT: the range is checked BEFORE the value is narrowed. A narrowing conversion of an out-of-range
        /// integral throws <see cref="OverflowException"/>, and a probe that throws is the exact fault this member is
        /// written to avoid.
        /// It answers instead of throwing because the Brokered Message Receiver awaits it INSIDE the generic
        /// processing-error recovery ladder, BEFORE the ladder decides between negatively acknowledging and
        /// deadlettering the delivery. A probe that threw there aborted the settlement the ladder was about to make,
        /// so the delivery was neither negatively acknowledged nor deadlettered: the infrastructure redelivered it,
        /// the next delivery hit the same unreadable header, and the very probe meant to detect an exhausted delivery
        /// was the thing preventing it — forever, while the receiver went on reporting healthy.
        /// The sentinel — rather than a count of zero, which would redeliver just as endlessly — routes such a
        /// delivery to the deadletter branch on its FIRST failure. That is the only escape observable from here:
        /// application properties arrive off the wire with no type guarantee, and this interface carries no logger to
        /// report the unusable value through, so the delivery must leave the receiving path where it can be
        /// inspected.
        /// </remarks>
        Task<int> MessageDeliveryCountAsync(MessageBrokerContext context, CancellationToken cancellationToken)
        {
            var messageContext = context?.BrokeredMessage?.MessageContext;

            if (messageContext == null
                || !messageContext.TryGetValue(MessageContext.ReceiveAttempts, out var storedReceiveAttempts))
            {
                return Task.FromResult(int.MaxValue);
            }

            var deliveryCount = storedReceiveAttempts switch
            {
                sbyte attempts => attempts >= 0 ? attempts : int.MaxValue,
                byte attempts => attempts,
                short attempts => attempts >= 0 ? attempts : int.MaxValue,
                ushort attempts => attempts,
                int attempts => attempts >= 0 ? attempts : int.MaxValue,
                uint attempts => attempts <= int.MaxValue ? (int)attempts : int.MaxValue,
                long attempts => attempts >= 0 && attempts <= int.MaxValue ? (int)attempts : int.MaxValue,
                ulong attempts => attempts <= int.MaxValue ? (int)attempts : int.MaxValue,
                _ => int.MaxValue,
            };

            return Task.FromResult(deliveryCount);
        }

        TransactionScope CreateLocalTransaction(TransactionContext context)
            => null;
    }
}
