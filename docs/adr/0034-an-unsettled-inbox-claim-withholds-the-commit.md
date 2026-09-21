---
status: accepted
date: 2026-09-20
---

# An unsettled inbox claim withholds the commit

ADR-0033 moved the relational inbox's claim ahead of the handler, so between the flush and the unit of work's
commit the claim exists in two places at once: as an entry in `TContext`'s change tracker, and as an already-flushed
write inside the ambient transaction. Nothing tied its durability to the handler's outcome. Each remediation undid
ONE of those places for ONE observed path, and every path nobody had enumerated became the next finding. This ADR
records the change that derives commit permission from the claim's own recorded outcome, why that is a derivation
rather than a longer list of handled cases, and what it leaves open.

**Amended in place, 2026-09-21.** This ADR is accepted and UNRELEASED, so it is corrected here rather than
superseded. The correction is confined to the CHANGE-TRACKER half of the split described below — the detach it
records as retained was REMOVED, and the undo is now `UnitOfWork<TContext>.ExecuteAsync` reconciling the tracker
with the rollback it owns (`docs/adr/0035-a-rolled-back-unit-of-work-reconciles-its-contexts-change-tracker.md`).
**Settlement is untouched**: the single grant, the register, the refusal at `PersistanceTransaction.CommitAsync`,
its ownership boundary, the raw-provider-commit slice and the eliminated class below all stand exactly as written.

## Context

### The claim lives in two places, and undoing one place at a time is what kept re-emitting findings

`TryClaimMessageIdAsync` stages the marker and calls `_context.SaveChangesAsync`. That single act puts the claim
into two stores with different undo mechanics:

- **The change tracker.** `_inbox.FindAsync` resolves from the identity map before it reaches the store, so a claim
  left tracked after its transaction rolled back is served to a redelivery arriving over that same scoped
  `DbContext`. The undo is `UnitOfWork<TContext>.ExecuteAsync` reconciling the tracker with the rollback it owns.
- **The transaction.** The flush pushed rows into the ambient transaction, where no detach reaches them. Their undo
  is a rollback, and `ReceiveViaInbox` neither owns nor issues one — `UnitOfWorkBehavior`'s single commit, or the
  unit of work's catch, decides that.

Three findings sit on that split, and they are three instances of one root rather than three defects:

1. **The change-tracker phantom on rollback.** A claim whose handler threw stayed tracked and suppressed a
   redelivery over the same context. First answered by the detach `66f969b` added to `ReceiveViaInbox`; that detach
   was later retired in favour of reconciling the tracker on the rollback itself, which is the general form of the
   same undo. ADR-0033 records the finding and ADR-0035 the mechanism that answers it.
2. **The flush surviving a swallowed handler failure.** The tracker undo above answers the identity map and nothing
   else.
   A caller that CATCHES `ReceiveViaInbox`'s rethrow and returns normally leaves the outer unit of work committing
   a transaction that still carries the flushed claim, so the marker becomes durable for a message nothing handled.
   That is the finding this ADR's decision answers.
