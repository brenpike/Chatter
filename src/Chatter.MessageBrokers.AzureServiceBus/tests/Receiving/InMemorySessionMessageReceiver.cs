using Chatter.MessageBrokers.AzureServiceBus.Receiving;
using Azure.Messaging.ServiceBus;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.AzureServiceBus.Tests.Receiving
{
    // In-memory IServiceBusSessionChildReceiver double used to drive SessionReceiverMultiplexer deterministically,
    // without a live Azure Service Bus namespace and without wall-clock waits. Every ReceiveAsync call parks on its
    // own TaskCompletionSource which the test completes with a message, with null, or with an exception, so the test
    // decides exactly when a child yields; the token the multiplexer arms with is honored so a cancelled/closed
    // multiplexer's pending receives complete instead of hanging.
    //
    // HeldSessionReceiver is always null: Azure.Messaging.ServiceBus.ServiceBusSessionReceiver is sealed, has no
    // accessible constructor and no ServiceBusModelFactory entry point, so a held SDK session receiver cannot be
    // faked. The multiplexer's SessionReceiverFor is therefore only observable here through its unknown-message
    // answer.
    internal sealed class InMemorySessionMessageReceiver : IServiceBusSessionChildReceiver
    {
        private readonly object _syncLock = new object();
        private readonly Queue<TaskCompletionSource<ServiceBusReceivedMessage>> _outstandingReceives
            = new Queue<TaskCompletionSource<ServiceBusReceivedMessage>>();
        private TaskCompletionSource<bool> _receiveObservedSource
            = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<ServiceBusReceivedMessage> CompletedMessages { get; } = new List<ServiceBusReceivedMessage>();
        public List<ServiceBusReceivedMessage> AbandonedMessages { get; } = new List<ServiceBusReceivedMessage>();
        public List<IDictionary<string, object>> AbandonPropertiesToModify { get; } = new List<IDictionary<string, object>>();
        public List<(ServiceBusReceivedMessage message, string reason, string description)> DeadLetteredMessages { get; }
            = new List<(ServiceBusReceivedMessage, string, string)>();

        public int ReceiveCount { get; private set; }
        public int CloseCount { get; private set; }
        public CancellationToken LastReceiveToken { get; private set; }

        public bool IsClosedOrClosing { get; set; }
        public ServiceBusSessionReceiver HeldSessionReceiver => null;
        public string HeldSessionId { get; set; }
        public DateTimeOffset? HeldSessionLockedUntil { get; set; }

        public int OutstandingReceiveCount
        {
            get
            {
                lock (_syncLock)
                {
                    return _outstandingReceives.Count;
                }
            }
        }

        public Task<ServiceBusReceivedMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            TaskCompletionSource<ServiceBusReceivedMessage> source;
            TaskCompletionSource<bool> observed;
            lock (_syncLock)
            {
                ReceiveCount++;
                LastReceiveToken = cancellationToken;
                source = new TaskCompletionSource<ServiceBusReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                _outstandingReceives.Enqueue(source);
                observed = _receiveObservedSource;
                _receiveObservedSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            cancellationToken.Register(() => source.TrySetCanceled(cancellationToken));
            observed.TrySetResult(true);
            return source.Task;
        }

        public void YieldMessage(ServiceBusReceivedMessage message) => NextOutstandingReceive().TrySetResult(message);

        public void YieldNoMessage() => NextOutstandingReceive().TrySetResult(null);

        public void FailReceive(Exception exception) => NextOutstandingReceive().TrySetException(exception);

        // Waits until ReceiveAsync has been called at least expectedCount times, without polling or sleeping:
        // each call publishes a signal the waiter is already parked on. Throws rather than hanging when the
        // count is never reached, so a failing expectation reports the counts instead of timing out the run.
        public async Task WaitForReceiveCountAsync(int expectedCount, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                Task observed;
                lock (_syncLock)
                {
                    if (ReceiveCount >= expectedCount)
                    {
                        return;
                    }

                    observed = _receiveObservedSource.Task;
                }

                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException($"Expected at least {expectedCount} receive call(s) but observed {ReceiveCount}.");
                }

                var first = await Task.WhenAny(observed, Task.Delay(remaining)).ConfigureAwait(false);
                if (!ReferenceEquals(first, observed))
                {
                    throw new TimeoutException($"Expected at least {expectedCount} receive call(s) but observed {ReceiveCount}.");
                }
            }
        }

        public Task CompleteAsync(ServiceBusReceivedMessage message)
        {
            lock (_syncLock)
            {
                CompletedMessages.Add(message);
            }

            return Task.CompletedTask;
        }

        public Task AbandonAsync(ServiceBusReceivedMessage message, IDictionary<string, object> propertiesToModify)
        {
            lock (_syncLock)
            {
                AbandonedMessages.Add(message);
                AbandonPropertiesToModify.Add(propertiesToModify);
            }

            return Task.CompletedTask;
        }

        public Task DeadLetterAsync(ServiceBusReceivedMessage message, string deadLetterReason, string deadLetterErrorDescription)
        {
            lock (_syncLock)
            {
                DeadLetteredMessages.Add((message, deadLetterReason, deadLetterErrorDescription));
            }

            return Task.CompletedTask;
        }

        public Task CloseAsync()
        {
            lock (_syncLock)
            {
                CloseCount++;
                IsClosedOrClosing = true;
            }

            return Task.CompletedTask;
        }

        private TaskCompletionSource<ServiceBusReceivedMessage> NextOutstandingReceive()
        {
            lock (_syncLock)
            {
                if (_outstandingReceives.Count == 0)
                {
                    throw new InvalidOperationException("No outstanding receive to complete; the multiplexer has not armed this child.");
                }

                return _outstandingReceives.Dequeue();
            }
        }
    }
}
