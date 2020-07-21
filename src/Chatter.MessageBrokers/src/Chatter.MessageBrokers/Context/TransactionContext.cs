using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Options;

namespace Chatter.MessageBrokers.Context
{
    /// <summary>
    /// Contextual information about the message broker transaction
    /// </summary>
    public sealed class TransactionContext : IContainContext
    {
        /// <summary>
        /// Creates an object that contains contextual information about the message broker transaction. The transaction mode
        /// is defaulted to <see cref="TransactionMode.None"/>.
        /// </summary>
        /// <param name="transactionReceiver">The message broker receiver</param>
        public TransactionContext(string transactionReceiver)
            : this(transactionReceiver, TransactionMode.None)
        { }

        /// <summary>
        /// Creates an object that contains contextual information about the message broker transaction
        /// </summary>
        /// <param name="transactionReceiver">The message broker receiver</param>
        /// <param name="transactionMode">The transaction mode</param>
        public TransactionContext(string transactionReceiver, TransactionMode transactionMode)
        {
            if (string.IsNullOrWhiteSpace(transactionReceiver))
            {
                throw new System.ArgumentException($"A receiver is required to create a {nameof(TransactionContext)}", nameof(transactionReceiver));
            }

            TransactionReceiver = transactionReceiver;
            TransactionMode = transactionMode;
        }

        /// <summary>
        /// The message broker receiver that is part of the transaction
        /// </summary>
        public string TransactionReceiver { get; } = null;
        /// <summary>
        /// The mode the of the transaction
        /// </summary>
        public TransactionMode TransactionMode { get; }
        public ContextContainer Container { get; } = new ContextContainer();
    }
}
