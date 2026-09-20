---
status: accepted
date: 2026-09-19
---

# The unit of work refuses a retrying execution strategy rather than re-executing the handler

`UnitOfWork<TContext>.ExecuteAsync` reads `IExecutionStrategy.RetriesOnFailure` and throws
`InvalidOperationException` before it hands the strategy anything to run. It does not clear the change tracker,
defer acceptance, or verify the outcome — it declines to re-execute at all. This ADR records why the refusal is
the answer for a framework unit of work, and records the design that would have made re-execution safe as a
DEFERRED option rather than a dismissed one, because that design is genuinely constructible and someone will
reach for it again.

## Context

**The defect.** `ExecuteAsync` opened with `_context.Database.CreateExecutionStrategy()` and then ran
`BeginAsync`, the caller's operation, and `CompleteAsync` inside the delegate handed to
`strategy.ExecuteAsync(...)`. `CompleteAsync` calls `_context.SaveChangesAsync(cancellationToken)` and then
`scope.CommitAsync(cancellationToken)`, in that order, in that same delegate
(`src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/UnitOfWork.cs`).

`DbContext.SaveChangesAsync(CancellationToken)` defaults `acceptAllChangesOnSuccess` to `true`, and acceptance
moves every tracked entry to `Unchanged`. A second attempt therefore runs over a change tracker reporting
nothing to write: `SaveChangesAsync` emits no SQL, `CommitAsync` commits a transaction carrying no work,
`ExecuteAsync` returns normally, and the caller is told the unit of work completed. The handler's writes went
back with the first attempt's transaction and are never reissued. The loss is silent — there is no exception, no
warning, and no row anywhere to notice it by. That is issue #484.

**What the platform guidance says, stated precisely.** Microsoft's Connection Resiliency guidance describes this
mechanism directly and lists FOUR responses to it, not one. Two are not mechanisms a library can adopt on a
consumer's behalf: Option 1 accepts the failure and constrains the application's key generation to client-side
values, and Option 2 discards the `DbContext`, rebuilds application state from the database, and tells the user
the last operation may not have completed. The two that verify the outcome mechanically — Option 3 state
verification and Option 4 manual transaction tracking — both hand EF a `verifySucceeded` predicate through
`IExecutionStrategy.ExecuteInTransactionAsync`, which EF invokes when a transient failure occurs during commit,
and both pair it with `SaveChangesAsync(acceptAllChangesOnSuccess: false, ...)` followed by
`ChangeTracker.AcceptAllChanges()`.

Chatter cannot write that predicate. `ExecuteAsync(Func<CancellationToken, Task> operation, TransactionContext
transactionContext, CancellationToken cancellationToken)` takes the operation as an opaque delegate, and on the
pipeline path that delegate is `next()` — the whole downstream handler. The unit of work does not know what was
written, so it cannot ask the database whether it landed. `verifySucceeded` is application knowledge by the
shape of the port, not by omission.

**What was already refused.** ADR-0006's commit-ownership amendment records that ADOPTION of a caller's
transaction is already unreachable under a retrying strategy: `ExecutionStrategy.OnFirstExecution()` throws
`InvalidOperationException` when `RetriesOnFailure` is true and any of `Database.CurrentTransaction`,
`Database.GetEnlistedTransaction()`, or a current ambient `TransactionScope` is non-null, and it does so before
the delegate is invoked. So EF already refused the retrying strategy for every caller who had a transaction
open. The case it does not cover is the one this unit of work takes on its own initiative: a retrying strategy
with no transaction open yet, where `BeginAsync` goes on to begin one inside the retried delegate. The decision
below extends the same answer to that case rather than inventing a second one.

## Considered Options

### Option A — refuse at the `UnitOfWork<TContext>` seam (ACCEPTED)

Read `strategy.RetriesOnFailure` immediately after `CreateExecutionStrategy()` and throw before
`strategy.ExecuteAsync` is ever called. Re-execution becomes unreachable rather than handled. The costs are
stated under *Decision* and *Consequences*.

### Option B' — emulate retry safety with the inbox marker as the witness (DEFERRED, not dismissed)

This is recorded in full because it is constructible, not because it is a straw man. Its shape:

- `strategy.ExecuteInTransactionAsync(operation, verifySucceeded)` replaces `strategy.ExecuteAsync(...)`, so EF
  owns beginning and committing the transaction for each attempt.
