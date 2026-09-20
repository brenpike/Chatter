using Chatter.MessageBrokers.Diagnostics;
using Chatter.MessageBrokers.Reliability.Configuration;
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
        private readonly ReliabilityOptions _reliabilityOptions;

        /// <param name="reliabilityOptions">
        /// Names the dispatch backoff a failed attempt is scheduled by. OPTIONAL, and omitting it takes the shipped
        /// defaults rather than throwing the way the dependencies above do, for the same reason
        /// <see cref="ReliabilityOptions.OutboxDispatchBackoffBaseInSeconds"/> carries its default as an initializer:
        /// the backoff is not opt-in, and a drain constructed without options must still back off. Dependency
        /// injection always supplies the registered instance, so the fallback is reached only by a direct
        /// construction.
        /// </param>
        public OutboxProcessor(IMessagingInfrastructureProvider infrastructureProvider,
                               ILogger<OutboxProcessor> logger,
                               IBodyConverterFactory bodyConverterFactory,
                               IBrokeredMessageOutbox brokeredMessageOutbox,
                               ReliabilityOptions reliabilityOptions = null)
        {
            _infrastructureProvider = infrastructureProvider ?? throw new ArgumentNullException(nameof(infrastructureProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _bodyConverterFactory = bodyConverterFactory ?? throw new ArgumentNullException(nameof(bodyConverterFactory));
            _brokeredMessageOutbox = brokeredMessageOutbox ?? throw new ArgumentNullException(nameof(brokeredMessageOutbox));
            _reliabilityOptions = reliabilityOptions ?? new ReliabilityOptions();
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

                    // INVARIANT: the row is recorded processed ONLY after the publish returns, on BOTH diagnostics
                    // branches above. A publish that throws leaves the row unprocessed so the next poll retries it;
                    // marking first would record a message that never reached the broker as delivered and lose it,
                    // because Process swallows the failure and no later poll would ever see the row again.
                    // The converse - a publish that succeeds and a mark that then fails - redelivers the message on
                    // the next poll. That duplicate is the accepted cost of at-least-once delivery here.
                    await pollable.UpdateProcessedDate(message, ct);
                }, null, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // INVARIANT: a drain the host stopped is NOT a failed dispatch. It spends no attempt and pushes no
                // due time out, so the row stays due now and the next host start takes it; spending one would defer
                // a perfectly good row and, under a configured attempt ceiling, burn its budget on restarts alone.
                // The filter is deliberate and matches ReliabilityRetentionPurgeService's stop handling: a
                // cancellation raised by any OTHER token - a broker client's own send timeout - is a real dispatch
                // failure and falls through to the catch below.
                // Oracles: MustNotRecordADispatchAttemptWhenProcessingIsCancelled pins the exemption, and
                // MustRecordADispatchAttemptWhenDispatchIsCancelledByAnotherToken pins its bound. Dropping the
                // `when` filter reddens the second and nothing else (observed).
                _logger.LogTrace($"Processing of outbox message with id '{message.Id}' was cancelled.");
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Unable to process outbox message with id '{message.Id}'");
                await RecordFailedDispatchAttempt(message, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Spends one dispatch attempt on the row and schedules the next one a backoff ahead, which is what makes
        /// the poll's due gate hold a failing message back instead of handing it to every batch.
        /// </summary>
        /// <remarks>
        /// INVARIANT: the stamp commits in its OWN unit of work. Dispatch runs inside one that ROLLS BACK when it
        /// throws, so a stamp staged there is discarded with the failure it records and the mechanism silently does
        /// nothing. Oracle: MustRecordTheDispatchAttemptOutsideTheRolledBackUnitOfWork, whose unit of work reverts
        /// attempt state staged by an operation that threw and which also counts the units of work opened. Calling
        /// the store directly here instead of through a unit of work reddens it and nothing else (observed);
        /// staging the stamp inside the DISPATCH unit of work instead reddens it together with
        /// MustNotRecordADispatchAttemptWhenProcessingIsCancelled - a stamp staged there also bypasses the
        /// cancellation exemption above - and nothing else (observed).
        /// INVARIANT: this covers BOTH non-success exits of the drain, because both leave the row unclaimed: a
        /// dispatch that threw, and a dispatch that SUCCEEDED whose claim commit then threw. The second is what
        /// keeps the due gate from re-publishing an already-published message on the very next poll.
        /// Oracles: MustRecordADispatchAttemptWhenDispatchFails and MustRecordADispatchAttemptWhenMarkingProcessedFails;
        /// swallowing the claim failure inside the unit of work reddens the second and nothing else (observed).
        /// INVARIANT: the backoff is taken from the attempt count THIS failure leaves the row at, so the waits run
        /// 5s, 10s, 20s rather than repeating the base for the first two failures.
        /// Oracle: MustGrowTheScheduledWaitWithTheAttemptsTheRowAlreadyCarries; passing the row's pre-increment
        /// count reddens it and nothing else, and scheduling the attempt at now rather than a backoff ahead reddens
        /// it together with MustScheduleTheNextAttemptOneBackoffAhead (both observed).
        /// A failure to record is itself logged and swallowed, which leaves the row exactly as it is without this
        /// method - due now, at the attempts it already carried, re-attempted next drain - so the drain can never
        /// be worse off than the behaviour this replaced. Oracle: MustNotThrowWhenRecordingTheDispatchAttemptFails.
        /// </remarks>
        private async Task RecordFailedDispatchAttempt(OutboxMessage message, CancellationToken cancellationToken)
        {
            try
            {
                var attemptsThisFailureLeaves = message.DispatchAttempts + 1;
                var nextAttemptAtUtc = DateTime.UtcNow.Add(_reliabilityOptions.CalculateDispatchBackoff(attemptsThisFailureLeaves));
                var pollable = (IPollableOutboxStore)_brokeredMessageOutbox;

                await ((IUnitOfWork)_brokeredMessageOutbox).ExecuteAsync(ct => pollable.RecordDispatchAttempt(message, nextAttemptAtUtc, ct), null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Unable to record a failed dispatch attempt for outbox message with id '{message.Id}'");
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
