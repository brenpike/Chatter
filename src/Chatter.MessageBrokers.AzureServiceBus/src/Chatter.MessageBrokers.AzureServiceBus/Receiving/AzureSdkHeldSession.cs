using Azure.Messaging.ServiceBus;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.AzureServiceBus.Receiving
{
    /// <summary>
    /// The production <see cref="IServiceBusHeldSession"/>: a pass-through over ONE accepted
    /// <see cref="ServiceBusSessionReceiver"/>. It is a seam, not a layer — it holds no state of its own beyond
    /// the wrapped receiver, and every member forwards to it unchanged.
    /// </summary>
    internal sealed class AzureSdkHeldSession : IServiceBusHeldSession
    {
        private readonly ServiceBusSessionReceiver _sessionReceiver;

        internal AzureSdkHeldSession(ServiceBusSessionReceiver sessionReceiver)
            => _sessionReceiver = sessionReceiver ?? throw new ArgumentNullException(nameof(sessionReceiver));

        public string SessionId => _sessionReceiver.SessionId;

        public DateTimeOffset SessionLockedUntil => _sessionReceiver.SessionLockedUntil;

        public bool IsClosed => _sessionReceiver.IsClosed;

        public ServiceBusSessionReceiver SdkSessionReceiver => _sessionReceiver;

        public Task<ServiceBusReceivedMessage> ReceiveMessageAsync(TimeSpan maxWaitTime, CancellationToken cancellationToken)
            => _sessionReceiver.ReceiveMessageAsync(maxWaitTime, cancellationToken);

        public Task CompleteMessageAsync(ServiceBusReceivedMessage message)
            => _sessionReceiver.CompleteMessageAsync(message);

        public Task AbandonMessageAsync(ServiceBusReceivedMessage message, IDictionary<string, object> propertiesToModify)
            => _sessionReceiver.AbandonMessageAsync(message, propertiesToModify);

        public Task DeadLetterMessageAsync(ServiceBusReceivedMessage message, string deadLetterReason, string deadLetterErrorDescription)
            => _sessionReceiver.DeadLetterMessageAsync(message, deadLetterReason, deadLetterErrorDescription);

        public Task RenewSessionLockAsync(CancellationToken cancellationToken)
            => _sessionReceiver.RenewSessionLockAsync(cancellationToken);

        public Task CloseAsync() => _sessionReceiver.CloseAsync();
    }
}
