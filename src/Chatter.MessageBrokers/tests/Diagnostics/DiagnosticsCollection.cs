using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.Events;
using Chatter.MessageBrokers.Configuration;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Recovery;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Routing.Options;
using Chatter.MessageBrokers.Sending;
using Chatter.MessageBrokers.Tests.Receiving.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Xunit;

namespace Chatter.MessageBrokers.Tests.Diagnostics
{
    /// <summary>
    /// Serialises every diagnostics test in this assembly onto one xunit collection.
    /// </summary>
    /// <remarks>
    /// This is correctness, not tidiness. A .NET <c>ActivityListener</c> and a .NET <c>MeterListener</c> are
    /// PROCESS-GLOBAL and the Chatter source and meter names are fixed literals, so an opted-in test running
    /// concurrently with an absence test would let the absence test observe the opted-in test's .NET listener and
    /// fail intermittently. The definition MUST live in this test assembly: xunit v2 discovers collection
    /// definitions only in the assembly under run, which is why <c>Chatter.Testing.Core</c> deliberately declares none.
    /// </remarks>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class DiagnosticsCollection
    {
        /// <summary>The collection name every diagnostics test class is attributed with.</summary>
        public const string Name = "chatter-diagnostics";
    }

    /// <summary>A Command whose dispatch to broker infrastructure is observed by the diagnostics tests.</summary>
    public sealed class TracedCommand : ICommand
    {
        public string Value { get; set; }
    }

    /// <summary>An Event whose dispatch to broker infrastructure is observed by the diagnostics tests.</summary>
    public sealed class TracedEvent : IEvent
    {
        public string Value { get; set; }
    }

    /// <summary>The message a <see cref="DiagnosticsReceiveHarness"/> delivers, deserialisable from a JSON body.</summary>
    public sealed class TracedDelivery : IMessage
    {
        public string Value { get; set; }
    }

    /// <summary>The exception a handler stand-in raises so failure spans and settlement tags can be observed.</summary>
    public sealed class DiagnosticsProbeException : Exception
    {
        public DiagnosticsProbeException(string message)
            : base(message)
        { }
    }

    /// <summary>
    /// A real <see cref="BrokeredMessageDispatcher"/> over a capturing Router, so the send-side diagnostics tests
    /// exercise the whole dispatch path rather than a stand-in for it.
    /// </summary>
    /// <remarks>
    /// Declared here rather than in its own file because it is shared by the off-state, send and foreign-SDK test
    /// classes, which is also part of the set this file's collection definition serialises.
    /// </remarks>
    public sealed class DiagnosticsSendHarness
    {
        /// <summary>The Messaging Infrastructure identity the routing options name; the <c>messaging.system</c> tag.</summary>
        public const string MessagingSystem = "diagnostics-infrastructure";

        /// <summary>The destination every dispatch in these tests targets.</summary>
        public const string DestinationPath = "diagnostics-destination";

        /// <summary>The <see cref="DispatchTimeline"/> entry recorded the moment the Router is handed the sequence.</summary>
        public const string RouterEnteredEntry = "router-entered";

        private readonly List<string> _dispatchTimeline = new List<string>();
        private readonly bool _routerEnumerates;
        private readonly Mock<IRouteBrokeredMessages> _messageRouter = new Mock<IRouteBrokeredMessages>();
        private readonly Mock<IForwardMessages> _forwarder = new Mock<IForwardMessages>();
        private readonly Mock<IBrokeredMessageAttributeDetailProvider> _detailProvider = new Mock<IBrokeredMessageAttributeDetailProvider>();
        private readonly Mock<IBodyConverterFactory> _bodyConverterFactory = new Mock<IBodyConverterFactory>();
        private readonly Mock<IMessageIdGenerator> _idGenerator = new Mock<IMessageIdGenerator>();
        private readonly BrokeredMessageDispatcher _dispatcher;

