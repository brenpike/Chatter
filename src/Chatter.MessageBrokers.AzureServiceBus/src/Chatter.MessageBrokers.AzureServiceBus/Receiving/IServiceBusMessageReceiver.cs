using Azure.Messaging.ServiceBus;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.AzureServiceBus.Receiving
{
    /// <summary>
    /// Internal port over the operations the <see cref="ServiceBusReceiver"/> needs from an Azure
    /// Service Bus <see cref="Azure.Messaging.ServiceBus.ServiceBusReceiver"/>. The production adapter
    /// (<see cref="AzureSdkMessageReceiverAdapter"/>) lazily constructs the SDK receiver from a shared
    /// <see cref="ServiceBusClient"/> and recreates it after a close/dispose; an in-memory adapter is
    /// used to pin receive/ack behavior in tests.
    /// </summary>
    /// <remarks>
    /// INVARIANT: settlement is by RECEIVED MESSAGE OBJECT (<see cref="ServiceBusReceivedMessage"/>),
    /// not by lock-token string — the Azure.Messaging.ServiceBus SDK settles against the message that
    /// carries the lock token internally.
    /// </remarks>
    internal interface IServiceBusMessageReceiver
    {
        /// <summary>True once the underlying receiver has been closed (or disposed).</summary>
        bool IsClosedOrClosing { get; }

        Task<ServiceBusReceivedMessage> ReceiveAsync();
        Task CompleteAsync(ServiceBusReceivedMessage message);
        Task AbandonAsync(ServiceBusReceivedMessage message, IDictionary<string, object> propertiesToModify);
        Task DeadLetterAsync(ServiceBusReceivedMessage message, string deadLetterReason, string deadLetterErrorDescription);
        Task CloseAsync();
    }
}
