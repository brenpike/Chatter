---
status: accepted
date: 2026-09-19
---

# Outbox selection is derived from durable attempt state, not from the absence of success

The outbox poll used to select on one predicate: `ProcessedFromOutboxAtUtc IS NULL`. That column is written only
when dispatch succeeds, and `OutboxProcessor.Process` logged every dispatch failure and did not rethrow, so *never
attempted* and *attempted and failed* were the same stored state. Starvation behind a permanently-failing row, the
re-dispatch storm, unbounded retry, the absence of backoff and the absence of any terminal state were all symptoms
of that one missing fact. This ADR records the change of the selection key to *unprocessed AND due*, where `due` is
a persisted `OutboxMessage.NextAttemptAtUtc` the failure path advances — and records why changing the key closed a
second finding that had been framed as a separate defect.

**Amended in place, 2026-09-20.** This ADR is accepted and UNRELEASED — `messagebrokers/v0.32.0` is not a tag, and
the release it is recorded under has not been published — so it is corrected here rather than superseded. The
correction is confined to the first and second bullets of *Subsumption required three conditions*:
`OutboxProcessor` now re-claims a row whose message is already on the broker before it spends an attempt, and
records the attempt straight on the store instead of through a unit of work of its own. The selection key, the two
columns and the closed class below are unchanged.

## Context

**The key that was, read from the bytes at `master`.**
`git show master:src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Reliability/Outbox/OutboxMessage.cs` shows an
entity of nine properties: `Id`, `MessageId`, `Destination`, `MessageContext`, `MessageBody`, `MessageContentType`,
`SentToOutboxAtUtc`, `ProcessedFromOutboxAtUtc` and `BatchId`. There is no attempt count and no next-attempt
instant. `git show master:src/.../Reliability.EntityFramework/BrokeredMessageOutbox.cs` shows the whole poll as
`outbox.Where(message => message.ProcessedFromOutboxAtUtc == null).ToListAsync(...)`, and the same file's
`UpdateProcessedDate` is the only writer of that column. `OutboxProcessor.Process` at `master` ended in a single
`catch (Exception e)` that called `_logger.LogError` and fell out of the method. **So a failed dispatch left the row
byte-identical to a row that had never been attempted**, and every poll thereafter selected it in exactly the same
place — at the head of the `SentToOutboxAtUtc` ordering, because it was the oldest.

**The key that is.** Both shipped stores now select on *unprocessed AND due*, with an OPTIONAL attempt ceiling on
top. `BrokeredMessageOutbox<TContext>.GetUnprocessedMessagesFromOutbox` reads `DateTime.UtcNow` into a local and
filters `message.ProcessedFromOutboxAtUtc == null && (message.NextAttemptAtUtc == null || message.NextAttemptAtUtc
<= now)`, then adds `message.DispatchAttempts < maxDispatchAttempts` only when
`ReliabilityOptions.OutboxMaxDispatchAttempts` has a value, then orders and takes the batch.
`InMemoryBrokeredMessageOutbox.GetUnprocessedMessagesFromOutbox` applies the same three clauses in the same order
over its dictionary. Oracles: `WhenManagingOutbox.MustSpendNoBatchSlotOnAMessageThatIsNotDue` and
`.MustGiveTheBatchSlotOfAFailingMessageToTheNextMessage` for the in-memory store,
`WhenGettingUnprocessedMessages.MustSpendNoBatchSlotOnAMessageThatIsNotDue` and
`.MustExcludeAMessageThatHasSpentTheConfiguredAttemptCeiling` for the relational one, and
`WhenGettingUnprocessedMessages.MustTranslateTheDueGateAndTheCeilingToSqlOverARelationalProvider` for the claim
that both new clauses are translated to SQL rather than evaluated in process.

**The two columns, and what their absent values mean.** `OutboxMessage.DispatchAttempts` is an `int` whose staged
value is `0` — never attempted. `OutboxMessage.NextAttemptAtUtc` is a `DateTime?` whose staged value is `null`, and
**null is the DUE-NOW value, not the never-due one**, which is what lets a row written before a store knew the
property existed be taken by a poll rather than held back for good. Oracle:
`WhenResolvingReliabilityStores.OutboxMessage_IsNeverAttemptedAndDueNowWhenStaged`.

