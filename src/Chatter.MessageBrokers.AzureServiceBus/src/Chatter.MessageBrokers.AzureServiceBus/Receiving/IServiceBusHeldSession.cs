using Azure.Messaging.ServiceBus;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.AzureServiceBus.Receiving
{
    /// <summary>
    /// Internal port over the ONE held session an <see cref="AzureSdkSessionMessageReceiverAdapter"/> works
    /// through: the session's facts, the receive it serves, the settlements it owes, its lock renewal and its
    /// close. It is exactly the surface the adapter uses and no larger, so acquiring and releasing a session
    /// is drivable from a test with no live Azure Service Bus namespace —
    /// <see cref="ServiceBusSessionReceiver"/> is sealed, has no accessible constructor and no
    /// <see cref="ServiceBusModelFactory"/> entry point, so it cannot be faked directly.
    /// </summary>
    internal interface IServiceBusHeldSession
    {
        /// <summary>The held session's SessionId.</summary>
        string SessionId { get; }

        /// <summary>The instant the held session's lock expires. Advances after each successful renewal.</summary>
        DateTimeOffset SessionLockedUntil { get; }

        /// <summary>True once the underlying session receiver has been closed.</summary>
        bool IsClosed { get; }

        /// <summary>
        /// The SDK session receiver behind this held session, or null when there is none (a test fake).
        /// </summary>
        /// <remarks>
        /// INVARIANT: this exists SOLELY so the adapter can keep answering the concrete
        /// <see cref="ServiceBusSessionReceiver"/> from <c>HeldSessionReceiver</c> and
        /// <see cref="IServiceBusSessionMessageReceiver.SessionReceiverFor(ServiceBusReceivedMessage)"/>. Those
        /// answers go into the transaction <c>Container</c>, which keys by the STATIC type of what it is handed,
        /// and the PUBLIC session-state extension resolves them by that same concrete type — so widening either
        /// answer to this port would silently break every session-state call.
        /// </remarks>
        ServiceBusSessionReceiver SdkSessionReceiver { get; }

        Task<ServiceBusReceivedMessage> ReceiveMessageAsync(TimeSpan maxWaitTime, CancellationToken cancellationToken);
        Task CompleteMessageAsync(ServiceBusReceivedMessage message);
        Task AbandonMessageAsync(ServiceBusReceivedMessage message, IDictionary<string, object> propertiesToModify);
        Task DeadLetterMessageAsync(ServiceBusReceivedMessage message, string deadLetterReason, string deadLetterErrorDescription);
        Task RenewSessionLockAsync(CancellationToken cancellationToken);
        Task CloseAsync();
    }
}
