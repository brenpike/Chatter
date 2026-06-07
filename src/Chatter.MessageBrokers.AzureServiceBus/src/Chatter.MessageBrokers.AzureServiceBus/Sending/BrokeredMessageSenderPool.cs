using Chatter.MessageBrokers.AzureServiceBus.Options;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Core;
using System;
using System.Collections.Concurrent;

namespace Chatter.MessageBrokers.AzureServiceBus.Sending
{
    class BrokeredMessageSenderPool
    {
        readonly ConcurrentDictionary<(string entityPath, (ServiceBusConnection connnection, string viaEntityPath)), ConcurrentQueue<MessageSender>> _senders;
        private readonly IServiceBusMessageSenderFactory _senderFactory;

        public BrokeredMessageSenderPool(ServiceBusOptions serviceBusOptions)
            : this(new AzureSdkMessageSenderFactory(serviceBusOptions))
        { }

        // Internal seam ctor: an IServiceBusMessageSenderFactory can be injected so the pool's
        // checkout/return logic is unit-testable without the live connection that a real
        // MessageSender opens on construction.
        internal BrokeredMessageSenderPool(IServiceBusMessageSenderFactory senderFactory)
        {
            _senders = new ConcurrentDictionary<(string, (ServiceBusConnection, string)), ConcurrentQueue<MessageSender>>();
            _senderFactory = senderFactory ?? throw new ArgumentNullException(nameof(senderFactory));
        }

        /// <summary>
        /// Gets a <see cref="MessageSender"/> from the pool
        /// </summary>
        /// <param name="destinationEntityPath">The destination entity path to be used by the sender</param>
        /// <param name="receiverConnectionAndPath">A <see cref="Tuple{T1, T2}"/> containing the <see cref="ServiceBusConnection"/> and the transfer path of the receiver</param>
        /// <returns>A <see cref="MessageSender"/></returns>
        public MessageSender GetOrCreate(string destinationEntityPath, (ServiceBusConnection connection, string sendViaPath) receiverConnectionAndPath)
        {
            var sendersForDestination = _senders.GetOrAdd((destinationEntityPath, receiverConnectionAndPath), _ => new ConcurrentQueue<MessageSender>());

            if (!sendersForDestination.TryDequeue(out var sender) || sender.IsClosedOrClosing)
            {
                sender = _senderFactory.Create(destinationEntityPath, receiverConnectionAndPath);
            }

            return sender;
        }

        /// <summary>
        /// Returns a <see cref="MessageSender"/> back to the pool.
        /// </summary>
        /// <param name="sender">The <see cref="MessageSender"/> to be returned</param>
        public void Return(MessageSender sender)
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