- `_context.ChangeTracker.Clear()` as the first statement of every attempt, so an attempt never inherits the
  previous attempt's tracked graph.
- `SaveChangesAsync(acceptAllChangesOnSuccess: false, ct)` inside `CompleteAsync`, with
  `ChangeTracker.AcceptAllChanges()` once the strategy returns — the pairing the guidance above prescribes.
- The inbox marker as the witness, which is Option 4's manually-tracked transaction row, already present and
  already carrying a message-scoped identity.

**The key property.** With the tracker cleared, the second attempt's `ReceiveViaInbox` re-reads from the
database through `_inbox.FindAsync(new object[] { messageId }, cancellationToken)` rather than from a tracker
still holding the first attempt's `Added` marker. If the first attempt's commit landed, the marker is found and
`ReceiveViaInbox` returns without invoking the handler; if it did not land, the marker is absent and the handler
runs, which is exactly what a rolled-back attempt should produce. Both branches are correct whatever
`verifySucceeded` answered — INCLUDING false. The marker, not the predicate, is what carries in-process
idempotence, and that is what makes B' more than a restatement of the guidance. The marker is inserted after the
handler returns (`BrokeredMessageInbox.ReceiveViaInbox` adds it only once `await handler()` has completed), so
it witnesses handler completion rather than message receipt, which is the property a witness row needs.

**Its four costs, each of which is real.**

1. **A two-path `UnitOfWork<TContext>`.** `ExecuteInTransactionAsync` begins and commits the transaction itself,
   which leaves `BeginAsync` with nothing to do and no `UnitOfWorkTransaction` handle to return. That handle is
   where per-invocation commit ownership lives: `BeginAsync` reads `_context.Database.CurrentTransaction` once
   and sets `BegunHere`, and `CompleteAsync`, `RollbackAsync` and `UnitOfWorkTransaction.DisposeAsync` each act
   only through it. The adopted-transaction rule ADR-0006 decided and the retry path cannot be one path, so the
   type grows a second one, and every subsequent change to either has to be reasoned about twice.

2. **`ChangeTracker.Clear()` detaches the drained outbox row.** `BrokeredMessageOutbox.UpdateProcessedDate`
   calls `outbox.Update(outboxMessage)` on a row the drain's own poll loaded, and
   `GetUnprocessedMessagesFromOutbox` keeps that query TRACKED deliberately: its `INVARIANT:` remark states the
   claim is safe under concurrency only because the tracked message keeps the `ProcessedFromOutboxAtUtc` it was
   loaded with as its original value, which `OutboxMessageConfiguration` maps to a concurrency token and EF
   emits as a "still unprocessed" predicate on the claiming update. A cleared tracker yields a detached row
   whose original value IS the stamp being written — the exact failure that remark attributes to
   `AsNoTracking` — so the claim would match no row and every drain would fail. The claim-check would have to
   become a set-based `ExecuteUpdateAsync` with a rowcount check, which breaks the tracker-state assertion at
   `src/Chatter.MessageBrokers.Reliability.EntityFramework/tests/UsingBrokeredMessageOutbox/WhenUpdatingProcessed.cs:38-43`,
   where `MustStampProcessedDateForSingleMessage` reads `ChangeTracker.Entries<OutboxMessage>()` for a single
   `Modified` entry.

3. **Proof needs fault injection this repository does not have.**
   `tests/Support/FaultingRelationalTransactionFactory.cs` cannot supply it: its override is
   `_faults.HasFlag(TransactionFaults.Commit) ? throw new TransactionFaultException(...) : base.CommitAsync(...)`,
   so it fails INSTEAD of committing and can never produce the landed-but-unacknowledged commit that is the
   whole subject of B'. What B' has to be proven against is an `IDbTransactionInterceptor.TransactionCommitted`
   that throws AFTER a real commit has gone through, plus a test execution strategy that genuinely re-executes.
   `tests/Support/RetryingExecutionStrategy` is not that either — it reports `RetriesOnFailure => true` and runs
   the operation exactly once, which is all Option A's refusal needs to be proven and is deliberately no more.

4. **Entities loaded before the unit of work, in the same scope, get detached.** `ChangeTracker.Clear()` is
   context-wide, and `UnitOfWork<TContext>` shares the scoped `TContext` with everything else in the dispatch
   scope. A caller that loaded an aggregate before dispatch and expected to keep holding it loses tracking, with
   no diagnostic at the point of loss.

