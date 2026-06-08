using Azure.Messaging.ServiceBus;
using System;

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
    internal class AzureSdkMessageSenderFactory : IServiceBusMessageSenderFactory
    {
        // TODO(STEP-006): the shared ServiceBusClient is injected from DI. EnableCrossEntityTransactions
        // is set on the client there so the send + the receiver's settle enlist in one cross-entity
        // transaction within ServiceBusMessageSender's TransactionScope.
        private readonly ServiceBusClient _client;

        public AzureSdkMessageSenderFactory(ServiceBusClient client)
            => _client = client ?? throw new ArgumentNullException(nameof(client));

        public ServiceBusSender Create(string destinationEntityPath)
            => _client.CreateSender(destinationEntityPath);
    }
}
