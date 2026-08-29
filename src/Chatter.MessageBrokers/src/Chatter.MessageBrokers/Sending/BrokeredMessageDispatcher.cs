using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.Events;
using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Diagnostics;
using Chatter.MessageBrokers.Receiving;
using Chatter.MessageBrokers.Routing;
using Chatter.MessageBrokers.Routing.Options;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Sending
{
    class BrokeredMessageDispatcher : IBrokeredMessageDispatcher
    {
        private readonly IRouteBrokeredMessages _messageRouter;
        private readonly IForwardMessages _forwarder;
        private readonly IBrokeredMessageAttributeDetailProvider _brokeredMessageDetailProvider;
        private readonly IBodyConverterFactory _bodyConverterFactory;
        private readonly IMessageIdGenerator _messageIdGenerator;

        public BrokeredMessageDispatcher(IRouteBrokeredMessages messageRouter,
                                         IForwardMessages forwarder,
                                         IBrokeredMessageAttributeDetailProvider brokeredMessageDetailProvider,
                                         IBodyConverterFactory bodyConverterFactory,
                                         IMessageIdGenerator messageIdGenerator)
        {
            _messageRouter = messageRouter ?? throw new ArgumentNullException(nameof(messageRouter));
            _forwarder = forwarder ?? throw new ArgumentNullException(nameof(forwarder));
            _brokeredMessageDetailProvider = brokeredMessageDetailProvider ?? throw new ArgumentNullException(nameof(brokeredMessageDetailProvider));
            _bodyConverterFactory = bodyConverterFactory ?? throw new ArgumentNullException(nameof(bodyConverterFactory));
            _messageIdGenerator = messageIdGenerator ?? throw new ArgumentNullException(nameof(messageIdGenerator));
        }

        /// <inheritdoc/>
        public Task Send<TMessage>(TMessage message, string destinationPath, TransactionContext transactionContext = null, SendOptions options = null) where TMessage : ICommand
            => Dispatch(new[] { message }, transactionContext, options ?? new SendOptions(), destinationPath);

        /// <inheritdoc/>
        public Task Send<TMessage>(TMessage message, string destinationPath, IMessageHandlerContext messageHandlerContext, SendOptions options = null) where TMessage : ICommand
           => Send(message, destinationPath, messageHandlerContext?.GetTransactionContext(), MergeSendOptionsWithMessageContext(messageHandlerContext, options));

        /// <inheritdoc/>
        public Task Send<TMessage>(TMessage message, TransactionContext transactionContext = null, SendOptions options = null) where TMessage : ICommand
            => Dispatch(new[] { message }, transactionContext, options ?? new SendOptions());

        /// <inheritdoc/>
        public Task Send<TMessage>(TMessage message, IMessageHandlerContext messageHandlerContext, SendOptions options = null) where TMessage : ICommand
            => Send(message, messageHandlerContext?.GetTransactionContext(), MergeSendOptionsWithMessageContext(messageHandlerContext, options));

        /// <inheritdoc/>
        public Task Publish<TMessage>(TMessage message, string destinationPath, TransactionContext transactionContext = null, PublishOptions options = null) where TMessage : IEvent
            => Dispatch(new[] { message }, transactionContext, options ?? new PublishOptions(), destinationPath);

        /// <inheritdoc/>
        public Task Publish<TMessage>(TMessage message, string destinationPath, IMessageHandlerContext messageHandlerContext, PublishOptions options = null) where TMessage : IEvent
            => Publish(message, destinationPath, messageHandlerContext?.GetTransactionContext(), MergePublishOptionsWithMessageContext(messageHandlerContext, options));

        /// <inheritdoc/>
        public Task Publish<TMessage>(TMessage message, TransactionContext transactionContext = null, PublishOptions options = null) where TMessage : IEvent
            => Dispatch(new[] { message }, transactionContext, options ?? new PublishOptions());

        /// <inheritdoc/>
        public Task Publish<TMessage>(TMessage message, IMessageHandlerContext messageHandlerContext, PublishOptions options = null) where TMessage : IEvent
            => Publish(message, messageHandlerContext?.GetTransactionContext(), MergePublishOptionsWithMessageContext(messageHandlerContext, options));

        /// <inheritdoc/>
        public Task Publish<TMessage>(IEnumerable<TMessage> messages, TransactionContext transactionContext = null, PublishOptions options = null) where TMessage : IEvent
            => Dispatch(messages, transactionContext, options ?? new PublishOptions());

        /// <inheritdoc/>
        public Task Publish<TMessage>(IEnumerable<TMessage> messages, IMessageHandlerContext messageHandlerContext, PublishOptions options = null) where TMessage : IEvent
            => Publish(messages, messageHandlerContext?.GetTransactionContext(), MergePublishOptionsWithMessageContext(messageHandlerContext, options));

        public Task Forward(InboundBrokeredMessage inboundBrokeredMessage, string forwardDestination, TransactionContext transactionContext)
            => _forwarder.Route(inboundBrokeredMessage, forwardDestination, transactionContext);

        public Task Forward(string forwardDestination, IMessageBrokerContext context)
            => Forward(context.BrokeredMessage, forwardDestination, context?.GetTransactionContext());

        Task Dispatch<TMessage, TOptions>(IEnumerable<TMessage> messages, TransactionContext transactionContext, TOptions options, string destinationPath = null)
        where TMessage : IMessage
        where TOptions : RoutingOptions, new()
        {
            // INVARIANT: ADR-0010 R1/R4 - Chatter's own off-guard is what decides. The off path is the on path minus
            // the payload: every off-path diagnostics call it reaches is a documented branch-and-return that
            // allocates nothing, reads no start timestamp, starts no span, and writes no trace-context header, and
            // this path itself adds no async state machine when an application has not opted into broker
            // diagnostics.
            if (!BrokerDiagnostics.IsEnabled)
            {
                var outbounds = Dispatch(messages, destinationPath, options, traceContextActivity: null, yieldedMessageCounter: null);
                options.MessageContext.TryGetValue(MessageContext.InfrastructureType, out var infraType);
                return _messageRouter.Route(outbounds, transactionContext, (string)infraType);
            }

            return DispatchWithDiagnostics(messages, transactionContext, options, destinationPath);
        }

        /// <summary>
        /// Dispatches with one send span covering the whole call. The span is started HERE, in the eager overload,
        /// because the overload below is lazy (ADR-0010 D7: one span per dispatch call, tagged with the batch count,
        /// since all N messages share one context dictionary and a per-message trace context is not representable).
        /// </summary>
        /// <remarks>
        /// INVARIANT: the batch count is derived from the ONE enumeration the router already performs, never from a
        /// walk, copy or count of the caller's own sequence. A caller that supplies a lazily-built sequence therefore
        /// sees it enumerated at exactly the same moment, with the same side effects and the same outbox-transaction
        /// scoping, whether or not diagnostics are on. The count is consequently unknown until the router is done, so
        /// the span carries it at STOP, and a dispatch that faults mid-enumeration records the messages actually
        /// yielded rather than an eagerly pre-walked total.
        /// </remarks>
        private async Task DispatchWithDiagnostics<TMessage, TOptions>(IEnumerable<TMessage> messages, TransactionContext transactionContext, TOptions options, string destinationPath)
        where TMessage : IMessage
        where TOptions : RoutingOptions, new()
        {
            options.MessageContext.TryGetValue(MessageContext.InfrastructureType, out var infraType);

            // The Messaging Infrastructure the routing options name is the only messaging-system identity this package
            // has; when the options carry none the tag is left unset rather than given an invented value.
            var messagingSystem = (string)infraType;
            var startTimestamp = Stopwatch.GetTimestamp();
            var yieldedMessageCounter = new StrongBox<int>();
            Exception failure = null;

            // The span starts with a count of zero because nothing has been enumerated yet; the tag is rewritten with
            // the yielded count below, before the span stops, and no listener can observe the placeholder because the
            // count tag is set after StartActivity has already made its sampling decision.
            using (var sendActivity = BrokerDiagnostics.StartSend(messagingSystem, BrokerDiagnostics.OperationTypes.Send, destinationPath, messageCount: 0))
            {
                // ADR-0010 D9/R3: head sampling makes StartSend return null while Chatter .NET ActivityListeners are
                // still attached, and a sampled-out span must not break the trace for a downstream hop that samples
                // independently - so propagation falls back to the ambient context. Reading Activity.Current is legal
                // ONLY here, inside Chatter's own HasListeners guard; it is never the off-guard itself (ADR-0010 R2),
                // because it is non-null in any host running unrelated instrumentation.
                var traceContextActivity = sendActivity ?? (BrokerDiagnostics.Source.HasListeners() ? Activity.Current : null);

                try
                {
                    await _messageRouter.Route(Dispatch(messages, destinationPath, options, traceContextActivity, yieldedMessageCounter), transactionContext, messagingSystem).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    failure = e;
                    BrokerDiagnostics.RecordFailure(sendActivity, e);
                    throw;
                }
                finally
                {
                    sendActivity?.SetTag(BrokerDiagnostics.BatchMessageCount, yieldedMessageCounter.Value);
                    BrokerDiagnostics.RecordSend(startTimestamp, messagingSystem, BrokerDiagnostics.OperationTypes.Send, destinationPath, yieldedMessageCounter.Value, failure);
                }
            }
        }

        // INVARIANT: this overload is a `yield return` ITERATOR. Its body does not run when it is called - it runs when
        // the result is ENUMERATED, which happens inside the router, and for an outbox-routed send inside the outbox's
        // own enumeration. That is why the span is started by the eager overload above and the activity is passed in
        // EXPLICITLY: an Activity.Current lookup here would read whatever activity happened to be current at
        // ENUMERATION time rather than at dispatch time, and would violate ADR-0010 R2. A future change that makes
        // this method eager must keep passing the activity explicitly rather than reaching for ambient state.
        // `yieldedMessageCounter` is the send span's batch count, counted HERE because this is the only place every
        // message of the batch is walked; it is null whenever broker diagnostics are off, so the counting costs one
        // null check per message on that path.
        IEnumerable<OutboundBrokeredMessage> Dispatch<TMessage, TOptions>(IEnumerable<TMessage> messages, string destinationPath, TOptions options, Activity traceContextActivity, StrongBox<int> yieldedMessageCounter)
        where TMessage : IMessage
        where TOptions : RoutingOptions, new()
        {
            if (options == null)
            {
                options = new TOptions();
            }

            if (string.IsNullOrWhiteSpace(options.ContentType))
            {
                throw new ArgumentNullException(nameof(options.ContentType), "Message content type is required");
            }

            var converter = _bodyConverterFactory.CreateBodyConverter(options.ContentType);

            foreach (var message in messages)
            {
                var destination = string.IsNullOrWhiteSpace(destinationPath)
                    ? _brokeredMessageDetailProvider.GetMessageName(message.GetType())
                    : destinationPath;

                if (string.IsNullOrWhiteSpace(destination))
                {
                    throw new ArgumentNullException(nameof(destination), $"Routing destination is required. Use {typeof(BrokeredMessageAttribute).Name} or overload that accepts 'destinationPath'");
                }

                OutboundBrokeredMessage outbound;

                if (string.IsNullOrWhiteSpace(options.MessageId))
                {
                    outbound = new OutboundBrokeredMessage(_messageIdGenerator, message, options.MessageContext, destination, converter);
                }
                else
                {
                    outbound = new OutboundBrokeredMessage(options.MessageId, message, options.MessageContext, destination, converter);
                }

                // The trace context is written at OutboundBrokeredMessage CREATION rather than at the router:
                // IRouteBrokeredMessages is DI-replaced by the outbox routers, so writing it at the router would drop
                // trace context for every outbox-routed send. Written here it is already inside the MessageContext the
                // outbox persists, so it survives store-and-forward (ADR-0010 D5 and the ADR's Propagation scope).
                // It OVERWRITES: MergeSendOptionsWithMessageContext copies an inbound context outward, so this hop's
                // traceparent replaces the upstream one in the copy. That happens ONLY when `traceContextActivity` is
                // non-null; it is null whenever broker diagnostics are off, and also on the metrics-only and
                // sampled-out-with-no-ambient-activity paths. Inject then writes nothing and the inbound traceparent
                // rides out unchanged, deliberately, because stripping it would be a wire write the application never
                // opted into (ADR-0010 R2; see TraceContextPropagator.SetTraceContextValue).
                TraceContextPropagator.Inject(traceContextActivity, outbound.MessageContext);

                if (yieldedMessageCounter != null)
                {
                    yieldedMessageCounter.Value++;
                }

                yield return outbound;
            }
        }

        private SendOptions MergeSendOptionsWithMessageContext(IMessageHandlerContext messageHandlerContext, SendOptions options)
            => SendOptions.Create(messageHandlerContext?.GetInboundBrokeredMessage()?.MessageContextImpl).Merge(options);

        private PublishOptions MergePublishOptionsWithMessageContext(IMessageHandlerContext messageHandlerContext, PublishOptions options)
            => PublishOptions.Create(messageHandlerContext?.GetInboundBrokeredMessage()?.MessageContextImpl).Merge(options);
    }
}
