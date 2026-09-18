using Chatter.MessageBrokers.AzureServiceBus.Receiving;
using Azure.Messaging.ServiceBus;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Receiving
{
    // In-memory IServiceBusMessageReceiver double used to drive ServiceBusReceiver's receive/ack
    // paths without a live Azure Service Bus namespace. Receive results (including null) are queued;
    // ack/nack/deadletter settle by the received MESSAGE OBJECT and are recorded, as are delivery
    // releases; IsClosedOrClosing is
    // toggleable; and a single transient ServiceBusException or an ObjectDisposedException can be
    // injected on the next receive.
    internal class InMemoryServiceBusMessageReceiver : IServiceBusMessageReceiver
    {
        private readonly Queue<Func<ServiceBusReceivedMessage>> _receiveResults = new Queue<Func<ServiceBusReceivedMessage>>();

        public List<ServiceBusReceivedMessage> CompletedMessages { get; } = new List<ServiceBusReceivedMessage>();
        public List<ServiceBusReceivedMessage> AbandonedMessages { get; } = new List<ServiceBusReceivedMessage>();
        public List<(ServiceBusReceivedMessage message, string reason, string description)> DeadLetteredMessages { get; }
            = new List<(ServiceBusReceivedMessage, string, string)>();
        public List<IDictionary<string, object>> AbandonPropertiesToModify { get; } = new List<IDictionary<string, object>>();

        // The deliveries the worker signalled it was finished with, recorded so a test can assert the release
        // reached a NON-SESSION inner receiver — the case the old session-only fork excluded.
        public List<ServiceBusReceivedMessage> ReleasedDeliveries { get; } = new List<ServiceBusReceivedMessage>();

        public int ReceiveCount { get; private set; }
        public int CloseCount { get; private set; }

        // The cancellation token observed on the most recent ReceiveAsync call, captured so tests can assert
        // that ServiceBusReceiver.ReceiveMessageAsync passes its loop token straight through to the inner port.
        public CancellationToken LastReceiveToken { get; private set; }

        public bool IsClosedOrClosing { get; set; }

        // The outcome every settle call answers, so a test can drive the receiver's mapping of a settlement
        // that never reached the broker as well as the settled path.
        public ServiceBusSettlementOutcome SettlementOutcome { get; set; } = ServiceBusSettlementOutcome.Settled;

        public void EnqueueMessage(ServiceBusReceivedMessage message) => _receiveResults.Enqueue(() => message);

        public void EnqueueNull() => _receiveResults.Enqueue(() => null);

        public void EnqueueThrow(Exception exception) => _receiveResults.Enqueue(() => throw exception);

        public Task<ServiceBusReceivedMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            ReceiveCount++;
            LastReceiveToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            if (_receiveResults.Count == 0)
            {
                return Task.FromResult<ServiceBusReceivedMessage>(null);
            }

            var next = _receiveResults.Dequeue();
            return Task.FromResult(next());
        }

        public Task<ServiceBusSettlementOutcome> CompleteAsync(ServiceBusReceivedMessage message)
        {
            CompletedMessages.Add(message);
            return Task.FromResult(SettlementOutcome);
        }

        public Task<ServiceBusSettlementOutcome> AbandonAsync(ServiceBusReceivedMessage message, IDictionary<string, object> propertiesToModify)
        {
            AbandonedMessages.Add(message);
            AbandonPropertiesToModify.Add(propertiesToModify);
            return Task.FromResult(SettlementOutcome);
        }

        public Task<ServiceBusSettlementOutcome> DeadLetterAsync(ServiceBusReceivedMessage message, string deadLetterReason, string deadLetterErrorDescription)
        {
            DeadLetteredMessages.Add((message, deadLetterReason, deadLetterErrorDescription));
            return Task.FromResult(SettlementOutcome);
        }

        public void DeliveryReleased(ServiceBusReceivedMessage message) => ReleasedDeliveries.Add(message);

        public Task CloseAsync()
        {
            CloseCount++;
            IsClosedOrClosing = true;
            return Task.CompletedTask;
        }
    }
}
