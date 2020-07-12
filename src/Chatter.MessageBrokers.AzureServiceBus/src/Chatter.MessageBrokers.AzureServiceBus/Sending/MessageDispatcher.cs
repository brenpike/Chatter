using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Options;
using Chatter.MessageBrokers.Sending;
using Microsoft.Azure.ServiceBus;
using System;
using System.Threading.Tasks;
using System.Transactions;

namespace Chatter.MessageBrokers.AzureServiceBus.Sending
{
    internal class MessageDispatcher : IBrokeredMessageDispatcher
    {
        readonly BrokeredMessageSenderPool _pool;

        public MessageDispatcher(BrokeredMessageSenderPool messageSenderPool)
        {
            _pool = messageSenderPool ?? throw new ArgumentNullException(nameof(messageSenderPool));
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

            ServiceBusConnection connection = null;
            transactionContext?.Container.TryGet(out connection);
            var sender = _pool.GetSender(brokeredMessage.Destination, (connection, transactionContext?.TransactionReceiver));

            try
            {
                var message = brokeredMessage.AsAzureServiceBusMessage();
                using var scope = CreateTransactionScope(transactionContext?.TransactionMode ?? TransactionMode.None);
                return sender.SendAsync(message);
            }
            finally
            {
                _pool.ReturnSender(sender);
            }
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
