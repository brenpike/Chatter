using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Options;

namespace Chatter.MessageBrokers.Context
{
    public sealed class TransactionContext : IContainContext
    {
        public TransactionContext(string transactionReceiver)
            : this(transactionReceiver, TransactionMode.ReceiveOnly)
        { }

        public TransactionContext(string transactionReceiver, TransactionMode transactionMode)
        {
            if (string.IsNullOrWhiteSpace(transactionReceiver))
            {
                throw new System.ArgumentException($"A receiver is required to create a {nameof(TransactionContext)}", nameof(transactionReceiver));
            }

            TransactionReceiver = transactionReceiver;
            TransactionMode = transactionMode;
        }

        public string TransactionReceiver { get; } = null;
        public TransactionMode TransactionMode { get; }
        public ContextContainer Container { get; } = new ContextContainer();
    }
}
