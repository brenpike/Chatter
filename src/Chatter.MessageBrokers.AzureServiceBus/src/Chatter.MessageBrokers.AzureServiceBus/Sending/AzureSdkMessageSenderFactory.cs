using Azure.Messaging.ServiceBus;
using System;
using System.Collections.Concurrent;

namespace Chatter.MessageBrokers.AzureServiceBus.Sending
{
    /// <summary>
    /// Production <see cref="IServiceBusMessageSenderFactory"/> that creates every
    /// <see cref="ServiceBusSender"/> off the shared <see cref="ServiceBusClient"/>. The old three-branch
    /// construction (send-via an existing receiver connection, namespace connection string under a null
    /// token provider, or endpoint + token provider) is collapsed: the shared client carries auth and
    /// EnableCrossEntityTransactions, so a single <see cref="ServiceBusClient.CreateSender(string)"/> call
    /// serves every destination.
    /// </summary>
    /// <remarks>
    /// INVARIANT: a <see cref="ServiceBusSender"/> holds an AMQP link and is intended to be reused for the
    /// lifetime of its parent client (Azure SDK guidance). Senders are therefore CACHED per destination and
    /// reused across dispatches rather than created fresh per outbound message — creating a new sender per
    /// message leaks links and can exhaust connections. Each cached sender's lifetime is bound to the shared
    /// singleton <see cref="ServiceBusClient"/>, which disposes its child senders when it is disposed by DI,
    /// so the factory holds no separate disposal responsibility.
    /// </remarks>
    internal class AzureSdkMessageSenderFactory : IServiceBusMessageSenderFactory
    {
        // The shared ServiceBusClient is injected from DI. EnableCrossEntityTransactions is set on the
        // client there so the send + the receiver's settle enlist in one cross-entity transaction within
        // ServiceBusMessageSender's TransactionScope.
        private readonly ServiceBusClient _client;
        private readonly ConcurrentDictionary<string, ServiceBusSender> _senders =
            new ConcurrentDictionary<string, ServiceBusSender>(StringComparer.Ordinal);

        public AzureSdkMessageSenderFactory(ServiceBusClient client)
            => _client = client ?? throw new ArgumentNullException(nameof(client));

        public ServiceBusSender Create(string destinationEntityPath)
            => _senders.GetOrAdd(destinationEntityPath, _client.CreateSender);
    }
}
