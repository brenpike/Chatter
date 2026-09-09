using Chatter.MessageBrokers.Context;
using Chatter.MessageBrokers.Reliability.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Chatter.MessageBrokers.Reliability.Inbox
{
    public class InMemoryBrokeredMessageInbox : IBrokeredMessageInbox, IInboxDeduplicator, IProcessLifetimeStore
    {
        // A reserved-but-not-yet-completed receipt. Every other value is the monotonic timestamp the receipt
        // completed at, which is what makes both removals value-conditional and an in-flight reservation
        // structurally unremovable rather than merely checked for.
        private const long InFlight = long.MinValue;
        private const long MillisecondsPerMinute = 60000L;
        private const long MaximumSweepIntervalInMilliseconds = 60000L;
        private const int CapHeadroomNumerator = 15;
        private const int CapHeadroomDenominator = 16;

        // INVARIANT: this dictionary is the process's ONLY record of which message ids have been received, so the
        // instance holding it must outlive any DI scope. ScopedReceivedMessageDispatcher opens a FRESH scope per
        // delivery, so a per-scope instance starts every redelivery with an empty dictionary and deduplicates
        // nothing. Hence IProcessLifetimeStore and the process-lifetime registration; the relational and document
        // provider stores keep their markers outside the instance and are correctly per-operation.
        private readonly ConcurrentDictionary<string, long> _inbox;
        private readonly ILogger<InMemoryBrokeredMessageInbox> _logger;
        private readonly Func<long> _clock;
        private readonly long _deduplicationWindowInMilliseconds;
        private readonly long _sweepIntervalInMilliseconds;
        private readonly int _maxEntries;

        // INVARIANT: _count tracks the dictionary size instead of reading ConcurrentDictionary.Count, which takes
        // every bucket lock and would put a whole-table lock on the receive path.
        private int _count;
        private int _capEvictionWarned;
        private long _nextSweepDueAt;

        public InMemoryBrokeredMessageInbox(ILogger<InMemoryBrokeredMessageInbox> logger, ReliabilityOptions reliabilityOptions)
            : this(logger, reliabilityOptions, () => Environment.TickCount64)
        { }

        // The deduplication window is an ELAPSED interval, so it is measured from a monotonic millisecond source
        // rather than the wall clock, exactly as the circuit breaker's cooling period is: an NTP correction, a VM
        // migration or an operator moving the system clock would otherwise expire a receipt early or hold it past
        // its configured window. This constructor lets a test supply that monotonic source directly.
        internal InMemoryBrokeredMessageInbox(ILogger<InMemoryBrokeredMessageInbox> logger, ReliabilityOptions reliabilityOptions, Func<long> clock)
        {
            _inbox = new ConcurrentDictionary<string, long>();
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));

            if (reliabilityOptions is null)
            {
                throw new ArgumentNullException(nameof(reliabilityOptions));
            }

            _deduplicationWindowInMilliseconds = reliabilityOptions.InMemoryInboxDeduplicationWindowInMinutes * MillisecondsPerMinute;
            _maxEntries = reliabilityOptions.InMemoryInboxMaxEntries;
            _sweepIntervalInMilliseconds = Math.Min(_deduplicationWindowInMilliseconds, MaximumSweepIntervalInMilliseconds);
            _nextSweepDueAt = _clock() + _sweepIntervalInMilliseconds;
        }

        internal int EntryCount => Volatile.Read(ref _count);

        public async Task ReceiveViaInbox<TMessage>(TMessage message, IMessageBrokerContext messageBrokerContext, Func<Task> messageReceiver)
        {
            var id = messageBrokerContext.BrokeredMessage.MessageId;

            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("A brokered message must have a message id to be persisted in the inbox.", nameof(id));
            }

            // INVARIANT: the id is reserved before the receiver runs, so concurrent receipts of one
            // message id contend on the reservation and only the winner invokes the receiver.
            if (!TryReserve(id))
            {
                _logger.LogTrace($"Brokered message of type '{typeof(TMessage).Name}' with id: '{id}' was already received.");
                return;
            }

            SweepExpiredEntriesIfDue();
            EnforceEntryCap();

            try
            {
                await messageReceiver().ConfigureAwait(false);
            }
            catch
            {
                Release(id);
                throw;
            }

            _inbox.TryUpdate(id, _clock(), InFlight);

            _logger.LogTrace($"Brokered message of type '{typeof(TMessage).Name}' with id: '{id}' was successfully received and added to inbox.");
        }

        // An in-flight reservation and a receipt still inside its deduplication window both report true; an expired
        // one reports false whether or not a sweep has reclaimed it yet.
        public Task<bool> HasBeenReceived(string messageId, CancellationToken cancellationToken = default)
            => Task.FromResult(_inbox.TryGetValue(messageId, out var observed) && !HasExpired(observed));

        // INVARIANT: at most two TryAdd attempts, never an unbounded retry loop - a receive path that could spin
        // against a concurrent sweep would trade a bounded duplicate for an unbounded stall.
        private bool TryReserve(string id)
        {
            if (TryReserveFreshly(id))
            {
                return true;
            }

            if (!_inbox.TryGetValue(id, out var observed))
            {
                // A sweep reclaimed the entry between the failed add and this read, so one more attempt settles it;
                // a second failure is a genuine concurrent duplicate rather than a lost race with the sweep.
                return TryReserveFreshly(id);
            }

            if (!HasExpired(observed))
            {
                return false;
            }

            // Losing this update means another delivery re-reserved the expired id first, which makes this one a
            // duplicate of that reservation. The entry count is unchanged either way - the entry never left.
            return _inbox.TryUpdate(id, InFlight, observed);
        }

        private bool TryReserveFreshly(string id)
        {
            if (!_inbox.TryAdd(id, InFlight))
            {
                return false;
            }

            Interlocked.Increment(ref _count);
            return true;
        }

        private void Release(string id)
        {
            if (_inbox.TryRemove(new KeyValuePair<string, long>(id, InFlight)))
            {
                Interlocked.Decrement(ref _count);
            }
        }

        // Expiry is decided ON CONTACT rather than by the sweep, so the deduplication window is exact no matter
        // when the sweep last ran. The window is compared against ELAPSED time rather than added to the completion
        // timestamp, so no expiry instant is computed and there is nothing left to overflow.
        private bool HasExpired(long completedAt)
        {
            if (completedAt == InFlight || _deduplicationWindowInMilliseconds <= 0)
            {
                return false;
            }

            return _clock() - completedAt >= _deduplicationWindowInMilliseconds;
        }

        // The routine reclaim. It is elected by one receipt per interval and is deliberately QUIET: reclaiming
        // entries the window has already released is the store working as configured, not a condition to report.
        private void SweepExpiredEntriesIfDue()
        {
            if (_deduplicationWindowInMilliseconds <= 0)
            {
                return;
            }

            var now = _clock();
            var due = Volatile.Read(ref _nextSweepDueAt);

            if (now < due)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _nextSweepDueAt, now + _sweepIntervalInMilliseconds, due) != due)
            {
                return;
            }

            SweepExpiredEntries();
        }

        private int SweepExpiredEntries()
        {
            var swept = 0;

            foreach (var entry in _inbox)
            {
                if (!HasExpired(entry.Value))
                {
                    continue;
                }

                // INVARIANT: value-conditional removal. An entry another delivery has re-reserved since this
                // enumeration read it holds InFlight, no longer matches, and so cannot be swept.
                if (_inbox.TryRemove(entry))
                {
                    Interlocked.Decrement(ref _count);
                    swept++;
                }
            }

            if (swept > 0)
            {
                _logger.LogTrace($"Expired {swept} received message id(s) from the in memory inbox.");
            }

            return swept;
        }

        // The cap is an OOM safety valve, never the deduplication guarantee: it truncates the advertised window
        // rather than defining one, so it reclaims what the window has already released before it takes anything
        // the window still owes.
        private void EnforceEntryCap()
        {
            if (Volatile.Read(ref _count) <= _maxEntries)
            {
                return;
            }

            if (_deduplicationWindowInMilliseconds > 0)
            {
                SweepExpiredEntries();

                if (Volatile.Read(ref _count) <= _maxEntries)
                {
                    return;
                }
            }

            var evicted = EvictCompletedEntries();

            if (evicted == 0)
            {
                // Every entry is still in flight. The store GROWS past its cap rather than blocking or throwing:
                // blocking self-deadlocks, because the entries are in flight only while their handlers run and the
                // thread that would block is one that must run a handler to free a slot; and throwing surfaces as a
                // receive failure, so the redelivery meets the same state and a soft bound becomes a hard outage.
                _logger.LogTrace($"The in memory inbox holds {Volatile.Read(ref _count)} received message id(s), above its cap of {_maxEntries}, because every entry is still in flight.");
                return;
            }

            ReportCapEviction(evicted);
        }

        // Victim order is whatever the enumeration yields - deliberately not oldest-first and not least-recently
        // used. The cap is a safety valve, so paying for a recency structure on every receipt to order evictions
        // that only happen under memory pressure is the wrong trade. Evicting down to a headroom below the cap
        // amortizes one pass over a cap/16 receipts rather than running one on every receipt.
        private int EvictCompletedEntries()
        {
            var evictUntil = (int)((long)_maxEntries * CapHeadroomNumerator / CapHeadroomDenominator);
            var evicted = 0;

            foreach (var entry in _inbox)
            {
                if (Volatile.Read(ref _count) <= evictUntil)
                {
                    break;
                }

                if (entry.Value == InFlight)
                {
                    continue;
                }

                // INVARIANT: value-conditional removal on a completion timestamp, so an in-flight reservation can
                // never be a victim - the cap bounds memory, it does not cancel a receipt that is still running.
                if (_inbox.TryRemove(entry))
                {
                    Interlocked.Decrement(ref _count);
                    evicted++;
                }
            }

            return evicted;
        }

        private void ReportCapEviction(int evicted)
        {
            var configuredWindow = _deduplicationWindowInMilliseconds > 0
                ? $"{_deduplicationWindowInMilliseconds / MillisecondsPerMinute} minutes"
                : "disabled";

            var report = $"The in memory inbox reached its cap of {_maxEntries} received message id(s) and evicted {evicted} unexpired one(s). "
                       + $"Its configured deduplication window ({configuredWindow}) is no longer being honoured under this load, so a "
                       + $"redelivery inside that window can be handled again. Raise {nameof(ReliabilityOptions.InMemoryInboxMaxEntries)} or shorten "
                       + $"{nameof(ReliabilityOptions.InMemoryInboxDeduplicationWindowInMinutes)}.";

            // Losing the advertised window is a configuration problem the operator has to see, but a store under
            // sustained pressure evicts on nearly every receipt, so the warning fires exactly once per store.
            if (Interlocked.CompareExchange(ref _capEvictionWarned, 1, 0) == 0)
            {
                _logger.LogWarning(report);
                return;
            }

            _logger.LogTrace(report);
        }
    }
}
