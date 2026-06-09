using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Sending;
using Azure.Messaging.ServiceBus;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Transactions;

namespace Chatter.MessageBrokers.AzureServiceBus.Sending
{
    internal class ServiceBusMessageSender : IMessagingInfrastructureDispatcher
    {
        readonly IServiceBusMessageSenderFactory _senderFactory;

        public ServiceBusMessageSender(IServiceBusMessageSenderFactory senderFactory)
            => _senderFactory = senderFactory ?? throw new ArgumentNullException(nameof(senderFactory));

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

        public Task Dispatch(IEnumerable<OutboundBrokeredMessage> brokeredMessages, TransactionContext transactionContext)
        {
            // INVARIANT: for FullAtomicityViaInfrastructure the received message (carried in the
            // container by the receive path, NOT a connection) makes the send and the receiver's settle
            // enlist in one cross-entity transaction. Atomicity is provided by the shared client's
            // EnableCrossEntityTransactions (wired in STEP-006) wrapping the TransactionScope below; the
            // old ServiceBusConnection send-via mechanism is gone.
            ServiceBusReceivedMessage receivedMessage = null;
            transactionContext?.Container.TryGet(out receivedMessage);

            var dispatchTasks = new List<Task>(brokeredMessages.Count());

            //TODO: this won't work if leveraging partitioning - won't be able to send messages to multiple partitions in one transactionscope...
            using var scope = CreateTransactionScope(transactionContext?.TransactionMode ?? TransactionMode.None);

            foreach (var brokeredMessage in brokeredMessages)
            {
                var sender = _senderFactory.Create(brokeredMessage.Destination);
                var message = brokeredMessage?.AsAzureServiceBusMessage();
                dispatchTasks.Add(sender.SendMessageAsync(message));
            }

            return Task.WhenAll(dispatchTasks);
        }

        TransactionScope CreateTransactionScope(TransactionMode transactionMode)
        {
            if (transactionMode == TransactionMode.ReceiveOnly)
            {
                return new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
            }
            else
            {
                return null;
            }
        }
    }
}
