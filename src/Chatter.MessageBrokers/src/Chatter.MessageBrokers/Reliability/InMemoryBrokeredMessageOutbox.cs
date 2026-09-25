using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Configuration;
using Chatter.MessageBrokers.Reliability.Outbox;
using Chatter.MessageBrokers.Sending;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability
{
    class InMemoryBrokeredMessageOutbox : IBrokeredMessageOutbox, IPollableOutboxStore, IUnitOfWork, IProcessLifetimeStore
    {
        // INVARIANT: this dictionary is the process's ONLY copy of the outbox rows, so the instance holding it must
        // outlive any DI scope. A sender writes a row in the scope its operation runs in, and
        // BrokeredMessageOutboxProcessor opens a FRESH scope for every poll, so a per-scope instance hands the drain
        // an empty dictionary and the default outbox delivers nothing at all. Hence IProcessLifetimeStore and the
        // process-lifetime registration; the relational and document provider stores keep their rows outside the
        // instance and are correctly per-operation.
        private readonly ConcurrentDictionary<string, OutboxMessage> _outbox;
        private readonly ILogger<InMemoryBrokeredMessageOutbox> _logger;
        private readonly ReliabilityOptions _reliabilityOptions;
        private readonly Action<string> _beforeTakingTheDrainClaim;

        // INVARIANT: every WRITE to the two fields the drain claim arbitrates on - NextAttemptAtUtc and
        // ProcessedFromOutboxAtUtc - is taken under this one gate, so the claim's read-compare-write runs as a
        // unit. Both fields are nullable DateTime values, which are wider than a machine word and therefore not
        // atomically writable, so a claim comparing against a field a sibling writer was midway through storing
        // could be granted on a value that was never in the row. The gate is held for a few field reads and
        // writes and NEVER across an await or across a dispatch - a gate held across a dispatch would wedge every
        // other drain behind one slow broker call.
        // BOUNDARY: ARBITRATION is atomic; SELECTION stays advisory. GetUnprocessedMessagesFromOutbox reads these
        // same fields UNGATED, because which rows a poll selects was already documented rather than enforced
        // (IPollableOutboxStore) and the worst a torn read there can do is hand back a row whose claim the gated
        // compare-and-set then refuses.
        // NO TEST PINS THIS GATE. Removing the monitor from all four write sites while leaving each site's
        // read-then-write order exactly as it is reddens NOTHING in either suite, on either target framework
        // (measured; the compile was proved fresh by an added warning, because a green run is the one result a
        // stale binary can fake). The interference seam below fires BEFORE the monitor is entered, so every
        // deterministic single-threaded fact reaches the same answer with the gate gone. What the gate buys is a
        // torn read a single-threaded fact cannot stage, so it is stated here rather than pinned. Hoisting the
        // claim's read-and-compare OUT of the monitor and leaving only the write inside reddens nothing either
        // (measured the same way), so the read-compare-write ORDER is unpinned as well.
        private readonly object _claimGate = new();

        IPersistanceTransaction IUnitOfWork.CurrentTransaction => null;
        bool IUnitOfWork.HasActiveTransaction => false;

        public InMemoryBrokeredMessageOutbox(ILogger<InMemoryBrokeredMessageOutbox> logger, ReliabilityOptions reliabilityOptions)
            : this(logger, reliabilityOptions, null)
        { }

        // The drain claim's correctness turns on what can happen BETWEEN the poll that reported a row's due
        // instant and the compare-and-set that moves it: a second drain can take the claim inside that window.
        // Reproducing that ordering with threads and timing would be flaky, so this constructor lets a test run
        // its interference at exactly that point. The callback runs BEFORE the gate is entered, so whatever it
        // drives there runs as a genuinely competing claim rather than a re-entrant one. Every production
        // construction leaves the callback null.
        internal InMemoryBrokeredMessageOutbox(ILogger<InMemoryBrokeredMessageOutbox> logger, ReliabilityOptions reliabilityOptions, Action<string> beforeTakingTheDrainClaim)
        {
            _outbox = new ConcurrentDictionary<string, OutboxMessage>();
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _reliabilityOptions = reliabilityOptions ?? throw new ArgumentNullException(nameof(reliabilityOptions));
            _beforeTakingTheDrainClaim = beforeTakingTheDrainClaim;
        }

        public async Task SendToOutbox(IEnumerable<OutboundBrokeredMessage> outboundBrokeredMessages, TransactionContext transactionContext, CancellationToken cancellationToken = default)
        {
            foreach (var outboundBrokeredMessage in outboundBrokeredMessages)
            {
                await SendToOutbox(outboundBrokeredMessage, transactionContext, cancellationToken).ConfigureAwait(false);
            }
        }

        public Task SendToOutbox(OutboundBrokeredMessage outboundBrokeredMessage, TransactionContext transactionContext, CancellationToken cancellationToken = default)
        {
            Guid transactionId = Guid.NewGuid();
            if (transactionContext != null)
            {
                transactionContext.Container.TryGet<IPersistanceTransaction>(out var transaction);
                transactionId = transaction?.TransactionId ?? transactionId;
            }

            var outboxMessage = new OutboxMessage
            {
                MessageId = outboundBrokeredMessage.MessageId,
                MessageContext = System.Text.Json.JsonSerializer.Serialize(outboundBrokeredMessage.MessageContext, ChatterJson.Options),
                Destination = outboundBrokeredMessage.Destination,
                MessageBody = outboundBrokeredMessage.Stringify(),
                MessageContentType = outboundBrokeredMessage.ContentType,
                SentToOutboxAtUtc = DateTime.UtcNow,
                ProcessedFromOutboxAtUtc = null,
                BatchId = transactionId
            };

            _logger.LogTrace($"Outbox message created for message with id: '{outboxMessage.MessageId}'");

            if (!_outbox.TryAdd(outboxMessage.MessageId, outboxMessage))
            {
                var error = $"Unable to add brokered message with id: '{outboxMessage.MessageId}' to the in memory outbox.";
                _logger.LogError(error);
                throw new InvalidOperationException(error);
            }

            _logger.LogTrace($"Outbox message with id: '{outboxMessage.MessageId}' added to the in memory outbox.");

            return Task.CompletedTask;
        }

        // INVARIANT: the Outbox Poll Batch contract - at most ReliabilityOptions.OutboxPollBatchSize rows that are
        // unprocessed and DUE, oldest SentToOutboxAtUtc first. The dictionary hands back its values in hash-bucket
        // order, which is unrelated to arrival, so the ordering must be applied BEFORE the cap or the cap would
        // drop arbitrary rows and an old message could sit behind newer ones for as long as the backlog stays
        // above the batch size.
        // INVARIANT: the due clause and the attempt ceiling are applied BEFORE the cap too, for a different
        // reason: gating the rows the cap has already taken shrinks the batch rather than filling it from behind,
        // so as few as OutboxPollBatchSize permanently-failing rows would leave the poll returning nothing at all.
        // This is the shipped default in-process outbox, so that wedge reaches the DEFAULT configuration and not
        // only the relational one. Moving the due clause into a Where AFTER the Take reddens
        // MustSpendNoBatchSlotOnAMessageThatIsNotDue and MustGiveTheBatchSlotOfAFailingMessageToTheNextMessage -
        // the second because at a batch size of one the held-back message takes the only slot and is then
        // filtered out of it - and nothing else. Moving the ceiling clause there reddens
        // MustSpendNoBatchSlotOnAMessageThatHasSpentTheAttemptCeiling and nothing else. Both measured across both
        // suites and both target frameworks.
        // NOTE: no oracle pins the boundary between `<=` and `<` against now. The instant is read from the wall
        // clock inside this method, so no test can name a row due at exactly it; the two differ only for a row
        // whose next attempt lands on that very tick, and a row a tick early is taken by the following poll.
        public Task<IEnumerable<OutboxMessage>> GetUnprocessedMessagesFromOutbox(CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;
            var maxDispatchAttempts = _reliabilityOptions.OutboxMaxDispatchAttempts;

            return Task.FromResult<IEnumerable<OutboxMessage>>(_outbox.Values
                    .Where(m => m.ProcessedFromOutboxAtUtc is null
                                && (m.NextAttemptAtUtc is null || m.NextAttemptAtUtc.Value <= now)
                                && (maxDispatchAttempts is null || m.DispatchAttempts < maxDispatchAttempts.Value))
                    .OrderBy(m => m.SentToOutboxAtUtc)
                    .Take(_reliabilityOptions.OutboxPollBatchSize)
                    .ToList());
        }

        // The processed stamp is one of the two fields the drain claim arbitrates on, so it is written under the
        // claim gate - see the gate's INVARIANT above for why. The expiry scan stays outside it: it writes
        // neither field, and it walks the whole dictionary, which is far longer than the gate is meant to be held.
        public Task UpdateProcessedDate(IEnumerable<OutboxMessage> outboxMessages, CancellationToken cancellationToken = default)
        {
            lock (_claimGate)
            {
                foreach (var outboxMessage in outboxMessages)
                {
                    outboxMessage.ProcessedFromOutboxAtUtc = DateTime.UtcNow;
                }
            }
            RemoveExpiredFromInboxOutbox();
            return Task.CompletedTask;
        }

        public Task UpdateProcessedDate(OutboxMessage outboxMessage, CancellationToken cancellationToken = default)
        {
            lock (_claimGate)
            {
                outboxMessage.ProcessedFromOutboxAtUtc = DateTime.UtcNow;
            }
            RemoveExpiredFromInboxOutbox();
            return Task.CompletedTask;
        }

        // INVARIANT: this writes THROUGH to the stored row rather than to a copy, because the dictionary holds the
        // very instances a poll hands back - the same reference identity UpdateProcessedDate stamps a processed
        // date through. Recording onto a copy would leave a failing message due now with no attempts spent and
        // re-attempted on every poll, which is exactly what the no-op default interface implementation still does
        // for a store that does not override it. Oracle: MustHoldTheRecordedAttemptStateOnTheStoredRow, which
        // records the attempt through the interface and then reads it back out of a fresh poll. Recording onto a
        // copy of the message instead reddens NINE facts and nothing else, measured across both suites and both
        // target frameworks. Six are oracles for this same write-through: that one,
        // MustCountOneMoreDispatchAttemptWhenRecordingAnAttempt,
        // MustAccumulateDispatchAttemptsAcrossRecordedAttempts,
        // MustExcludeAMessageThatHasSpentTheConfiguredAttemptCeiling,
        // MustSpendNoBatchSlotOnAMessageThatHasSpentTheAttemptCeiling and
        // MustGiveTheBatchSlotOfAFailingMessageToTheNextMessage. The other three read the due instant this method
        // also writes, so they fall out with it: MustRefuseADrainClaimAgainstAStaleObservedDueInstant here, and
        // MustRecordTheFailedDispatchAndLeaveTheRowUnprocessed and MustCostTheRowNothingWhenTheDrainLosesTheRace
        // in UsingOutboxProcessor.WhenDrainingTheDefaultInMemoryOutbox.
        // The due instant written here is the other field the drain claim arbitrates on, so this write is taken
        // under the claim gate as well - see the gate's INVARIANT above for why.
        public Task RecordDispatchAttempt(OutboxMessage outboxMessage, DateTime nextAttemptAtUtc, CancellationToken cancellationToken = default)
        {
            lock (_claimGate)
            {
                outboxMessage.DispatchAttempts++;
                outboxMessage.NextAttemptAtUtc = nextAttemptAtUtc;
            }
            return Task.CompletedTask;
        }

        // INVARIANT: the drain claim is a COMPARE-AND-SET on the STORED row, mutating that row IN PLACE under the
        // claim gate rather than replacing the dictionary's value. ConcurrentDictionary.TryUpdate would be the
        // obvious primitive and is deliberately not used: it swaps in a NEW instance, which breaks the
        // write-through reference identity documented on RecordDispatchAttempt above - the poll hands back the
        // stored instances themselves, so a drain holding a row polled a moment earlier would go on writing to an
        // instance the dictionary no longer holds.
        // The honest cost of that choice, recorded rather than hidden: this is NOT one atomic dictionary
        // operation, which is the shape issue #519 asks for. That shape and write-through reference identity
        // cannot both hold here, and it is write-through that six named facts pin and the single-operation shape
        // that none pin, so the single-operation shape is the one that yields.
        // A claim is granted only when the row is unprocessed, its due instant is still the one the poll reported,
        // and it is due at claim time. Each conjunct was measured by removing it, across both suites and both
        // target frameworks:
        // Dropping the comparison against the reported instant reddens
        // MustGrantExactlyOneOfTwoCompetingDrainClaims, MustRefuseADrainClaimAgainstAStaleObservedDueInstant and
        // UsingOutboxProcessor.WhenDrainingTheDefaultInMemoryOutbox.MustCostTheRowNothingWhenTheDrainLosesTheRace
        // - three facts. No fact in WhenProcessingOutboxMessage is among them: that fixture drives OutboxProcessor
        // against a MOCK outbox, so no mutation of this store executes there at all.
        // Dropping the processed conjunct reddens MustRefuseADrainClaimOnAProcessedMessage and nothing else.
        // Writing any instant other than the one the caller supplied - DateTime.MaxValue was measured - reddens
        // MustLeaveAClaimedMessageDueAgainOneBackoffLater, MustRefuseADrainClaimOnAMessageAnotherDrainHolds and
        // MustGrantExactlyOneOfTwoCompetingDrainClaims - three facts.
        // Replacing this in-place write with a ConcurrentDictionary.TryUpdate of a copy reddens those same three
        // plus two in UsingOutboxProcessor.WhenDrainingTheDefaultInMemoryOutbox -
        // MustDispatchTheSameRowOnTheNextDrainOnceItsBackoffHasElapsed and
        // MustCostTheRowNothingWhenTheDrainLosesTheRace - five facts, the last two reading the orphaned instance
        // through a real drain. The six write-through facts named on RecordDispatchAttempt stay GREEN under it,
        // because that mutation only orphans a row a claim touched and those six take no claim - the breadth
        // belongs to the mutation, not to the reasoning.
        // NOTE: the due gate is applied HERE and deliberately nowhere else. GetUnprocessedMessagesFromOutbox is
        // due-gated too, but GetUnprocessedBatch is not (see its remarks on IPollableOutboxStore), so in this
        // store an in-request drain can read a row AFTER another drain claimed it, observe the claim's own value
        // and satisfy a bare comparison; refusing a row that is not due at claim time is what closes that. A
        // store whose claim is invisible until it commits does not have that window and must not carry this gate.
        // Dropping the due gate reddens MustRefuseADrainClaimOnAMessageThatIsNotDue and nothing else - ONE fact,
        // measured across both suites and both target frameworks. That is the whole of its pinned reach: the
        // window it closes needs a second drain to read a row between another drain's claim and that drain's
        // publish, and the only fact that stages it is the one named.
        // NOTE: as with the poll's due clause, no oracle pins the boundary between `<=` and `<` against now, and
        // for the same reason given there.
        public Task<bool> TryClaimForDispatch(OutboxMessage outboxMessage, DateTime? observedNextAttemptAtUtc, DateTime claimedNextAttemptAtUtc, CancellationToken cancellationToken = default)
        {
            _beforeTakingTheDrainClaim?.Invoke(outboxMessage.MessageId);

            if (!_outbox.TryGetValue(outboxMessage.MessageId, out var stored))
            {
                return Task.FromResult(false);
            }

            var now = DateTime.UtcNow;

            lock (_claimGate)
            {
                if (stored.ProcessedFromOutboxAtUtc != null
                    || stored.NextAttemptAtUtc != observedNextAttemptAtUtc
                    || (stored.NextAttemptAtUtc != null && stored.NextAttemptAtUtc.Value > now))
                {
                    return Task.FromResult(false);
                }

                stored.NextAttemptAtUtc = claimedNextAttemptAtUtc;
            }

            return Task.FromResult(true);
        }

        private void RemoveExpiredFromInboxOutbox()
        {
            var ttl = _reliabilityOptions.MinutesToLiveInMemory;

            if (ttl <= 0)
            {
                return;
            }

            foreach (var kvp in _outbox)
            {
                var message = kvp.Value;
                if (!message.ProcessedFromOutboxAtUtc.HasValue)
                {
                    continue;
                }

                // INVARIANT: the ttl is compared against ELAPSED time rather than added to the processed
                // timestamp, so no expiry instant is computed and there is nothing left to overflow -
                // subtracting two DateTime values always fits a TimeSpan and comparing two doubles is total.
                // AddMinutes was the one operation here that could throw, and OutboxProcessor calls
                // UpdateProcessedDate inside its unit of work AFTER dispatching and catches every exception
                // around the whole block, so a ttl too large to add stamped each message processed, logged one
                // line and left it never dispatched and never retried. A ttl no elapsed time can reach now
                // simply never expires anything, which is what such a ttl asks for.
                if ((DateTime.UtcNow - message.ProcessedFromOutboxAtUtc.Value).TotalMinutes < ttl)
                {
                    continue;
                }

                _outbox.TryRemove(kvp.Key, out _);
            }
        }

        public Task<IEnumerable<OutboxMessage>> GetUnprocessedBatch(Guid transactionId, CancellationToken cancellationToken = default)
                => Task.FromResult<IEnumerable<OutboxMessage>>(_outbox.Values
                        .Where(m => m.ProcessedFromOutboxAtUtc is null && m.BatchId == transactionId)
                        .ToList());

        // INVARIANT: a NON-TRANSACTIONAL pass-through. OutboxProcessor obtains the unit of work by
        // casting the single resolved outbox (Reliability-Store Facet Resolution), so the default
        // in-memory store must realize this facet or the drain never stamps a processed date and
        // never dispatches. There is nothing to enlist in-memory, so the operation runs as-is and
        // its failure travels out unchanged rather than being swallowed by a fake transaction.
        Task IUnitOfWork.ExecuteAsync(Func<CancellationToken, Task> operation, TransactionContext transactionContext, CancellationToken cancellationToken)
            => operation(cancellationToken);
    }
}
