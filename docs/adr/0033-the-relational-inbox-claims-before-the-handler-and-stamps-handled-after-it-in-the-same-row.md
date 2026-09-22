---
status: accepted
date: 2026-09-21
---

# The relational inbox claims before the handler and stamps handled after it, in the same row

The inbox exists so that a consumer does not have to be idempotent. A broker delivers AT LEAST ONCE; the inbox
turns that into AT MOST ONCE HANDLING by remembering which message ids it has already handled, inside the same
transaction as the handler's own writes. That contract is only as good as the record it keeps, and the record was
one row whose MERE EXISTENCE was read as *handled* — so the row could not distinguish a message id a delivery had
taken from a message id whose handler had finished. This ADR records the change that puts both states in the row
itself, and records the interruption analysis and the measurements that were the price of believing it.

Issue #514.

## Context

**What the row could not say.** `InboxMessage` carries exactly two properties, `MessageId` and a nullable
`ReceivedByInboxAtUtc`. The relational inbox wrote the row ONCE, after the handler returned, and every reader
asked only whether a row was there. A row therefore meant *some delivery of this id reached the point of writing
a row*, and nothing distinguished the delivery that had merely started from the one that had finished. That is a
single value doing two jobs, and every attempt to tell the two apart had to find the missing bit somewhere else.

**The state was held OUT OF BAND, and every window was a fresh defect.** The answer to *did the handler finish?*
lived in EF's identity map — a tracked entity in one state rather than another — and in registers this type kept
in memory across the handler call. Each of those was written at a moment that was NOT ATOMIC with the write it
described: the register was set after the flush, or before it, or in a `finally` that the commit had not
reached. So for every pair of writes there was a span in which the two disagreed, and each span was reachable by
a different interruption. The findings arrived one span at a time, and each fix closed one span and opened the
question of the next.

**The encoding, and it needs no DDL.** This is the headline.

| Row state | Meaning |
| --- | --- |
| No row | A FRESH message id. No delivery has claimed it. |
| Row present, `ReceivedByInboxAtUtc` **NULL** | CLAIMED. A delivery took the id; its handler has not completed. |
| Row present, `ReceivedByInboxAtUtc` **set** | HANDLED, at that instant. |

`ReceivedByInboxAtUtc` already existed and was already `DateTime?`. `InboxMessageConfiguration.Configure`
declares `HasKey(t => t.MessageId)` and `Property(t => t.MessageId).IsRequired()` and states nothing about the
timestamp's nullability, so EF maps it nullable by convention and the deployed column already permits NULL. **No
column is added, no column is widened, and no type changes.** What is added is an ANNOTATION —
`builder.Property(t => t.ReceivedByInboxAtUtc).IsConcurrencyToken()` — which changes the `WHERE` clause the
provider emits on an UPDATE and leaves the table shape alone. Oracle:
`UsingInboxMessageConfiguration/WhenConfiguring.MustTreatReceivedDateAsTheOnlyConcurrencyToken`; removing the
`IsConcurrencyToken()` call reddens that one fact and no other, measured on `net8.0`.

**Evidence that reading NULL as a new state is safe.** The flip is only safe if no already-released binary ever
wrote a NULL into that column, because such a row would silently change meaning on upgrade from *handled at an
unknown time* to *claimed, handler never completed*. A history search finds no such writer:
`git grep -n 'ReceivedByInboxAtUtc = null' master -- 'src/*' | grep -v '/tests/'` returns NOTHING, and the two
files on `master` that write one at all —
`src/Chatter.MessageBrokers.Reliability.EntityFramework/tests/Integration/WhenPurgingRetentionOnSqlServer.cs` (the
`inbox-undated` seed) and `tests/Creators/MessageBrokers/InboxMessageCreator.cs` — are both TEST sources, packed
into no `nupkg`. **So every row a `0.9.0` consumer holds carries a stamp, and reads as HANDLED under the new
encoding exactly as it read as *present* under the old one.**

### What the receive path does

`BrokeredMessageInbox<TContext>.ReceiveViaInbox` reads the marker with
`_inbox.FindAsync(new object[] { messageId }, cancellationToken)`, returns without invoking the handler when
`IsHandledWithinTheDeduplicationWindow` says the marker is a stamp still inside the window, and otherwise:

