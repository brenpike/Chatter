---
status: accepted
date: 2026-09-19
---

# The unit of work reports an indeterminate commit as a failure, because reporting success is strictly worse

`UnitOfWork<TContext>.ExecuteAsync` routes every exception raised out of `CompleteAsync` into one `catch`,
cleans up, and rethrows. A commit whose outcome the client cannot determine — one that became durable at the
server and then threw because the acknowledgement was lost — takes that same path, so the caller is told the
unit of work failed for work that may be committed. This ADR records that as an ACCEPTED RESIDUAL. It records
the residual as inherited rather than introduced, states what bounds it and what the bound requires, and
records why the remediation the review recommended is a change to a public contract rather than a fix.

## Context

**The window.** `CompleteAsync` awaits `_context.SaveChangesAsync(cancellationToken)` and then
`scope.CommitAsync(cancellationToken)`
(`src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/UnitOfWork.cs`).
Single-phase commit over a network has an outcome the client cannot always observe: the server can make the
commit durable and the acknowledgement can be lost to a dropped connection or a timeout, and the client then
sees an exception for work that stands. `ExecuteAsync` cannot tell that exception from one raised because the
commit did not stand, and it does not try. Both enter the `catch` at `UnitOfWork.cs:71-77`, which rolls back —
a no-op against a commit that already stood, and against a connection that is gone a rollback that itself
throws, which `CleanUpAsync` logs at Warning and swallows — disposes, logs the causal exception at Error, and
rethrows. The caller receives failure, and a broker redelivery or a caller retry then re-drives the message.

**The finding, treated as data.** This was raised as finding `cb35ad41` in the pre-PR review of
`bugfix/308-ef-reliability-durability`, titled *Indeterminate commit can durably succeed and still be surfaced
as a failed unit of work*, against `UnitOfWork.cs:67-76`. Its recommendation was to treat commit exceptions as
an indeterminate outcome and introduce an application-verifiable transaction or outcome record. It was raised
in the review loop's SECOND iteration and is the finding on which that loop exited with `planner-escalation`
rather than with a patch.

**Inherited, not introduced — from the bytes at `master`.**
`git show master:src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/UnitOfWork.cs`
shows `CompleteAsync` already ending in `await scope.CommitAsync(cancellationToken);`, its only call site
already inside the `try`, and the handler already written as:

```csharp
catch (Exception ex)
{
    await RollbackAsync(scope, ct);
    _logger.LogError(ex, "Error occurred during unit of work");
    throw;
}
```

So `master` already routed every commit exception — in-doubt or not — into one catch, rolled back and rethrew.
This branch changed two things, and neither is the routing. It changed WHICH exception is reported: cleanup now
runs through `CleanUpAsync`, so a rollback or disposal that fails on top of a causal failure is logged at
Warning instead of replacing the exception the caller sees
(`MustRethrowTheOperationExceptionWhenRollbackAlsoThrows`,
`MustRethrowTheOperationExceptionWhenDisposeAlsoThrows`,
`MustRethrowTheOperationExceptionWhenBothRollbackAndDisposeThrow`, and for the commit phase specifically
`MustPropagateTheCommitExceptionWhenRollbackAlsoThrows`, all in
`tests/UsingBrokeredMessageOutbox/WhenExecutingUnitOfWorkOverSqlite.cs`). And it changed which token cleanup
honours: `CancellationToken.None`, so a cancelled operation does not skip its own rollback. The in-doubt window
is a property of single-phase commit; no edit in this diff widened it.

**What no test here can currently show, stated rather than glossed.** The repository's commit fault fires
INSTEAD of committing: `FaultingRelationalTransaction.CommitAsync` is
`_faults.HasFlag(TransactionFaults.Commit) ? throw new TransactionFaultException(TransactionFaultPhase.Commit) : base.CommitAsync(cancellationToken)`
(`tests/Support/FaultingRelationalTransactionFactory.cs`). `MustPropagateTheCommitExceptionWhenRollbackAlsoThrows`
accordingly asserts the row is ABSENT from a fresh context afterwards — it pins the commit-did-not-stand half
and cannot produce the landed-but-unacknowledged one. ADR-0025 records the same gap for its Option B', and
names the same instrument that would close it: an `IDbTransactionInterceptor.TransactionCommitted` that throws
AFTER a real commit has gone through. Every claim below about the in-doubt case is therefore reasoning about
single-phase commit rather than an assertion pinned by a fact in this repository, and is marked where it
matters.

