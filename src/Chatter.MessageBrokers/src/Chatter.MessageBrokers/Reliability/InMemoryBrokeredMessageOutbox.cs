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

        IPersistanceTransaction IUnitOfWork.CurrentTransaction => null;
        bool IUnitOfWork.HasActiveTransaction => false;

        public InMemoryBrokeredMessageOutbox(ILogger<InMemoryBrokeredMessageOutbox> logger, ReliabilityOptions reliabilityOptions)
        {
            _outbox = new ConcurrentDictionary<string, OutboxMessage>();
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _reliabilityOptions = reliabilityOptions ?? throw new ArgumentNullException(nameof(reliabilityOptions));
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
        // filtered out of it - and nothing else (observed). Moving the ceiling clause there reddens
        // MustSpendNoBatchSlotOnAMessageThatHasSpentTheAttemptCeiling and nothing else (observed).
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

        public Task UpdateProcessedDate(IEnumerable<OutboxMessage> outboxMessages, CancellationToken cancellationToken = default)
        {
            foreach (var outboxMessage in outboxMessages)
            {
                outboxMessage.ProcessedFromOutboxAtUtc = DateTime.UtcNow;
            }
            RemoveExpiredFromInboxOutbox();
            return Task.CompletedTask;
        }

        public Task UpdateProcessedDate(OutboxMessage outboxMessage, CancellationToken cancellationToken = default)
        {
            outboxMessage.ProcessedFromOutboxAtUtc = DateTime.UtcNow;
            RemoveExpiredFromInboxOutbox();
            return Task.CompletedTask;
        }

        // INVARIANT: this writes THROUGH to the stored row rather than to a copy, because the dictionary holds the
        // very instances a poll hands back - the same reference identity UpdateProcessedDate stamps a processed
        // date through. Recording onto a copy would leave a failing message due now with no attempts spent and
        // re-attempted on every poll, which is exactly what the no-op default interface implementation still does
        // for a store that does not override it. Oracle: MustHoldTheRecordedAttemptStateOnTheStoredRow, which
        // records the attempt through the interface and then reads it back out of a fresh poll. Recording onto a
        // copy of the message instead reddens six facts, every one of them an oracle for this same write-through:
        // that one, MustCountOneMoreDispatchAttemptWhenRecordingAnAttempt,
        // MustAccumulateDispatchAttemptsAcrossRecordedAttempts,
        // MustExcludeAMessageThatHasSpentTheConfiguredAttemptCeiling,
        // MustSpendNoBatchSlotOnAMessageThatHasSpentTheAttemptCeiling and
        // MustGiveTheBatchSlotOfAFailingMessageToTheNextMessage - and nothing else (observed).
        public Task RecordDispatchAttempt(OutboxMessage outboxMessage, DateTime nextAttemptAtUtc, CancellationToken cancellationToken = default)
        {
            outboxMessage.DispatchAttempts++;
            outboxMessage.NextAttemptAtUtc = nextAttemptAtUtc;
            return Task.CompletedTask;
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
                // UpdateProcessedDate inside its unit of work BEFORE dispatching and catches every exception
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
