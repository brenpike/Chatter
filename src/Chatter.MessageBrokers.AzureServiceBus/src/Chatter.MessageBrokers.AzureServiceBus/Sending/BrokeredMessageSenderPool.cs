using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Chatter.MessageBrokers.AzureServiceBus.Options;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Core;

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

        public MessageSender GetMessageSender(string destinationEntityPath, (ServiceBusConnection connection, string sendViaPath) receiverConnectionAndPath)
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

        public void ReturnMessageSender(MessageSender sender)
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

        public Task Close()
        {
            var tasks = new List<Task>();

            foreach (var key in _senders.Keys)
            {
                var queue = _senders[key];

                foreach (var sender in queue)
                {
                    tasks.Add(sender.CloseAsync());
                }
            }

            return Task.WhenAll(tasks);
        }
    }
}
