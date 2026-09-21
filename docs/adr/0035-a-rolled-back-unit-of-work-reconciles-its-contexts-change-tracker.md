---
status: accepted
date: 2026-09-21
---

# A rolled-back unit of work reconciles its context's change tracker

`UnitOfWork<TContext>.ExecuteAsync` rolled its transaction back and left `TContext`'s change tracker holding every
entity the failed attempt had flushed, each in its post-flush state, describing writes the store does not hold.
Four remediations in a row each stepped around one of those entries at one site — the inbox detached its claim
when the handler threw, the absorption path detached the entry the store rejected, the outbox's failure path went
around the tracker entirely, and the outbox's claim stated its own original value — and the next site nobody had
walked became the next finding. This ADR
records the change that reconciles the tracker with the rollback in one place, why that eliminates a class rather
than extending a handled set, where the change deliberately stops, and what it costs.

## Context

### The root is EF's acceptance point, not any one exit

`SaveChangesAsync` calls `AcceptAllChanges` when the flush succeeds. Acceptance is keyed on the SAVE, not on the
COMMIT, so an entity flushed into a transaction that later rolls back is left `Unchanged`, carrying the values the
rolled-back write staged, with no pre-flush value surviving in the entry to restore from. An entity whose flush
FAILED is left `Added` or `Modified`, still carrying those same values and still due to be written by the next
`SaveChangesAsync` on that context.

`DbSet.FindAsync` resolves from the identity map before it reaches the store. So a retry, a redelivery, or any
later read over that same scoped `DbContext` is answered by a phantom: a row the tracker asserts and the store does
not carry. In the relational inbox that is a marker for a message nothing handled, and the message is skipped.

**ELIMINATED CLASS: a change-tracker entry describing a write a transaction this unit of work rolled back never
made.** The four remediations above are four members of it. A cancellation raised out of a flush, a nested dispatch
that never resumes, an early return added later, a companion entity failing a flush the claim shared — each is
another member, and each was reachable by the same reasoning that produced the first four.

### The correctness argument does not depend on the inbox

The inbox is where the class was observed, and it is not what makes the class wrong. After a rollback, a tracker
still reporting a row the store does not hold is ALREADY WRONG, for any entity and any reader. The reconciliation
is therefore stated as a property of the unit of work rather than as a fix for one of its callers: it is what makes
the tracker's report of `TContext` agree with what that context's transaction actually did.

`WhenExecutingUnitOfWorkOverSqlite.MustLeaveNoTrackedChangesWhenAUnitOfWorkItBeganRollsBack` is the oracle written
at that altitude — a bare `OutboxMessage` staged by an operation that throws, with the CHANGE TRACKER as the
discriminating observation and the store deliberately not asserted on, because nothing commits on that path and an
empty store reads the same either way.

### The `BegunHere` boundary is an ownership line

`_context.ChangeTracker.Clear()` runs only when `scope.BegunHere` is true. Where the unit of work ADOPTED a
transaction the caller began, it performs no rollback — `RollbackAsync` returns early for exactly that reason, and
`UnitOfWorkTransaction` carries the rule that commit, rollback and dispose act only on a transaction this unit of
work began. It also did not stage the state sitting on that tracker: the caller opened the transaction, the caller
owns what went into it, and the caller decides whether it stands. Discarding that state would throw away work this
unit of work neither began nor completes.