1. **Claims.** `ClaimMessageIdAsync` refuses outright when `_context.Database.CurrentTransaction` is null, then
   either `AddAsync`es a row with `ReceivedByInboxAtUtc = null` or takes the existing row, sets it to null, and
   FORCES the write with `_context.Entry(claim).Property(m => m.ReceivedByInboxAtUtc).IsModified = true`. It then
   calls `SaveChangesAsync`. This is a FLUSH, not a commit: the statement enters the ambient transaction and takes
   the row's lock there.
2. **Invokes the handler.**
3. **Stamps.** It assigns `claim.ReceivedByInboxAtUtc = DateTime.UtcNow` and calls `SaveChangesAsync` again,
   inside the same transaction.

`ReceiveViaInbox` issues no `Commit` and no `Rollback` of its own. `UnitOfWorkBehavior`'s single commit remains
the only one, which is what keeps the marker atomic with the handler's work — ADR-0006's rule for the relational
tier, unchanged. Oracle: `WhenReceivingViaInbox.MustFlushTheClaimWithoutCommittingIt`, which counts commits
through an `IDbTransactionInterceptor`.

The forced write matters because a RE-claim of a row that already carried NULL writes the value already there.
Without the force, EF sees no change and emits no statement — and a statement is the only thing that takes the
row's lock. Oracle: `Integration/WhenDeduplicatingInboxOnSqlServer.MustInvokeTheHandlerOnceWhenTwoRedeliveriesRaceAClaimNoHandlerCompleted`,
which asserts one handler invocation and observes the racing delivery blocked; deleting the forced `IsModified`
reddens EXACTLY THAT ONE FACT and no other, measured.

### The interruption table

Every row below is *what a reader of the table finds, and what the NEXT delivery of that id does*. `caller` means
any code between `ReceiveViaInbox` and the unit of work's commit.

| Interruption | What commits | What the next delivery does |
| --- | --- | --- |
| Before the claim flush — id null/empty/whitespace | No marker; the handler runs regardless | Fresh id. Handler runs. |
| The claim flush throws — no ambient transaction | Nothing; the refusal precedes every write | Fresh id. Handler runs. |
| The claim flush throws — concurrency token lost to a racing claim | Nothing; the unit of work rolls back | Fresh id or a committed claim, per the WINNER's outcome. |
| Crash after the claim, before the handler | Nothing — the transaction never commits | Fresh id. Handler runs. |
| The handler throws and PROPAGATES | Nothing; the unit of work rolls back and clears its tracker | Fresh id. Handler runs. |
| **The handler throws and a CALLER SWALLOWS it** | **A row with a NULL stamp, alongside the caller's own work** | **Reads UNHANDLED. Handler runs.** |
| Cancellation requested before the handler | Nothing; the throw precedes the claim | Fresh id. Handler runs. |
| Crash after the handler, before the stamp flush | Nothing — the transaction never commits | Fresh id. Handler runs; non-transactional side effects repeat. |
| The stamp flush fails and propagates | Nothing; the unit of work rolls back | Fresh id. Handler runs. |
| The stamp flush fails and a caller swallows it | A row with a NULL stamp | Reads UNHANDLED. Handler runs. |
| The unit of work's own commit fails | Nothing; rollback, and the tracker is reconciled | Fresh id. Handler runs. |
| An INDETERMINATE commit (ADR-0028) | Unknown — either the stamped row or nothing | Suppressed if the stamp landed, handled again if it did not. |
| Nested dispatch, one INNER delivery's failure swallowed | Each id's own marker, each carrying its own outcome | Each id independently, per its own stamp. |
| A caller commits the RAW provider transaction | Whatever is flushed at that instant — a NULL stamp if the handler has not returned | Reads UNHANDLED if unstamped. Handler runs. |
| The retention purge races a live claim | The purge's DELETE waits on the claim's row lock; the claim's own outcome decides the row | Per the committed row, or fresh if the purge took it. |

**The row that matters is the swallow.** A caller that catches the handler's exception and carries on commits its
OWN work together with a marker that says CLAIMED, NOT HANDLED — so the caller's work is kept and the message
stays handleable. The predecessor design could not produce that outcome: it either rolled the caller's work back
with the claim, or committed a marker that read *handled* for a message whose handler had thrown. Oracles:
`WhenReceivingViaInbox.MustCommitAnUnstampedClaimWhenACallerSwallowsTheHandlersFailure` and
`.MustCarryEachClaimsOwnOutcomeWhenANestedDeliverysFailureIsSwallowed`.