3. **The unit of work's own commit failing after handler success.** The handler returned, so there is nothing for
   `ReceiveViaInbox` to observe, and it has already returned by the time the commit fails. Tracked by
   [issue #512](https://github.com/brenpike/Chatter/issues/512) and NOT closed here; see *#512 is unaffected* below.

The shape of the first two is identical: an undo aimed at one storage location, sized to the one control-flow path
the reviewer had walked. A third path — cancellation, a nested dispatch that never resumes, an early return added
later — would have been the next finding of exactly that shape.

### One positive grant, so the un-walked paths are not cases at all

`BrokeredMessageInbox` opens every claim UNSETTLED, in `InboxClaimRegister.Open`, at the moment
`TryClaimMessageIdAsync` returns the claim. It settles a claim in exactly ONE place: the `claim.Settle()` statement
that follows the handler await, reached only when the handler RETURNED. There is no second settlement site, and
none in the catch.

This is what makes the change a derivation rather than a longer list. A throw, a caller that swallows the rethrow,
a cancellation, a nested dispatch that never resumes, an early return added years from now — none of these is a
case anything handles. Each is a way of FAILING TO REACH the one grant, and failing to reach it is the default
state the claim was opened in. The key changed from *did an exception reach the commit point* — the caller's
control flow, which this package neither owns nor can see through a `catch` — to *is every claim on this
transaction settled*, which is ground truth the inbox itself recorded.

The POSITION of that single grant is what carries the derivation, so it is pinned rather than assumed: settling
BEFORE the handler instead of after reddens both refusal facts named below.

### Where the grant is read

`PersistanceTransaction.CommitAsync` is this package's single commit of a provider transaction. Every commit path
the package offers funnels through it — `UnitOfWork<TContext>.CompleteAsync` through its `UnitOfWorkTransaction`,
and a consumer committing through `IUnitOfWork.CurrentTransaction`, the `TransactionContext` container, or
`BrokeredMessageOutbox<TContext>`'s re-exposure of the transaction. It refuses while `InboxClaimRegister` reports
any unsettled claim on the transaction it wraps, and the refusal message names the message ids and the `TContext`
that claimed them.

**The refusal does not roll back.** Whoever began the transaction rolls it back. That is the ownership rule
`UnitOfWork<TContext>.UnitOfWorkTransaction` already carries — commit, rollback and dispose act only on a
transaction this unit of work began — and the refusal is deliberately confined to withholding permission rather
than seizing the lifecycle.

### The key is the `IDbContextTransaction` object, and no coarser key is available

`UnitOfWork.CurrentTransaction` and `UnitOfWork.BeginAsync` each build a FRESH `PersistanceTransaction` over the
same provider transaction, so state keyed on the wrapper is lost between the inbox's flush and the commit. The
register therefore keys a `ConditionalWeakTable` on the `IDbContextTransaction` itself and holds a SET of open
claims per transaction, because a dispatch nested inside another handler opens more than one claim on one
transaction. The stored value carries only strings — a value holding its own key keeps the table entry alive
forever, so an abandoned transaction carrying an unsettled claim would leak.

Keying on the `TContext` is not expressible at the reading end: `PersistanceTransaction` holds a transaction and no
context. Keying any coarser than one transaction lets an abandoned claim refuse a commit that has nothing to do
with it, and that is measured rather than argued — see the last row of the table below.

### What the suite pins, measured rather than argued

Five mutations were applied and counted, each on both target frameworks.

| Mutation | What reddens |
| --- | --- |
| Delete the unsettled-claim refusal in `PersistanceTransaction.CommitAsync` | Exactly `MustRefuseTheCommitWhenAHandlerSwallowedAClaimedMessagesFailure` and `MustRefuseTheCommitWhenOnlyOneOfTwoClaimsWasSwallowed`. Nothing else |
| Collapse settlement to a per-transaction flag the last settlement clears | Exactly `MustRefuseTheCommitWhenOnlyOneOfTwoClaimsWasSwallowed` |
| Delete the handler-throw detach `66f969b` added, while it was still the only undo of the tracker | Exactly `MustNotSuppressARedeliveryOverTheSameContextWhenTheHandlerThrewOnAFreshMessageId` and `MustNotSuppressARedeliveryOverTheSameContextWhenTheHandlerThrewOnAnExpiredMessageId` |
| Settle the claim BEFORE the handler instead of after | Both refusal facts named in the first row |
| Serve every transaction from one shared collection instead of keying on the transaction | Fifteen facts, among them `MustNotSuppressARedeliveryOverTheSameContextWhenTheHandlerThrewOnAFreshMessageId`, which runs a second `ExecuteAsync` over the SAME context, and `MustCommitTheClaimWhenTheHandlerReturns` |

**The first and third rows are DISJOINT, and that disjointness is the measurement that matters.** Settlement could
plausibly have made the tracker undo redundant: both answer a handler that did not return. It does not, and the
counting is how that is known rather than assumed. The tracker undo answers the IDENTITY MAP — a redelivery
arriving over the same scoped context reads the tracker before the store — while settlement answers DURABILITY.
Neither set of facts moves when the other mechanism is removed. The disjointness survives the detach's retirement:
what changed is which component performs the tracker undo, not that a separate mechanism is needed for it. With
the reconciliation in place, deleting the detach reddens nothing, and the third row's two facts are carried by the
reconciliation instead — measured, and recorded in ADR-0035.

Three facts are new, in
`src/Chatter.MessageBrokers.Reliability.EntityFramework/tests/UsingBrokeredMessageInbox/WhenReceivingViaInbox.cs`:

- `MustRefuseTheCommitWhenAHandlerSwallowedAClaimedMessagesFailure` is the honest oracle for the finding. Its
  DISCRIMINATING assertion is the THROW, not the empty store: on the defect the swallow leaves `ExecuteAsync`
  returning normally, so a variant that never reached a commit at all leaves the store empty either way and an
  empty-store assertion alone would be blind. The fact also asserts the swallow actually happened, so it cannot
  pass against an operation that never threw.
- `MustCommitTheClaimWhenTheHandlerReturns` is the anti-vacuity oracle. Without it, a refusal of EVERY commit would
  pass the fact above just as well.
- `MustRefuseTheCommitWhenOnlyOneOfTwoClaimsWasSwallowed` pins the per-claim reading. It orders the swallowed
  delivery FIRST, because a last-writer flag is only distinguishable when a settlement follows the claim that went
  unsettled.

The package's suite is 227 passed, 0 failed, 0 skipped per target framework, and `Chatter.MessageBrokers` is
unchanged at its 1357 baseline.

## Considered Options

### Option A — derive commit permission from the claim's own settlement (ACCEPTED)

The claim records its outcome where the commit point can read it, and the commit point refuses while any claim on
its transaction is unsettled. One grant, one reader, and no dependence on what the caller did with the exception.

### Option B — have the inbox roll the transaction back when the handler fails (REJECTED)

The obvious answer, and it is worse than the defect. Rolling back from inside the inbox strands `TContext` with NO
ambient transaction, so the context's next `SaveChangesAsync` AUTOCOMMITS — which reopens exactly the class
`MustRefuseToClaimOutsideATransaction` exists to prevent, a claim standing on its own with a failure between it and
the handler's work suppressing the message permanently. It also seizes lifecycle ownership of the transaction that
belongs to the unit of work, against the rule `UnitOfWorkTransaction` carries.

### Option C — doom the transaction at the provider (REJECTED)

Marking the transaction unusable so any later commit fails is SQL Server's `XACT_ABORT` and error 3930 and nothing
else: a provider dialect table this package would have to own and extend per provider. That is the same mistake
ADR-0033 rejected in its own Option D, for the same reason — the package targets EF Core generally, and the
question can be answered without a table.

### Option D — take a savepoint before the claim and roll back to it (REJECTED)

It closes by provider CAPABILITY rather than by construction. ADR-0033 already documents that EF Core's
`BatchExecutor` skips savepoints entirely when the connection reports `SupportsSavepoints` as false, which is what
`MultipleActiveResultSets=true` produces, logged as `SavepointsDisabledBecauseOfMARS`. A guarantee that evaporates
on a supported connection string is not a guarantee.

### Option E — a two-state marker column keyed on HANDLED rather than CLAIMED (REJECTED)

Writing the claim as `claimed` and promoting it to `handled` after the handler returns would put the same
derivation in the store itself, durably, and would survive a process crash as this in-process register does not.
It needs a new column on `InboxMessage` and therefore a migration every consumer generates and owns — this package
ships `IEntityTypeConfiguration` types applied inside the application's own `OnModelCreating`. This release
deliberately carries no schema change, and this is not a decision to make as a remediation step inside a review
loop.

## Decision

**Commit permission is derived from the claim's own recorded outcome.** `BrokeredMessageInbox<TContext>` opens
every claim unsettled against the `IDbContextTransaction` it flushed into, and settles it in exactly one place —
after the handler returns. `PersistanceTransaction.CommitAsync`, this package's single commit of a provider
transaction, refuses while any claim on that transaction is unsettled, names the message ids and the `TContext`,
and leaves the rollback to whoever began the transaction.

## Closed-by-Construction Acceptance Test

> What class of future finding does this make impossible, and why?

**ELIMINATED CLASS: a transaction THIS PACKAGE commits while carrying an inbox claim whose handler did not return.**

Not *a transaction committed after a swallowed exception*, and not *after a cancellation* — those are two members
of the class, and enumerating members is the shape this change exists to stop. The class is named by the grant: a
claim is settled by ONE statement reached on ONE condition, so any control flow that does not reach it leaves the
claim in the state it was opened in, and the commit point reads that state rather than the control flow that
produced it. A path added to `ReceiveViaInbox` later is refused by default, because default is what unsettled
means.

**The ownership boundary.** The refusal withholds permission and does nothing else. It issues no rollback, so the
transaction stays exactly as its owner left it and the rule `UnitOfWorkTransaction` carries — act only on a
transaction this unit of work began — is untouched. `UnitOfWork<TContext>.ExecuteAsync`'s catch then rolls back the
transaction it began, the delivery fails, and the broker redelivers.

**What this does NOT close: the raw-provider-commit slice.** A consumer that pulls
`context.Database.CurrentTransaction` and commits it directly on the EF API never passes through
`PersistanceTransaction`, so the gate never sees that commit and never has a chance to withhold it. The claim
becomes durable for a message nothing handled.

This is an OWNERSHIP LINE, not another un-enumerated path — one nameable boundary, stated once: the gate covers the
commits this package owns, and a commit issued on EF Core's own API is not one of them. Reaching it needs a
consumer to do BOTH of two things at once: bypass `IUnitOfWork` for the commit, AND swallow the dispatch failure.
Neither alone suffices. The instance the review reported — a handler swallowing a nested dispatch failure while the
unit of work commits — sits squarely INSIDE the covered region, and is refused.

**No deterministic oracle pins this slice, and none is claimed.** A test there would construct the bypass and then
assert that the claim was committed, which asserts the gap rather than any behaviour this package decided. Per
ADR-0027 that is stated plainly rather than papered over. The slice carries root cause, bounded impact and the two
review threads — [r4058599905](https://github.com/brenpike/Chatter/pull/511#discussion_r4058599905) and
[r4058761610](https://github.com/brenpike/Chatter/pull/511#discussion_r4058761610) — in
[issue #513](https://github.com/brenpike/Chatter/issues/513), which tracks it as work introduced by this change
rather than as an inherited residual.

**#512 is unaffected by this decision.** There the handler SUCCEEDS, so the claim SETTLES, and the unit of work's
own commit then fails for its own reason; the gate never fires, because there is nothing unsettled for it to read.
What #512 reports is the change-tracker half of the split this ADR opens with, on the one path `ReceiveViaInbox`
cannot observe because it has already returned. `CompleteAsync` is awaited inside `ExecuteAsync`'s `try`, so a
commit that throws there lands in the same catch and the tracker reconciliation ADR-0035 records runs for it.
Nothing in THIS decision closes #512, and nothing here should be read as closing it; its disposition is decided
against what shipped, not here.

## Consequences

- **A caller that swallows a dispatch failure can no longer commit its own work.** This is a behavioural break, and
  it is the point: such a caller previously committed the claim along with everything else in the transaction, and
  now gets an `InvalidOperationException` from the commit naming the message ids whose claims went unsettled. Its
  own work rolls back with them, and the broker redelivers. The refusal message says so and names the two ways out
  — let the handler return, or let its failure propagate.
- **A new internal type, `InboxClaimRegister`.** It is static rather than an injected collaborator, because
  `BrokeredMessageInbox<TContext>` is public and hand-constructed, so a channel supplied through its constructor
  would be absent wherever a consumer did not supply one — and a commit point reading an absent channel finds every
  claim settled. NO test pins that choice: a constructor parameter that does not exist cannot be left unsupplied by
  a fact. The two refusal facts do construct the inbox by hand, so they exercise the path a consumer takes rather
  than one a container wired up.
- **The register's entries are weak, and its values hold no transaction.** A `ConditionalWeakTable` keyed on the
  `IDbContextTransaction` releases its entry with the transaction, and `UnsettledClaim` carries only the message id
  and the context type name, so an abandoned transaction carrying an unsettled claim is collected rather than
  retained.
- **The tracker undo is a SEPARATE mechanism, not superseded by settlement.** It answers the identity map and
  settlement answers durability; the measured mutation sets are disjoint, as recorded above. Which component
  performs it moved — from ADR-0033's handler-throw detach to ADR-0035's reconciliation on the rollback — and the
  disjointness is unchanged by that move.
- **Three new facts, and no change to the existing ones.** The three are listed under *What the suite pins* above;
  nothing already in the suite changed meaning.

## References

- Issue #512 — the change-tracker residue of a commit that fails after the handler succeeded. Unaffected by this
  decision, and not claimed closed by ADR-0035 either.
- Issue #513 — the raw-provider-commit slice this decision does not cover; introduced by this change and tracked
  with full scope.
- Issue #380 — the concurrent-delivery finding whose remediation created the two-places split this ADR resolves.
- ADR-0006 — *Two-tier reliability: relational ambient-tx vs NoSQL stage-then-commit*. The participation-versus-
  ownership distinction that lets the inbox flush into a transaction it does not own.
- ADR-0009 — *Standalone Cosmos inbox: confirm, not infer, and fail loud*. The abandoned-marker class Option B
  would have reopened.
- ADR-0025 — *The unit of work refuses a retrying execution strategy*. Why a failed attempt is never re-executed,
  which is what leaves redelivery as the whole recovery story for a refused commit.
- ADR-0026 — *The relational inbox decides expiry at receive*. Owns `HasMarkerExpired` and the deduplication
  window.
- ADR-0027 — *An `INVARIANT:` comment names the oracle that falsifies it*. Why each claim above names its oracle
  and the mutation that reddens it, and why the raw-provider slice says plainly that no fact pins it.
- ADR-0033 — *The relational inbox claims the message id before the handler, inside the ambient transaction*. The
  claim-first mechanism and the no-transaction refusal.
- ADR-0035 — *A rolled-back unit of work reconciles its context's change tracker*. The identity-map half of the
  split above, and the decision that retired ADR-0033's handler-throw detach.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/InboxClaimRegister.cs`
  — the register, the weak key, and `UnsettledClaim`.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/PersistanceTransaction.cs`
  (`CommitAsync`) — the refusal and its message.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/BrokeredMessageInbox.cs`
  (`ReceiveViaInbox`, `TryClaimMessageIdAsync`, `ClaimedMessageId`) — where a claim is opened and settled.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/UnitOfWork.cs`
  (`UnitOfWorkTransaction`) — the ownership rule the refusal declines to override.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/tests/UsingBrokeredMessageInbox/WhenReceivingViaInbox.cs`
  — `MustRefuseTheCommitWhenAHandlerSwallowedAClaimedMessagesFailure`, `MustCommitTheClaimWhenTheHandlerReturns`
  and `MustRefuseTheCommitWhenOnlyOneOfTwoClaimsWasSwallowed`.