**Bounded impact, and the three things the bound requires.** `BrokeredMessageInbox<TContext>.ReceiveViaInbox`
stages its marker with `_inbox.AddAsync` and calls no `SaveChangesAsync` of its own, and `Extensions.cs`
resolves the reliability behaviours in the canonical order `[OutboxProcessingBehavior<>, UnitOfWorkBehavior<>,
InboxBehavior<>]`, so the inbox behavior runs INSIDE `UnitOfWorkBehavior`'s `ExecuteAsync` and the marker is
committed by that unit of work's single save and commit, alongside the handler's own work. A commit that stood
durably therefore made the dedup marker durable in the same transaction, and the next delivery of that message
finds it. The bound holds only where all three of these hold:

- **The inbox behavior is registered.** `WithInboxBehavior<TContext>()` registers the unit of work itself
  (`Extensions.cs:46`), so the two are never separated in that direction — but `WithUnitOfWorkBehavior<TContext>()`
  alone stages no marker, and there is then nothing to suppress the redelivery. ADR-0006's at-least-once drain
  contract is what remains.
- **The message carries a non-whitespace `MessageId`.** `ReceiveViaInbox` bypasses the inbox entirely otherwise
  (`MustBypassInboxAndInvokeHandlerWhenMessageIdIsNullEmptyOrWhitespace`).
- **The redelivery arrives while the marker still suppresses.** With `InboxDeduplicationWindow` unset — the
  default — any existing marker suppresses (`MustSkipHandlerForAnyExistingMarkerWhenDeduplicationWindowIsUnset`);
  with a window configured, suppression is bounded to it. ADR-0026 owns that decision.

**The suppression is `ReceiveViaInbox`'s lookup, not `HasBeenReceived`.** On the relational tier the redelivery
is suppressed by `ReceiveViaInbox`'s own `_inbox.FindAsync(new object[] { messageId }, cancellationToken)`,
pinned by `MustNotInvokeHandlerOrAddSecondRowForDuplicateMessageId`. `HasBeenReceived` answers on the same
terms — ADR-0026 records that deliberately — but it is the `IInboxDeduplicator` port read and has NO production
call site in this repository; a grep for `.HasBeenReceived(` across `src/` outside `tests/` returns NOTHING —
all 18 matches of that call form sit under a `/tests/` path. The interface declaration, the tier
implementations and the doc comments match the BARE token, which carries no leading dot. Naming it as the
suppressing symbol would be wrong, so the record names the lookup that actually runs.

**What the marker does not bound.** The marker stops the handler running twice. It does not stop the failure
being REPORTED once for work that committed, so the application's own failure handling — the recovery pipeline,
an Error log, an eventual dead-letter — reacts to a failure that did not occur. That cost is real and is the
residual.

## Considered Options

### Option A — report the failure, and record the residual (ACCEPTED)

Leave the catch as it is. The caller receives the commit's own exception, and this document is the record.

### Option B — a distinct in-doubt exception type (REJECTED: design change, not remediation)

Raise an `InDoubtCommitException` (or return an outcome) when the commit phase throws. This changes WHEN and
WHAT the public `IUnitOfWork.ExecuteAsync` contract throws. `IUnitOfWork` is public in `Chatter.MessageBrokers`
(`Reliability/IUnitOfWork.cs`), and `Chatter.MessageBrokers.Reliability.EntityFramework` carries
`<Version>0.9.0</Version>` and is being released from this branch. The interface's own doc comment states the
split ADR-0006 decided — "Relational-only... The document tier never implements this interface" — so a new
terminal outcome on `ExecuteAsync` is a change to the two-tier reliability port, not to this type alone: the
document tier's batch-lifecycle behavior would have to answer the same question or the two tiers would disagree
about what a failed unit of work means. A further problem is that the branch cannot be WRITTEN honestly today:
nothing in `ExecuteAsync` can classify a provider exception as in-doubt without provider-specific knowledge, and
the repository has no instrument that produces a real in-doubt commit to test the classification against (see
the paragraph above). This is a planner's decision, and it surfaced at the review loop's second iteration.

### Option C — report success, with a warning (REJECTED: strictly worse)

Reporting SUCCESS on an in-doubt commit is strictly worse than reporting failure, and the asymmetry is the
whole argument. Reporting failure has two outcomes: the commit stood, in which case the redelivery is
suppressed by the marker above and the cost is a spurious failure report; or the commit did not stand, in which
case the retry correctly redoes the work. Reporting success has two outcomes too: the commit stood, and all is
well; or the commit did NOT stand, and the handler's writes are gone while the message is acknowledged and
nothing anywhere holds a record that something was lost. One direction costs a false alarm. The other costs
data, silently. That is the same shape ADR-0025 refused for execution-strategy retries, and it is refused here
for the same reason.