**The purge row is reasoning, not a fact.** No test in this repository races a purge pass against a live claim.
What is pinned is the eligibility half — the purge takes NULL-stamped markers at all, by
`Integration/WhenPurgingRetentionOnSqlServer.MustPurgeAnInboxMarkerClaimedButNeverHandled` and
`UsingReliabilityRetentionPurgeService/WhenPurgingRetentionOverSqlite.MustPurgeAnInboxMarkerClaimedButNeverHandledWhileSparingAFreshOne`
— and the ordering half is the store's row lock, stated here and pinned nowhere.

### Two fake oracles, caught by the work itself

Both were GREEN against the very defect they were written for. They are recorded because they are what the
acceptance bar costs to maintain, not because either survived.

**1. A staged stamp was indistinguishable from a flushed one.** The first oracle for *the stamp is flushed after
the handler returns, not merely staged* asserted the entity's tracked state. Staging the stamp instead of
flushing it reddened ZERO facts — every fact's unit of work commits afterwards and flushes the staged value
anyway, so the forbidden shape was UNPINNED and the assertion could not see the difference. It was fixed by
reading the row back FROM THE STORE, inside the still-open transaction, which is the only vantage point from
which a flush and a stage differ. That is
`WhenReceivingViaInbox.MustFlushTheStampWhenTheHandlerReturnsAndCommitIt`.

**2. The inherited race fact asserted a count the primary key already guaranteed.**
`MustPersistExactlyOneInboxRowWhenTwoReceiversRaceForTheSameMessageId` asserted the ROW count after two
concurrent deliveries. `MessageId` IS the primary key, so that count is one whether or not the store ordered
anything — the fact was green on a completely unordered race and pinned nothing about claiming. It was renamed
and re-pointed at the HANDLER count:
`Integration/WhenDeduplicatingInboxOnSqlServer.MustInvokeTheHandlerOnceWhenTwoDeliveriesRaceForTheSameMessageId`.

### The measurements

Taken by performing the mutation and counting, not by prediction. Framework coverage is stated per measurement.

- **The premise the plan flagged as UNPROVEN is ANSWERED: writing NULL over NULL DOES take the row's exclusive
  lock.** Two concurrent redeliveries racing a COMMITTED NULL-stamped marker invoke the handler ONCE, on both
  target frameworks, over three consecutive runs. The blocked-request poll observes the racing delivery WAITING
  on the lock rather than on a sleep, and the racer then loses on the concurrency token with a
  `DbUpdateConcurrencyException`. Facts:
  `Integration/WhenDeduplicatingInboxOnSqlServer.MustInvokeTheHandlerOnceWhenTwoRedeliveriesRaceAClaimNoHandlerCompleted`
  and `.MustMakeASecondDeliverysClaimWaitOnTheFirstsRowLock`. **The DELETE-then-INSERT re-claim fallback the plan
  held in reserve was therefore NOT NEEDED and is not built.**
- **Deleting the claim's flush reddens SIX facts**, on both target frameworks.
- **Committing the ambient transaction inside the claim reddens FIFTEEN facts** — and those fifteen are a BLAST
  RADIUS, not an oracle set. Ending the transaction early changes every step downstream of it, so most of the
  fifteen fail for reasons that have nothing to do with the claim's own contract. Presenting them as fifteen
  targeted oracles would itself be a false claim. The oracle for that shape is the single commit-counting fact
  `WhenReceivingViaInbox.MustFlushTheClaimWithoutCommittingIt`.
- **Deleting the forced `IsModified` reddens EXACTLY ONE fact and no other**:
  `Integration/WhenDeduplicatingInboxOnSqlServer.MustInvokeTheHandlerOnceWhenTwoRedeliveriesRaceAClaimNoHandlerCompleted`.
- **The suite: 233 passed, 0 failed, 0 SKIPPED, per target framework**, with Docker confirmed reachable so the
  SQL Server integration facts ran rather than skipping.

The per-`INVARIANT:` exclusivity counts for the receive path live on
`src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/BrokeredMessageInbox.cs`,
beside the mechanisms they measure, and are not restated here — per ADR-0027 the rationale is written once next
to the mechanism and cited, and a count copied away from its mechanism goes stale without its reader noticing.

## Considered Options

### Option A — put both states in the row, as NULL and not-NULL on the column that already exists (ACCEPTED)

One value, one writer per state, each written into the transaction at the moment the state changes. There is no
second place for the answer to live, so there is no window in which two places can disagree.

### Option B — claim first, and carry the handler's outcome in memory to a commit gate (REJECTED, on evidence)

