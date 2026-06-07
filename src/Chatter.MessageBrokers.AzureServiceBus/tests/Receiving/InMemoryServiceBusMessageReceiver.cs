using Chatter.MessageBrokers.AzureServiceBus.Receiving;
using Microsoft.Azure.ServiceBus;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Receiving
{
    // In-memory IServiceBusMessageReceiver double used to drive ServiceBusReceiver's receive/ack
    // paths without a live Azure Service Bus namespace. Receive results (including null) are queued;
    // ack/nack/deadletter lock tokens are recorded; IsClosedOrClosing is toggleable; and a single
    // transient ServiceBusException or an ObjectDisposedException can be injected on the next receive.
    internal class InMemoryServiceBusMessageReceiver : IServiceBusMessageReceiver
    {
        private readonly Queue<Func<Message>> _receiveResults = new Queue<Func<Message>>();

        public List<string> CompletedLockTokens { get; } = new List<string>();
        public List<string> AbandonedLockTokens { get; } = new List<string>();
        public List<(string lockToken, string reason, string description)> DeadLetteredLockTokens { get; }
            = new List<(string, string, string)>();
        public List<IDictionary<string, object>> AbandonPropertiesToModify { get; } = new List<IDictionary<string, object>>();

        public int ReceiveCount { get; private set; }
        public int CloseCount { get; private set; }

        public bool IsClosedOrClosing { get; set; }

        public ServiceBusConnection ServiceBusConnection { get; set; }

        public void EnqueueMessage(Message message) => _receiveResults.Enqueue(() => message);

        public void EnqueueNull() => _receiveResults.Enqueue(() => null);

        public void EnqueueThrow(Exception exception) => _receiveResults.Enqueue(() => throw exception);

        public Task<Message> ReceiveAsync()
        {
            ReceiveCount++;
            if (_receiveResults.Count == 0)
            {
                return Task.FromResult<Message>(null);
            }

            var next = _receiveResults.Dequeue();
            return Task.FromResult(next());
        }

        public Task CompleteAsync(string lockToken)
        {
            CompletedLockTokens.Add(lockToken);
            return Task.CompletedTask;
        }

        public Task AbandonAsync(string lockToken, IDictionary<string, object> propertiesToModify)
        {
            AbandonedLockTokens.Add(lockToken);
            AbandonPropertiesToModify.Add(propertiesToModify);
            return Task.CompletedTask;
        }

        public Task DeadLetterAsync(string lockToken, string deadLetterReason, string deadLetterErrorDescription)
        {
            DeadLetteredLockTokens.Add((lockToken, deadLetterReason, deadLetterErrorDescription));
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