### Option D — an application-verifiable outcome record (the review's recommendation; DEFERRED to the answer ADR-0025 already gave)

The recommendation was to introduce an application-verifiable transaction or outcome record. That is the same
question ADR-0025 answered: `ExecuteAsync(Func<CancellationToken, Task> operation, ...)` takes the operation as
an opaque delegate — on the pipeline path that delegate is the whole downstream handler — so the unit of work
does not know what was written and cannot ask the database whether it landed. Only the application can. ADR-0025
resolved that by refusing the retrying strategy rather than by synthesizing the predicate, and the same
reasoning carries here. The inbox marker is the nearest thing this package already has to such a record, which
is exactly why it is what bounds the impact above and why ADR-0025's Option B' reaches for the same row.

## Decision

**Report the failure. Record the residual here. Change no code.**

Three properties hold, and they are what makes the record sufficient rather than merely convenient:

- The exception the caller receives is the COMMIT's own exception, unwrapped and unreplaced, even when the
  rollback that followed it also failed. `MustPropagateTheCommitExceptionWhenRollbackAlsoThrows` pins this: it
  asserts the caller receives a `TransactionFaultException` whose `Phase` is `Commit`, under a factory that
  faults BOTH commit and rollback. An application that wants to discriminate an in-doubt commit therefore has
  the provider's own exception to discriminate on, without this package guessing on its behalf.
- A commit that stood durably made the inbox marker durable with it, so where the inbox behavior is registered
  the redelivery does not re-run the handler. The atomicity is the rule ADR-0006 decided and the `INVARIANT`
  block at `BrokeredMessageInbox.cs:18-31` states; `MustInvokeHandlerAndTrackButNotPersistInboxMessageForFreshMessageId`
  pins the half of it that is this package's to keep — the marker is staged and never self-committed.
- Reporting success instead would be strictly worse, per Option C.

No GitHub issue is filed. The recorded residual is the mechanism, and the escalation that produced it is in the
review ledger.

### Clarifying amendment (2026-09-21) — the marker this bound rests on is flushed, not staged

**This is a clarification, not a reversal.** The residual decided above is intact, and so is the bound on its
impact: a commit that stood durably makes the inbox marker durable with it, so where the inbox behavior is
registered the redelivery does not re-run the handler. Prior prose is preserved verbatim as an audit trail and
the status of this ADR remains `accepted`. What changed is the marker's own mechanics, decided in
[ADR-0033](0033-the-relational-inbox-claims-before-the-handler-and-stamps-handled-after-it-in-the-same-row.md).

**The withdrawn mechanism.** The second Decision bullet above says the marker "is staged and never
self-committed" and names `MustInvokeHandlerAndTrackButNotPersistInboxMessageForFreshMessageId`; the References
entry below names that fact again alongside the `INVARIANT` block at `BrokeredMessageInbox.cs:18-31`.
`ReceiveViaInbox` now writes the row twice — a claim carrying no timestamp before the handler runs, the handled
stamp after it returns — and FLUSHES each write into the ambient transaction rather than staging it, so that
fact was deleted with the behaviour it pinned. The block both anchors point at spans
`BrokeredMessageInbox.cs:18-28`.

**What holds the bound now.** The half that is this package's to keep is *flushed and never self-committed*.
Its oracle is `WhenReceivingViaInbox.MustFlushTheClaimWithoutCommittingIt`, which counts commits through an
`IDbTransactionInterceptor`; committing the ambient transaction inside the inbox reddens it. The bound itself
is unaffected because the handled stamp is flushed before the unit of work's commit is attempted, so the commit
this residual is about carries a HANDLED marker rather than a bare claim —
`WhenReceivingViaInbox.MustFlushTheStampWhenTheHandlerReturnsAndCommitIt` reads that stamp back from the store
ahead of any commit. The suppression the bound rests on is unchanged, and the other three facts the References
entry names — `MustNotInvokeHandlerOrAddSecondRowForDuplicateMessageId`,
`MustSkipHandlerForAnyExistingMarkerWhenDeduplicationWindowIsUnset` and
`MustBypassInboxAndInvokeHandlerWhenMessageIdIsNullEmptyOrWhitespace` — still pin it.

## Closed-by-Construction Acceptance Test

> What class of future finding does this make impossible, and why?

