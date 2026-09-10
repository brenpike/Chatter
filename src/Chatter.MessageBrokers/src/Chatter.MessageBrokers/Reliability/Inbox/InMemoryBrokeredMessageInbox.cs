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
        private readonly Action<string> _beforeReReservingAnEntry;
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
        // INVARIANT: the supplied clock must only ever return NON-NEGATIVE readings, which Environment.TickCount64
        // does for the first ~292 million years of a process. That is the precondition the entry encoding below
        // depends on to keep a reservation and a completed receipt apart.
        internal InMemoryBrokeredMessageInbox(ILogger<InMemoryBrokeredMessageInbox> logger, ReliabilityOptions reliabilityOptions, Func<long> clock)
            : this(logger, reliabilityOptions, clock, null)
        { }

        // The reserve path's correctness turns on what can happen BETWEEN its read of an entry that is already
        // present and its conditional update of that entry: a concurrent sweep can remove the entry inside that
        // window, which makes the update fail for a reason that is not a competing reservation. Reproducing that
        // ordering with threads and timing would be flaky, so this constructor lets a test run its interference at
        // exactly that point. The callback runs INSIDE the reserve path, so whatever receipt it drives there must
        // complete synchronously - one that parked on a reservation of its own would still be holding it when the
        // reserve path resumed. Every production construction leaves the callback null.
        internal InMemoryBrokeredMessageInbox(ILogger<InMemoryBrokeredMessageInbox> logger, ReliabilityOptions reliabilityOptions, Func<long> clock, Action<string> beforeReReservingAnEntry)
        {
            _inbox = new ConcurrentDictionary<string, long>();
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _beforeReReservingAnEntry = beforeReReservingAnEntry;

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

        // Every entry's value carries BOTH the state it is in and the monotonic instant it entered that state: a
        // completed receipt stores its completion instant as it is, and a reservation stores its reservation
        // instant negated and shifted down by one. The shift is what makes a reservation taken at instant 0
        // negative too, so the two states never collide at any NON-NEGATIVE reading - which is every reading the
        // clock seam is required to produce. One reclamation rule then covers both of them, which is why neither
        // removal below needs a case for a reservation.
        private static long Reservation(long reservedAt) => -reservedAt - 1;

        private static bool IsReservation(long value) => value < 0;

        private static long InstantOf(long value) => value < 0 ? -value - 1 : value;

        public async Task ReceiveViaInbox<TMessage>(TMessage message, IMessageBrokerContext messageBrokerContext, Func<Task> messageReceiver)
        {
            var id = messageBrokerContext.BrokeredMessage.MessageId;

            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("A brokered message must have a message id to be persisted in the inbox.", nameof(id));
            }

            // INVARIANT: the id is reserved before the receiver runs, so concurrent receipts of one
            // message id contend on the reservation and only the winner invokes the receiver.
            if (!TryReserve(id, out var reservation))
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
                Release(id, reservation);
                throw;
            }

            // INVARIANT: conditional on THIS receipt's own reservation. A receipt that outran the window was
            // abandoned and its id may have been re-reserved or reclaimed since, and it must not overwrite
            // whatever took its place.
            _inbox.TryUpdate(id, _clock(), reservation);

            _logger.LogTrace($"Brokered message of type '{typeof(TMessage).Name}' with id: '{id}' was successfully received and added to inbox.");
        }

        // A reservation the window still honours and a receipt still inside that window both report true; once the
        // window has elapsed both report false, whether or not a sweep has reclaimed the entry yet.
        public Task<bool> HasBeenReceived(string messageId, CancellationToken cancellationToken = default)
            => Task.FromResult(_inbox.TryGetValue(messageId, out var observed) && !IsReclaimable(observed));

        // INVARIANT: at most two TryAdd attempts, never an unbounded retry loop - a receive path that could spin
        // against a concurrent sweep would trade a bounded duplicate for an unbounded stall.
        private bool TryReserve(string id, out long reservation)
        {
            reservation = Reservation(_clock());

            if (TryAddReservation(id, reservation))
            {
                return true;
            }

            if (_inbox.TryGetValue(id, out var observed))
            {
                if (!IsReclaimable(observed))
                {
                    return false;
                }

                _beforeReReservingAnEntry?.Invoke(id);

                // Losing this update means EITHER that another delivery re-reserved the reclaimable id first -
                // which makes this one a duplicate of that reservation - OR that the key is gone, because
                // TryUpdate also reports false for an absent key. The add below tells the two apart. The entry
                // count is unchanged on success here, since the entry never left.
                if (_inbox.TryUpdate(id, reservation, observed))
                {
                    return true;
                }
            }

            // Either the read above missed the entry or the update above found it already gone: a sweep reclaimed
            // it since the failed add, so one more attempt settles it. A second failure is a genuine concurrent
            // duplicate rather than a lost race with the sweep.
            return TryAddReservation(id, reservation);
        }

        private bool TryAddReservation(string id, long reservation)
        {
            if (!_inbox.TryAdd(id, reservation))
            {
                return false;
            }

            Interlocked.Increment(ref _count);
            return true;
        }

        private void Release(string id, long reservation)
        {
            if (_inbox.TryRemove(new KeyValuePair<string, long>(id, reservation)))
            {
                Interlocked.Decrement(ref _count);
            }
        }

        // INVARIANT: the single reclamation rule, the only place the window is read, and TOTAL - it takes no branch
        // on configuration, so EVERY entry becomes reclaimable once its instant is older than the window, in EVERY
        // configuration a host can start with. No entry state and no configured value can produce an entry that
        // nothing is able to remove. ReliabilityOptionsBuilder refuses a window below one minute for exactly that
        // reason: a rule this one could be switched off would leave a hung handler's reservation held for good and
        // the entry cap, which never takes a reservation the window still honours, with nothing left to evict.
        //
        // It decides ON CONTACT rather than waiting for the sweep, so the window is exact no matter when the sweep
        // last ran, and it asks the same question of a reservation as of a completed receipt: an id is remembered for
        // the window, and a reservation is honoured for the window. So a handler that has not returned holds its id
        // for the window and no longer - whether it is dead or merely slow. The window is compared against ELAPSED
        // time rather than added to the stored instant, so no deadline is computed and there is nothing left to
        // overflow.
        private bool IsReclaimable(long value) => _clock() - InstantOf(value) >= _deduplicationWindowInMilliseconds;

        // The routine reclaim. It is elected by one receipt per interval and is deliberately QUIET: reclaiming
        // entries the window has already released is the store working as configured, not a condition to report.
        private void SweepExpiredEntriesIfDue()
        {
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
                if (!IsReclaimable(entry.Value))
                {
                    continue;
                }

                // INVARIANT: value-conditional removal. The check above already spares a reservation the window
                // still honours; this is what spares an entry another delivery has completed or re-reserved SINCE
                // this enumeration read it, because such an entry holds a newer value and no longer matches.
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

            SweepExpiredEntries();

            if (Volatile.Read(ref _count) <= _maxEntries)
            {
                return;
            }

            var evicted = EvictEntriesWithoutAHonouredReservation();

            if (evicted == 0)
            {
                // Every entry holds a reservation the window still honours. The store GROWS past its cap rather
                // than blocking or throwing: blocking self-deadlocks, because the entries are in flight only while
                // their handlers run and the thread that would block is one that must run a handler to free a
                // slot; and throwing surfaces as a receive failure, so the redelivery meets the same state and a
                // soft bound becomes a hard outage. The growth is bounded IN TIME, unconditionally: the window is
                // mandatory and positive, so every one of these reservations stops being honoured once its lease
                // elapses and is then reclaimable like any other entry.
                _logger.LogTrace($"The in memory inbox holds {Volatile.Read(ref _count)} received message id(s), above its cap of {_maxEntries}, because every entry is still in flight.");
                return;
            }

            ReportCapEviction(evicted);
        }

        // Victim order is whatever the enumeration yields - deliberately not oldest-first and not least-recently
        // used. The cap is a safety valve, so paying for a recency structure on every receipt to order evictions
        // that only happen under memory pressure is the wrong trade. Evicting down to a headroom below the cap
        // amortizes one pass over a cap/16 receipts rather than running one on every receipt.
        private int EvictEntriesWithoutAHonouredReservation()
        {
            var evictUntil = (int)((long)_maxEntries * CapHeadroomNumerator / CapHeadroomDenominator);
            var evicted = 0;

            foreach (var entry in _inbox)
            {
                if (Volatile.Read(ref _count) <= evictUntil)
                {
                    break;
                }

                if (IsReservation(entry.Value) && !IsReclaimable(entry.Value))
                {
                    continue;
                }

                // INVARIANT: value-conditional removal, and the one entry the cap will not take is a reservation
                // the window still honours - the cap bounds memory, it does not cancel a receipt that is still
                // inside the window it was promised.
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
            var report = $"The in memory inbox reached its cap of {_maxEntries} received message id(s) and evicted {evicted} unexpired one(s). "
                       + $"Its configured deduplication window ({_deduplicationWindowInMilliseconds / MillisecondsPerMinute} minutes) is no longer being honoured under this load, so a "
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
