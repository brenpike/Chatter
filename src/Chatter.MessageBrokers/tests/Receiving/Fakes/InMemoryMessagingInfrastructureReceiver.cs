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

        // INVARIANT: optional opt-in gate that holds InitializeAsync until released, making the receiver's Starting
        // lifecycle window (after the SUT assigns _infrastructureReceiver, before go-live) observable to a test. Default
        // null so the many existing tests that construct this fake are unaffected; only the startup-window test sets it.
        private TaskCompletionSource<bool> _initializeGate;

        // INVARIANT: optional opt-in latch that makes the FIRST infra teardown call (StopReceiver or DisposeAsync) throw
        // exactly once, then behave normally on subsequent calls. Lets the fault-resettable-retry test force the first
        // quiesce body to throw and assert a later teardown re-runs cleanup. Default 0 (disarmed) so existing tests are
        // unaffected; armed via ArmThrowOnceOnTeardown.
        private int _throwOnceOnTeardownArmed;

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

        /// <summary>
        /// Arms an opt-in gate that holds <see cref="InitializeAsync"/> until <see cref="ReleaseInitializeGate"/> is
        /// called, so a test can pin the receiver in its Starting lifecycle window (infra receiver assigned, not yet
        /// live) and exercise a teardown landing there. Records <see cref="ReceiverCall.Initialize"/> on entry as usual.
        /// </summary>
        public Task ArmInitializeGate()
        {
            _initializeGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _initializeGate.Task;
        }

        /// <summary>Releases the gate armed by <see cref="ArmInitializeGate"/> so <see cref="InitializeAsync"/> returns.</summary>
        public void ReleaseInitializeGate() => _initializeGate?.TrySetResult(true);

        /// <summary>
        /// Arms a one-shot latch so the FIRST <see cref="StopReceiver"/> or <see cref="DisposeAsync"/> call throws an
        /// <see cref="InvalidOperationException"/>, then subsequent teardown calls behave normally. Used to drive the
        /// fault-resettable-retry path: the first quiesce body faults and a later teardown must re-run and dispose.
        /// </summary>
        public void ArmThrowOnceOnTeardown() => Interlocked.Exchange(ref _throwOnceOnTeardownArmed, 1);

        // ------------------------------------------------------------------ IMessagingInfrastructureReceiver

        public async Task InitializeAsync(ReceiverOptions options, CancellationToken cancellationToken)
        {
            RecordCall(ReceiverCall.Initialize);

            // When a test arms the gate, hold here — the SUT has already assigned _infrastructureReceiver and advanced to
            // the Starting lifecycle state but has not reached go-live, so the test can observe and tear down that window.
            var gate = _initializeGate;
            if (gate != null)
            {
                await gate.Task.ConfigureAwait(false);
            }
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
            ThrowOnceIfArmed();
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
            ThrowOnceIfArmed();
            RecordCallOnce(ReceiverCall.Dispose);
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            RecordCallOnce(ReceiverCall.Dispose);
        }

        // ------------------------------------------------------------------ helpers

        // INVARIANT: if the throw-once latch is armed, atomically disarm it and throw on this single call; otherwise no-op.
        // Interlocked.Exchange makes exactly one teardown call throw even under a concurrent teardown race.
        private void ThrowOnceIfArmed()
        {
            if (Interlocked.Exchange(ref _throwOnceOnTeardownArmed, 0) == 1)
            {
                throw new InvalidOperationException("Simulated infrastructure teardown failure (throw-once).");
            }
        }

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