**Who advances the due time.** `OutboxProcessor.RecordFailedDispatchAttempt` computes
`message.DispatchAttempts + 1`, converts it to a wait through `ReliabilityOptions.CalculateDispatchBackoff` — base
`OutboxDispatchBackoffBaseInSeconds` (default 5), doubling per attempt, clamped at `OutboxDispatchBackoffCapInSeconds`
(default 60) — and calls `IPollableOutboxStore.RecordDispatchAttempt` with `DateTime.UtcNow` plus that wait. Oracles:
`WhenBuilding.MustDoubleTheDispatchBackoffPerAttemptAndStopAtTheCap` for the schedule,
`WhenProcessingOutboxMessage.MustScheduleTheNextAttemptOneBackoffAhead` and
`.MustGrowTheScheduledWaitWithTheAttemptsTheRowAlreadyCarries` for the drain's use of it.

### Why one change closed two findings

The second finding was raised as a within-drain duplicate: one drain handing the same row to the Outbox Processor
more than once. **It was re-SELECTION, not re-iteration.** `BrokeredMessageOutboxProcessor.SendOutboxMessagesAsync`
takes one batch per poll and the dispatch loop walks that list once, so a row cannot appear twice inside a single
poll. The row reappeared because the NEXT poll's predicate still matched it — the same predicate, for the same
reason, as the starvation finding. **Changing the predicate removes both, and removes them further than the
alternative would have**: `DrainAsync` keeps its `seenIdentities` as a `HashSet<(int Id, string MessageId)>`
declared inside the method, so it is process-local and starts empty on every drain and every restart, whereas
`NextAttemptAtUtc` is a column and survives both.

That the seen set is deliberately NOT a dispatch filter is itself pinned:
`WhenSendingOutboxMessages.MustDispatchEveryRowAPollReturnsEvenWhenTheDrainHasAlreadySeenIt` goes red the moment the
set filters a batch before dispatch. The set terminates re-polling and nothing else, bounded by
`BrokeredMessageOutboxProcessor.MaxDrainIdentities` (`10000`).

**Subsumption required three conditions, and all three are built.**

- **The stamp is written on EVERY exit that leaves the row UNCLAIMED**, including a dispatch that SUCCEEDED whose
  claim commit and whose re-claim both threw — that exit leaves the row unclaimed too, so without the stamp the due
  gate would re-publish an already-published message on the very next poll. Oracles:
  `WhenProcessingOutboxMessage.MustRecordADispatchAttemptWhenDispatchFails` and
  `.MustRecordADispatchAttemptWhenTheReClaimAlsoFails`. A claim commit that threw after a publish is re-claimed
  first and spends no attempt when that re-claim succeeds, because the row ends CLAIMED and selection never reaches
  it again — pinned by `.MustReClaimTheRowWhenTheClaimCommitFailsAfterAPublish` and
  `.MustNotRecordADispatchAttemptWhenTheReClaimSucceeds`. The one deliberate exemption is a drain the host
  cancelled, which spends no attempt and pushes no due time out — pinned by
  `.MustNotRecordADispatchAttemptWhenProcessingIsCancelled`, whose bound is
  `.MustRecordADispatchAttemptWhenDispatchIsCancelledByAnotherToken`.
- **It goes STRAIGHT to the store, outside any unit of work.** Dispatch runs inside one that rolls back when it
  throws, so a stamp staged there would be discarded together with the failure it records — and a unit of work
  opened to carry the stamp would commit, alongside it, whatever the rolled-back one left staged.
  `RecordFailedDispatchAttempt` calls `IPollableOutboxStore.RecordDispatchAttempt` directly rather than through
  `IUnitOfWork.ExecuteAsync`. Oracle:
  `WhenProcessingOutboxMessage.MustRecordTheDispatchAttemptOutsideTheRolledBackUnitOfWork`.
- **The relational write bypasses the change tracker**, via `ExecuteUpdateAsync` over a `Where` on the message's own
  `Id`, incrementing the count in the database rather than from the possibly-stale in-memory value. Oracles:
  `WhenUpdatingProcessed.MustCountOneMoreDispatchAttemptOnTheStoredRow` and
  `.MustRecordTheAttemptOnlyOnTheMessageItWasHanded`.

### The tracker bypass is a failure-path requirement, not a universal one

An earlier step brief of this work stated the bypass as universally necessary. **Measurement disproved that, and the
accurate version is recorded here.** Swapping `ExecuteUpdateAsync` for a tracked `Update` plus `SaveChangesAsync`
reddens EXACTLY ONE fact — `WhenUpdatingProcessed.MustRecordTheAttemptAfterAFailedClaimLeftTheMessageStagedAsProcessed`,
the post-rollback case. The two clean-entity facts beside it pass either way, because a tracked save reaches a clean
row just as well.

