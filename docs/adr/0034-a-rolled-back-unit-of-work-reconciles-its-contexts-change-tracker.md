---
status: accepted
date: 2026-09-21
---

# A rolled-back unit of work reconciles its context's change tracker

Entity Framework accepts changes when it SAVES, not when it COMMITS. A unit of work whose transaction rolled back
therefore left every entity its attempt had flushed tracked as though the write had landed, and `FindAsync`
resolves from the identity map before it reaches the store. A later operation on the same scoped `DbContext` then
read rows the database did not hold. This ADR records the reconciliation, and records the OWNERSHIP line that
decides when it runs.

Issue #512, which is an INHERITED defect on `master` rather than one this branch introduced.

## Context

**The mechanism.** `DbContext.SaveChangesAsync(CancellationToken)` defaults `acceptAllChangesOnSuccess` to
`true`, and acceptance moves every tracked entry to `Unchanged` with its current values promoted to original
values. That happens at the moment the SQL is issued, which is INSIDE the transaction and long before anyone
decides whether the transaction commits. `UnitOfWork<TContext>.ExecuteAsync` rolls its transaction back when the
operation throws, so the store discards the statements — and the tracker keeps reporting them as settled facts.

**Why that is observable rather than merely untidy.** `DbContext` is registered scoped, so a second operation in
the same scope gets the SAME tracker. `DbSet<T>.FindAsync` is specified to return a tracked entity WITHOUT
querying when the key is already in the identity map. So a retry, a compensating write, or a second delivery over
that scope reads the rolled-back entity and believes it. That is the #512 shape: a phantom claim surviving a
failed commit, visible only through a read that never reaches the database.

**What now happens.** In `ExecuteAsync`'s `catch` clause, after the rollback attempt and before the disposal,
`UnitOfWork<TContext>` calls `_context.ChangeTracker.Clear()` — gated on `scope.BegunHere`.

**The tracker is cleared WHOLESALE, not entry by entry.** A targeted detach of the entity that was flushed leaves
that entity's companions tracked — the same wrong answer, read through a different object. There is no smaller
correct unit than the tracker, because the transaction that was discarded is not partitioned by entity.

**The SUCCESS path is untouched**, because a commit that stood leaves a truthful tracker: the accepted state and
the stored state agree, which is exactly what acceptance was for.

### The `BegunHere` gate is an OWNERSHIP line, not an optimisation

`UnitOfWorkTransaction` captures ownership ONCE, when the transaction is begun, and carries it. Where
`BeginAsync` found an ambient transaction it returns `begunHere: false`, and from there the unit of work commits
nothing, rolls nothing back, and disposes nothing — it PARTICIPATES, flushing through its own
`SaveChangesAsync` into a transaction whose completion belongs to the caller.

The reconciliation follows that same line for the same reason. **A unit of work that adopted a caller's
transaction performed no rollback, so there is nothing to reconcile the tracker WITH** — the transaction is still
open and its outcome is still the caller's to decide. Clearing there would discard state the CALLER staged, in a
transaction the caller still intends to commit, which is a data-loss bug introduced in the name of fixing one.
Ownership is never re-derived from the context's ambient transaction; it is the flag captured at begin time.

### The two oracles

Both were measured by performing the mutation and counting, on BOTH target frameworks.

- **Deleting the reconciliation reddens exactly TWO facts and nothing else**:
  `UsingBrokeredMessageOutbox/WhenExecutingUnitOfWorkOverSqlite.MustLeaveNoTrackedChangesWhenAUnitOfWorkItBeganRollsBack`,
  which asserts the tracker directly, and
  `UsingBrokeredMessageInbox/WhenReceivingViaInbox.MustNotSuppressARedeliveryOverTheSameContextWhenTheCommitFailedAfterTheHandlerReturned`,
  which is the #512 shape end to end: a commit fails after the handler returned, and the next delivery over the
  same context must NOT be suppressed by the marker the failed attempt left tracked.
- **Reconciling WITHOUT the `BegunHere` gate reddens exactly one fact and nothing else**:
  `UsingBrokeredMessageOutbox/WhenExecutingUnitOfWorkOverSqlite.MustLeaveTheCallersTrackedChangesAloneWhenTheCallerBeganTheTransaction`.
  That fact is what makes the gate a decision rather than a coincidence — without it, the gate could be deleted
  and the suite would stay green.

