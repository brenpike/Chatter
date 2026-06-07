using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;

namespace Chatter.MessageBrokers.Tests.Receiving.Fakes
{
    /// <summary>
    /// In-memory test double for <see cref="IMessagingInfrastructureReceiver"/>.
    /// Enqueue <see cref="MessageBrokerContext"/> instances via <see cref="Enqueue"/> before
    /// starting the receiver loop; the double drains them in order and signals
    /// <see cref="Drained"/> when the last message has been returned.
    /// </summary>
    public sealed class InMemoryMessagingInfrastructureReceiver : IMessagingInfrastructureReceiver
    {
        private readonly ConcurrentQueue<MessageBrokerContext> _messageQueue = new ConcurrentQueue<MessageBrokerContext>();
        private readonly List<ReceiverCall> _callLog = new List<ReceiverCall>();
        private readonly TaskCompletionSource<bool> _drainedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _expectedMessageCount;
        private int _deliveryCount;
        private int _receiveCallCount;
        private bool _disposed;

        /// <param name="expectedMessageCount">
        /// Number of messages the test will enqueue; <see cref="Drained"/> completes after this
        /// many <see cref="ReceiveMessageAsync"/> calls return a non-null context.
        /// </param>
        public InMemoryMessagingInfrastructureReceiver(int expectedMessageCount)
        {
            if (expectedMessageCount < 0)
                throw new ArgumentOutOfRangeException(nameof(expectedMessageCount));
            _expectedMessageCount = expectedMessageCount;
            _deliveryCount = 1;

            // INVARIANT: if zero messages are expected the queue is already drained.
            if (_expectedMessageCount == 0)
                _drainedTcs.TrySetResult(true);
        }

        // ------------------------------------------------------------------ public test API

        /// <summary>
        /// Ordered log of calls made by the receiver loop. Returns a locked snapshot so test-thread
        /// reads never touch the backing <see cref="List{T}"/> while the loop is mutating it under
        /// <see cref="RecordCall"/>'s lock (<see cref="List{T}"/> is not safe for concurrent read/write).
        /// </summary>
        public IReadOnlyList<ReceiverCall> CallLog
        {
            get
            {
                lock (_callLog)
                    return _callLog.ToArray();
            }
        }

        /// <summary>
        /// Completes when <see cref="ReceiveMessageAsync"/> has returned the configured number
        /// of non-null contexts. Tests await this instead of sleeping.
        /// </summary>
        public Task Drained => _drainedTcs.Task;

        /// <summary>
        /// Sets the value returned by <see cref="MessageDeliveryCountAsync"/> for every call.
        /// Default is 1.
        /// </summary>
        public int DeliveryCount
        {
            get => _deliveryCount;
            set => _deliveryCount = value;
        }

        /// <summary>Enqueues a message to be returned by the next <see cref="ReceiveMessageAsync"/> call.</summary>
        public void Enqueue(MessageBrokerContext context)
        {
            if (context is null) throw new ArgumentNullException(nameof(context));
            _messageQueue.Enqueue(context);
        }

        // ------------------------------------------------------------------ IMessagingInfrastructureReceiver

        public Task InitializeAsync(ReceiverOptions options, CancellationToken cancellationToken)
        {
            RecordCall(ReceiverCall.Initialize);
            return Task.CompletedTask;
        }

        public async Task<MessageBrokerContext> ReceiveMessageAsync(TransactionContext transactionContext, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_messageQueue.TryDequeue(out var context))
            {
                RecordCall(ReceiverCall.Receive);

                int callCount = Interlocked.Increment(ref _receiveCallCount);
                if (callCount >= _expectedMessageCount)
                    _drainedTcs.TrySetResult(true);

                return context;
            }

            // Queue empty: block until cancellation instead of returning null synchronously.
            // Returning null here would let the receiver loop spin-call back into this method,
            // pegging CPU and growing CallLog unbounded while the test winds down. Awaiting the
            // token parks the loop until cts.Cancel(), at which point the OCE unwinds it cleanly.
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return null; // unreachable: the await above always throws on cancellation.
        }

        public Task<bool> AckMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken)
        {
            RecordCall(ReceiverCall.Ack);
            return Task.FromResult(true);
        }

        public Task<bool> NackMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, CancellationToken cancellationToken)
        {
            RecordCall(ReceiverCall.Nack);
            return Task.FromResult(true);
        }

        public Task<bool> DeadletterMessageAsync(MessageBrokerContext context, TransactionContext transactionContext, string deadLetterReason, string deadLetterErrorDescription, CancellationToken cancellationToken)
        {
            RecordCall(ReceiverCall.Deadletter);
            return Task.FromResult(true);
        }

        public Task StopReceiver()
        {
            RecordCall(ReceiverCall.Stop);
            return Task.CompletedTask;
        }

        /// <summary>Returns <see cref="DeliveryCount"/> for every call, bypassing the default-impl read of MessageContext.</summary>
        public Task<int> MessageDeliveryCountAsync(MessageBrokerContext context, CancellationToken cancellationToken)
            => Task.FromResult(_deliveryCount);

        /// <summary>Returns null so the loop's <c>localTransaction?.Complete()</c> is a no-op.</summary>
        public TransactionScope CreateLocalTransaction(TransactionContext context)
            => null;

        // ------------------------------------------------------------------ IAsyncDisposable / IDisposable

        public ValueTask DisposeAsync()
        {
            RecordCallOnce(ReceiverCall.Dispose);
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            RecordCallOnce(ReceiverCall.Dispose);
        }

        // ------------------------------------------------------------------ helpers

        private void RecordCall(ReceiverCall call)
        {
            lock (_callLog)
                _callLog.Add(call);
        }

        private void RecordCallOnce(ReceiverCall call)
        {
            if (_disposed) return;
            _disposed = true;
            RecordCall(call);
        }
    }

    /// <summary>Identifies a method call on <see cref="InMemoryMessagingInfrastructureReceiver"/>.</summary>
    public enum ReceiverCall
    {
        Initialize,
        Receive,
        Ack,
        Nack,
        Deadletter,
        Stop,
        Dispose
    }
}
