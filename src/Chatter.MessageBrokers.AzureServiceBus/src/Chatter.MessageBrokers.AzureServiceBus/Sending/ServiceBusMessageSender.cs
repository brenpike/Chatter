using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using Azure.Messaging.ServiceBus;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Transactions;

namespace Chatter.MessageBrokers.AzureServiceBus.Sending
{
    internal class ServiceBusMessageSender : IMessagingInfrastructureDispatcher
    {
        readonly IServiceBusMessageSenderFactory _senderFactory;
        readonly IDispatchTransactionScopeFactory _scopeFactory;

        // INVARIANT: this is the only PUBLIC constructor. Microsoft.Extensions.DependencyInjection
        // selects among public constructors only, so the scope-factory seam below stays internal to
        // keep activation of the AddScoped<ServiceBusMessageSender>() registration unambiguous.
        public ServiceBusMessageSender(IServiceBusMessageSenderFactory senderFactory)
            : this(senderFactory, new DispatchTransactionScopeFactory())
        {
        }

        internal ServiceBusMessageSender(IServiceBusMessageSenderFactory senderFactory, IDispatchTransactionScopeFactory scopeFactory)
        {
            _senderFactory = senderFactory ?? throw new ArgumentNullException(nameof(senderFactory));
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        }

        public Task Dispatch(OutboundBrokeredMessage brokeredMessage, TransactionContext transactionContext)
        {
            if (brokeredMessage == null)
            {
                throw new ArgumentNullException(nameof(brokeredMessage), $"An outgoing message is required.");
            }

            if (string.IsNullOrWhiteSpace(brokeredMessage.Destination))
            {
                throw new ArgumentNullException(nameof(brokeredMessage.Destination), $"A destination is required.");
            }

            return Dispatch(new[] { brokeredMessage }, transactionContext);
        }

        public async Task Dispatch(IEnumerable<OutboundBrokeredMessage> brokeredMessages, TransactionContext transactionContext)
        {
            // INVARIANT: for FullAtomicityViaInfrastructure the received message (carried in the
            // container by the receive path, NOT a connection) makes the send and the receiver's settle
            // enlist in one cross-entity transaction. Atomicity is provided by the shared client's
            // EnableCrossEntityTransactions (wired in STEP-006) wrapping the TransactionScope below; the
            // old ServiceBusConnection send-via mechanism is gone.
            ServiceBusReceivedMessage receivedMessage = null;
            transactionContext?.Container.TryGet(out receivedMessage);

            // INVARIANT: brokeredMessages is enumerated exactly once, per the single-pass
            // enumeration contract on IMessagingInfrastructureDispatcher.Dispatch. No capacity
            // hint is taken: sizing the list would walk the sequence a second time, re-running a
            // lazy producer's per-yield side effects. Do not reintroduce one.
            var dispatchTasks = new List<Task>();

            // INVARIANT: the scope outlives every send it covers. The sends are awaited INSIDE the using,
            // so the scope is disposed only once all of them have finished; returning the Task.WhenAll
            // unawaited would dispose the scope while sends were still in flight.
            //TODO: this won't work if leveraging partitioning - won't be able to send messages to multiple partitions in one transactionscope...
            using var scope = _scopeFactory.Create(transactionContext?.TransactionMode ?? TransactionMode.None);

            foreach (var brokeredMessage in brokeredMessages)
            {
                var sender = _senderFactory.Create(brokeredMessage.Destination);
                var message = brokeredMessage?.AsAzureServiceBusMessage();
                dispatchTasks.Add(sender.SendMessageAsync(message));
            }

            await Task.WhenAll(dispatchTasks);

            scope.Complete();
        }
    }

    /// <summary>
    /// The transaction scope a single batch dispatch runs under.
    /// </summary>
    internal interface IDispatchTransactionScope : IDisposable
    {
        void Complete();
    }

    /// <summary>
    /// Creates the transaction scope for a batch dispatch from the transaction mode the dispatch runs under.
    /// </summary>
    internal interface IDispatchTransactionScopeFactory
    {
        IDispatchTransactionScope Create(TransactionMode transactionMode);
    }

    internal sealed class DispatchTransactionScopeFactory : IDispatchTransactionScopeFactory
    {
        public IDispatchTransactionScope Create(TransactionMode transactionMode)
            => transactionMode == TransactionMode.ReceiveOnly
                ? (IDispatchTransactionScope)new SuppressedDispatchTransactionScope()
                : InertDispatchTransactionScope.Instance;
    }

    /// <summary>
    /// Keeps a <see cref="TransactionMode.ReceiveOnly"/> dispatch out of the ambient receive transaction.
    /// </summary>
    internal sealed class SuppressedDispatchTransactionScope : IDispatchTransactionScope
    {
        readonly TransactionScope _scope = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);

        public void Complete() => _scope.Complete();

        public void Dispose() => _scope.Dispose();
    }

    /// <summary>
    /// The scope used by every transaction mode other than <see cref="TransactionMode.ReceiveOnly"/>. It
    /// takes no ambient transaction action at all, replacing the null scope the sender used to carry.
    /// </summary>
    internal sealed class InertDispatchTransactionScope : IDispatchTransactionScope
    {
        public static readonly InertDispatchTransactionScope Instance = new InertDispatchTransactionScope();

        InertDispatchTransactionScope()
        {
        }

        public void Complete()
        {
        }

        public void Dispose()
        {
        }
    }
}