**UNVERIFIED.** No part of B' has been executed. The specific unknown is what EF does with a `verifySucceeded`
that THROWS rather than returning a bool — whether the predicate's exception propagates out of
`ExecuteInTransactionAsync` to the caller or is folded into the retry and lost. The probe is a fact that drives
a commit failure under a retrying strategy with a `verifySucceeded` that throws, and asserts which exception
reaches the caller. Until that probe runs, the cost list above is an estimate and B' is not a candidate for
adoption.

**B' is safe ONLY if the inbox is on.** The marker is the witness and `InboxBehavior` is what puts it there.
With no inbox registered, a re-executed attempt re-runs the handler with nothing to tell it the previous
attempt's work landed, and the resulting semantics are at-least-once — which is the guarantee the broker path
already carries and which ADR-0006 already documents for the drain. B' would therefore buy a guarantee that
varies with a separate registration, and a consumer reading `ExecuteAsync` could not tell which one they had.
Option A's single refusal does not vary.

## Decision

**Adopt Option A. Refuse at the `UnitOfWork<TContext>` seam, loudly, on every delivery.**

`ExecuteAsync` reads `strategy.RetriesOnFailure` immediately after `CreateExecutionStrategy()` and throws
`InvalidOperationException` when it is true — before `strategy.ExecuteAsync` is called, before `BeginAsync`
runs, and therefore before any transaction exists to be left in an unknown state. The message names the context
type, the strategy type, the mechanism, and both ways out: remove `EnableRetryOnFailure` from that `DbContext`,
or stop registering the unit of work for it. `MustNameTheContextStrategyAndBothMigrationsWhenRefusing` pins
every one of those elements of the message, and `MustRefuseARetryingExecutionStrategyOnEveryCall` pins that the
refusal is per call rather than a once-latched decision — this is a dispatch-time refusal, not a startup gate.

**Nothing is lost by refusing**, and that is what makes the refusal cheap rather than merely safe. Three
separate facts hold:

- Chatter's own recovery pipeline already retries above this seam —
  `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Recovery/RetryWithCircuitBreakerStrategy.cs`, which
  re-drives the receive rather than a half-accepted change tracker.
- Broker redelivery already retries below it, and the drain is at-least-once by design (ADR-0006, *What the
  concurrency token buys, and what it does not*).
- The adopted-transaction path has ALWAYS refused this combination — EF's own
  `ExecutionStrategy.OnFirstExecution()` throws, as recorded in ADR-0006's commit-ownership amendment. Refusing
  here makes the library's behaviour uniform across both paths instead of safe on one and silently lossy on the
  other.

So the refusal removes a retry layer that was never safe, on a path where two layers that ARE safe remain.

This is a BREAKING change: a `DbContext` configured with `EnableRetryOnFailure` and registered through
`WithUnitOfWorkBehavior`, `WithInboxBehavior` or `WithOutboxProcessingBehavior` previously dispatched and lost
writes, and now throws on every dispatch.

## Closed-by-Construction Acceptance Test

> What class of future finding does this make impossible, and why?

**"A retrying strategy silently discards or duplicates a handler's work."**

The class is eliminated by removing the re-execution, not by making re-execution correct. The throw precedes
`strategy.ExecuteAsync`, so there is no second attempt to reason about anywhere downstream of it: no acceptance
to defer, no tracker to clear, no witness to consult, and no `verifySucceeded` to get wrong. A future EF release
that changes what `SaveChangesAsync` accepts, or a provider strategy with different retry semantics, needs no
new conjunct here — the refusal keys on `RetriesOnFailure` alone and on no property of any particular attempt,
which is what distinguishes it from a predicate that would have to grow a case per discovered failure shape.

`MustNotInvokeTheOperationUnderARetryingExecutionStrategy` is what makes the unreachability checkable rather
than asserted: it counts operation invocations AND the context's `SaveChangesAsync` calls, and requires both to
be zero after the throw. A refusal that fired after the operation had already run would fail it.