        /// <param name="routerEnumerates">
        /// Whether the Router walks the sequence it is handed. A Router that does NOT — an outbox router that
        /// persists the sequence for later, for instance — is the case in which nothing is ever yielded.
        /// </param>
        /// <param name="attributeDestinations">
        /// The destinations the <see cref="BrokeredMessageAttribute"/> detail provider resolves, one per resolution in
        /// order, for the overloads that omit an explicit destination. Null resolves <see cref="DestinationPath"/>
        /// every time; a list SHORTER than the batch repeats its last entry, so a two-entry list makes a batch
        /// heterogeneous. This is what makes an attribute-routed batch's destination observable at all.
        /// </param>
        public DiagnosticsSendHarness(bool routerEnumerates = true, IReadOnlyList<string> attributeDestinations = null)
        {
            _routerEnumerates = routerEnumerates;

            var bodyConverter = new JsonBodyConverter();
            _bodyConverterFactory.Setup(factory => factory.CreateBodyConverter(It.IsAny<string>())).Returns(bodyConverter);

            if (attributeDestinations is null)
            {
                _detailProvider.Setup(provider => provider.GetMessageName(It.IsAny<Type>())).Returns(DestinationPath);
            }
            else
            {
                var resolutionCount = 0;
                _detailProvider
                    .Setup(provider => provider.GetMessageName(It.IsAny<Type>()))
                    .Returns(() => attributeDestinations[Math.Min(resolutionCount++, attributeDestinations.Count - 1)]);
            }

            _idGenerator.Setup(generator => generator.GenerateId(It.IsAny<byte[]>())).Returns(() => Guid.NewGuid());

            _messageRouter
                .Setup(router => router.Route(It.IsAny<IEnumerable<OutboundBrokeredMessage>>(), It.IsAny<TransactionContext>(), It.IsAny<string>()))
                .Callback<IEnumerable<OutboundBrokeredMessage>, TransactionContext, string>(CaptureRoutedMessages)
                .Returns(Task.CompletedTask);

            _dispatcher = new BrokeredMessageDispatcher(
                _messageRouter.Object,
                _forwarder.Object,
                _detailProvider.Object,
                _bodyConverterFactory.Object,
                _idGenerator.Object);
        }

        /// <summary>The sequence handed to the Router, captured WITHOUT being enumerated.</summary>
        /// <remarks>
        /// <c>BrokeredMessageDispatcher.Dispatch</c> is a <c>yield return</c> iterator whose body runs at
        /// enumeration time inside the Router, so the type of this sequence is what proves the trace context is
        /// injected lazily rather than eagerly.
        /// </remarks>
        public IEnumerable<OutboundBrokeredMessage> RoutedSequence { get; private set; }

        /// <summary>The messages the Router materialised, in routing order.</summary>
        public IReadOnlyList<OutboundBrokeredMessage> RoutedMessages { get; private set; } = new OutboundBrokeredMessage[0];

        /// <summary>The Messaging Infrastructure identity the Router was told to route to.</summary>
        public string RoutedInfrastructureType { get; private set; }

        /// <summary>
        /// The ordered record of Router entry and of every pull a <see cref="SinglePassEventSequence"/> built here
        /// received, so the two can be compared for ORDER rather than merely for count.
        /// </summary>
        public IReadOnlyList<string> DispatchTimeline => _dispatchTimeline.ToArray();

        public Task SendOne() => _dispatcher.Send(new TracedCommand { Value = "one" }, DestinationPath, (TransactionContext)null, BuildSendOptions());

        public Task PublishBatch(int messageCount)
        {
            var messages = new List<TracedEvent>(messageCount);

            for (var index = 0; index < messageCount; index++)
            {
                messages.Add(new TracedEvent { Value = "event-" + index });
            }

            return _dispatcher.Publish(messages, (TransactionContext)null, BuildPublishOptions());
        }

        /// <summary>
        /// Builds a lazily-pulled publish batch wired to this harness's <see cref="DispatchTimeline"/>.
        /// </summary>
        /// <param name="messageCount">How many messages the batch yields before completing.</param>
        /// <param name="faultAfterYieldCount">How many messages to yield before raising, or -1 to yield them all.</param>
        public SinglePassEventSequence CreateSinglePassBatch(int messageCount, int faultAfterYieldCount = -1)
            => new SinglePassEventSequence(messageCount, faultAfterYieldCount, _dispatchTimeline);

        /// <summary>
        /// Publishes a caller-supplied sequence, so the batch stays exactly as lazy as the caller built it all the
        /// way into the Router. <see cref="PublishBatch"/> hands over an already-materialised list and therefore
        /// cannot show whether anything walked the caller's own sequence.
        /// </summary>
        public Task PublishSequence(IEnumerable<TracedEvent> messages)
            => _dispatcher.Publish(messages, (TransactionContext)null, BuildPublishOptions());

        /// <summary>The routed message's context keys, ordered, so two runs can be compared for wire equality.</summary>
        public IReadOnlyList<string> RoutedContextKeys(int messageIndex = 0)
            => RoutedMessages[messageIndex].MessageContext.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();

        private static SendOptions BuildSendOptions()
        {
            var options = new SendOptions();
            options.UseMessagingInfrastructure(_ => MessagingSystem);
            return options;
        }

