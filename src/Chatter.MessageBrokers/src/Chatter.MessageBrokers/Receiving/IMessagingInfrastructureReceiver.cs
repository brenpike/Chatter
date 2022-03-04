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
    public interface IMessagingInfrastructureReceiver : IAsyncDisposable, IDisposable
    {
        Task<MessageBrokerContext> ReceiveMessageAsync(TransactionContext transactionContext, CancellationToken cancellationToken);

        /// <summary>
        /// Starts receiving messages via the message broker infrastructure
        /// </summary>
        Task InitializeAsync(ReceiverOptions options, CancellationToken cancellationToken);

        Task StopReceiver();

        Task<bool> AckMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken);
        Task<bool> NackMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken);
        Task<bool> DeadletterMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, string deadLetterReason, string deadLetterErrorDescription, CancellationToken cancellationToken);
        Task<int> MessageDeliveryCountAsync(MessageBrokerContext context, CancellationToken cancellationToken) => Task.FromResult((int)context?.BrokeredMessage?.MessageContext[MessageContext.ReceiveAttempts]);

        TransactionScope CreateLocalTransaction(TransactionContext context) => null;
    }
}
