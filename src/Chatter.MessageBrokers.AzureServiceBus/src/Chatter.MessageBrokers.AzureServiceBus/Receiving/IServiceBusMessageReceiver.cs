using Azure.Messaging.ServiceBus;
using System.Collections.Generic;
using System.Threading;
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

        Task<ServiceBusReceivedMessage> ReceiveAsync(CancellationToken cancellationToken);
        Task<ServiceBusSettlementOutcome> CompleteAsync(ServiceBusReceivedMessage message);
        Task<ServiceBusSettlementOutcome> AbandonAsync(ServiceBusReceivedMessage message, IDictionary<string, object> propertiesToModify);
        Task<ServiceBusSettlementOutcome> DeadLetterAsync(ServiceBusReceivedMessage message, string deadLetterReason, string deadLetterErrorDescription);
        Task CloseAsync();
    }

    /// <summary>
    /// What a settle call on <see cref="IServiceBusMessageReceiver"/> actually did with the delivery it targeted.
    /// </summary>
    /// <remarks>
    /// INVARIANT: no path may answer <see cref="Settled"/> for a settlement that did not reach Azure Service Bus.
    /// A returning settle call is NOT evidence of a settlement — a session adapter whose session was released
    /// before settlement ran reaches no broker at all, and a ReceiveAndDelete delivery owes no settlement in the
    /// first place. Those are DIFFERENT answers, which is why this is three states rather than a bool.
    /// </remarks>
    internal enum ServiceBusSettlementOutcome
    {
        /// <summary>The settlement reached Azure Service Bus and the delivery is settled.</summary>
        Settled,

        /// <summary>No settlement was owed: the delivery was received in ReceiveAndDelete mode.</summary>
        NotOwed,

        /// <summary>
        /// A settlement was owed but the receiver can no longer reach the delivery it targets, so the broker
        /// still holds it. Deterministic — the same call would find the same absence.
        /// </summary>
        DeliveryUnreachable,
    }
}