The predecessor design. A claim row is written before the handler, and whether the handler completed is
remembered by this type — a tracked entity state and in-memory registers — and consulted by a gate at commit
time. It is rejected on the RECORD rather than argued against in the abstract: it was built, reviewed and
iterated over SEVEN review rounds in <https://github.com/brenpike/Chatter/pull/511>, and each round closed one
window between a write and the register describing it while leaving the next one reachable. The shape is what
generates the windows — a register set at a moment not atomic with the write it describes admits an interruption
between them — so a round that closes one window cannot close the class. Option A removes the register.

### Option C — add a second column, or a status enum (REJECTED)

`Claimed`/`Handled` as a discriminator, or a separate `HandledAtUtc`, says the same thing as Option A and costs a
BREAKING SCHEMA CHANGE on every deployed consumer, plus a backfill decision for existing rows. Option A reads the
identical two states out of a nullable column that is already there and already nullable. A second column would
also reintroduce the original defect in miniature: two columns can disagree, and something would have to keep
them consistent.

### Option D — keep the handler-first write and rely on the primary key (REJECTED — this is `master`)

The shipped behaviour. The row is written after the handler returns, so nothing is visible to a concurrent
delivery while the handler runs and both deliveries execute it; the backstop is the primary key, which keeps
DATABASE side effects once-only and does nothing about a handler's non-transactional ones. This is issue #380,
and it is the defect the claim exists to answer.

### Option E — re-claim by DELETE then INSERT (REJECTED, and unnecessary)

Held in reserve against the possibility that writing NULL over NULL would emit no statement, and therefore take
no lock, leaving two redeliveries of a committed claim unordered. The measurement above shows the FORCED write
does take the lock, so the fallback buys nothing and costs a row that briefly does not exist — a window in which
a third delivery reads the id as FRESH.

## Decision

**The inbox row records which of its message id's states it is in, and it is written into `TContext`'s ambient
transaction at the moment that state changes.** No row means a fresh id; a row with no timestamp means a CLAIM
whose handler has not completed; a row with a timestamp means the handler completed at that instant. The claim is
FLUSHED before the handler runs and the stamp is FLUSHED after it returns, both inside the handler's own
transaction, and neither is committed by this type.

Three readers agree on that encoding, because all three read the same column:

- `IsHandledWithinTheDeduplicationWindow` suppresses only a marker carrying a timestamp, so a NULL-stamped row
  never suppresses under any setting of the Deduplication Window. Oracles:
  `WhenReceivingViaInbox.MustInvokeHandlerForAClaimWithNoTimestampWhenDeduplicationWindowIsSet` and
  `.MustInvokeHandlerForAClaimWithNoTimestampWhenDeduplicationWindowIsUnset`.
- `HasBeenReceived` — the tier-neutral `IInboxDeduplicator` read — adds `m.ReceivedByInboxAtUtc != null` to both
  of its queries, so it answers for the HANDLER rather than for the row. Reporting received from a row's presence
  alone would tell a caller a message was handled while its handler was still running. Oracles:
  `.MustNotReportReceivedForAClaimWithNoTimestampWhenDeduplicationWindowIsUnset` and
  `.MustNotReportReceivedForAClaimWithNoTimestampWhenDeduplicationWindowIsSet`.
- `ReliabilityRetentionPurgeService.PurgeOnceAsync` takes a NULL-stamped marker as ELIGIBLE, because no cutoff on
  a NULL column can ever age one out and sparing them would accrue rows for the life of the table. Oracles:
  `Integration/WhenPurgingRetentionOnSqlServer.MustPurgeAnInboxMarkerClaimedButNeverHandled` and
  `UsingReliabilityRetentionPurgeService/WhenPurgingRetentionOverSqlite.MustPurgeAnInboxMarkerClaimedButNeverHandledWhileSparingAFreshOne`.

**`ReceivedByInboxAtUtc` is the inbox's sole concurrency token**, which is what makes a delivery whose claim lost
to a concurrent one FAIL its write rather than overwrite the winner.

## Closed-by-Construction Acceptance Test

> What class of future finding does this make impossible, and why?

**ELIMINATED CLASS: a committed marker whose state disagrees with whether its handler returned.**