This is ONE NAMEABLE BOUNDARY, drawn along ownership, rather than a gap in an enumeration. The gate has an
exclusive oracle:
`WhenExecutingUnitOfWorkOverSqlite.MustLeaveTheCallersTrackedChangesAloneWhenTheCallerBeganTheTransaction`, which
proves the adopted branch RAN rather than assuming the shape produces it — the operation observes the caller's own
`TransactionId` and the transaction is still active after the failure, both of which hold only when `BegunHere` is
false. A unit of work nested inside another's transaction never reaches the reconciliation, which is the same rule
read from the other side. The residual this boundary leaves is tracked by
[issue #513](https://github.com/brenpike/Chatter/issues/513) and stated under *What this does NOT close* below.

### Why `BrokeredMessageOutbox`'s recorded rejection is superseded, and the collateral it correctly predicted

`RecordDispatchAttempt` carried a recorded rejection of this exact fix, on three grounds. Two are answered and one
was RIGHT, and the right one is why a companion change had to land first.

- **"It is this module's shared commit primitive, pinned by 26 facts, so the change is not local to the outbox."**
  True as a description of the surface, and answered by measurement rather than by argument: the reconciliation and
  its gate each have a small, counted, named oracle set on both target frameworks, and not one of those 26 facts
  moved beyond them. The counts are in the table below.
- **"It cannot clear safely when `BegunHere` is false."** Correct, and it is the boundary the section above draws.
  The rejection read that as a reason the fix was unavailable; it is instead the condition the fix is gated on.
- **"It would falsify the premise the `ExecuteUpdateAsync` bypass rests on."** CORRECT, and it named real
  collateral. The outbox's re-claim depended on tracker residue for its own `still unprocessed` predicate: a
  message the poll had tracked carried the `null` it was loaded with as the entry's ORIGINAL value, and
  `OutboxMessageConfiguration` maps `ProcessedFromOutboxAtUtc` as a concurrency token, so that original value IS
  the predicate. Reconciling the tracker leaves the message DETACHED, and `Update` on a detached entity sets
  original from current — which would make the claim's predicate read `= the stamp just written`, match no row, and
  leave a message the broker already took unprocessed for the next poll to publish again.

  `UpdateProcessedDate` therefore states that original value as `null` itself, so the predicate is the same whether
  the message it is handed is tracked or detached, and the claim no longer inherits anything from the tracker.
  Oracle: `WhenUpdatingProcessed.MustStateTheClaimAsUnprocessedWhenTheMessageIsDetached`; dropping the
  original-value statement reddens it and nothing else. That statement is what makes the reconciliation safe for
  the outbox, and it landed before it.

ADR-0031 recorded the falsified premise as the reason the bypass exists at all; it is amended there rather than
restated here, and the bypass and its oracle are unchanged.

### What the suite pins, measured rather than argued

Each row below was counted on both target frameworks, and the rows were counted IN THIS ORDER, each against the
shape that preceded it. The order matters to how the first row reads, so it is recorded rather than flattened.

| # | Mutation | What reddens |
| --- | --- | --- |
| 1 | Delete `_context.ChangeTracker.Clear()` from `ExecuteAsync`'s catch, with `ReceiveViaInbox`'s handler-throw detach still in place | Exactly `WhenExecutingUnitOfWorkOverSqlite.MustLeaveNoTrackedChangesWhenAUnitOfWorkItBeganRollsBack`, `WhenReceivingViaInbox.MustNotSuppressARedeliveryOverTheSameContextWhenACompanionWriteFailedTheClaimsFlush` and `WhenReceivingViaInbox.MustNotSuppressARedeliveryOverTheSameContextWhenAnExpiredMarkersRefreshFailedOutsideDbUpdateException`. Nothing else |
| 2 | Place the clear UNGATED, ignoring `BegunHere` | Exactly `WhenExecutingUnitOfWorkOverSqlite.MustLeaveTheCallersTrackedChangesAloneWhenTheCallerBeganTheTransaction` |
| 3 | Remove `ReceiveViaInbox`'s handler-throw detach, with the clear in place | NOTHING. `WhenReceivingViaInbox.MustNotSuppressARedeliveryOverTheSameContextWhenTheHandlerThrewOnAFreshMessageId` and `.MustNotSuppressARedeliveryOverTheSameContextWhenTheHandlerThrewOnAnExpiredMessageId` — the two facts that detach was added for — stay green through the reconciliation |

**Row 1's count of three is scoped to the shape it was taken against, and rows 1 and 3 must be read together.** At
that point the detach was what kept the two handler-throw facts green, so they were outside row 1's set. Row 3
shows the reconciliation carrying them on its own. So against the shape the two changes leave behind, deleting the
clear reddens FIVE facts rather than three — the three of row 1 plus the two of row 3. That fifth-and-fourth
addition is READ OFF row 3 rather than separately counted, and per ADR-0027 that provenance is stated plainly
rather than presented as a fresh measurement. `BrokeredMessageInbox.ReceiveViaInbox` names those two facts as
oracles for the reconciliation on exactly this basis.

The package's suite is 232 passed, 0 failed and 0 skipped per target framework, and `Chatter.MessageBrokers` is
unchanged at its 1357 baseline.

**The third row is the strongest evidence in this record that the primitive is right rather than being a fifth
patch.** The change REMOVED a mechanism instead of adding one: the detach, its `ClaimedMessageId.Marker` field, and
the `try`/`catch` that carried it are all gone, a net twelve lines fewer, and the behaviour those lines existed for
is still pinned by the two facts that pinned it before. A fix that merely extended the handled set could not have
retired a member of the set it extends.

### The reviewer's finding named two exits, and one of them is narrower than stated

The finding that occasioned this change described two ways `TryClaimMessageIdAsync` leaves a claim in the tracker.
Only one of them is reachable by a lone claim, and that is measured rather than reasoned from the source.

- **The absorption-gate rethrow is NOT reachable by a lone claim.** On EF Core 8.0.30 and 10.0.11 identically —
  the provider versions this package's two test legs resolve — a command fault produces a `DbUpdateException`
  whose `Entries.Count` is ONE, naming the failing command's entry.
  Faulting the claim's OWN write therefore makes the gate MATCH, and the method absorbs — detaching the entry
  itself on the way. Reaching the rethrow requires a COMPANION entity in the same change tracker, which is the
  nested-dispatch shape, and that is exactly how
  `MustNotSuppressARedeliveryOverTheSameContextWhenACompanionWriteFailedTheClaimsFlush` arranges it.
- **A cancellation, or any non-`DbUpdateException` out of the flush, is reachable exactly as the finding
  describes**, and it is the ROUTINE path: EF Core wraps a statement failure into a `DbUpdateException` but passes
  an `OperationCanceledException` through untouched, so `catch (DbUpdateException)` never sees it and the entry is
  left `Added` or `Modified`. `MustNotSuppressARedeliveryOverTheSameContextWhenAnExpiredMarkersRefreshFailedOutsideDbUpdateException`
  pins it on the expired branch, where the leftover entry carries the refreshed timestamp `HasMarkerExpired` reads.

The loss class stands. One of its two named paths is narrower than the finding states, and that is recorded so a
later reader does not size the class from the finding's prose.

### A named guard that does not guard

`Integration/WhenDrainingPastAPermanentlyFailingRowOnSqlServer` was named as the regression guard for the outbox's
original-value statement. It is NOT one, measured both before and after the reconciliation: the class opens its own
context per poll and calls `processor.Process` directly, so `UnitOfWork` appears nowhere in it and the detached
branch is unreachable from it. Its facts EXECUTED rather than skipped in both measurements, so their green is a
green about something else. The real oracle for that statement is the unit fact
`WhenUpdatingProcessed.MustStateTheClaimAsUnprocessedWhenTheMessageIsDetached`. This is written down so a reader
does not mistake the integration class's green for coverage of the claim's predicate.

## Considered Options

### Option A — reconcile the change tracker on the rollback the unit of work owns (ACCEPTED)

One statement, at the one place a rollback this type performs is issued, gated on the ownership rule the type
already carries. Every exit that can leave a phantom reaches it, including exits added later, because they reach it
by rolling back rather than by being enumerated.

### Option B — detach by entity STATE rather than clearing (REJECTED)

Detaching only `Added` and `Modified` entries would keep entities the operation merely READ, which is a real cost
this option was weighed for. It does not work, and that is MEASURED rather than argued: the handler-throw entry is
`Unchanged`, because a successful flush accepted it, while the flush-failure entries are `Added` or `Modified` —
after a companion failure two `Added` entries survive. No state partition covers both halves of the class, so the
partition would have to be widened per finding, which is the shape this change exists to stop.

### Option C — detach at each exit that can produce a phantom (REJECTED)

This is the enumerating patch this initiative already performed three times, and the reason a fourth exit kept
appearing. Each instance is correct for the control-flow path its author walked and silent about the next one; the
third row of the measurement table is that shape being retired rather than extended.

### Option D — record the behaviour and leave it (REJECTED)

The obvious residual, and it fails its own conditions. The impact is NOT bounded: the phantom is served to the next
read over that scope, so it can suppress a message nothing handled, and the class has re-emitted across the
initiative rather than sitting still. A recorded residual asserts that a decision was made and why; here the
decision available was cheap, local and measurable.

### Option E — clear on the adopted path too (REJECTED)

It would discard state a caller staged into a transaction this unit of work neither began, nor rolls back, nor
completes. The ownership rule `UnitOfWorkTransaction` carries is the same rule that keeps the refusal in
`PersistanceTransaction.CommitAsync` from issuing a rollback of its own, and widening one of them would not leave
the other coherent. The gate's exclusive oracle exists because this option is the mutation that would remove it.

## Decision

**A unit of work that BEGAN its own transaction clears its context's change tracker after rolling that transaction
back.** A unit of work that ADOPTED a caller's transaction leaves the tracker as it found it, because it rolls
nothing back and owns none of the state staged on it. The success path is untouched: a commit that stood leaves a
truthful tracker.

## Closed-by-Construction Acceptance Test

> What class of future finding does this make impossible, and why?

**ELIMINATED CLASS: a change-tracker entry describing a write a transaction this unit of work rolled back never
made.**

The key changed from *which exit produced the entry* — control flow, which a caller owns and which grows a member
per finding — to *did this unit of work roll its own transaction back*, which is one condition this type decides
for itself. Findings of the shape *"entry X survived a rolled-back attempt and was served to a later read"* cannot
exist over a transaction this unit of work began, for any X, any entity type and any exit, including an exit added
later, because the reconciliation is on the rollback rather than on the exit. An exit added to `ReceiveViaInbox`,
to a handler, or to any other participant reaches the rollback by failing, and reaching the rollback is the whole
condition.

The claim also stands on its own terms, independent of any caller: after a rollback, a tracker still reporting a
row the store does not hold is already wrong, so removing the class is a correctness property of `TContext` rather
than a guard for one consumer.

**What this does NOT close: the adopted-transaction shape.** Where a caller began the transaction and the unit of
work merely participates, `ExecuteAsync` rolls nothing back and clears nothing, so state that caller's operation
flushed stays tracked after the caller rolls back on its own. A claim staged by `ReceiveViaInbox` inside such a
transaction is one instance of it. This is the ownership line drawn above, not an un-enumerated exit: it is the
same boundary that keeps this type off a transaction it does not own. **No fact in this suite pins it** — nothing
in the suite drives `ReceiveViaInbox`, or any other participant, inside a caller-begun transaction — and per
ADR-0027 that is stated plainly rather than implied. It carries its root cause and bounds in
[issue #513](https://github.com/brenpike/Chatter/issues/513), which also tracks the raw-provider-commit slice on
the same ownership line.

**What this does not decide: #512.** A commit that throws inside `CompleteAsync` — the unsettled-claim refusal, or
a provider failure — is awaited inside `ExecuteAsync`'s `try`, so it lands in this same catch and both the rollback
and the reconciliation run for it. [Issue #512](https://github.com/brenpike/Chatter/issues/512) reports the
change-tracker residue of exactly that path. **This ADR does not claim #512 closed**; it records what the
reconciliation does on that path and leaves the disposition of the issue to be decided against what shipped.

## Consequences

- **A caller's own staged entities are discarded with the claim.** The reconciliation clears the whole tracker of
  `TContext`, not the participant's entries alone, so an application that staged its own work inside the same
  `ExecuteAsync` loses those entries too when the operation fails. They describe writes the same rolled-back
  transaction did not make, which is the point, but the entries are gone rather than reverted.
- **Entities loaded for READS are detached.** Clearing does not distinguish a read from a write. Objects the caller
  already holds survive as objects — they are simply no longer tracked, so a later `SaveChangesAsync` will not see
  edits made to them and a later `FindAsync` re-reads the store instead of returning the same instance. Option B
  above is the shape that would have kept them, and it is rejected on measurement.
- **An application that catches the failure OUTSIDE the unit of work and keeps using the same scoped context finds
  its entities detached.** This is the visible contract price. The alternative is the context continuing to report
  rows that do not exist, which is the defect.
- **A rollback that itself throws does not skip the reconciliation.** `CleanUpAsync` swallows a failed rollback and
  logs it, and the reconciliation runs after it regardless, because a tracker describing writes into a doomed
  transaction is wrong whether or not the rollback statement succeeded.
- **ADR-0028's indeterminate commit is neither claimed nor denied.** The reconciliation runs after a rollback
  attempt whose own outcome may be unknown, and detaching asserts nothing about the store in either direction.
- **The inbox lost a mechanism.** `ReceiveViaInbox` no longer wraps its handler await, and `ClaimedMessageId` no
  longer carries the marker it detached. Settlement — ADR-0034's durability half — is untouched and is still
  granted in exactly one place.

## References

- Issue #512 — `UnitOfWork<TContext>` change-tracker residue after a commit that failed. Not claimed closed here.
- Issue #513 — the ownership-line residual: the adopted-transaction shape above and the raw-provider-commit slice.
- Issue #380 — the concurrent-delivery finding whose remediation created the phantom-claim class this decision
  eliminates.
- ADR-0006 — *Two-tier reliability: relational ambient-tx vs NoSQL stage-then-commit*. The participation-versus-
  ownership distinction that lets a participant flush into a transaction it does not own.
- ADR-0027 — *An `INVARIANT:` comment names the oracle that falsifies it*. Why each claim above names its oracle and
  the mutation that reddens it, and why the adopted-transaction shape says plainly that no fact pins it.
- ADR-0028 — *The unit of work reports an indeterminate commit as a failure*. The commit outcome this decision
  neither claims nor denies.
- ADR-0031 — *Outbox selection is derived from durable attempt state*. Holds the `ExecuteUpdateAsync` bypass whose
  stated premise this decision falsified, amended there.
- ADR-0033 — *The relational inbox claims the message id before the handler*. The claim-first mechanism, and the
  handler-throw detach this decision retires.
- ADR-0034 — *An unsettled inbox claim withholds the commit*. The durability half of the claim, untouched here.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/UnitOfWork.cs`
  (`ExecuteAsync`, `UnitOfWorkTransaction`) — the reconciliation, the `BegunHere` gate and the ownership rule.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/BrokeredMessageInbox.cs`
  (`ReceiveViaInbox`, `TryClaimMessageIdAsync`) — the participant whose claim the reconciliation undoes, and the
  absorption gate whose rethrow exit needs a companion entity.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/BrokeredMessageOutbox.cs`
  (`UpdateProcessedDate`, `RecordDispatchAttempt`) — the stated original value that makes the claim's predicate
  independent of tracker residue, and the recorded rejection this decision supersedes.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/tests/UsingBrokeredMessageOutbox/WhenExecutingUnitOfWorkOverSqlite.cs`
  — `MustLeaveNoTrackedChangesWhenAUnitOfWorkItBeganRollsBack` and
  `MustLeaveTheCallersTrackedChangesAloneWhenTheCallerBeganTheTransaction`.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/tests/UsingBrokeredMessageInbox/WhenReceivingViaInbox.cs`
  — the two flush-exit facts and the two handler-throw facts the retired detach was added for.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/tests/UsingBrokeredMessageOutbox/WhenUpdatingProcessed.cs`
  — `MustStateTheClaimAsUnprocessedWhenTheMessageIsDetached`.
