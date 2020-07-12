using Chatter.MessageBrokers.AzureServiceBus.Options;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Core;
using System;
using System.Collections.Concurrent;

namespace Chatter.MessageBrokers.AzureServiceBus.Sending
{
    class BrokeredMessageSenderPool
    {
        readonly ServiceBusConnectionStringBuilder _connectionStringBuilder;
        readonly RetryPolicy _retryPolicy;
        readonly ConcurrentDictionary<(string entityPath, (ServiceBusConnection connnection, string viaEntityPath)), ConcurrentQueue<MessageSender>> _senders;

        public BrokeredMessageSenderPool(ServiceBusOptions serviceBusOptions)
        {
            if (serviceBusOptions == null)
            {
                throw new ArgumentNullException(nameof(serviceBusOptions), $"Service Bus options are required to use {nameof(BrokeredMessageSenderPool)}");
            }

            _senders = new ConcurrentDictionary<(string, (ServiceBusConnection, string)), ConcurrentQueue<MessageSender>>();
            _retryPolicy = serviceBusOptions.Policy;
            _connectionStringBuilder = new ServiceBusConnectionStringBuilder(serviceBusOptions.ConnectionString);
        }

        /// <summary>
        /// Gets a <see cref="MessageSender"/> from the pool
        /// </summary>
        /// <param name="destinationEntityPath">The destination entity path to be used by the sender</param>
        /// <param name="receiverConnectionAndPath">A <see cref="Tuple{T1, T2}"/> containing the <see cref="ServiceBusConnection"/> and the transfer path of the receiver</param>
        /// <returns>A <see cref="MessageSender"/></returns>
        public MessageSender GetSender(string destinationEntityPath, (ServiceBusConnection connection, string sendViaPath) receiverConnectionAndPath)
        {
            var sendersForDestination = _senders.GetOrAdd((destinationEntityPath, receiverConnectionAndPath), _ => new ConcurrentQueue<MessageSender>());

            if (!sendersForDestination.TryDequeue(out var sender) || sender.IsClosedOrClosing)
            {
                if (receiverConnectionAndPath.connection != null && receiverConnectionAndPath.sendViaPath != null)
                {
                    sender = new MessageSender(receiverConnectionAndPath.connection, destinationEntityPath, receiverConnectionAndPath.sendViaPath, _retryPolicy);
                }
                else
                {
                    sender = new MessageSender(_connectionStringBuilder.GetNamespaceConnectionString(), destinationEntityPath, _retryPolicy);
                }
            }

            return sender;
        }

        /// <summary>
        /// Returns a <see cref="MessageSender"/> back to the pool.
        /// </summary>
        /// <param name="sender">The <see cref="MessageSender"/> to be returned</param>
        public void ReturnSender(MessageSender sender)
        {
            if (sender.IsClosedOrClosing)
            {
                return;
            }

            var connectionToUse = sender.OwnsConnection ? null : sender.ServiceBusConnection;
            var destinationPath = sender.OwnsConnection ? sender.Path : sender.TransferDestinationPath;

            if (_senders.TryGetValue((destinationPath, (connectionToUse, sender.ViaEntityPath)), out var sendersForDestination))
            {
                sendersForDestination.Enqueue(sender);
            }
        }
    }
}
