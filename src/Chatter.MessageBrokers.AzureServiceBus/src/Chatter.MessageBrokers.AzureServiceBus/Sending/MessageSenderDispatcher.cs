using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Options;
using Chatter.MessageBrokers.Sending;
using Microsoft.Azure.ServiceBus;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Transactions;

namespace Chatter.MessageBrokers.AzureServiceBus.Sending
{
    internal class MessageSenderDispatcher : IBrokeredMessageInfrastructureDispatcher
    {
        readonly BrokeredMessageSenderPool _pool;

        public MessageSenderDispatcher(BrokeredMessageSenderPool messageSenderPool)
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

            return Dispatch(new[] { brokeredMessage }, transactionContext);
        }

        public Task Dispatch(IList<OutboundBrokeredMessage> brokeredMessages, TransactionContext transactionContext)
        {
            ServiceBusConnection connection = null;
            transactionContext?.Container.TryGet(out connection);

            var dispatchTasks = new List<Task>(brokeredMessages.Count);

            //TODO: this won't work if leveraging partitioning - won't be able to send messages to multiple partitions in one transactionscope...
            using var scope = CreateTransactionScope(transactionContext?.TransactionMode ?? TransactionMode.None);

            foreach (var brokeredMessage in brokeredMessages)
            {
                var sender = _pool.GetOrCreate(brokeredMessage.Destination, (connection, transactionContext?.TransactionReceiver));
                try
                {
                    var message = brokeredMessage?.AsAzureServiceBusMessage();
                    dispatchTasks.Add(sender.SendAsync(message));
                }
                finally
                {
                    _pool.Return(sender);
                }
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
