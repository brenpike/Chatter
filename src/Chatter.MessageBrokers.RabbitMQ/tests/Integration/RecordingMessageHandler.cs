using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Context;

namespace Chatter.MessageBrokers.RabbitMQ.Tests.Integration
{
    // What a RecordingMessageHandler<TMessage> captured when Chatter's pipeline invoked it: the deserialized
    // payload and the IMessageBrokerContext the receive path passed to the handler. WaitForHandledAsync returns
    // this so a test can assert on both the message that round-tripped through RabbitMQ and the broker context
    // Chatter built for it. Broker-agnostic — mirrors the SQL Service Broker / Azure Service Bus recorder so the
    // RabbitMQ integration tests read the same way.
    public sealed class HandledRecord<TMessage>
    {
        public HandledRecord(TMessage message, IMessageBrokerContext context)
        {
            Message = message;
            Context = context;
        }

        public TMessage Message { get; }
        public IMessageBrokerContext Context { get; }
    }

    // Shared, harness-owned signal for one message type. Registered as a singleton so the single instance is
    // visible to BOTH the test (which awaits Handled) and the DI-resolved RecordingMessageHandler<TMessage>
    // (which Chatter constructs transiently per receive scope and which signals through this registry).
    //
    // ThrowOnHandle drives the nack-redelivery and deadletter scenarios: when set, the handler throws after
    // recording, so Chatter's receiver loop runs its retry/deadletter path. Handled is still completed in that
    // case so a test can observe that the handler WAS invoked even though it then threw.
    public sealed class HandlerSignal<TMessage>
    {
        private readonly TaskCompletionSource<HandledRecord<TMessage>> _handled =
            new TaskCompletionSource<HandledRecord<TMessage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<HandledRecord<TMessage>> _records =
            new ConcurrentQueue<HandledRecord<TMessage>>();
        private int _invocationCount;

        // When non-null, the handler throws this (a fresh instance per invocation) after recording the
        // message, exercising Chatter's receiver retry/deadletter path.
        public Func<Exception> ThrowOnHandle { get; set; }

        public Task<HandledRecord<TMessage>> Handled => _handled.Task;

        // The total number of times Chatter's pipeline has invoked the handler for TMessage. Lets a test
        // observe redelivery vs a single delivery without holding the transiently-constructed handler instance.
        public int InvocationCount => Volatile.Read(ref _invocationCount);

        // Every captured invocation in arrival order. Unlike Handled (which holds only the FIRST invocation),
        // this lets a test assert on the PAYLOAD or ReceiveAttempts of a later delivery rather than inferring it
        // from the invocation count alone.
        public IReadOnlyCollection<HandledRecord<TMessage>> Records => _records;

        internal void Record(HandledRecord<TMessage> record)
        {
            Interlocked.Increment(ref _invocationCount);
            _records.Enqueue(record);
            _handled.TrySetResult(record);
        }
    }

    // Registry of per-message-type signals, registered as a singleton. The harness creates/looks up the
    // HandlerSignal<TMessage> for a message type; the DI-resolved RecordingMessageHandler<TMessage> resolves
    // this same registry and reports its capture through it. Keying by Type keeps the test's expectation and
    // the handler instance pointed at one shared signal without the test holding the handler instance
    // (Chatter owns construction).
    public sealed class HandlerSignalRegistry
    {
        private readonly ConcurrentDictionary<Type, object> _signals = new ConcurrentDictionary<Type, object>();

        public HandlerSignal<TMessage> GetOrAdd<TMessage>()
            => (HandlerSignal<TMessage>)_signals.GetOrAdd(typeof(TMessage), _ => new HandlerSignal<TMessage>());
    }

    // A Chatter IMessageHandler<TMessage> that records the payload + broker context and signals the shared
    // HandlerSignal<TMessage> from the registry. Registered explicitly by ChatterRabbitMqPipelineHarness as a
    // closed IMessageHandler<TMessage> (not discovered by Chatter's assembly scan, whose IsValidMessageHandler
    // filter excludes open generics), so it participates in the real receive path Chatter drives.
    public sealed class RecordingMessageHandler<TMessage> : IMessageHandler<TMessage> where TMessage : IMessage
    {
        private readonly HandlerSignalRegistry _registry;

        public RecordingMessageHandler(HandlerSignalRegistry registry)
            => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

        public Task Handle(TMessage message, IMessageHandlerContext context)
        {
            var signal = _registry.GetOrAdd<TMessage>();
            signal.Record(new HandledRecord<TMessage>(message, context as IMessageBrokerContext));

            var thrower = signal.ThrowOnHandle;
            if (thrower != null)
            {
                throw thrower();
            }

            return Task.CompletedTask;
        }
    }
}
