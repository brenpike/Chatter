using Azure.Messaging.ServiceBus;

namespace Chatter.MessageBrokers.AzureServiceBus.Sending
{
    /// <summary>
    /// Internal seam over the construction of an Azure Service Bus <see cref="ServiceBusSender"/>. The
    /// sender is created off the shared <see cref="ServiceBusClient"/>, which carries auth and
    /// EnableCrossEntityTransactions, so the old connection/token branch selection is gone: a single
    /// <see cref="ServiceBusClient.CreateSender(string)"/> call serves every destination. The seam lets
    /// <see cref="ServiceBusMessageSender"/>'s send logic be unit-tested with a sender double instead of
    /// the live sender the real client would otherwise produce.
    /// </summary>
    internal interface IServiceBusMessageSenderFactory
    {
        /// <summary>
        /// Creates a <see cref="ServiceBusSender"/> for <paramref name="destinationEntityPath"/> off the
        /// shared client.
        /// </summary>
        ServiceBusSender Create(string destinationEntityPath);
    }
}