The bypass is essential on the FAILURE path for a reason specific to that path: after a failed claim the message
this method is handed may still carry the `ProcessedFromOutboxAtUtc` stamp as its CURRENT value against the `null`
it was loaded with. `OutboxMessageConfiguration` maps that property `IsConcurrencyToken()`, so a tracked save there
would re-emit the very "still unprocessed" predicate that just failed — and would commit the claim if it now
matched. That same mapping decision is why neither new column is a concurrency token: oracle
`WhenConfiguring.MustTreatProcessedDateAsTheOnlyConcurrencyToken`.

**Amended in place, 2026-09-21 — the PREMISE above changed; the DECISION did not.** This paragraph used to state
the reason as *EF does not reset the change tracker when the surrounding transaction rolls back*. That no longer
holds for THIS package's own unit of work: `UnitOfWork<TContext>` clears its context's change tracker when it rolls
back a transaction IT BEGAN, per ADR-0034. The bypass SURVIVES, because the states it has to be correct over are
now wider rather than narrower — a message can reach `RecordDispatchAttempt` DETACHED, where `Update` sets original
from current, or still tracked holding the stale stamp where the unit of work adopted a caller's transaction and
reconciled nothing. `ExecuteUpdateAsync` reaches the row whatever the tracker holds, which is what
`WhenUpdatingProcessed.MustRecordTheAttemptAfterAFailedClaimLeftTheMessageStagedAsProcessed` pins, and that fact
still reddens exclusively on the tracked-save mutation. The second reason — the write must LAND, because the
transaction that carried the claim has already rolled back and there is no unit of work left to commit with — is
untouched. The ADR-0034 reconciliation also required a change in the same file to a DIFFERENT method,
`UpdateProcessedDate`, which now states its own `null` original value so that the claim emits the same predicate
detached or tracked; that is recorded in ADR-0034, not here.

## Considered Options

### Option A — make durable attempt state the selection key (ACCEPTED)

Persist an attempt count and a next-attempt instant, advance them on every non-success exit, and gate the poll on
the instant. Selection stops being a function of the absence of success.

### Option B — pre-filter each batch against the in-drain seen set (REJECTED)

This is the complete-the-known-set shape: it answers the duplicate that was observed and nothing adjacent to it. The
set is a local of `DrainAsync`, so it is lost on restart and reset per drain, and it closes NONE of the starvation —
a permanently-failing row filtered out of dispatch still occupies its slot in the batch the store returned. It would
also contradict `WhenSendingOutboxMessages.MustDispatchEveryRowAPollReturnsEvenWhenTheDrainHasAlreadySeenIt`, which
exists precisely to keep the set off the dispatch path.

### Option C — enlarge the poll batch, or keep a skip list (REJECTED)

Enlarging `OutboxPollBatchSize` moves the threshold at which a poison population fills a batch; it eliminates no
value of that population. A skip list is the same shape as Option B with a different container, and carries the same
process-local lifetime.

### Option D — stamp `ProcessedFromOutboxAtUtc` on failure (REJECTED)

It would free the slot, and it would lose the message. That column is the claim, and it is the only thing that
distinguishes a delivered row from one still owed: `UpdateProcessedDate` is its sole writer in both stores, the poll
excludes every row carrying it, and `ReliabilityRetentionPurgeService` deletes only rows where
`ProcessedFromOutboxAtUtc != null`. Stamping it on failure would mark an undelivered message delivered and then make
it eligible for deletion.

### Option E — advance `SentToOutboxAtUtc` instead of adding a column (REJECTED)

`SentToOutboxAtUtc` is the FIFO key both stores order by, and it is the ordering key the retention purge uses as
well. Advancing it to defer a row would reorder the backlog against arrival and corrupt the purge's notion of
oldest-first. Deferral needs a column of its own precisely because it must not disturb arrival order.

## Decision

**Selection is `ProcessedFromOutboxAtUtc IS NULL` AND due, where due means `NextAttemptAtUtc` is null or has
passed. The failure path advances that instant. There is NO terminal state by default.**

**Backoff alone closes the wedge.** A row whose dispatch keeps failing stops occupying the hot selection window
without anyone deciding to give up on it, so the mechanism that fixes the reported defect requires no policy
decision from an operator and ships working. This is why `OutboxDispatchBackoffBaseInSeconds` and
`OutboxDispatchBackoffCapInSeconds` carry their defaults as property initializers rather than taking them from
`ReliabilityOptionsBuilder`: a directly constructed `ReliabilityOptions` must back off too. Oracle:
`WhenBuilding.MustBackOffFromADirectlyConstructedReliabilityOptions`.

