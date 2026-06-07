using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.ServiceBus.Core;

namespace Chatter.MessageBrokers.AzureServiceBus.Sending
{
    /// <summary>
    /// Internal seam over the construction of an Azure Service Bus <see cref="MessageSender"/>. The
    /// SDK opens a connection on sender construction, so this seam lets the branch-selection logic
    /// (send-via an existing receiver connection; namespace connection string with a
    /// <see cref="NullTokenProvider"/>; endpoint with a token provider) be unit-tested in isolation
    /// from the live connection that the produced sender would otherwise open.
    /// </summary>
    internal interface IServiceBusMessageSenderFactory
    {
        /// <summary>
        /// Creates a <see cref="MessageSender"/> for <paramref name="destinationEntityPath"/>,
        /// selecting the construction branch from <paramref name="receiverConnectionAndPath"/>.
        /// </summary>
        MessageSender Create(string destinationEntityPath, (ServiceBusConnection connection, string sendViaPath) receiverConnectionAndPath);
    }
}
