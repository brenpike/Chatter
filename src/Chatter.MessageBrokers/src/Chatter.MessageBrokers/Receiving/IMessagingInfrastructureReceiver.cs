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

        Task<int> MessageDeliveryCountAsync(MessageBrokerContext context, CancellationToken cancellationToken)
            => Task.FromResult((int)context?.BrokeredMessage?.MessageContext[MessageContext.ReceiveAttempts]);

        TransactionScope CreateLocalTransaction(TransactionContext context)
            => null;
    }
}