**Give-up is opt-in operator policy.** `ReliabilityOptions.OutboxMaxDispatchAttempts` is `int?` and defaults to
absent, which re-attempts a message for good; a configured value below 1 is refused while the options are being
built. Oracles: `WhenBuilding.MustAcceptAnOmittedOutboxMaxDispatchAttempts` read with `.MustBuildDefaultOptions`,
and `.MustRefuseAConfiguredOutboxMaxDispatchAttemptsOfZero`. The precedent is
`EntityFrameworkReliabilityOptions.ProcessedOutboxRetention`, set earlier on this same branch and defaulted to null
for the same reason: a finite default changes the behaviour of a host already running.

**The divergence from the Document Tier is deliberate.** `CosmosBrokeredMessageOutbox` declares
`: IBrokeredMessageOutbox` and does NOT implement `IPollableOutboxStore` — it dispatches through the change-feed
Outbox Relay — and it already carries a STAMPED terminal state: `CosmosOutboxRelay` patches
`CosmosOutboxDocument.StatusUndeliverable` onto a document it gives up on, and `OutboxDeliverySettings` refuses a
configured delivered-status value equal to that constant so the two can never be confused. The relational terminal
state is DERIVED instead — `DispatchAttempts < maxDispatchAttempts` is evaluated per poll against the currently
configured ceiling — so raising the ceiling revives the row and the policy stays a live operator dial. The reason
the two tiers differ is the dispatch mechanism: a poll can re-query with a new predicate, a change feed cannot.
Cosmos is out of scope for this change and nothing in it moves.

## Closed-by-Construction Acceptance Test

> What class of future finding does this make impossible, and why?

**Eliminated class: a dispatch failure that is invisible to selection.**

A permanently-failing row can no longer occupy a selection slot, because occupancy is no longer a function of
failure. It is a function of a durable due-time the failure path advances, and every non-success exit of the drain
advances it. Findings of the shape *"row X keeps being selected, or keeps starving others, because its dispatch
fails"* cannot exist — for any X, any failure mode, and any batch size — because the predicate that selects X no
longer has the same value before and after X fails.

The within-drain duplicate is inside that class rather than beside it: it was the same predicate re-matching on the
next poll, so the finding is closed by the same property and not by a second guard.

**What this does NOT close.** A store that inherits the no-op default implementation of
`IPollableOutboxStore.RecordDispatchAttempt` records nothing, so its rows stay at zero attempts and due now and it
keeps today's behaviour. That is bounded rather than unbounded — `DrainAsync`'s identity set and
`MaxDrainIdentities` end such a drain after one unsuccessful poll and then take the interval wait — and it is
deliberate, because a third-party pollable store written against the previous shape of the interface must still
compile and still satisfy the cast at the poll site. Oracle:
`WhenResolvingReliabilityStores.OutboxCustomPrimaryImplementingBoth_RecordDispatchAttemptDefaultsToANoOp`.

## Migration

**This is a hard breaking schema change for the relational store, and no graceful-degradation shape exists.**
Nullability governs whether a row can be INSERTed without a value; it does not govern whether the column exists. An
upgraded-but-unmigrated consumer fails on the FIRST poll, because the poll's own `WHERE` names
`NextAttemptAtUtc` — SQL Server answers with `Invalid column name`. No oracle pins that message: it is the
provider's, not this package's.

**One nuance stated precisely, because an earlier step brief of this work got it wrong.** The columns were never
unmapped at the MODEL level. EF maps public scalar properties by convention, so `DispatchAttempts` and
`NextAttemptAtUtc` are part of the `OutboxMessage` entity type the moment the properties exist;
`OutboxMessageConfiguration` only refines them, with `IsRequired().HasDefaultValue(0)` and `IsRequired(false)`
respectively. **The breakage is STORE-side only** — the table lacks the columns the model already names.

**What is available, and what the documented upgrade order therefore is.** The change needs two `ALTER TABLE ADD`
statements and **no backfill**: `DispatchAttempts` lands `NOT NULL` with a store default of `0`, which is exactly the
never-attempted value, and `NextAttemptAtUtc` lands nullable, where null already means due now. **Both columns are
backward-compatible with the OLD binary**, because old code never names them — its `SELECT` projects only the
properties its own model mapped, and its `INSERT` omits both, leaving the store default and the nullability to
supply them.

**So the documented order is schema first, then binaries.** Apply the migration against a fleet still running the
previous version, which keeps working unchanged, then roll the binaries. This is a normal two-step upgrade, not a
coordinated cutover, and it is the order this package documents. As with every other model change in this package,
the application generates and owns the migration (`src/README.md`, *Reliability*).