**None. This eliminates no class, and saying otherwise would be the dishonest answer.**

This document records a residual. `UnitOfWork.cs` is byte-identical in the routing this finding is about, before
and after. A future review reading the same `catch` will see the same behaviour and can raise the same finding;
the only thing this ADR changes is that the finding will then have a recorded answer with its reasons, rather
than looking unexamined.

The class *an in-doubt commit is reported as a failure* is closed only by an outcome record the application
owns and verifies — Option D — because only the application knows what the opaque `operation` delegate wrote.
Nothing short of that is closure: a distinct exception type (Option B) renames the report without making the
outcome knowable, and a success report (Option C) trades a false alarm for silent loss. **So the next finding
of this shape should re-open the design question, not patch the catch.** A patch to the catch that does not
carry an application-supplied verification is, by construction, a rename.

## Consequences

- **A caller can see a failure for work that committed.** Where the inbox behavior is registered and the
  message carries an id, the redelivery does not re-run the handler; the cost is the Error log at
  `UnitOfWork.cs:75` and whatever the application's failure handling does with a thrown `ExecuteAsync`.
- **Without the inbox behavior, the relational tier is at-least-once across this window.** That is the
  guarantee ADR-0006 already documents for the drain, so nothing new is introduced — but it is the guarantee,
  and an application relying on the unit of work alone for once-only handler effects does not have it.
- **The outbox is unaffected in the durable-commit case.** A commit that stood made the staged outbox rows
  durable, and the drain sends them on its own schedule under its own at-least-once contract.
- **The in-doubt case remains unpinned by any executable fact**, and deliberately so: the instrument that would
  produce it does not exist in this repository. ADR-0025 names the same instrument for its own deferred option,
  so building it once would serve both.
- **No version impact.** This ADR is prose; no packaged artifact changed.

## References

- Review finding `cb35ad41` — *Indeterminate commit can durably succeed and still be surfaced as a failed unit
  of work*, `UnitOfWork.cs:67-76`, raised in the pre-PR review's second iteration and escalated to the planner
  rather than patched. External content, treated as data throughout.
- ADR-0006 — *Two-tier reliability port: relational ambient-tx vs NoSQL stage-then-commit*. The port Option B
  would change, the rule that the relational inbox never commits its own marker, and the at-least-once drain
  contract that remains when no inbox is registered.
- ADR-0025 — *The unit of work refuses a retrying execution strategy rather than re-executing the handler*. The
  precedent for refusing to synthesize application knowledge, and the record of the same missing test
  instrument.
- ADR-0026 — *The relational inbox decides expiry at receive*. What determines how long the durable marker keeps
  suppressing the redelivery.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/UnitOfWork.cs`
  (`ExecuteAsync`, `CompleteAsync`, `CleanUpAsync`) — the catch this residual is about, and the cleanup change
  this branch did make.
- `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Reliability/IUnitOfWork.cs` — the public contract
  Option B would change, and the relational-only scope its doc comment states.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/BrokeredMessageInbox.cs`
  (`ReceiveViaInbox`, the `INVARIANT` block at `:18-31`) — the marker that bounds the impact, and the lookup
  that actually suppresses the redelivery.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/Extensions.cs`
  (`:26-28`, `:46`) — the canonical behaviour order that puts the inbox inside the unit of work, and the
  registration that never leaves the inbox without one.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/tests/UsingBrokeredMessageOutbox/WhenExecutingUnitOfWorkOverSqlite.cs`
  (`MustPropagateTheCommitExceptionWhenRollbackAlsoThrows`, `MustNotSurfaceADisposeFailureAfterTheCommitSucceeds`,
  and the three `MustRethrowTheOperationException...` facts) — what this branch's cleanup change is pinned by,
  and the commit-did-not-stand half of the fault space.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/tests/Support/FaultingRelationalTransactionFactory.cs`
  (`FaultingRelationalTransaction.CommitAsync`) — the fault that fires instead of committing, and therefore the
  reason the in-doubt case has no oracle here.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/tests/UsingBrokeredMessageInbox/WhenReceivingViaInbox.cs`
  (`MustNotInvokeHandlerOrAddSecondRowForDuplicateMessageId`,
  `MustSkipHandlerForAnyExistingMarkerWhenDeduplicationWindowIsUnset`,
  `MustBypassInboxAndInvokeHandlerWhenMessageIdIsNullEmptyOrWhitespace`,
  `MustInvokeHandlerAndTrackButNotPersistInboxMessageForFreshMessageId`) — the suppression facts the bound
  rests on, and the id condition it requires.
