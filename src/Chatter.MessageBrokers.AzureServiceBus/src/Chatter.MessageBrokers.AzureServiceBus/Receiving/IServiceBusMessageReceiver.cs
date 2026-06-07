using Microsoft.Azure.ServiceBus;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.AzureServiceBus.Receiving
{
    /// <summary>
    /// Internal port over the operations the <see cref="ServiceBusReceiver"/> needs from an Azure
    /// Service Bus <see cref="Microsoft.Azure.ServiceBus.Core.MessageReceiver"/>. The production
    /// adapter (<see cref="AzureSdkMessageReceiverAdapter"/>) lazily constructs the SDK receiver and
    /// holds the live connection; an in-memory adapter is used to pin receive/ack behavior in tests.
    /// </summary>
    internal interface IServiceBusMessageReceiver
    {
        /// <summary>
        /// The connection backing the receiver, included in the transaction container for
        /// <see cref="Chatter.MessageBrokers.Receiving.TransactionMode.FullAtomicityViaInfrastructure"/>.
        /// </summary>
        ServiceBusConnection ServiceBusConnection { get; }

        /// <summary>True once the underlying receiver has begun closing.</summary>
        bool IsClosedOrClosing { get; }

        Task<Message> ReceiveAsync();
        Task CompleteAsync(string lockToken);
        Task AbandonAsync(string lockToken, IDictionary<string, object> propertiesToModify);
        Task DeadLetterAsync(string lockToken, string deadLetterReason, string deadLetterErrorDescription);
        Task CloseAsync();
    }
}