### ADR-0028's indeterminate commit is neither claimed nor denied

The reconciliation runs after a ROLLBACK ATTEMPT whose own outcome is unknown — `CleanUpAsync` swallows a
rollback failure and logs it, because the causal failure is the operation's, not the rollback's. Clearing the
tracker asserts nothing about the store either way: it removes this process's belief about rows it can no longer
account for. A commit whose outcome is indeterminate is still reported as a failure, exactly as ADR-0028 records.

## The outbox collateral, and why it was required

Clearing the tracker changed what `BrokeredMessageOutbox<TContext>.UpdateProcessedDate` is handed, and that
required a second change in the same commit.

`OutboxMessageConfiguration` maps `ProcessedFromOutboxAtUtc` as a concurrency token, so EF emits the entry's
ORIGINAL value as the predicate on the claiming UPDATE — which is how the claim says *still unprocessed*. A
TRACKED message keeps the `null` it was loaded with, so the predicate reads `ProcessedFromOutboxAtUtc IS NULL`
and is correct. **But `Update` on a DETACHED message sets original FROM CURRENT**, and the current value is the
stamp the claim just assigned. The predicate would then read *= the stamp just written*, match ZERO rows, and
leave a message the broker had ALREADY TAKEN sitting unprocessed for the next poll to publish AGAIN.

A cleared tracker is exactly what makes the re-claim receive a detached message. So the claim now STATES its own
original value — `entry.OriginalValues[nameof(OutboxMessage.ProcessedFromOutboxAtUtc)] = null` — which emits the
same predicate whether the message arrived tracked or detached. Oracle:
`UsingBrokeredMessageOutbox/WhenUpdatingProcessed.MustStateTheClaimAsUnprocessedWhenTheMessageIsDetached`;
dropping the original-value statement reddens it and nothing else, measured.

## Considered Options

### Option A — clear the tracker on a rollback of a transaction this unit of work began (ACCEPTED)

The reconciliation is placed where the rollback is, gated by the same ownership flag the rollback is gated by, so
the two cannot drift apart.

### Option B — detach only the entities the failed attempt flushed (REJECTED)

Smaller in appearance and wrong in substance. The discarded transaction is not partitioned by entity, so a
targeted detach leaves every companion of a flushed entity tracked in its accepted state — the same phantom read,
reached through a different object. It also requires this type to know which entries an arbitrary caller's
operation touched, which it does not and should not.

### Option C — save with `acceptAllChangesOnSuccess: false` and accept explicitly after the commit (REJECTED)

This is the shape ADR-0025 records as the deferred Option B' for re-execution under a retrying strategy, and it
is a larger change than this defect needs: every `SaveChangesAsync` call site in the unit of work would have to
move to the two-argument overload and a matching `ChangeTracker.AcceptAllChanges()` would have to run on the
success path, which puts a NEW failure mode on the path that works. It also does nothing for a caller
who saves through the context directly inside the operation. Recorded as available, not adopted here.

### Option D — register `DbContext` as transient, or open a scope per attempt (REJECTED)

It would make the stale tracker unreachable by making it a different tracker, and it is not this package's
registration to change: the context's lifetime is the APPLICATION's, declared in its own `AddDbContext` call. A
package that only works under one lifetime the consumer did not choose is a worse contract than one that
reconciles what it broke.

### Option E — document the residual and leave the tracker alone (REJECTED)

The read that surfaces it — `FindAsync` resolving from the identity map — is the documented, correct behaviour of
the framework, so no amount of prose makes a caller's correct code safe. The unit of work is the type that rolled
the transaction back, so it is the type that knows the tracker is now describing writes that did not land.

## Decision

**A unit of work that BEGAN the transaction it rolls back clears its context's change tracker as part of that
rollback. A unit of work that ADOPTED a caller's transaction leaves the tracker exactly as it found it.**

The gate is the ownership flag captured at begin time, and it carries the same rule the type already states for
commit, rollback and dispose: act only on a transaction this unit of work began.

## Closed-by-Construction Acceptance Test

> What class of future finding does this make impossible, and why?

**ELIMINATED CLASS: a change-tracker entry describing a write that a transaction this unit of work rolled back
never made.**