**Read the class at the scope it is stated: `UnitOfWork<TContext>`.** This constrains what the library does on
its own initiative, exactly as ADR-0006's commit-ownership boundary does. A consumer who calls
`CreateExecutionStrategy()` and `ExecuteAsync` themselves is unaffected, and a consumer who keeps
`EnableRetryOnFailure` for their own queries and `SaveChanges` outside the reliability pipeline keeps it — the
refusal is raised by this type, for this type's own use of the strategy.

## Consequences

- **Breaking at dispatch, not at registration.** A consumer combining `EnableRetryOnFailure` with the relational
  reliability behaviours now receives an `InvalidOperationException` on every dispatch. The misconfiguration is
  discovered on the first message rather than at startup, because the strategy is a property of the resolved
  `DbContext` and the unit of work only sees one when it runs.

- **The drain path logs Error per row and continues.** `OutboxProcessor.Process` wraps its entire body —
  including `((IUnitOfWork)_brokeredMessageOutbox).ExecuteAsync(...)` — in
  `catch (Exception e) { _logger.LogError(e, $"Unable to process outbox message with id '{message.Id}'"); }`.
  Under a retrying strategy the refusal is therefore raised, logged at Error, and swallowed once per row, and
  `ProcessBatch` moves straight to the next row. No row is stamped, so the next poll re-reads the same rows and
  logs again. The misconfiguration surfaces as a repeating Error line per unprocessed row rather than as a
  single failure, and the outbox stops draining while continuing to accept enqueues. That is a loud failure
  where the previous behaviour was a silent one, but it is not a startup gate and it does not halt the host.

- **The transactional guarantee is unchanged for every consumer on a non-retrying strategy.**
  `SqlServerExecutionStrategy.RetriesOnFailure` and `NonRetryingExecutionStrategy.RetriesOnFailure` are both
  `false`, and only `EnableRetryOnFailure(...)` sets it true, so the default-strategy path takes the same
  begin / save / commit sequence it always did.

- **Option B' remains available and is recorded above rather than in a tracker.** It is a decision with a stated
  reason, not outstanding work, and the probe that would make it decidable is named. If a consumer genuinely
  needs `EnableRetryOnFailure` on the same `DbContext` as the reliability pipeline, B' is where to start, and
  the four costs are what a proposal has to answer.

- **Unrelated carry.** The same commit added `ConfigureAwait(false)` to the remaining bare awaits in
  `UnitOfWork.cs` and `PersistanceTransaction.cs` (#383). It has no bearing on the refusal and is recorded here
  only so a reader of that diff is not left looking for a connection.

## References

- Issue #484 — *`UnitOfWork.CompleteAsync` accepts changes before committing inside a retrying execution
  strategy*. The defect this decision closes; deferred by ADR-0006 and resolved here.
- Issue #383 — the bare-await sweep carried in the same commit.
- ADR-0006 — *Two-tier reliability port*. Its **The deferral.** paragraph is the record this decision resolves;
  its **Boundary — adoption is unreachable under a retrying execution strategy.** paragraph is the precedent
  this decision extends; its **What the concurrency token buys, and what it does not** paragraph is the
  at-least-once drain contract the refusal leaves intact.
- Microsoft, *Connection Resiliency — EF Core*
  (https://learn.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency), §*Transaction commit failure
  and the idempotency issue*. The four responses, the `verifySucceeded` predicate, and the
  `acceptAllChangesOnSuccess: false` / `AcceptAllChanges()` pairing that Option B' would adopt.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/UnitOfWork.cs`
  — the refusal, and the `UnitOfWorkTransaction` handle Option B' would displace.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/BrokeredMessageInbox.cs`
  (`ReceiveViaInbox`) — the marker Option B' would use as its witness, and the post-handler insertion point that
  makes it one.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/BrokeredMessageOutbox.cs`
  (`GetUnprocessedMessagesFromOutbox`) — the tracked-poll `INVARIANT:` that Option B' cost 2 would violate.
- `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Reliability/Outbox/OutboxProcessor.cs` (`Process`) —
  the per-row Error log and swallow that shapes the drain consequence above.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/tests/UsingBrokeredMessageOutbox/WhenExecutingUnitOfWork.cs`
  and `tests/Support/RetryingExecutionStrategyFactory.cs` — the four refusal facts and the strategy double that
  claims retry without performing one.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/tests/Support/FaultingRelationalTransactionFactory.cs`
  — the existing commit fault, and why it is the wrong half of the pair Option B' needs.