        private static PublishOptions BuildPublishOptions()
        {
            var options = new PublishOptions();
            options.UseMessagingInfrastructure(_ => MessagingSystem);
            return options;
        }

        // The materialisation happens HERE, inside the Router, because that is where it happens in production.
        private void CaptureRoutedMessages(IEnumerable<OutboundBrokeredMessage> outboundMessages, TransactionContext transactionContext, string infrastructureType)
        {
            _dispatchTimeline.Add(RouterEnteredEntry);
            RoutedSequence = outboundMessages;
            RoutedInfrastructureType = infrastructureType;

            if (!_routerEnumerates)
            {
                return;
            }

            RoutedMessages = outboundMessages.ToArray();
        }
    }

    /// <summary>
    /// A publish batch that is built as it is pulled, records every pull onto a shared timeline, and REFUSES a
    /// second enumeration.
    /// </summary>
    /// <remarks>
    /// This shape is what makes eager materialisation STRUCTURALLY detectable rather than merely unasserted.
    /// Instrumentation that walked the caller's own sequence to obtain a batch count would either request its
    /// enumerator BEFORE the Router was entered — visible as an out-of-order <see cref="DiagnosticsSendHarness.DispatchTimeline"/>
    /// — or leave the Router's own enumeration to be the SECOND one, which this type rejects outright. It also moves
    /// the caller's iterator side effects, so a batch that raises partway proves where the exception originated.
    /// Declared beside <see cref="DiagnosticsSendHarness"/> because the harness is what wires it to a timeline.
    /// </remarks>
    public sealed class SinglePassEventSequence : IEnumerable<TracedEvent>
    {
        /// <summary>The timeline entry recorded each time an enumerator is asked for.</summary>
        public const string EnumeratorRequestedEntry = "sequence-enumerator-requested";

        /// <summary>The timeline entry prefix recorded per yielded message; the zero-based index is appended.</summary>
        public const string YieldedEntryPrefix = "sequence-yielded-";

        private readonly int _messageCount;
        private readonly int _faultAfterYieldCount;
        private readonly List<string> _dispatchTimeline;
        private int _enumeratorRequestCount;
        private int _yieldedCount;

        internal SinglePassEventSequence(int messageCount, int faultAfterYieldCount, List<string> dispatchTimeline)
        {
            _messageCount = messageCount;
            _faultAfterYieldCount = faultAfterYieldCount;
            _dispatchTimeline = dispatchTimeline;

            Fault = faultAfterYieldCount < 0
                ? null
                : new DiagnosticsProbeException($"The publish batch failed deliberately after {faultAfterYieldCount} message(s).");
        }

        /// <summary>The exception this batch raises mid-enumeration, or <c>null</c> when it yields its whole batch.</summary>
        public DiagnosticsProbeException Fault { get; }

        /// <summary>How many times an enumerator was asked for; anything above one has already thrown.</summary>
        public int EnumeratorRequestCount => _enumeratorRequestCount;

        /// <summary>How many messages were actually handed out.</summary>
        public int YieldedCount => _yieldedCount;

        public IEnumerator<TracedEvent> GetEnumerator()
        {
            _dispatchTimeline.Add(EnumeratorRequestedEntry);
            _enumeratorRequestCount++;

            // Raised HERE rather than from the iterator body below, so that a count, copy or `ToList` of this batch
            // fails at the call itself rather than being silently tolerated until someone pulls from it.
            if (_enumeratorRequestCount > 1)
            {
                throw new InvalidOperationException(
                    "The publish batch was enumerated more than once, so something walked it besides the Router.");
            }

            return YieldMessages();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private IEnumerator<TracedEvent> YieldMessages()
        {
            for (var index = 0; index < _messageCount; index++)
            {
                if (_yieldedCount == _faultAfterYieldCount)
                {
                    throw Fault;
                }

                _dispatchTimeline.Add(YieldedEntryPrefix + index);
                _yieldedCount++;

                yield return new TracedEvent { Value = "event-" + index };
            }
        }
    }

    /// <summary>
    /// A real <see cref="BrokeredMessageReceiver{TMessage}"/> over the in-memory Messaging Infrastructure fakes, so
    /// the receive-side diagnostics tests exercise the per-delivery worker seam the instrumentation actually sits on.
    /// </summary>
    public sealed class DiagnosticsReceiveHarness : IDisposable
    {
        /// <summary>The receiving path every delivery in these tests arrives on; the <c>messaging.destination.name</c> tag.</summary>
        public const string ReceiverPath = "diagnostics-queue";