## Consequences

- **Undeliverable rows are never purged.** `ReliabilityRetentionPurgeService` selects only
  `ProcessedFromOutboxAtUtc != null`, so a row that spent a configured ceiling is unselectable by the poll AND
  ineligible for retention deletion. This is deliberate evidence retention and matches the Document Tier, where an
  Undeliverable Outbox Document likewise remains. For a consumer who opts into a ceiling it is unbounded table
  growth, and reclaiming those rows is that consumer's operational task.
- **Clock skew degrades, it does not wedge.** Both stores read `DateTime.UtcNow` inside
  `GetUnprocessedMessagesFromOutbox`, so `NextAttemptAtUtc` is compared against the POLLING host's clock. A host
  running behind re-selects a deferred row early; the worst case is today's behaviour, and no comparison can make a
  row permanently unselectable, because the instant is a past value the clock passes.
- **No oracle pins the `<=` / `<` boundary against now**, in either store. The instant is read from the wall clock
  inside the method, so no test can name a message due at exactly it; the two spellings differ only for a message
  whose next attempt lands on that tick, and a message taken a tick early is simply taken by the following poll.
  This is recorded rather than tested.
- **The seen set stays, and its role is now the narrow one.** It is no longer the mechanism that keeps a poison
  batch from spinning; the due gate is. It remains as the backstop for a store taking the no-op default, and the
  bound on `DrainAsync`'s own retention.
- **A SQL Server integration proof of these properties lands alongside this ADR.** It is not cited by name here,
  because it is in flight as this is written and this document verifies every other citation by symbol.

## References

- ADR-0027 — *An `INVARIANT:` comment names the oracle that falsifies it*. Why every behavioural claim above names
  the fact that would refute it.
- ADR-0006 — *Two-tier reliability: relational ambient-tx vs NoSQL stage-then-commit*. The tier split the Cosmos
  divergence above is drawn against.
- ADR-0007 — *Cosmos outbox: co-resident change feed relay*. The dispatch mechanism that makes the Document Tier's
  terminal state stamped rather than derived.
- ADR-0025 — *The unit of work refuses a retrying execution strategy*. The seam the separate failure-path unit of
  work is opened through.
- ADR-0034 — *A rolled-back unit of work reconciles its context's change tracker*. The decision that superseded
  this ADR's stated premise for the tracker bypass, and that required `UpdateProcessedDate` to state its own
  original value.
- `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Reliability/Outbox/OutboxMessage.cs` — `DispatchAttempts`
  and `NextAttemptAtUtc`, and the null-means-due-now invariant.
- `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Reliability/Outbox/IPollableOutboxStore.cs` —
  `GetUnprocessedMessagesFromOutbox`'s three-clause contract and `RecordDispatchAttempt`'s no-op default
  implementation.
- `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Reliability/Outbox/OutboxProcessor.cs` —
  `RecordFailedDispatchAttempt`, the cancellation exemption, and the separate unit of work.
- `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Reliability/Outbox/BrokeredMessageOutboxProcessor.cs` —
  `SendOutboxMessagesAsync`, `seenIdentities` and `MaxDrainIdentities`.
- `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Reliability/InMemoryBrokeredMessageOutbox.cs` — the shipped
  default store's copy of the three clauses and its write-through `RecordDispatchAttempt`.
- `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Reliability/Configuration/ReliabilityOptions.cs` —
  `OutboxDispatchBackoffBaseInSeconds`, `OutboxDispatchBackoffCapInSeconds`, `OutboxMaxDispatchAttempts` and
  `CalculateDispatchBackoff`.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/BrokeredMessageOutbox.cs`
  — the relational poll and the `ExecuteUpdateAsync` failure-path write.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/OutboxMessageConfiguration.cs`
  — the two new column mappings and the sole-concurrency-token invariant.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/ReliabilityRetentionPurgeService.cs`
  — the `ProcessedFromOutboxAtUtc != null` purge predicate that makes an undeliverable row unreclaimable.
- `src/Chatter.MessageBrokers.Reliability.Cosmos/src/Chatter.MessageBrokers.Reliability.Cosmos/Reliability/CosmosBrokeredMessageOutbox.cs`
  (declares `IBrokeredMessageOutbox` only), `.../CosmosOutboxDocument.cs` (`StatusUndeliverable`),
  `.../CosmosOutboxRelay.cs` (the patch that stamps it) and `.../OutboxDeliverySettings.cs` (the refusal that keeps
  it distinct).