Findings of the shape *"the inbox recorded X, but the handler did Y"* cannot exist — for any interruption, any
caller behaviour, and any number of concurrent deliveries — because the record and the handler's completion are
no longer two facts that have to be kept in step. There is ONE fact, the column, and the only thing that writes a
timestamp into it is the statement that runs after the handler returned, in the transaction that carries the
handler's work. An interruption anywhere either commits that transaction or does not: if it commits, the stamp is
there precisely when the handler returned; if it does not, no part of the marker survives. There is no third
place for the answer to be, so there is no span in which two places can disagree, so there is no NEXT window for
a subsequent finding to be about.

This is the difference from every round of the predecessor design. Each of those made one window absent. This
makes the windows uncountable-by-construction, because it removes the second location that the count was over.

## What this does NOT close

- **A handler that COMPLETES and whose stamp flush then FAILS leaves a NULL-stamped row, so the handler RE-RUNS
  on redelivery.** That is at-least-once handling, and a handler's non-transactional side effects repeat —
  exactly as they do on `master`, and exactly as they do whenever the unit of work's commit fails after a
  successful handler. The inbox's guarantee is at-most-once for work INSIDE the transaction; it never was, and is
  not now, a guarantee about side effects outside it.
- **Issue #380's fresh-id race is NARROWED, not closed at the level of the handler's external side effects.** Two
  concurrent first deliveries are now ordered by the claim's row lock, and the loser fails on the concurrency
  token instead of running the handler — measured above. What remains is the general at-least-once residual in
  the bullet above.
- **Message-id equality is still the `MessageId` column's COLLATION, not an ordinal comparison.** Unchanged by
  this decision and recorded, with root cause and bounds, under *the store's collation, not the application,
  decides message-id equality* in ADR-0026.
- **Nothing here pins the purge-versus-claim ordering.** Stated in the interruption table and pinned nowhere; the
  eligibility half is pinned, the race half is reasoning over the store's row lock.
- **The claim's refusal outside a transaction is a runtime read, not a compile-time impossibility.** A caller that
  resolves `IBrokeredMessageInbox` and calls `ReceiveViaInbox` outside a unit of work gets an
  `InvalidOperationException` naming the registration that fixes it. Oracle:
  `WhenReceivingViaInbox.MustRefuseToClaimOutsideATransaction`.

## Migration

**No DDL, and no backfill.** The column exists, is already nullable in every deployed schema, and no released
binary has written a NULL into it (evidence above). An upgraded consumer's existing rows all carry a stamp and
therefore all read as HANDLED, which is what they meant before. The only model change is the
`IsConcurrencyToken()` annotation, which alters the `WHERE` clause on an UPDATE and not the table.

**EF Core 9 and 10 nevertheless require a migration with an EMPTY `Up()`.** Both refuse to run against a model
with pending changes, and an added concurrency-token annotation IS a model change from the migrations snapshot's
point of view even though it produces no schema operation. So the consumer runs `dotnet ef migrations add` once,
finds `Up()` and `Down()` empty, and applies it — which advances the snapshot and leaves the table untouched. As
with every other model change in this package, the application generates and owns the migration
(`src/README.md`, *Reliability*).

**Both orders are safe.** An old binary against a migrated model reads and writes the same column with the same
nullability and simply does not emit the token predicate. A new binary against an un-migrated schema works too —
there is no missing column to fail on — but EF's pending-model-changes refusal will stop it first, which is why
the empty migration is required rather than merely tidy.

## Pointer ledger

Per ADR-0030's doctrine, anchors that this change staled in documents OUTSIDE this decision's scope are recorded
here rather than edited.

| Site | What is stale | Why it is recorded rather than corrected |
| --- | --- | --- |
| ADR-0025, *"It does not clear the change tracker"* (`:8`) | True of the REFUSAL, which throws before anything runs; no longer true of `ExecuteAsync` as a whole, which clears the tracker on a rollback of a transaction it began. | ADR-0025 is out of this decision's scope. The sentence is accurate about the path it is describing; a reader who generalises it is misled, which is what this row is for. See ADR-0034. |
| ADR-0025, the deferred Option B' narrative (`:76-77`, `:100`) | Describes `ReceiveViaInbox` re-reading through `FindAsync` on a second attempt, written when the receive path had one write. | Same scope boundary. The deferred design's reasoning is unaffected — a cleared tracker still forces a re-read — but its worked example now describes a two-write path. |
| `Chatter.MessageBrokers.Reliability.EntityFramework/CONTEXT.md` `_Avoid_` (`:10`) | Cites `MustNotDeclareADbContextField` as a live reflection guard. That fact was DELETED: the inbox now declares a `TContext` field deliberately, because flushing requires `SaveChangesAsync`. | Owned by the concurrent corrective to this package's `CONTEXT.md`. Surfaced, not edited. |