        /// <summary>The Messaging Infrastructure identity the receiver options name; the <c>messaging.system</c> tag.</summary>
        public const string MessagingSystem = InMemoryMessagingInfrastructureProvider.InfrastructureType;

        private readonly InMemoryMessagingInfrastructureReceiver _infrastructureReceiver;
        private readonly Mock<IReceivedMessageDispatcher> _receivedMessageDispatcher = new Mock<IReceivedMessageDispatcher>();
        private readonly BrokeredMessageReceiver<TracedDelivery> _receiver;
        private readonly int _failedDispatchCount;
        private readonly int _maxReceiveAttempts;
        private readonly string _infrastructureType;
        private int _dispatchCount;

        /// <param name="failedDispatchCount">How many leading dispatches raise a <see cref="DiagnosticsProbeException"/>.</param>
        /// <param name="maxRecoveryAttempts">How many times Recovery re-runs a failing dispatch before giving up.</param>
        /// <param name="deliveryCount">The delivery count the infrastructure reports, which selects Nack versus Deadletter.</param>
        /// <param name="maxReceiveAttempts">The receiver's configured maximum delivery count.</param>
        /// <param name="infrastructureType">
        /// The Messaging Infrastructure identity the receiver options name. Defaults to <see cref="MessagingSystem"/>,
        /// so a test that does not name one is unaffected; a BLANK value is the receiver configured without one.
        /// </param>
        public DiagnosticsReceiveHarness(int failedDispatchCount = 0, int maxRecoveryAttempts = 1, int deliveryCount = 1, int maxReceiveAttempts = 10, string infrastructureType = MessagingSystem)
        {
            _failedDispatchCount = failedDispatchCount;
            _maxReceiveAttempts = maxReceiveAttempts;
            _infrastructureType = infrastructureType;
            _infrastructureReceiver = new InMemoryMessagingInfrastructureReceiver(expectedMessageCount: 1) { DeliveryCount = deliveryCount };

            _receivedMessageDispatcher
                .Setup(dispatcher => dispatcher.DispatchAsync(It.IsAny<TracedDelivery>(), It.IsAny<MessageBrokerContext>(), It.IsAny<CancellationToken>()))
                .Returns<TracedDelivery, MessageBrokerContext, CancellationToken>((payload, messageContext, token) => DispatchNextAsync(messageContext));

            var messageBrokerOptions = new MessageBrokerOptions();
            messageBrokerOptions.TransactionMode = TransactionMode.None;

            _receiver = new BrokeredMessageReceiver<TracedDelivery>(
                infrastructureProvider: new InMemoryMessagingInfrastructureProvider(_infrastructureReceiver),
                messageBrokerOptions: messageBrokerOptions,
                logger: NullLogger<BrokeredMessageReceiver<TracedDelivery>>.Instance,
                recoveryAction: new Mock<IMaxReceivesExceededAction>().Object,
                criticalFailureNotifier: new Mock<ICriticalFailureNotifier>().Object,
                recoveryStrategy: new AttemptRetryingRecoveryStrategy(maxRecoveryAttempts),
                receivedMessageDispatcher: _receivedMessageDispatcher.Object);
        }

        /// <summary>
        /// Runs while the delivery is being handled, with the same inbound context the handler would see. Used to
        /// stand in for a handler that forwards or replies.
        /// </summary>
        public Action<MessageBrokerContext> OnDispatch { get; set; }

        /// <summary>The ordered calls the receiver made against the Messaging Infrastructure.</summary>
        public IReadOnlyList<ReceiverCall> CallLog => _infrastructureReceiver.CallLog;

        /// <summary>How many times the Received Message Dispatcher was invoked for the delivery.</summary>
        public int DispatchCount => _dispatchCount;

        /// <summary>
        /// Arms the Messaging Infrastructure so every acknowledgement raises <paramref name="ackFailure"/>, which is
        /// the delivery whose HANDLING SUCCEEDED and whose settlement then failed. The receiver swallows that fault
        /// into a <c>bool</c>, so it never leaves the worker's processing block and the exception-filter choke point
        /// cannot observe it (ADR-0010 D11).
        /// </summary>
        public void ArmAckFailure(Exception ackFailure) => _infrastructureReceiver.ArmAckFailure(ackFailure);

