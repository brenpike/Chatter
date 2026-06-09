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
    // ack/nack/deadletter settle by the received MESSAGE OBJECT and are recorded; IsClosedOrClosing is
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

        public int ReceiveCount { get; private set; }
        public int CloseCount { get; private set; }

        // The cancellation token observed on the most recent ReceiveAsync call, captured so tests can assert
        // that ServiceBusReceiver.ReceiveMessageAsync passes its loop token straight through to the inner port.
        public CancellationToken LastReceiveToken { get; private set; }

        public bool IsClosedOrClosing { get; set; }

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

        public Task CompleteAsync(ServiceBusReceivedMessage message)
        {
            CompletedMessages.Add(message);
            return Task.CompletedTask;
        }

        public Task AbandonAsync(ServiceBusReceivedMessage message, IDictionary<string, object> propertiesToModify)
        {
            AbandonedMessages.Add(message);
            AbandonPropertiesToModify.Add(propertiesToModify);
            return Task.CompletedTask;
        }

        public Task DeadLetterAsync(ServiceBusReceivedMessage message, string deadLetterReason, string deadLetterErrorDescription)
        {
            DeadLetteredMessages.Add((message, deadLetterReason, deadLetterErrorDescription));
            return Task.CompletedTask;
        }

        public Task CloseAsync()
        {
            CloseCount++;
            IsClosedOrClosing = true;
            return Task.CompletedTask;
        }
    }
}