Findings of the shape *"a later read over the same scope saw X, but the database never held X"* cannot exist for
any X this unit of work's own attempt flushed — because the rollback and the reconciliation are the same step,
gated by the same flag, on the same path. There is no ordering between them for a later change to get wrong, and
no entity-level judgment for a later change to get incomplete: the whole tracker goes.

The class is bounded by ownership rather than by entity type, which is what makes it a class and not a list. Any
entity, any operation, any provider: if this unit of work began the transaction and rolled it back, nothing it
flushed is still believed.

## What this does NOT close

- **State a CALLER staged into a transaction the caller began is untouched, deliberately.** If that caller rolls
  its own transaction back and keeps using the same context, the phantom is reachable again — and reconciling it
  is the caller's, because the caller owns both the transaction and what was staged into it. Stated on
  `ExecuteAsync` and pinned from the other side by
  `MustLeaveTheCallersTrackedChangesAloneWhenTheCallerBeganTheTransaction`.
- **A rollback that itself FAILED still clears.** The reconciliation runs after `CleanUpAsync` has swallowed any
  rollback failure, so a transaction whose rollback did not take still has its tracker cleared. That is the
  deliberate choice — the process cannot account for those rows either way, and continuing to believe them is
  strictly worse than forgetting them — but it means a cleared tracker is not evidence that the store rolled
  back. No test pins that distinction, and none is claimed.
- **Re-execution under a retrying execution strategy remains REFUSED.** Clearing the tracker on rollback is not
  the missing piece that would make a retry safe; ADR-0025 records why, and that refusal stands unchanged.

## Consequences

- **A caller that holds a reference to an entity across a failed `ExecuteAsync` finds it DETACHED.** Reads off
  that instance still work; a subsequent `Update` on it takes the detached path, where `Update` sets original
  from current. The outbox claim above is the one place in this package that depended on the difference, and it
  now states its original value rather than inheriting it.
- **The first read after a failed attempt goes to the DATABASE.** That is the point — it is what makes the read
  truthful — and it costs a round trip that the identity map was previously answering from memory.
- **`ReceiveViaInbox` over a scoped context is safe across a failed commit.** A claim or stamp the failed attempt
  flushed is no longer in the identity map, so the next delivery's `FindAsync` reaches the store and finds
  whatever actually committed. This is what lets ADR-0033's interruption table state *"Fresh id. Handler runs."*
  for every rollback row rather than *"depends on whether the scope was reused."*
- **The two packages move together.** The reconciliation is in
  `Chatter.MessageBrokers.Reliability.EntityFramework`; the `OutboxProcessor` behaviour it interacts with is in
  `Chatter.MessageBrokers`. Neither public surface changes.

## References

- Issue #512 — `UnitOfWork<TContext>` leaving rolled-back entities tracked, so a phantom claim survives a failed
  commit on the same scope. Inherited on `master`.
- ADR-0025 — *The unit of work refuses a retrying execution strategy rather than re-executing the handler*. Owns
  the refusal, and records the deferred `acceptAllChangesOnSuccess: false` design this ADR rejects as Option C.
- ADR-0027 — *An `INVARIANT:` comment names the oracle that falsifies it*. Why each claim above names its fact or
  says plainly that none exists.
- ADR-0028 — *The unit of work reports an indeterminate commit as a failure*. Why the reconciliation asserts
  nothing about the store.
- ADR-0031 — *Outbox selection is derived from durable attempt state*. Owns the failure-path tracker bypass, whose
  stated premise this decision amends.
- ADR-0033 — *The relational inbox claims before the handler and stamps handled after it, in the same row*. The
  interruption table whose rollback rows depend on this reconciliation.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/UnitOfWork.cs`
  — `ExecuteAsync`'s `catch` clause, the `BegunHere` gate, and `UnitOfWorkTransaction`'s ownership capture.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/BrokeredMessageOutbox.cs`
  — `UpdateProcessedDate`'s stated original value, and `RecordDispatchAttempt`'s tracker bypass beside it.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/tests/UsingBrokeredMessageOutbox/WhenExecutingUnitOfWorkOverSqlite.cs`,
  `.../tests/UsingBrokeredMessageOutbox/WhenUpdatingProcessed.cs` and
  `.../tests/UsingBrokeredMessageInbox/WhenReceivingViaInbox.cs` — the facts named throughout.