        /// <summary>
        /// Arms the Messaging Infrastructure so every acknowledgement RETURNS <paramref name="ackOutcome"/> rather
        /// than raising, which is how an infrastructure reports that it settled nothing or that the settlement it
        /// attempted did not happen. A returned failure never leaves the worker's processing block either, so it is
        /// retained at the same single swallow site the raised one is (ADR-0010 D11).
        /// </summary>
        public void ArmAckOutcome(SettlementResult ackOutcome) => _infrastructureReceiver.ArmAckOutcome(ackOutcome);

        /// <summary>
        /// Arms the Messaging Infrastructure to hand the receiver a real local transaction, so
        /// <see cref="LocalTransactionStatus"/> reports whether the delivery's settlement completed it.
        /// </summary>
        public void ArmLocalTransaction() => _infrastructureReceiver.ArmLocalTransaction();

        /// <summary>
        /// Whether the local transaction armed by <see cref="ArmLocalTransaction"/> was committed, aborted, or never
        /// created. Read after the delivery has drained.
        /// </summary>
        public TransactionStatus? LocalTransactionStatus => _infrastructureReceiver.LocalTransactionStatus;

        /// <summary>Queues one delivery carrying <paramref name="messageContextValues"/> as its context.</summary>
        public MessageBrokerContext Deliver(IDictionary<string, object> messageContextValues = null, byte[] body = null)
        {
            var bodyConverter = new JsonBodyConverter();
            var messageContext = new MessageBrokerContext(
                messageId: Guid.NewGuid().ToString(),
                body: body ?? bodyConverter.Convert(new TracedDelivery { Value = "delivered" }),
                applicationProperties: messageContextValues ?? new Dictionary<string, object>(),
                messageReceiverPath: ReceiverPath,
                receiverCancellationToken: CancellationToken.None,
                bodyConverter: bodyConverter);

            _infrastructureReceiver.Enqueue(messageContext);
            return messageContext;
        }

        /// <summary>
        /// Starts the receive loop, waits for <paramref name="expectedSettlement"/> to be recorded against the
        /// Messaging Infrastructure, then shuts the loop down and waits for the in-flight worker to drain.
        /// </summary>
        /// <remarks>
        /// The drain matters for these tests specifically: the per-delivery span is stopped by the worker's own
        /// <c>using</c> scope, which runs AFTER the settlement call, so an assertion made before the drain would
        /// race the span's stop.
        /// </remarks>
        public async Task RunUntilSettledAsync(ReceiverCall expectedSettlement)
        {
            using (var loopCancellation = new CancellationTokenSource())
            using (var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                var receiveLoop = Task.Run(() => _receiver.StartReceiver(BuildReceiverOptions(), loopCancellation.Token));

                while (!CallLog.Contains(expectedSettlement))
                {
                    watchdog.Token.ThrowIfCancellationRequested();
                    await Task.Yield();
                }

                loopCancellation.Cancel();
                await receiveLoop;
            }
        }

        public void Dispose() => _receiver.Dispose();

        private ReceiverOptions BuildReceiverOptions()
            => new ReceiverOptions
            {
                InfrastructureType = _infrastructureType,
                MessageReceiverPath = ReceiverPath,
                SendingPath = ReceiverPath,
                ErrorQueuePath = "diagnostics-error-queue",
                DeadLetterQueuePath = "diagnostics-deadletter-queue",
                TransactionMode = TransactionMode.None,
                MaxReceiveAttempts = _maxReceiveAttempts,
                MaxConcurrentCalls = 1,
            };

        private Task DispatchNextAsync(MessageBrokerContext messageContext)
        {
            var dispatchNumber = Interlocked.Increment(ref _dispatchCount);
            OnDispatch?.Invoke(messageContext);

            if (dispatchNumber <= _failedDispatchCount)
            {
                throw new DiagnosticsProbeException($"The delivery failed deliberately on dispatch {dispatchNumber}.");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// A Recovery strategy that re-runs a failing action up to a fixed number of attempts, which is the shape the
        /// per-delivery attempt count and the retry span events are specified against (ADR-0010 D7).
        /// </summary>
        private sealed class AttemptRetryingRecoveryStrategy : IRecoveryStrategy
        {
            private readonly int _maxAttempts;

            internal AttemptRetryingRecoveryStrategy(int maxAttempts) => _maxAttempts = maxAttempts;

            public async Task<TResult> ExecuteAsync<TResult>(Func<Task<TResult>> action, CancellationToken token)
            {
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        return await action().ConfigureAwait(false);
                    }
                    catch (Exception) when (attempt < _maxAttempts && !token.IsCancellationRequested)
                    {
                        // Retried below. Shutdown cancellation is never retried, so teardown stays prompt.
                    }
                }
            }
        }
    }
}
