using Chatter.MessageBrokers.Context;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Receiving
{
    /// <summary>
    /// The message broker infrastructure used to receive messages
    /// </summary>
    public interface IMessagingInfrastructureReceiver : IAsyncDisposable, IDisposable
    {
        Task<MessageBrokerContext> ReceiveMessageAsync(TransactionContext transactionContext, CancellationToken cancellationToken);

        /// <summary>
        /// Starts receiving messages via the message broker infrastructure
        /// </summary>
        Task InitializeAsync(ReceiverOptions options, CancellationToken cancellationToken);

        Task StopReceiver();

        Task AckMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken);
        Task NackMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken);
        Task DeadletterMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, string deadLetterReason, string deadLetterErrorDescription, CancellationToken cancellationToken);

        IDisposable BeginTransaction(TransactionContext transactionContext);
        void RollbackTransaction(TransactionContext transactionContext);
        void CompleteTransaction(TransactionContext transactionContext);

        Task<int> CurrentMessageDeliveryCountAsync(MessageBrokerContext context, CancellationToken cancellationToken);
    }
}