## Consequences

- **`BrokeredMessageInbox<TContext>` now declares a `TContext` field and calls `SaveChangesAsync` twice.** The
  reflection tripwire that forbade a `DbContext`-typed field (`MustNotDeclareADbContextField`) is GONE, and the
  rule it enforced is replaced by a narrower and more accurate one: this type FLUSHES but never COMMITS, pinned
  by counting commits through an `IDbTransactionInterceptor` rather than by denying a field. ADR-0006's
  relational-tier rule — the inbox marker commits with the handler's work and never on its own — is unchanged and
  is now pinned by an oracle that measures the commit rather than a proxy for it.
- **A claimed-but-unhandled marker is a real, durable row.** It is produced whenever a caller swallows a handler
  failure, it is read as unhandled by every reader, and it is reclaimed by the retention purge. A consumer that
  configures NO Deduplication Window runs no purge pass for the inbox and accrues those rows, exactly as it
  accrues handled ones.
- **A losing concurrent delivery surfaces `DbUpdateConcurrencyException` rather than running the handler.** That
  exception reaches the receive pipeline, which is the correct outcome — the message is not lost, the broker
  redelivers it, and the redelivery reads the winner's stamp. It is a behaviour change for a consumer that was
  previously getting two handler invocations and no exception.
- **Two `SaveChangesAsync` calls per delivery replace one, and the first takes a row lock held for the duration
  of the handler.** Two deliveries of the SAME message id serialise on that lock; deliveries of different ids do
  not contend. A long-running handler therefore holds a lock on its own id's row for its whole duration, which is
  the cost of ordering redeliveries by the store rather than by a read both could pass.
- **No version impact beyond the packages this branch already moves.** No public type, member or signature
  changes; the observable changes are the two flushes, the token annotation, and the purge predicate.

## References

- Issue #514 — the conflation of CLAIMED with HANDLED in one row; the occasion for this decision.
- Issue #380 (OPEN) — the concurrent-delivery race this decision orders at the store.
- <https://github.com/brenpike/Chatter/pull/511> — the predecessor design's seven review iterations, cited as the
  EVIDENCE that Option B's shape generates windows rather than as an argument against it.
- ADR-0006 — *Two-tier reliability: relational ambient-tx vs NoSQL stage-then-commit*. The rule that the
  relational inbox never commits its own marker; unchanged, and now pinned by a commit count.
- ADR-0026 — *The relational inbox decides expiry at receive*. Owns `IsHandledWithinTheDeduplicationWindow`, the
  Deduplication Window, and the collation residual; amended for this decision.
- ADR-0027 — *An `INVARIANT:` comment names the oracle that falsifies it*. Why every claim above names the fact
  that would refute it, or says plainly that none does.
- ADR-0028 — *The unit of work reports an indeterminate commit as a failure*. The indeterminate row of the
  interruption table.
- ADR-0030 — *The Cosmos tier contrast names the wrong relational symbol*. The pointer-ledger doctrine the
  section above follows.
- ADR-0034 — *A rolled-back unit of work reconciles its context's change tracker*. The reconciliation that makes
  the rollback rows of the interruption table hold across a retry on the same scoped context.
- `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Reliability/Inbox/InboxMessage.cs` — the two properties
  the encoding is carried in.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/BrokeredMessageInbox.cs`
  — `ReceiveViaInbox`, `ClaimMessageIdAsync`, `HasBeenReceived` and `IsHandledWithinTheDeduplicationWindow`, and
  the home of the per-`INVARIANT:` exclusivity counts.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/InboxMessageConfiguration.cs`
  — the `IsConcurrencyToken()` annotation and why it is not a column.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/ReliabilityRetentionPurgeService.cs`
  — the inbox predicate that takes a NULL stamp as eligible.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/tests/UsingBrokeredMessageInbox/WhenReceivingViaInbox.cs`,
  `.../tests/Integration/WhenDeduplicatingInboxOnSqlServer.cs`,
  `.../tests/Integration/WhenPurgingRetentionOnSqlServer.cs`,
  `.../tests/UsingReliabilityRetentionPurgeService/WhenPurgingRetentionOverSqlite.cs` and
  `.../tests/UsingInboxMessageConfiguration/WhenConfiguring.cs` — the facts named throughout.
