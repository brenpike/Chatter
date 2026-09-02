using Chatter.MessageBrokers.Diagnostics;
using Chatter.MessageBrokers.Sending;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Outbox
{
    public class OutboxProcessor : IOutboxProcessor
    {
        /// <summary>A drain publishes exactly one row, so the batch count on its send span is always one.</summary>
        private const int DrainedMessageCount = 1;

        private readonly IMessagingInfrastructureProvider _infrastructureProvider;
        private readonly ILogger<OutboxProcessor> _logger;
        private readonly IBodyConverterFactory _bodyConverterFactory;
        private readonly IBrokeredMessageOutbox _brokeredMessageOutbox;

        public OutboxProcessor(IMessagingInfrastructureProvider infrastructureProvider,
                               ILogger<OutboxProcessor> logger,
                               IBodyConverterFactory bodyConverterFactory,
                               IBrokeredMessageOutbox brokeredMessageOutbox)
        {
            _infrastructureProvider = infrastructureProvider ?? throw new ArgumentNullException(nameof(infrastructureProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _bodyConverterFactory = bodyConverterFactory ?? throw new ArgumentNullException(nameof(bodyConverterFactory));
            _brokeredMessageOutbox = brokeredMessageOutbox ?? throw new ArgumentNullException(nameof(brokeredMessageOutbox));
        }

        public async Task Process(OutboxMessage message, CancellationToken cancellationToken = default)
        {
            try
            {
                // The persisted MessageContext is a JSON string whose values are NOT all strings:
                // WithTimeToLive/RefreshTimeToLive write a TimeSpan, Azure Service Bus
                // WithScheduledEnqueueTimeUtc writes a DateTime, and SSB receive/deadletter paths write an
                // integer ReceiveAttempts. MaterializePersistedContext deserializes the string through
                // ChatterJson.Options, where the registered MaterializingObjectConverter restores inline the
                // CLR types Newtonsoft's untyped read produced — so the (string)/(DateTime?)/integer reads on
                // the replayed context below and downstream remain correct.
                IDictionary<string, object> messageContext = MessageContext.MaterializePersistedContext(message.MessageContext);

                var contentType = message.MessageContentType;
                if (string.IsNullOrWhiteSpace(message.MessageContentType))
                {
                    contentType = (string)messageContext[MessageContext.ContentType];
                    _logger.LogTrace($"Outbox message did not contain content type. Retrieved from message context.");
                }

                messageContext.TryGetValue(MessageContext.InfrastructureType, out var infra);
                var infrastructureType = (string)infra;
                var dispatcherInfrastructure = _infrastructureProvider.GetDispatcher(infrastructureType);

                if (string.IsNullOrWhiteSpace(contentType))
                {
                    _logger.LogTrace($"No content type set in outbox message or message context. Unable to dispatch message.");
                    throw new ArgumentNullException(nameof(contentType), "A content type is required to serialize and send brokered message.");
                }

                var converter = _bodyConverterFactory.CreateBodyConverter(contentType);

                var outbound = new OutboundBrokeredMessage(message.MessageId, converter.GetBytes(message.MessageBody), messageContext, message.Destination, converter);
                _logger.LogTrace($"Processing message '{message.MessageId}' from outbox.");

                var pollable = (IPollableOutboxStore)_brokeredMessageOutbox;
                await ((IUnitOfWork)_brokeredMessageOutbox).ExecuteAsync(async ct =>
                {
                    await pollable.UpdateProcessedDate(message, ct);

                    // INVARIANT: ADR-0010 R1/R4 - Chatter's own off-guard is what decides, and it decides HERE
                    // rather than inside the scope. Argument evaluation precedes the guard INSIDE SendScope.Open,
                    // so a call site that reaches DispatchObserved has already resolved the persisted parent and
                    // has already entered a second async state machine. An application that never opted into broker
                    // diagnostics therefore takes the same bare dispatch it took before this hop was instrumented.
                    // This matches the three sibling send sites (ForwardingRouter, ReplyRouter,
                    // BrokeredMessageDispatcher), which each branch on the guard before their diagnostics method.
                    if (!BrokerDiagnostics.IsEnabled)
                    {
                        await dispatcherInfrastructure.Dispatch(outbound, null);
                    }
                    else
                    {
                        await DispatchObserved(dispatcherInfrastructure, outbound, infrastructureType);
                    }

                    _logger.LogTrace($"Message '{message.MessageId}' dispatched to messaging infrastructure from outbox.");

                }, null, cancellationToken);
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Unable to process outbox message with id '{message.Id}'");
            }
        }

        /// <summary>
        /// Publishes ONE drained row to broker infrastructure under its own send span (ADR-0010 D7), so the hop that
        /// actually reaches the broker — minutes after the write, in another process, where it can fail entirely on
        /// its own — is observable rather than silent.
        /// </summary>
        /// <remarks>
        /// INVARIANT: the dispatch stays BELOW the Router. The drain calls the messaging-infrastructure dispatcher
        /// directly, deliberately, so replaying a row cannot re-enter the reliability pipeline and write it to the
        /// outbox again; the scope only OBSERVES the call that was already being made and reroutes nothing.
        /// The Messaging Infrastructure the persisted context names is the only messaging-system identity this drain
        /// has; it is passed through AS-IS, and BrokerDiagnostics normalizes a blank identifier to an unset span
        /// attribute rather than inventing one.
        /// </remarks>
        private static async Task DispatchObserved(IMessagingInfrastructureDispatcher dispatcherInfrastructure, OutboundBrokeredMessage outbound, string messagingSystem)
        {
            using (var scope = SendScope.Open(messagingSystem, BrokerDiagnostics.OperationTypes.Send, outbound.Destination, DrainedMessageCount, ResolvePersistedParent(outbound.MessageContext)))
            {
                // OVERWRITES the persisted write-time traceparent with this hop's, because the drain IS the send that
                // put the message on the broker and is therefore what a downstream receive must parent to. The trace
                // stays intact: the span whose context is written here is itself a child of the context it replaced.
                // The overwrite happens ONLY when the scope has a trace context to travel - with diagnostics off, on
                // the metrics-only path, and on a sampled-out DEFERRED send, Inject writes nothing and the persisted
                // record rides out unchanged (ADR-0010 R2).
                scope.Inject(outbound.MessageContext);

                try
                {
                    await dispatcherInfrastructure.Dispatch(outbound, null);
                }
                catch (Exception e)
                {
                    scope.RecordFailure(e);
                    throw;
                }
            }
        }

        /// <summary>
        /// Reads back the trace context the WRITER persisted with the row, or <c>default</c> when the row carries
        /// none — one written while diagnostics were off, or received over a path that propagates no context.
        /// </summary>
        /// <remarks>
        /// INVARIANT: ADR-0010 R1 — the drain call site has ALREADY run Chatter's own off-guard, so an application
        /// that never opted in never reaches this method. The guard is repeated as the FIRST statement here so the
        /// helper stays safe to call from a site that has not, and so no extraction can precede it either way.
        /// INVARIANT: <c>default</c> means ABSENCE, never "use the current activity". The deferred
        /// <see cref="SendScope"/> overload starts a FRESH ROOT for it rather than adopting the drain loop's ambient
        /// activity, which would report that the poll caused the message when the write did (ADR-0010 D6).
        /// </remarks>
        private static ActivityContext ResolvePersistedParent(IDictionary<string, object> messageContext)
        {
            if (!BrokerDiagnostics.IsEnabled)
            {
                return default;
            }

            TraceContextPropagator.TryExtractFromMessageContext(messageContext, out var persistedParent);
            return persistedParent;
        }

        public async Task ProcessBatch(Guid batchId, CancellationToken cancellationToken = default)
        {
            var pollable = (IPollableOutboxStore)_brokeredMessageOutbox;
            var messages = await pollable.GetUnprocessedBatch(batchId, cancellationToken).ConfigureAwait(false);
            _logger.LogTrace($"Processing '{messages.Count()}' messages for batch '{batchId}'.");

            foreach (var message in messages)
            {
                await Process(message, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
