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
            var published = false;

            try
            {
                // The persisted MessageContext is a JSON string whose values are NOT all strings:
                // WithTimeToLive/RefreshTimeToLive write a TimeSpan, Azure Service Bus
                // WithScheduledEnqueueTimeUtc writes a DateTime, and SSB receive/deadletter paths write an
                // integer ReceiveAttempts. MaterializePersistedContext deserializes the string through
                // ChatterJson.Options, where the registered MaterializingObjectConverter restores inline the
                // CLR types Newtonsoft's untyped read produced — so the (string)/(DateTime?)/integer reads
                // downstream remain correct.
                IDictionary<string, object> messageContext = MessageContext.MaterializePersistedContext(message.MessageContext);

                var contentType = message.MessageContentType;
                if (string.IsNullOrWhiteSpace(message.MessageContentType))
                {
                    // INVARIANT: the persisted content type is read by a KIND TEST, so a context whose value is
                    // absent or is not a string reaches the classified "a content type is required" refusal below
                    // rather than a KeyNotFoundException or an InvalidCastException from the read itself.
                    // Oracles: MustRefuseAnOutboxMessageWhosePersistedContentTypeIsNotAString and
                    // MustRefuseAnOutboxMessageWhosePersistedContentTypeKeyIsAbsent; restoring the indexer-and-cast
                    // read reddens both and nothing else - measured across this suite on both target frameworks.
                    messageContext.TryGetValue(MessageContext.ContentType, out var persistedContentType);
                    contentType = persistedContentType as string;
                    _logger.LogTrace($"Outbox message did not contain content type. Retrieved from message context.");
                }

                // INVARIANT: a persisted infrastructure type that is present but not a string is read as ABSENT, so
                // the row is dispatched via the default Messaging Infrastructure rather than refused on every poll,
                // and the misroute is logged so it is observable rather than silent.
                // Oracles: MustDispatchViaTheDefaultInfrastructureWhenThePersistedInfrastructureTypeIsNotAString and
                // MustLogThatThePersistedInfrastructureTypeWasUnreadable; restoring the (string) cast reddens both,
                // and dropping the warning reddens the second alone - each measured across this suite on both
                // target frameworks.
                messageContext.TryGetValue(MessageContext.InfrastructureType, out var infra);
                var infrastructureType = infra as string;
                if (infra != null && infrastructureType == null)
                {
                    _logger.LogWarning($"Outbox message '{message.MessageId}' carries a '{MessageContext.InfrastructureType}' that is not a string. Dispatching it via the default messaging infrastructure.");
                }

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

                // INVARIANT: the value the drain claim's compare-and-set is made against is read HERE, before the
                // unit of work opens, and never at claim time. Why the comparison is pinned to what the poll
                // reported rather than to whatever the row says by the time the claim runs is recorded once, on
                // IPollableOutboxStore.TryClaimForDispatch.
                // Oracle: MustClaimAgainstTheDueTimeThePollRead; passing message.NextAttemptAtUtc at the call
                // instead of this local reddens it and nothing else - measured across both suites and both target
                // frameworks.
                // The instant claimed is the one a FAILED attempt would have been scheduled by. This introduces no
                // number of its own. What that instant does to the row, and what ends a claim carrying it, is
                // recorded once in the remarks on IPollableOutboxStore.TryClaimForDispatch.
                var observedNextAttemptAtUtc = message.NextAttemptAtUtc;
                var claimedNextAttemptAtUtc = DateTime.UtcNow.Add(_reliabilityOptions.CalculateDispatchBackoff(message.DispatchAttempts + 1));

                await ((IUnitOfWork)_brokeredMessageOutbox).ExecuteAsync(async ct =>
                {
                    // INVARIANT: the DRAIN CLAIM is taken as the FIRST statement inside the unit of work, before the
                    // publish. It arbitrates which of several drains that polled this row gets to try it; how it
                    // differs from the claim the processed stamp carries is recorded once, on
                    // IPollableOutboxStore.TryClaimForDispatch. Taking it INSIDE the unit of work is what makes the
                    // arbitration hold on a relational store: the losing drain blocks on the row's lock for the
                    // winner's whole publish and then matches nothing, rather than being told it won a row that is
                    // already on the broker. A denial RETURNS rather than throwing - a throw lands in the generic
                    // catch below and spends a dispatch attempt on a row this drain never attempted - so a denied
                    // drain publishes nothing, stamps nothing and costs the row nothing.
                    // Measured sets, each taken across BOTH suites and both target frameworks - none of these three
                    // mutations is exclusive to one fact, because the same decision is read by the mock-store
                    // fixture here, by the real in-memory drain, and by the SQL Server arbitration fixture:
                    //  - dropping the denial guard and dispatching anyway reddens FOUR facts -
                    //    MustDispatchNothingWhenTheDrainClaimIsDenied,
                    //    WhenDrainingTheDefaultInMemoryOutbox.MustDispatchTheRacedRowExactlyOnce,
                    //    WhenDrainingTheDefaultInMemoryOutbox.MustCostTheRowNothingWhenTheDrainLosesTheRace and
                    //    the EntityFramework suite's
                    //    Integration.WhenArbitratingOutboxDrainsOnSqlServer.MustDenyTheWaitingDrainsClaimOnceTheWinningDrainCommits
                    //    (both of its cases);
                    //  - throwing instead of returning on a denial reddens THREE -
                    //    MustNotSpendADispatchAttemptWhenTheDrainClaimIsDenied,
                    //    WhenDrainingTheDefaultInMemoryOutbox.MustCostTheRowNothingWhenTheDrainLosesTheRace and
                    //    that same SQL Server fact;
                    //  - moving the claim BELOW the publish reddens FIVE facts here and in
                    //    WhenDrainingTheDefaultInMemoryOutbox - MustTakeTheDrainClaimBeforeDispatching,
                    //    MustDispatchNothingWhenTheDrainClaimIsDenied,
                    //    MustRecordTheDispatchAttemptAfterTheRollbackDiscardsTheDrainClaim,
                    //    MustDispatchTheRacedRowExactlyOnce and MustCostTheRowNothingWhenTheDrainLosesTheRace -
                    //    plus the WHOLE of Integration.WhenArbitratingOutboxDrainsOnSqlServer, all four of its
                    //    facts in both of their cases. That last set is the one that shows the position is a
                    //    relational claim and not only a mock-ordering one.
                    // INVARIANT: a write to NextAttemptAtUtc is exactly as durable as the fact it records, which is
                    // why this claim and RecordDispatchAttempt write the same column from opposite sides of the unit
                    // of work. RecordDispatchAttempt records something that HAPPENED - a publish that failed - and
                    // has to outlive the rollback that failure caused, so it goes straight to the store OUTSIDE any
                    // unit of work. This claim records something ABOUT TO happen and must not outlive a publish that
                    // did not, so it goes INSIDE one. The two meet on the failure exit, in that order: the rollback
                    // discards the claim and the attempt stamp lands after it, leaving the row scheduled by the
                    // failure rather than by a claim the failure already voided.
                    // Oracle: MustRecordTheDispatchAttemptAfterTheRollbackDiscardsTheDrainClaim, which reads the
                    // SEQUENCE of writes rather than the row, because the claimed and the recorded instant are both
                    // one backoff ahead of now and the row alone does not tell them apart. A store whose unit of
                    // work does not roll back - the in-memory one keeps no transaction - keeps the claim instead,
                    // and the attempt stamp that lands after it overwrites the same column either way; what ends
                    // a claim no rollback ends is recorded once in the remarks on
                    // IPollableOutboxStore.TryClaimForDispatch.
                    if (!await pollable.TryClaimForDispatch(message, observedNextAttemptAtUtc, claimedNextAttemptAtUtc, ct))
                    {
                        return;
                    }

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

                    // INVARIANT: the flag reads "this message is on the broker", so it is raised AFTER the publish
                    // returns and BEFORE the claim - the two exits below branch on it and neither may treat a
                    // message the broker never took as delivered. Raising it before the dispatch instead turns
                    // every failed publish into a re-claim, and reddens TWELVE facts and nothing else (measured
                    // across both suites and both target frameworks): the seven WhenProcessingOutboxMessage facts
                    // whose dispatch fails - MustLeaveOutboxMessageUnprocessedWhenDispatchFails,
                    // MustRecordADispatchAttemptWhenDispatchFails,
                    // MustRecordADispatchAttemptWhenDispatchIsCancelledByAnotherToken,
                    // MustRecordTheDispatchAttemptOutsideTheRolledBackUnitOfWork,
                    // MustRecordTheDispatchAttemptAfterTheRollbackDiscardsTheDrainClaim,
                    // MustScheduleTheNextAttemptOneBackoffAhead and
                    // MustGrowTheScheduledWaitWithTheAttemptsTheRowAlreadyCarries - the three real-store drain facts
                    // in WhenDrainingTheDefaultInMemoryOutbox, MustRecordTheFailedDispatchAndLeaveTheRowUnprocessed,
                    // MustDispatchTheSameRowOnTheNextDrainOnceItsBackoffHasElapsed and
                    // MustCostTheRowNothingWhenTheDrainLosesTheRace - and, in the EntityFramework suite, the two
                    // Integration.WhenDrainingPastAPermanentlyFailingRowOnSqlServer facts that read attempt state
                    // back out of the database, MustCarryTheAttemptStateOfEveryRefusedMessageInTheDatabase and
                    // MustClaimAMessageWhoseAttemptStateWasWrittenOutsideTheChangeTracker.
                    published = true;

                    _logger.LogTrace($"Message '{message.MessageId}' dispatched to messaging infrastructure from outbox.");

                    // INVARIANT: the row is recorded processed ONLY after the publish returns, on BOTH diagnostics
                    // branches above. A publish that throws leaves the row unprocessed so the next poll retries it;
                    // marking first would record a message that never reached the broker as delivered and lose it,
                    // because Process swallows the failure and no later poll would ever see the row again.
                    // Oracle: MustLeaveOutboxMessageUnprocessedWhenDispatchFails. The converse - a publish that
                    // succeeds and this claim that then fails - is re-claimed rather than redelivered; the rationale
                    // for that is on TryReClaimPublishedMessage.
                    await pollable.UpdateProcessedDate(message, ct);
                }, null, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // INVARIANT: a drain the host stopped is NOT a failed dispatch. It spends no attempt, because
                // spending one would defer a perfectly good row and, under a configured attempt ceiling, burn its
                // budget on restarts alone.
                // This exit neither records an attempt nor undoes the drain claim taken above, so where the row is
                // LEFT follows from that claim's lifetime, recorded once in the remarks on
                // IPollableOutboxStore.TryClaimForDispatch.
                // The filter is deliberate and matches ReliabilityRetentionPurgeService's stop handling: a
                // cancellation raised by any OTHER token - a broker client's own send timeout - is a real dispatch
                // failure and falls through to the catch below.
                // Oracles: MustNotRecordADispatchAttemptWhenProcessingIsCancelled pins the NO-ATTEMPT half of the
                // exemption ALONE - it drives a mocked store and asserts only that RecordDispatchAttempt is never
                // called - and MustRecordADispatchAttemptWhenDispatchIsCancelledByAnotherToken pins its bound.
                // Dropping the `when` filter reddens the second and nothing else - measured across both suites and
                // both target frameworks. NO oracle pins where the row is LEFT: releasing the claim back to an
                // instant long past on this exit reddens NOTHING in either suite on either target framework
                // (measured, against a control mutation that reddens each of the four test binaries), because no
                // fact drives a drain's OWN token to cancellation against a store that keeps a row at all.
                _logger.LogTrace($"Processing of outbox message with id '{message.Id}' was cancelled.");
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Unable to process outbox message with id '{message.Id}'");

                if (published && await TryReClaimPublishedMessage(message, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                await RecordFailedDispatchAttempt(message, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Claims a row whose message is already on the broker a second time, in a unit of work of its own, after
        /// the claim the drain made rolled back with the failure that ended the drain. Reports whether the row is
        /// claimed.
        /// </summary>
        /// <remarks>
        /// INVARIANT: this runs ONLY on an exit where the publish returned, so it can never record a message the
        /// broker never took as delivered; the flag it is gated on is raised at the one point where that becomes
        /// true, and the mutations that redden its position are named there.
        /// Oracles: MustReClaimTheRowWhenTheClaimCommitFailsAfterAPublish and
        /// MustNotRecordADispatchAttemptWhenTheReClaimSucceeds, joined over a relational store by the
        /// EntityFramework suite's
        /// <c>WhenReclaimingAfterAFailedClaimOverSqlite.MustLeaveTheRowProcessedAndSpendNoAttemptWhenTheClaimFailsAfterAPublish</c>.
        /// Measured across both suites and both target frameworks: removing the re-claim reddens all THREE, and
        /// recording an attempt after a re-claim that SUCCEEDED reddens TWO of them,
        /// MustNotRecordADispatchAttemptWhenTheReClaimSucceeds and the SQLite fact. Neither measured mutation
        /// isolates MustReClaimTheRowWhenTheClaimCommitFailsAfterAPublish.
        /// INVARIANT: the claim is STAGED here rather than inherited. Where the rolled-back unit of work began its
        /// own transaction, rollback clears its change tracker, so no residue of the staged claim survives for a
        /// later unit of work to flush by accident; where it adopted a caller's transaction, the caller owns
        /// rollback and the tracker is left as it was found. Issuing the claim again does not lean on either case:
        /// it is what performs the claim, not a write that happens to restate what a tracker might still carry.
        /// No oracle separates the two paths: residue belongs to a relational store and a mocked one has none, so
        /// this is stated rather than pinned.
        /// INVARIANT: no DRAIN CLAIM is taken here. The message is already on the broker, so there is nothing left
        /// for a claim to arbitrate, and a claim that came back DENIED would abandon the processed stamp on a row
        /// that WAS published - the one outcome this method exists to prevent. The rationale for the claim itself is
        /// recorded once, at the call site in <see cref="Process"/>. No oracle separates this from a re-claim that
        /// took one: every store grants an uncontended claim, so a claim added here would be granted on every path
        /// that reaches this method.
        /// INVARIANT: a re-claim that throws is logged and reported unclaimed, so the caller falls through to the
        /// dispatch attempt and the row is held back by the backoff instead of being published again next poll.
        /// Oracle: MustRecordADispatchAttemptWhenTheReClaimAlsoFails; swallowing the failure and reporting the row
        /// claimed reddens it and nothing else - measured across both suites and both target frameworks.
        /// </remarks>
        private async Task<bool> TryReClaimPublishedMessage(OutboxMessage message, CancellationToken cancellationToken)
        {
            try
            {
                var pollable = (IPollableOutboxStore)_brokeredMessageOutbox;

                await ((IUnitOfWork)_brokeredMessageOutbox).ExecuteAsync(ct => pollable.UpdateProcessedDate(message, ct), null, cancellationToken).ConfigureAwait(false);

                return true;
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Unable to record outbox message with id '{message.Id}' processed after it was dispatched");
                return false;
            }
        }

        /// <summary>
        /// Spends one dispatch attempt on the row and schedules the next one a backoff ahead, which is what makes
        /// the poll's due gate hold a failing message back instead of handing it to every batch.
        /// </summary>
        /// <remarks>
        /// INVARIANT: the stamp goes STRAIGHT to the store, outside any unit of work. Dispatch runs inside one that
        /// ROLLS BACK when it throws, so a stamp staged there is discarded with the failure it records and the
        /// mechanism silently does nothing. The relational store writes the stamp without saving a change tracker
        /// at all, for the reasons recorded on
        /// BrokeredMessageOutbox.RecordDispatchAttempt. Oracle:
        /// MustRecordTheDispatchAttemptOutsideTheRolledBackUnitOfWork, whose unit of work reverts attempt state
        /// staged by an operation that threw and which also counts the units of work opened. Wrapping this call in
        /// a unit of work reddens it and nothing else - measured across both suites and both target frameworks.
        /// INVARIANT: this covers the two exits that leave the row unclaimed: a dispatch that threw, and a dispatch
        /// that SUCCEEDED whose claim AND re-claim both threw. The second is what keeps the due gate from
        /// re-publishing an already-published message on the very next poll.
        /// Oracles: MustRecordADispatchAttemptWhenDispatchFails and MustRecordADispatchAttemptWhenTheReClaimAlsoFails,
        /// whose exclusive mutation is recorded on TryReClaimPublishedMessage.
        /// INVARIANT: the backoff is taken from the attempt count THIS failure leaves the row at, so the waits run
        /// 5s, 10s, 20s rather than repeating the base for the first two failures.
        /// Oracle: MustGrowTheScheduledWaitWithTheAttemptsTheRowAlreadyCarries; passing the row's pre-increment
        /// count reddens it and nothing else (measured across both suites and both target frameworks).
        /// Scheduling the attempt at now rather than a backoff ahead is a much wider mutation: it reddens EIGHT
        /// facts - that one, MustScheduleTheNextAttemptOneBackoffAhead, both
        /// WhenDrainingTheDefaultInMemoryOutbox facts that let a backoff elapse
        /// (MustRecordTheFailedDispatchAndLeaveTheRowUnprocessed and
        /// MustDispatchTheSameRowOnTheNextDrainOnceItsBackoffHasElapsed), and all four
        /// Integration.WhenDrainingPastAPermanentlyFailingRowOnSqlServer facts in the EntityFramework suite, which
        /// is what a backoff of zero costs: a failing message is handed to the very next poll again. The breadth
        /// belongs to that mutation, not to the reasoning above it.
        /// A failure to record is itself logged and swallowed, so the row keeps the attempts it already carried and
        /// is re-attempted rather than lost, and the drain can never be worse off than the behaviour this replaced.
        /// WHEN the row comes back then follows from the drain claim's lifetime, recorded once in the remarks on
        /// IPollableOutboxStore.TryClaimForDispatch.
        /// Oracle: MustNotThrowWhenRecordingTheDispatchAttemptFails, which pins only that the
        /// failure does not escape; NO oracle pins the due time this exit leaves.
        /// </remarks>
        private async Task RecordFailedDispatchAttempt(OutboxMessage message, CancellationToken cancellationToken)
        {
            try
            {
                var attemptsThisFailureLeaves = message.DispatchAttempts + 1;
                var nextAttemptAtUtc = DateTime.UtcNow.Add(_reliabilityOptions.CalculateDispatchBackoff(attemptsThisFailureLeaves));
                var pollable = (IPollableOutboxStore)_brokeredMessageOutbox;

                await pollable.RecordDispatchAttempt(message, nextAttemptAtUtc, cancellationToken).ConfigureAwait(false);
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
