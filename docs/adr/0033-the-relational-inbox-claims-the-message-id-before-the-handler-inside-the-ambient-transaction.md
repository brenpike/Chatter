---
status: accepted
date: 2026-09-20
---

# The relational inbox claims the message id before the handler, inside the ambient transaction

`BrokeredMessageInbox<TContext>.ReceiveViaInbox` read the inbox marker, ran the handler, and staged the marker
afterwards. Two concurrent deliveries of one message id therefore both read nothing and both ran the handler;
only the primary key stopped the second row, and it stopped it at commit, long after the second handler's
non-transactional side effects had already happened (#380). This ADR records the change that makes the claim the
first durable act of the receive — an `INSERT` flushed into the ambient transaction ahead of the handler — why
that closes the class rather than narrowing it, and what it costs.

**Amended in place, 2026-09-21.** This ADR is accepted and UNRELEASED, so it is corrected here rather than
superseded. The correction is confined to the claim's IDENTITY-MAP half: the handler-throw detach this decision
introduced was REMOVED, measured to redden nothing, and the undo is now
`UnitOfWork<TContext>.ExecuteAsync` reconciling the change tracker with the rollback it owns
(`docs/adr/0035-a-rolled-back-unit-of-work-reconciles-its-contexts-change-tracker.md`). The claim-first ordering,
the no-transaction refusal, the absorption gate, the concurrency token and the closed class below are unchanged.
The pointer ledger's rows were re-anchored against the files as they stand at this amendment's date.

**Amended again, 2026-09-21.** `TryClaimMessageIdAsync` gained a SECOND refusal: it also refuses a transaction no
unit of work began, so the claim goes only into a transaction whose failure and whose commit this package
controls. ADR-0035 owns that decision, its measurements and the reason it is not a re-derivation of ownership;
this ADR cites it. Two things here are corrected for it — the caller-begun residual recorded below, which a claim
can no longer reach, and the exclusivity recorded for `MustRefuseToClaimOutsideATransaction`, which the second
refusal falsified until that fact was repaired.

## Context

**The read cannot order two deliveries, and no read can.** `_inbox.FindAsync` answers from a snapshot. Under
`READ_COMMITTED_SNAPSHOT` on SQL Server, and under MVCC generally, a delivery reading while another delivery's
uncommitted `INSERT` is in flight sees the pre-insert state and is *correct* to do so. Making the read stricter
does not help: an isolation level strong enough to order two readers is provider-specific, and a lock hint is
not expressible through `FindAsync` on a package that targets EF Core generally.

**The write does order them.** `Integration/WhenDeduplicatingInboxOnSqlServer.MustBlockADuplicateInboxInsertUntilTheHoldingTransactionResolves`
drives two real transactions against a SQL Server container and observes the second `INSERT` waiting on
`LCK_M_X` — an exclusive key lock held by the first, uncommitted transaction — with `READ_COMMITTED_SNAPSHOT`
switched on for that database. Snapshot isolation does not release that lock; it changes what READS see and
never what WRITES take. The fact tests the database, not this package, and it is the premise everything below
rests on.

**Every provider this package can run over orders the second writer the same way.** SQL Server takes the
exclusive key lock above. PostgreSQL's unique-index inserter finds the conflicting in-progress tuple and waits
on that transaction's id until it commits or aborts. SQLite admits one writer at a time. The mechanism differs;
the observable consequence does not — the second delivery's `INSERT` cannot complete until the first
transaction resolves, so the second handler cannot start until then either.

### The claim is the `INSERT`, not the read

`TryClaimMessageIdAsync` stages the marker and calls `_context.SaveChangesAsync` before `ReceiveViaInbox`
invokes the handler. The flush is what acquires the key lock. A second delivery's `FindAsync` still returns
nothing — correctly — and then flushes its own `INSERT` and blocks. When the first transaction commits, the
second's `INSERT` fails on the primary key; when it rolls back, the second's `INSERT` succeeds and that
delivery's handler runs.
`Integration/WhenDeduplicatingInboxOnSqlServer.MustInvokeTheHandlerOnceWhenASecondDeliveryRacesTheSameMessageId`
pins the first outcome and `MustInvokeTheHandlerOnTheWaitingDeliveryWhenTheClaimingDeliveryRollsBack` the
second. Moving the flush in `TryClaimMessageIdAsync` to after the handler reddens four facts.

Within a single delivery the ordering is pinned separately, by
`UsingBrokeredMessageInbox/WhenReceivingViaInbox.MustRecordTheClaimBeforeInvokingTheHandlerForAFreshMessageId`,
which observes the claim recorded at the moment the handler is entered rather than after it returns.

### The flush is not a commit, and the distinction is the whole design

`ReceiveViaInbox` issues no `Commit` of its own. `UnitOfWorkBehavior`'s single commit stays the only one, so the
marker and the handler's work are still atomic: a handler that throws leaves no claim in the **store** — nothing
was ever committed — and the message is handled on redelivery. The **change tracker** is a separate question
with a separate answer. The claim was flushed, so it is tracked; it is undone because
`UnitOfWork<TContext>.ExecuteAsync` reconciles the tracker with the rollback it owns, as
*A handler that throws is undone in the change tracker too* below records. ADR-0006 already states this in the
general form — *"Saving is participation:
it flushes the handler's staged work into whichever transaction is active"* (`0006-...md:162`) — and it is the
sentence that makes claim-first available at all.

`MustFlushTheClaimWithoutCommittingForAFreshMessageId` and
`MustFlushTheRefreshedClaimWithoutCommittingForAnExpiredMessageId` count commits through an
`IDbTransactionInterceptor`, asserting zero across a full receive and one after an explicit commit. Committing
the ambient transaction after the flush in `TryClaimMessageIdAsync` reddens seven facts.

### A handler that throws is undone in the change tracker too

`_inbox.FindAsync` resolves from the identity map before it reaches the store, so a claim left tracked after its
transaction rolled back is served to a redelivery arriving over that same scoped `DbContext` and suppresses a
message nothing ever committed. The undo is `UnitOfWork<TContext>.ExecuteAsync` reconciling the tracker with the
rollback it owns, which takes the claim back out of the identity map on both branches — the fresh insert and the
in-place refresh — and on every exit that reaches that rollback rather than on the handler await alone. The
mechanism, its ownership gate and its measured oracle sets are recorded in
`docs/adr/0035-a-rolled-back-unit-of-work-reconciles-its-contexts-change-tracker.md` and are not restated here.

The expired branch is why this matters beyond the lookup. There the claim is not a new entity: it is the marker
the `FindAsync` at `BrokeredMessageInbox.cs:104` loaded, mutated in place, and flushed. `ReceivedByInboxAtUtc` is
the concurrency token (`InboxMessageConfiguration.cs:22`), so a marker left tracked carrying a rolled-back
timestamp would arm the next refresh's `UPDATE` predicate from a value no store row holds — and the refreshed
stamp is also what `HasMarkerExpired` reads, so the redelivery's lookup would resolve a marker the deduplication
window ages out in the store but not in the identity map.

`UsingBrokeredMessageInbox/WhenReceivingViaInbox.MustNotSuppressARedeliveryOverTheSameContextWhenTheHandlerThrewOnAFreshMessageId`
and `.MustNotSuppressARedeliveryOverTheSameContextWhenTheHandlerThrewOnAnExpiredMessageId` are the oracles. The
pre-existing `.MustPropagateHandlerExceptionAndNotPersistInboxMessage` is blind to this defect: it asserts the
*store* is empty, which holds either way because nothing was ever committed, so it was left as it stands rather
than stretched to cover it.

**`ReceiveViaInbox` performs no detach of its own, and that is MEASURED.** This decision originally wrapped the
handler await in a catch that detached the claim. Removing that catch reddened NOTHING on either target
framework, and the two facts named above stay green through the reconciliation, so the local undo was retired
rather than retained — a net twelve lines fewer, with the behaviour still pinned. The claim's other half —
DURABILITY, the flushed write no tracker operation reaches — is answered in ADR-0034, which opens every claim
unsettled against the transaction it was flushed into, settles it only when the handler returns, and refuses the
commit while any claim on that transaction is unsettled. That covers the case the tracker cannot: a caller that
catches the rethrow above and returns normally, leaving the unit of work to commit the flushed claim. Deleting
ADR-0034's commit refusal reddens two facts of its own, disjoint from the two named above.

**What this does not close.** The other way this context can hold a claim no commit stands behind is the flush
succeeding, the handler succeeding, and the unit of work's own commit then failing; `ReceiveViaInbox` has already
returned and cannot observe it. `CompleteAsync` is awaited inside `ExecuteAsync`'s `try`, so a commit that throws
there lands in the same catch and both the rollback and the tracker reconciliation run for it. That path is not
introduced by this decision — it is present on `master` — and it is tracked by
[issue #512](https://github.com/brenpike/Chatter/issues/512), which stays open on its own terms; nothing recorded
here decides it.

The residual this decision once left on the tracker half was a transaction the CALLER began and owns:
`ExecuteAsync` rolls back and reconciles only a transaction it began itself, so a claim staged into a
caller-begun transaction stayed in this context's change tracker after that caller rolled back. **A claim cannot
reach that shape.** `TryClaimMessageIdAsync` refuses a transaction no unit of work began, so what the
reconciliation reaches is exactly what the inbox admits, and
`UsingBrokeredMessageInbox/WhenReceivingViaInbox.MustRefuseToClaimInsideATransactionNoUnitOfWorkBegan` is the
oracle — the ownership guard, or the registration in `UnitOfWork.BeginAsync`, reddens it when removed. The
ownership line itself is unchanged and still bounds what `ExecuteAsync` reconciles for OTHER participants;
ADR-0035 owns that boundary and the refusal that keeps the claim on the inside of it.
[Issue #513](https://github.com/brenpike/Chatter/issues/513) re-scopes accordingly: this half is eliminated and
the raw-provider-commit slice survives in the narrower form ADR-0034 records.

ADR-0034 narrows the durability half at one point and leaves it standing at the other. A handler failure a CALLER
swallows no longer reaches a commit, because the commit point refuses a transaction carrying an unsettled claim.
The path named above is the one where the handler SUCCEEDED — the claim settled, so nothing unsettled remains for
that refusal to read — so **#512 is unaffected by ADR-0034.**

### Why the refusal is load-bearing rather than defensive

`TryClaimMessageIdAsync` throws `InvalidOperationException` when `_context.Database.CurrentTransaction is null`,
before it stages anything. Without an ambient transaction the flush would **autocommit**, and an autocommitted
claim *is* the commit-first variant: a failure anywhere between that autocommit and the handler's work leaves a
marker suppressing a message nothing ever handled. That class is not hypothetical — it is the abandoned-marker
class the document tier needed a whole second phase to answer, recorded in ADR-0009 as *confirm, do not infer*.
The refusal makes it unrepresentable in this package rather than merely unlikely: there is no code path on which
this type writes a claim that can stand on its own.

`MustRefuseToClaimOutsideATransaction` is the oracle, and removing the guard reddens exactly that one fact and
nothing else in the suite. That exclusivity was REPAIRED rather than restated. The fact asserted only that the
refusal message contains `WithInboxBehavior`, which the second refusal — the ownership guard ADR-0035 records —
also contains, so from the moment a second guard existed, deleting the `CurrentTransaction` guard reddened
nothing and the exclusivity recorded here was false. The fact now also asserts a phrase unique to the
no-transaction message, and the ownership fact asserts one unique to ownership, so each guard has an oracle the
other cannot satisfy.

### Why the loser re-reads by key rather than reading a provider error code

The absorption path catches `DbUpdateException`, detaches the failed entry, re-reads the marker by key, and
decides on what it finds: a committed marker the deduplication window does not age out means another delivery
owns the id, so the handler is skipped; no marker, or an expired one, is a failure to report and the exception
is rethrown.

Deciding on the re-read rather than on `2627` or `23505` is ground truth over provider dialect — the error code
names *which constraint the provider thinks was violated*, while the re-read names *what is actually committed*,
which is the question being asked. It also means one catch serves both losers, because
`DbUpdateConcurrencyException` derives from `DbUpdateException`: the delivery whose `INSERT` hit the primary key
on a fresh id, and the delivery whose in-place refresh of an expired marker matched no row, arrive at the same
handler and are answered by the same read.

**The gate on the catch is the re-read's precondition.** The re-read runs on the same transaction as the flush,
so it can report rows that transaction itself wrote and has not committed. It is ground truth about what
*another* transaction committed only once the store has rejected this transaction's own write on that key, and
that is what the catch establishes before it touches anything: `ex.Entries.Count != 1 ||
!ReferenceEquals(ex.Entries[0].Entity, claim)` rethrows. Past that test the failing batch implicates the claim
and nothing else, so no row of this transaction's sits on the message id and the re-read answers the question
the paragraphs above claim it answers.

Gating on `Entries` merely *containing* the claim would not establish that. EF Core's relational batch throws
`new DbUpdateException(RelationalStrings.UpdateStoreException, ex, ModificationCommands.SelectMany(c => c.Entries).ToList())`,
so `Entries` carries the **whole batch** rather than the failing command alone. A claim that succeeded in a
batch some unrelated entry failed is still contained in `Entries`, which is exactly the shape that would absorb
a failure no handler ever ran for. `Count == 1` is what excludes it; simplifying the gate to a containment check
reopens the class.

The failed entry is detached after that test because it is still tracked, and re-staging or re-flushing it would
write it back on the unit of work's commit.

### The expired-id branch needs a concurrency token, and gets one without a migration

Two concurrent redeliveries of one **expired** id are not separated by the primary key: both refresh the same
existing row, so both issue an `UPDATE` and both would succeed. `InboxMessage.ReceivedByInboxAtUtc` is therefore
declared `IsConcurrencyToken()` in `InboxMessageConfiguration`, which carries the value each delivery read into
its own `UPDATE` predicate, so the delivery reaching the row second matches no row and surfaces
`DbUpdateConcurrencyException` — which the shared catch above already absorbs.

This is a model-snapshot annotation, not a schema change: the column already exists and its type is unchanged,
so the generated migration's `Up()` is empty and no DDL is emitted. `OutboxMessageConfiguration.cs:14` is the
precedent in this same package, where `ProcessedFromOutboxAtUtc` carries the same annotation.
`UsingInboxMessageConfiguration/WhenConfiguring.MustTreatReceivedDateAsAConcurrencyToken` and
`Integration/WhenDeduplicatingInboxOnSqlServer.MustInvokeTheHandlerOnceWhenASecondDeliveryRefreshesTheSameExpiredMessageId`
are the two oracles; removing `IsConcurrencyToken()` reddens exactly those two and nothing else.

### MARS, stated precisely

EF Core's `BatchExecutor` skips its rollback-to-savepoint when the connection reports `SupportsSavepoints` as
false, which is what `MultipleActiveResultSets=true` produces — logged as `SavepointsDisabledBecauseOfMARS`. The
detach and the re-read still execute in that configuration, and a `2627` with `XACT_ABORT` off leaves the
transaction usable. The savepoint is what would undo a partially-applied batch, so where it is skipped a claim
write that succeeded inside a failed batch stays applied — and the re-read, sharing that transaction, would
report this transaction's own uncommitted row and skip a handler that never ran.

The absorption **decision** is independent of the savepoint, but that independence is earned by the gate rather
than inherent in re-reading a row. Absorption is entered only for a batch the store rejected for the claim's own
write and nothing else, and a rejected write leaves no row of this transaction's on that key, so whatever the
re-read finds there was committed by some other transaction. The savepoint's presence or absence does not change
that.

**What the suite pins, measured rather than argued.** Fully inverting the gate reddens exactly two facts,
identically on both target frameworks:
`Integration/WhenDeduplicatingInboxOnSqlServer.MustInvokeTheHandlerOnceWhenASecondDeliveryRacesTheSameMessageId`
and
`Integration/WhenDeduplicatingInboxOnSqlServer.MustInvokeTheHandlerOnceWhenASecondDeliveryRefreshesTheSameExpiredMessageId`.
Deleting the gate outright reddens nothing, because that is the shape the suite was already green against. Those
two facts therefore pin one direction only — that a lone rejected claim is still absorbed. Per ADR-0027 the rest
is stated plainly rather than implied: **no fact in this repository pins the rethrow direction, and no fact
exercises a flush with savepoints disabled.**

That absence is a priced decision rather than an oversight. Pinning either would take a savepoint-disabling
transaction factory plus a faulting interceptor, roughly 130 lines of test machinery, and durable message loss
needs five conditions to hold at once, the first four of which roll back harmlessly on their own. The machinery
was weighed against that and declined.

## Considered Options

### Option A — claim by flushing the `INSERT` into the ambient transaction, ahead of the handler (ACCEPTED)

The store's own write ordering becomes the arbiter. Atomicity is preserved because the flush participates in a
transaction someone else commits.

### Option B — keep read-then-act and let the primary key catch the duplicate at commit (REJECTED)

This is the behaviour being replaced. The primary key does stop the second **row**, so database side effects
stay once-only; it stops it at the unit of work's commit, by which time the second handler has already run and
whatever it did outside the transaction has already happened. It narrows the class to non-transactional side
effects rather than closing it.

### Option C — have the inbox commit its own claim, then run the handler (REJECTED)

The obvious way to make a claim visible to a concurrent delivery, and the reason claim-first was previously
judged unavailable. It breaks the atomicity ADR-0006 decided: a crash, a rollback, or a throwing handler between
the claim's commit and the unit of work's commit leaves a committed marker for a message nothing handled, which
suppresses every redelivery forever. Option A obtains the same visibility ordering from the key lock without the
commit, which is why this one is not needed.

### Option D — branch on the provider's error code (REJECTED)

`2627` on SQL Server, `23505` on PostgreSQL, `SQLITE_CONSTRAINT_PRIMARYKEY` on SQLite: a dialect table this
package would have to own and extend per provider, in service of a question the store can be asked directly. The
re-read answers it on every provider with no table.

### Option E — strengthen the read with a lock hint or a serializable transaction (REJECTED)

`UPDLOCK, HOLDLOCK` on the lookup would order two readers, and is SQL Server syntax that `FindAsync` cannot
express. Raising the ambient transaction to serializable is provider-dependent in what it actually guarantees,
is not this package's isolation level to choose — the application opens the transaction — and would widen
locking for every handler on the pipeline in order to order the inbox lookup.

## Decision

**The relational inbox claims the message id before the handler runs, by flushing the claim into the ambient
transaction, and refuses to claim when there is no ambient transaction to flush into.** The flush is
participation, not a commit; `UnitOfWorkBehavior` remains the single commit point for the claim and the
handler's work together. A delivery that loses the claim re-reads the committed marker by key and either skips
the handler or reports the failure.

## Closed-by-Construction Acceptance Test

> What class of future finding does this make impossible, and why?

**Eliminated class: two concurrent deliveries of one message id both running the handler.**

Neither delivery can enter its handler without having completed an `INSERT` or `UPDATE` against the marker row
inside its own transaction, and the store admits exactly one such write at a time for one key. There is no
ordering of the two deliveries in which both writes succeed, because the second one waits — it does not read
stale state and proceed. The claim is not a flag the code checks and could forget to check; it is a write the
store adjudicates.

Three findings sit inside that class and are closed with it:

- **#380** — two concurrent deliveries of one *fresh* id both run the handler. Closed by the key lock: the
  second `INSERT` waits, then fails, and the loser skips.
- **#507** — the marker refresh races the retention purge. Under claim-first the refresh **precedes** the
  handler, so a purge can only invalidate a write no handler has depended on. `PurgeOnceAsync`'s
  `ExecuteDeleteAsync` blocks on the refresh's exclusive lock, re-evaluates
  `ReceivedByInboxAtUtc < inboxCutoffUtc` against the committed value, finds the marker fresh, and skips it.
- **ADR-0026's expired-id residual** — two concurrent redeliveries of one expired id both re-running the
  handler. Closed by the concurrency token plus the claim-first ordering, as recorded above and amended in
  ADR-0026 itself.

**Eliminated class: the absorption re-read returning this transaction's own uncommitted claim.**

The re-read shares the flush's transaction, so a claim write that survived a failed batch would be visible to it
and would skip a handler that never ran. The gate on the catch removes that ordering: absorption is entered only
for a batch the store rejected for the claim's own write and nothing else, and a rejected write leaves no row of
this transaction's on that key. Everything the re-read can find there was committed by another transaction. The
argument holds without EF Core's rollback-to-savepoint, which is skipped when the connection reports
`SupportsSavepoints` as false.

**What this does NOT close.** A single delivery whose handler has non-transactional side effects and whose
transaction then rolls back re-runs those side effects on redelivery. That is the at-least-once contract of the
whole reliability tier and is unchanged here. Message-id equality also remains the store column's collation's
to decide — ADR-0026 owns that residual and this decision does not touch it.

The gate carries three residuals of its own, and each of them ends in a redelivery rather than a skipped
handler:

- **A duplicate claim whose flush batch carries other entries is reported rather than absorbed.** The gate sees
  more than one entry and rethrows, the delivery fails, the broker redelivers, and the redelivery deduplicates
  against the committed marker.
- **A `DbContext` that table-splits, or that maps an owned type onto the inbox table, can put more than one
  `EntityEntry` on the claim's own modification command.** `Count > 1` then rethrows, so for that mapping a
  legitimate concurrent-claim absorption becomes a redelivery. Safe degradation rather than a dropped message,
  but a behaviour change for such a model.
- **`DbUpdateException.Entries` is a third-party contract.** EF Core documents it loosely, as the entries
  involved in the error, and this package multi-targets EF Core 8.0.27 and 10.0.0. The fail-closed direction is
  therefore stated explicitly: while `Entries` carries the whole batch, a mixed batch gives `Count > 1` and
  rethrows; were EF to narrow it to the failing command, an unrelated failure fails `ReferenceEquals` and
  rethrows. Drift in that contract degrades toward rethrow-and-redeliver and never toward a skipped handler.

## Consequences

- **Two saves under one commit.** ADR-0006:126 states that `UnitOfWorkBehavior`'s single `SaveChanges` is "the
  only commit point"; the relational tier now performs **two saves under one commit**. The commit-point claim is
  intact and the save-count clause is refined, not reversed. ADR-0006 is cited rather than edited: see the
  pointer ledger below.
- **A second concurrent delivery blocks for up to the first handler's duration.** The wait is bounded by
  `CommandTimeout` — 30 seconds by default on both SqlClient and Npgsql — after which the flush throws, the
  delivery fails, the broker redelivers, and the redelivery is deduplicated by the now-committed marker. A host
  with long handlers and a duplicate storm can therefore time out deliveries and hold a receiver slot for the
  duration of the wait. An application in that position raises `CommandTimeout`, shortens the handler, or
  accepts the redelivery.
- **The flush is change-tracker-wide.** A dispatch nested inside another handler pushes the outer handler's
  staged entries out early, into the same transaction. They stay atomic with the claim — one transaction, one
  commit — but a constraint or validation error on an outer entry surfaces at the claim rather than at the unit
  of work's commit.
- **A duplicate claim that shares its flush batch with other entries is reported rather than absorbed.** The
  absorption path is entered only for a batch the store rejected for the claim's own write and nothing else, so
  the change-tracker-wide flush above has a second visible effect: a concurrent duplicate that arrives in a
  mixed batch surfaces as a failed delivery, and the broker's redelivery deduplicates against the committed
  marker instead of the loser skipping in place. The trade is a redelivery in exchange for the re-read never
  answering from this transaction's own uncommitted row.
- **A host running the inbox outside a unit of work gets a loud refusal.** It previously got silent
  non-deduplication: the marker was staged and, with nothing to commit it, never became durable. The refusal
  names the context, the message id, and the two supported registrations, so the failure is diagnosable at the
  first delivery rather than discovered as duplicate handling later.
- **`BrokeredMessageInbox<TContext>` holds a `DbContext` field, and that is the mechanism.** The type needs
  `Database.CurrentTransaction` and `SaveChangesAsync`, so the field is required by the design rather than
  tolerated by it. The guarantee it used to stand in for — that the inbox never commits — is pinned directly, by
  the two commit-counting facts named above.

## Pointer ledger — citations this change leaves standing elsewhere

Per ADR-0030's doctrine of correcting only where history is not rewritten, the following anchors name an oracle
this change renamed or a line it shifted, in files no step of this change edits. They are recorded here rather
than corrected in place. Line numbers are as at this ADR's amendment date, 2026-09-21.

| Citation | What it says | What is true now |
| --- | --- | --- |
| `0006-...md:126` | "leaving `UnitOfWorkBehavior`'s single `SaveChanges` as the only commit point"; the inbox "calls no `SaveChanges` of its own"; `MustInvokeHandlerAndTrackButNotPersistInboxMessageForFreshMessageId` pins it | The commit point is unchanged. The inbox calls `SaveChangesAsync` as a flush. The named fact was replaced by `MustRecordTheClaimBeforeInvokingTheHandlerForAFreshMessageId`, `MustFlushTheClaimWithoutCommittingForAFreshMessageId` and `MustFlushTheRefreshedClaimWithoutCommittingForAnExpiredMessageId` |
| `0006-...md:126` | `MustNotDeclareADbContextField` "fails the build if a `DbContext`-typed field ever returns" | That fact no longer exists. A `DbContext` field is the mechanism, and the commit-counting facts pin what the tripwire approximated |
| `0028-...md:158` | `INVARIANT` block at `BrokeredMessageInbox.cs:18-31` | The block is at `BrokeredMessageInbox.cs:19-30` |
| `0028-...md:157-159`, `0028-...md:233` | `MustInvokeHandlerAndTrackButNotPersistInboxMessageForFreshMessageId` | Renamed as above. ADR-0028's claim — the marker is staged and never self-committed — still holds under the commit-counting facts |
| `0030-...md:20-22`, `0030-...md:157` | `BrokeredMessageInbox.cs:99` (the pre-read) and `:101-105` (the expiry gate) | The pre-read is at `:104` and the skip at `:106-110`. The pre-read-and-skip behaviour ADR-0030 defends is unchanged |
| `0030-...md:24-25`, `0030-...md:158-161` | `WhenReceivingViaInbox.cs:98` and `:179` | `MustNotInvokeHandlerOrAddSecondRowForDuplicateMessageId` is at `:258` and `MustSkipHandlerForAnyExistingMarkerWhenDeduplicationWindowIsUnset` at `:713`. Both facts exist and both still hold |
| `characterization-findings.md:9` (row 3) | The marker is added to the change tracker and "`SaveChangesAsync` is never called; the row is not persisted until the surrounding `DbContext` is saved externally" | The claim is flushed before the handler and is durable at the unit of work's commit. The row is still not committed by the inbox. That file is an observation log of behaviour as it stood and is not rewritten |

## References

- Issue #380 — two concurrent deliveries of one message id both running the handler; the occasion for this
  decision.
- Issue #507 — the marker refresh racing the retention purge; closed as part of the same class.
- ADR-0006 — *Two-tier reliability: relational ambient-tx vs NoSQL stage-then-commit*. The ambient-transaction
  rule this decision works within, and the source of the participation-versus-ownership distinction at `:162`
  that makes a pre-handler flush available.
- ADR-0009 — *Standalone Cosmos inbox: confirm, not infer, and fail loud*. The abandoned-marker class the
  refusal exists to keep unrepresentable here.
- ADR-0026 — *The relational inbox decides expiry at receive*. Owns `HasMarkerExpired`, the deduplication window
  the absorption path consults, and the expired-id residual this decision closes.
- ADR-0027 — *An `INVARIANT:` comment names the oracle that falsifies it*. Why each claim above names its oracle
  and the mutation that reddens it, and why the MARS section states plainly that no fact pins the absorption
  gate's rethrow direction or a flush with savepoints disabled.
- ADR-0030 — *Corrected only where history is not rewritten*. The doctrine the pointer ledger follows.
- ADR-0034 — *An unsettled inbox claim withholds the commit*. Answers the durability half of the claim, and
  records why #512 is untouched by it.
- ADR-0035 — *A rolled-back unit of work reconciles its context's change tracker*. Answers the identity-map half,
  retires this one's handler-throw detach, and owns the ownership refusal that keeps a claim inside the
  reconciliation's reach.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/BrokeredMessageInbox.cs`
  (`ReceiveViaInbox`, `TryClaimMessageIdAsync`) — the claim, the refusal, the flush and the absorption path.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/InboxMessageConfiguration.cs`
  — the concurrency token on `ReceivedByInboxAtUtc`.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/tests/Integration/WhenDeduplicatingInboxOnSqlServer.cs`
  — the blocking-premise fact and the three race facts, two of which —
  `MustInvokeTheHandlerOnceWhenASecondDeliveryRacesTheSameMessageId` and
  `MustInvokeTheHandlerOnceWhenASecondDeliveryRefreshesTheSameExpiredMessageId` — are the whole measured
  coverage of the absorption gate.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/tests/UsingBrokeredMessageInbox/WhenReceivingViaInbox.cs`
  — the ordering fact, the two commit-counting facts, and the two refusals:
  `MustRefuseToClaimOutsideATransaction` and `MustRefuseToClaimInsideATransactionNoUnitOfWorkBegan`.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/tests/UsingInboxMessageConfiguration/WhenConfiguring.cs`
  — `MustTreatReceivedDateAsAConcurrencyToken`.
