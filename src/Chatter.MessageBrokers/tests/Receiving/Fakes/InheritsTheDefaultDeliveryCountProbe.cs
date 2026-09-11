using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Tests.Receiving.Fakes
{
    /// <summary>
    /// Forwards every REQUIRED <see cref="IMessagingInfrastructureReceiver"/> member to an inner
    /// <see cref="InMemoryMessagingInfrastructureReceiver"/> and declares NONE of the optional ones, so every
    /// default interface member — <see cref="IMessagingInfrastructureReceiver.MessageDeliveryCountAsync"/> above
    /// all — is what executes.
    /// </summary>
    /// <remarks>
    /// INVARIANT: this double exists because <see cref="InMemoryMessagingInfrastructureReceiver"/> DECLARES its own
    /// delivery-count probe and is sealed, so it SHADOWS the published default and no test reaching the probe
    /// through it can observe what an infrastructure that says nothing actually inherits. Wrapping rather than
    /// subclassing is the only shape available: it keeps the inner double's arming hooks, enqueued deliveries and
    /// call log while leaving the probe inherited. Do NOT declare the probe here — declaring it would silently turn
    /// every test built on this double into a test of the inner double.
    /// </remarks>
    public sealed class InheritsTheDefaultDeliveryCountProbe : IMessagingInfrastructureReceiver
    {
        public InheritsTheDefaultDeliveryCountProbe(InMemoryMessagingInfrastructureReceiver innerReceiver)
            => InnerReceiver = innerReceiver ?? throw new ArgumentNullException(nameof(innerReceiver));

        /// <summary>The double every forwarded call lands on; tests enqueue deliveries and read the call log here.</summary>
        public InMemoryMessagingInfrastructureReceiver InnerReceiver { get; }

        public Task<MessageBrokerContext> ReceiveMessageAsync(TransactionContext transactionContext, CancellationToken cancellationToken)
            => InnerReceiver.ReceiveMessageAsync(transactionContext, cancellationToken);

        public Task InitializeAsync(ReceiverOptions options, CancellationToken cancellationToken)
            => InnerReceiver.InitializeAsync(options, cancellationToken);

        public Task StopReceiver() => InnerReceiver.StopReceiver();

        public Task<SettlementResult> AckMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken)
            => InnerReceiver.AckMessageAsync(context, transactionContext, cancellationToken);

        public Task<SettlementResult> NackMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken)
            => InnerReceiver.NackMessageAsync(context, transactionContext, cancellationToken);

        public Task<SettlementResult> DeadletterMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, string deadLetterReason, string deadLetterErrorDescription, CancellationToken cancellationToken)
            => InnerReceiver.DeadletterMessageAsync(context, transactionContext, deadLetterReason, deadLetterErrorDescription, cancellationToken);

        public ValueTask DisposeAsync() => InnerReceiver.DisposeAsync();

        public void Dispose() => InnerReceiver.Dispose();
    }
}
