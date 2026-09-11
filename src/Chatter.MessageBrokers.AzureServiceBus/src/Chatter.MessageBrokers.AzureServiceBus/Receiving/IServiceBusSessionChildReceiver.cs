using Azure.Messaging.ServiceBus;
using System;

namespace Chatter.MessageBrokers.AzureServiceBus.Receiving
{
    /// <summary>
    /// Internal surface for a receiver that holds exactly ONE Azure Service Bus session, exposing that one
    /// session's facts. It is SEPARATE from <see cref="IServiceBusSessionMessageReceiver"/> on purpose: a
    /// receiver that holds N sessions at once cannot honestly answer "what is YOUR held session id", so these
    /// single-session members must not sit on the port such a receiver implements. A multi-session receiver
    /// composes children of this shape and answers PER MESSAGE through
    /// <see cref="IServiceBusSessionMessageReceiver.SessionReceiverFor(ServiceBusReceivedMessage)"/> instead.
    /// </summary>
    internal interface IServiceBusSessionChildReceiver : IServiceBusMessageReceiver
    {
        /// <summary>The held SDK session receiver, or null when no session is held.</summary>
        ServiceBusSessionReceiver HeldSessionReceiver { get; }

        /// <summary>The held session's SessionId, or null when no session is held.</summary>
        string HeldSessionId { get; }

        /// <summary>The instant the held session's lock expires, or null when no session is held.</summary>
        DateTimeOffset? HeldSessionLockedUntil { get; }
    }
}
