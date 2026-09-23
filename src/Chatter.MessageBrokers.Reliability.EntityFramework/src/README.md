# <a name="chatter-reliability-entityframework"></a> Chatter.MessageBrokers.Reliability.EntityFramework

Durable EF Core inbox/outbox and unit-of-work for [Chatter.MessageBrokers](#chatter-messagebrokers).

## Overview

`Chatter.MessageBrokers.Reliability.EntityFramework` is the EF Core implementation of the reliability ports defined by [Chatter.MessageBrokers](#chatter-messagebrokers): the brokered message **inbox**, **outbox**, and **unit of work**. Out of the box Chatter wires these ports to in-memory defaults; registering this package **replaces those in-memory defaults with durable, relational storage** backed by your application's `DbContext`.

This gives you:

- **Idempotent (once-only) message handling** via a persisted inbox of processed message ids.
- **Reliable publish** via the transactional outbox pattern — outgoing messages are written to your database in the *same* transaction as your domain state, then dispatched separately.
- **Atomic units of work** that commit your domain changes and inbox/outbox writes together (or roll them all back) through a single EF transaction.

Because the inbox, outbox, and unit of work all run against the same `DbContext`, your business state and the messaging bookkeeping share one transaction and one commit.

## Migration: the inbox marker's two states

**No DDL. No new column. No backfill.** The inbox marker now tells *claimed* from *handled*, and the nullable `ReceivedByInboxAtUtc` column the previous schema already has is what carries that distinction. The table shape does not change.

**Generate one empty migration and commit it.** `InboxMessageConfiguration` maps `ReceivedByInboxAtUtc` with `IsConcurrencyToken()`, which is a model annotation rather than a column, so `dotnet ef migrations add <name>` produces a migration whose `Up()` and `Down()` are empty and whose model snapshot carries the annotation. From EF Core 9 onward, applying migrations against a model that differs from the last snapshot raises `PendingModelChangesWarning` as an error, so the empty migration is required there; EF Core 8 has no such check and it is optional.

**Your existing rows read as handled, exactly as before.** Every marker a released version of this package has written carries a stamp, because the marker was previously inserted or refreshed only after the handler returned.

**Optionally**, if you hand-wrote inbox rows with a null `ReceivedByInboxAtUtc` expecting them to suppress their message ids, those rows now read as unfinished claims and no longer suppress. One statement restores the old reading, against the table your model maps `InboxMessage` to:

```sql
UPDATE [InboxMessages] SET [ReceivedByInboxAtUtc] = SYSUTCDATETIME() WHERE [ReceivedByInboxAtUtc] IS NULL;
```

## Migration: the outbox attempt columns

`OutboxMessageConfiguration` maps two columns the 0.8.0 schema does not have — `DispatchAttempts` and `NextAttemptAtUtc` — so an existing outbox table needs two additive statements. Generate the migration with `dotnet ef migrations add` as you would for any other model change; against SQL Server it emits the two statements below, where `OutboxMessages` is whatever table your model maps `OutboxMessage` to:

```sql
ALTER TABLE [OutboxMessages] ADD [NextAttemptAtUtc] datetime2 NULL;
ALTER TABLE [OutboxMessages] ADD [DispatchAttempts] int NOT NULL DEFAULT 0;
```

Those types follow the configuration: `NextAttemptAtUtc` is mapped `IsRequired(false)`, and `DispatchAttempts` is mapped `IsRequired()` with `HasDefaultValue(0)`.

**No backfill.** Both statements are additive and neither rewrites an existing row: the store default fills `DispatchAttempts` with `0`, and a null `NextAttemptAtUtc` means *due now*, so every row staged before the migration is selectable by the first poll after it.

**The DDL is backward-compatible with the previous binary**, which names neither column, so **schema-first is the required deploy order: apply the migration, then roll the binaries.** That is a normal two-step upgrade rather than a coordinated cutover.

Rolling the binary first breaks the poll, and the breakage is **store-side only**. `OutboxMessage` declares both as public scalar properties, which EF maps by convention whether or not `OutboxMessageConfiguration` names them, so an upgraded binary has a complete model; what an unmigrated database does not have is the two columns, and the first outbox poll fails reading them. That failure surfaces as your database provider's own error rather than one this package raises, and no test in this repository pins its text.

## Migration: the drain claim needs none

**No DDL. No new column. No migration.** A drain now claims the row it is about to publish *before* it publishes it, and that claim is a value of the `NextAttemptAtUtc` column [Migration: the outbox attempt columns](#migration-the-outbox-attempt-columns) already added. No property, mapping or annotation changes, so `dotnet ef migrations add` has nothing to emit for it — and this package ships no migrations of its own, because your application owns the schema through the `IEntityTypeConfiguration` types it applies.

**Both deploy directions are safe, in either order.** A claim is invisible outside the transaction that took it — it never lands on its own, so no other binary ever reads a row mid-claim — and a claim that rolls back leaves the row exactly as the poll found it, which is what lets the drain that was waiting take its own claim against the instant its own poll read and publish the message. A new binary reading rows an old one wrote sees `NULL` or a past instant, and both are claimable. Pinned by `WhenClaimingForDispatch.MustEnlistTheClaimInTheAmbientTransaction` and, over a real SQL Server, by `Integration/WhenArbitratingOutboxDrainsOnSqlServer.MustGrantTheWaitingDrainsClaimOnceTheWinningDrainRollsBack`.

**The one operational note is not a migration step.** A drain now holds an exclusive lock on the single row it is publishing for the duration of that publish, so a second host that polled the same row waits there. Drains of *different* rows do not contend — see [Outbox (reliable publish)](#outbox-reliable-publish).

## Installation

```sh
dotnet add package Chatter.MessageBrokers.Reliability.EntityFramework
```

The package targets `net8.0` and `net10.0`, and pulls in `Microsoft.EntityFrameworkCore` / `Microsoft.EntityFrameworkCore.Relational` for the matching framework.

## Getting Started

Registration happens against the **command pipeline builder**, which Chatter exposes through the `pipelineBuilder` action on `AddChatterCqrs(...)` (the same `IChatterBuilder` you call `AddMessageBrokers(...)` on). Each extension method is generic over *your* `DbContext` type (`TContext : DbContext`).

```csharp
public void ConfigureServices(IServiceCollection services)
{
    // Register your DbContext as usual.
    services.AddDbContext<MyDbContext>(opt =>
        opt.UseSqlServer(Configuration.GetConnectionString("Chatter")));

    services.AddChatterCqrs(Configuration, pipeline =>
            {
                // Replace the in-memory inbox with the durable EF inbox
                // (idempotent / once-only handling).
                pipeline.WithInboxBehavior<MyDbContext>();

                // Process the EF outbox for reliable publish.
                pipeline.WithOutboxProcessingBehavior<MyDbContext>();

                // Or, to opt into transactional units of work only:
                // pipeline.WithUnitOfWorkBehavior<MyDbContext>();

                // Optional: put inbox markers and processed outbox rows on a
                // retention window. Both windows are disabled by default.
                pipeline.WithReliabilityRetention<MyDbContext>(retention =>
                {
                    retention.InboxDeduplicationWindow = TimeSpan.FromDays(7);
                    retention.ProcessedOutboxRetention = TimeSpan.FromDays(3);
                    retention.PurgeInterval = TimeSpan.FromMinutes(5);
                });
            })
            .AddMessageBrokers(/* message broker options */);
}
```

The available pipeline extension methods (`Microsoft.Extensions.DependencyInjection.Extensions`):

| Method | Effect |
| --- | --- |
| `WithUnitOfWorkBehavior<TContext>()` | Replaces `IUnitOfWork` with the EF `UnitOfWork<TContext>` (scoped) and adds the `UnitOfWorkBehavior`. |
| `WithInboxBehavior<TContext>()` | Adds the unit of work, replaces `IBrokeredMessageInbox` with `BrokeredMessageInbox<TContext>`, and adds the `InboxBehavior`. |
| `WithOutboxProcessingBehavior<TContext>()` | Adds the `OutboxProcessingBehavior`, replaces `IBrokeredMessageOutbox` with `BrokeredMessageOutbox<TContext>` and `IRouteBrokeredMessages` with the outbox router, and adds the unit of work. |
| `WithReliabilityRetention<TContext>(configure)` | Registers the `EntityFrameworkReliabilityOptions` the configure delegate populates, replacing any instance an earlier call registered, and adds the `ReliabilityRetentionPurgeService<TContext>` hosted service. Adds no pipeline behavior. |

`WithReliabilityRetention<TContext>` refuses a non-positive `InboxDeduplicationWindow`, `ProcessedOutboxRetention`, or `PurgeInterval` with an `ArgumentOutOfRangeException` at registration rather than at the first purge. It is one door over both tables, so it reads the same whichever reliability behaviors a host registered; the last call wins, and the purge service is added once however many times you call it. Each purge pass issues **exactly one bounded `DELETE` per configured table** — the oldest 1,000 eligible rows — and leaves the remainder for the next pass, so a table is reclaimed at no more than 1,000 rows per `PurgeInterval`: **288,000 rows per table per day at the five-minute default**. **A table accruing eligible rows faster than that is never drained** — `PurgeInterval` is the dial, so shorten it to raise the ceiling, and size `InboxDeduplicationWindow` and `ProcessedOutboxRetention` knowing that retention bounds table growth only up to that rate. Pinned by `WhenPurgingRetentionOverSqlite.MustPurgeAnOutboxBacklogOneBoundedStatementPerPass` and `.MustPurgeAnInboxBacklogOneBoundedStatementPerPass`; the reasoning is recorded in the `INVARIANT:` on `ReliabilityRetentionPurgeService.PurgeOnceAsync`.

> The order in which you call these methods does not affect the resolved pipeline order — see [Reliability Behavior Order](#reliability-behavior-order) below.

### Reliability Behavior Order

Regardless of which order you call `WithInboxBehavior<TContext>()`, `WithOutboxProcessingBehavior<TContext>()`, and `WithUnitOfWorkBehavior<TContext>()` — and no matter how many times you call them — the package always resolves the three reliability behaviors into the same nesting: **outbox processing wraps the unit of work, which wraps the inbox**.

- The pipeline composes last-to-first, so the first-resolved behavior ends up outermost.
- The inbox marker sits inside the unit of work so that, given the precondition below, it commits or rolls back together with the handler's work.
- Outbox processing dispatches to the broker after the handler returns and must only dispatch committed rows, so it sits outside the unit of work.

This guarantee is independent of call order and call count — calling the extension methods in any sequence, or calling one of them more than once, always produces the same nesting. A behavior your application registers between the extension calls keeps the pipeline slot it was registered into, but may end up on a different side of the reliability behaviors than before.

> **Scope:** this ordering guarantee covers only descriptors registered through `WithUnitOfWorkBehavior<TContext>()`, `WithInboxBehavior<TContext>()`, and `WithOutboxProcessingBehavior<TContext>()`. Later direct `WithBehavior` calls, and closed-generic, factory, keyed, or decorated registrations of these behavior types, are intentionally outside normalization and are not reordered.

> **One `DbContext`, enforced at registration (durability, not ordering):** the ordering above is independent of `TContext`, but the commit-together guarantee is not — the unit of work has to commit the very context that holds the inbox marker and the staged outbox rows. `WithUnitOfWorkBehavior<TContext>()`, `WithInboxBehavior<TContext>()`, `WithOutboxProcessingBehavior<TContext>()`, and `WithReliabilityRetention<TContext>(...)` each bind that context as their first statement. The first call to arrive records its `TContext`; a later call naming the same one is a no-op however many times it arrives; a later call naming a *different* one throws an `InvalidOperationException` naming both contexts. Because the binding is checked before anything is registered, a refused call leaves the service collection exactly as it found it. A lone `WithInboxBehavior<TContext>()` call is sufficient — it registers the matching unit of work itself. Retention is bound on the same terms, because its purge deletes through `TContext` too.
>
> This replaces earlier behavior in which the last call simply won: `IUnitOfWork` resolved to the `TContext` of the last call to any of the three behavior methods while `IBrokeredMessageInbox` resolved to the `TContext` of the last `WithInboxBehavior<TContext>()`, so a mismatched pair ran with the unit of work committing a different `DbContext` than the one holding the marker, and nothing said so.

### Configuring the DbContext

The inbox and outbox entities — `InboxMessage` and `OutboxMessage` (from `Chatter.MessageBrokers.Reliability.Inbox` / `.Outbox`) — must be mapped onto your `DbContext`. This package ships `IEntityTypeConfiguration<>` classes for both. Apply them in `OnModelCreating`:

```csharp
using Chatter.MessageBrokers.Reliability.EntityFramework;
using Chatter.MessageBrokers.Reliability.Inbox;
using Chatter.MessageBrokers.Reliability.Outbox;
using Microsoft.EntityFrameworkCore;

public class MyDbContext : DbContext
{
    public MyDbContext(DbContextOptions<MyDbContext> options) : base(options) { }

    // Your own aggregates / tables also live here.
    public DbSet<InboxMessage> InboxMessages { get; set; }
    public DbSet<OutboxMessage> OutboxMessages { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfiguration(new InboxMessageConfiguration());
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
    }
}
```

There is no separate "Chatter DbContext" — you supply your own, and the inbox/outbox tables live alongside your domain tables so they share the same transaction. Use EF migrations to create the tables.

#### Opt-in: the outbox poll index

`OutboxMessageConfiguration` declares no index, so out of the box the outbox poll has none to use. This package ships a second, separate `IEntityTypeConfiguration<OutboxMessage>` — `OutboxMessagePollIndexConfiguration` — that adds one over `ProcessedFromOutboxAtUtc` then `SentToOutboxAtUtc`: the leading column is the one both unprocessed-message polls filter on, and the second follows it because a drain works the oldest staged message first. The index is neither filtered nor unique, so it stays provider-neutral.

It is a separate configuration rather than part of `OutboxMessageConfiguration` precisely so that it is *your* decision. Applying it is a model change like any other, and you generate and own the migration for it exactly as you would for any other model change:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);

    modelBuilder.ApplyConfiguration(new InboxMessageConfiguration());
    modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());

    // Opt in to the poll index — then generate a migration for it.
    modelBuilder.ApplyConfiguration(new OutboxMessagePollIndexConfiguration());
}
```

## Inbox & Outbox

### Inbox (idempotency)

`BrokeredMessageInbox<TContext>` enforces **once-only handling**, and the marker row carries its own answer. `ReceivedByInboxAtUtc` records which of two states a message id is in: **no stamp means claimed, with the handler not completed; a stamp means handled, at that instant.** When a message arrives, the inbox reads the marker for its `MessageId` by key:

- If a marker is present carrying a stamp within the deduplication window, the handler is skipped and the suppression is logged at Information with the message id.
- If a marker is present carrying a stamp older than the window, it is treated as spent and the message is handled again.
- If a marker is present carrying **no** stamp, it records a claim no handler completed, so the message is handled again.
- If no marker is present, the message id is fresh.

In each of the three handling cases the inbox then **claims the id before running the handler**: it writes `ReceivedByInboxAtUtc` as null — inserting the row for a fresh id, updating it for one already there — and flushes that write into the ambient transaction. `MessageId` is the primary key, so an existing row is re-claimed in place; a second row for the same id would not insert. The flush is what takes the store's lock on the row, which is how the store orders a concurrent delivery of the same id behind the claim rather than letting both read no marker and both run. When the handler returns, the inbox stamps the claim with the handled time and flushes again.

Both flushes go into the transaction the surrounding unit of work commits; the inbox itself issues no commit and no rollback, so the marker and the handler's effects still land together or not at all. `ReceiveViaInbox` therefore **refuses to run without an ambient transaction**, throwing `InvalidOperationException`: the flush would otherwise commit on its own and leave a claim standing outside the unit of work meant to carry it. Register through `WithInboxBehavior<TContext>()`, which registers the matching unit of work, or run the call inside `IUnitOfWork.ExecuteAsync`.

Because the claim is durable only for as long as the transaction carrying it, a handler that throws takes its claim down with the rollback and the message id is free again. A caller that *catches* the handler's failure and lets the unit of work commit anyway commits the claim without a stamp — its own work lands, and the message id reads as unhandled, so the next delivery runs the handler again rather than being dropped.

If the incoming message has no message id, the inbox simply executes the handler (no idempotency tracking is possible), with no marker and no transaction requirement. The registration guard in [Reliability Behavior Order](#reliability-behavior-order) is what makes every relational reliability participant share one `DbContext`, so the transaction the unit of work commits is the one holding the marker. The reasoning is recorded in [ADR-0033](https://github.com/brenpike/Chatter/blob/master/docs/adr/0033-the-relational-inbox-claims-before-the-handler-and-stamps-handled-after-it-in-the-same-row.md).

`HasBeenReceived` answers for the **handler**, not for the row: it reports `true` only for a marker carrying a stamp — and, where a window is configured, only one still inside it. A message id whose handler is still running, or has already failed, is not reported as received.

#### The deduplication window

`EntityFrameworkReliabilityOptions.InboxDeduplicationWindow` is **`null` — disabled — by default**, and `WithReliabilityRetention<TContext>(...)` is what sets it. With no window configured, nothing expires: every existing marker suppresses its id however old that marker is. A finite default was deliberately not chosen, because it would change the behavior of a host already running this package — a late redelivery its inbox suppresses today would start being handled again.

When a window *is* configured, expiry is decided at receive rather than left to the purge, so the period an id is actually suppressed is the window itself and not the window plus however long the next purge pass takes to arrive. A marker carrying no `ReceivedByInboxAtUtc` has no age to compare against and never suppresses at all, so the window has nothing to say about it; the purge reclaims it on the same pass as an aged-out one.

Size the window **at or above your worst-case redelivery horizon**. `MessageId` is a wire value chosen by whatever produced the message, so a forged or merely reused id suppresses a legitimate message for as long as its marker stays live; a window sized that way spends its suppression on genuine redeliveries and releases the id afterwards.

Two deliveries racing on the same expired id are ordered by the claim rather than by the window: both read the marker and find it expired, but only one of them re-claims the row, and the other waits on the lock that claim took. The waiter reads no outcome — it **surfaces a `DbUpdateConcurrencyException`**, because the winner replaced the timestamp the waiter's own re-claim was written against, so that re-claim matches no row and its handler never runs. Pinned over a real database by `Integration/WhenDeduplicatingInboxOnSqlServer.MustInvokeTheHandlerOnceWhenTwoRedeliveriesRaceAnExpiredMarker`. The message is not lost — the exception reaches the receive pipeline and the broker's redelivery reads the winner's stamp — but it is a behavior change for an application that was previously getting two handler invocations and no exception, recorded in [ADR-0033](https://github.com/brenpike/Chatter/blob/master/docs/adr/0033-the-relational-inbox-claims-before-the-handler-and-stamps-handled-after-it-in-the-same-row.md). The reasoning behind the disabled default is recorded in [ADR-0026](https://github.com/brenpike/Chatter/blob/master/docs/adr/0026-the-relational-inbox-decides-expiry-at-receive-so-purge-timing-cannot-suppress-a-legitimate-message.md).

### Outbox (reliable publish)

`BrokeredMessageOutbox<TContext>` implements the transactional outbox. Outgoing messages are serialized and staged as `OutboxMessage` rows via `SendToOutbox`. Each row captures the serialized body, message context (JSON), destination, content type, send time, and a `BatchId` (the current transaction id). `SendToOutbox` participates in the surrounding unit of work, so the staged row and the work that produced it commit together — the outbox never saves on its own, and a message is never published unless the local state change commits. An enqueue with no surrounding unit of work is staged, not persisted. The registration guard in [Reliability Behavior Order](#reliability-behavior-order) is what makes every relational reliability participant share one `DbContext`, so the transaction the unit of work commits is the one holding the staged rows.

Processing then drains the outbox separately:

- `GetUnprocessedMessagesFromOutbox` selects **unprocessed and due**, returning **at most `ReliabilityOptions.OutboxPollBatchSize` rows (default 100), oldest-staged first** — it translates to `WHERE ProcessedFromOutboxAtUtc IS NULL AND (NextAttemptAtUtc IS NULL OR NextAttemptAtUtc <= @now) ORDER BY SentToOutboxAtUtc` with the batch size applied as the row limit, so one poll never drags the whole backlog into memory. When `ReliabilityOptions.OutboxMaxDispatchAttempts` carries a value, `DispatchAttempts < @max` joins that predicate; it has none by default, so no attempt ceiling applies. Both added clauses sit ahead of the ordering and the row limit, so a message being held back costs no batch slot. Pinned by `WhenGettingUnprocessedMessages.MustSpendNoBatchSlotOnAMessageThatIsNotDue`, `.MustSpendNoBatchSlotOnAMessageThatHasSpentTheAttemptCeiling` and `.MustTranslateTheDueGateAndTheCeilingToSqlOverARelationalProvider`, which runs the poll over a relational provider; the reasoning is recorded in the `INVARIANT:` comments on `GetUnprocessedMessagesFromOutbox`, and the decision in [ADR-0031](https://github.com/brenpike/Chatter/blob/master/docs/adr/0031-outbox-selection-is-derived-from-durable-attempt-state-not-from-the-absence-of-success.md). The batch-size bound comes from the constructor taking `ReliabilityOptions`, which is the one the container resolves and which refuses a batch size below 1 with an `ArgumentOutOfRangeException`. The older two-argument constructor remains as the uncapped path for a caller that builds the outbox by hand; it applies neither the cap nor the ceiling, but the due clause is not options-dependent and holds there too (`.MustDueGateWithoutReliabilityOptions`).
- When a dispatch fails, `RecordDispatchAttempt` spends one attempt on the row and pushes `NextAttemptAtUtc` a backoff ahead, which is what the due clause above reads. It writes both columns with a single set-based `ExecuteUpdateAsync` keyed on the message's own `Id`, incrementing the count **in the database** rather than from the value the supplied message carries, and it bypasses the change tracker deliberately — the reasoning is recorded in the `INVARIANT:` comments on `RecordDispatchAttempt`, pinned by `WhenUpdatingProcessed.MustCountOneMoreDispatchAttemptOnTheStoredRow`, `.MustRecordTheAttemptAfterAFailedClaimLeftTheMessageStagedAsProcessed` and `.MustRecordTheAttemptOnlyOnTheMessageItWasHanded`.
- `BrokeredMessageOutboxProcessor` then **drains rather than trickles**: within one processing interval it re-polls for as long as a poll comes back full, and stops on the first poll that returns fewer rows than the batch size or adds no message identity the drain has not already seen. The seen set spans the **whole drain** rather than only the poll before, and is keyed on the `(Id, MessageId)` pair of each row. It is what keeps a full batch that cannot be dispatched from spinning against the store — dispatch failures are logged and swallowed, so such a batch is otherwise re-fetched identically forever. A drain also ends once it has seen `MaxDrainIdentities` — 10,000 — identities, which is what bounds that set; the ceiling is read after a poll's identities are tallied, so the poll that crosses it is kept whole, and whatever is still unprocessed is taken by the next poll after the interval wait.
- `GetUnprocessedBatch(batchId)` is deliberately left **uncapped, un-due-gated and unceilinged**: its caller dispatches one unit of work's staged messages with no re-poll behind it, so any of the three would drop a message nothing would ever come back for rather than defer it. Pinned by `WhenGettingUnprocessedMessages.MustNeitherDueGateNorCeilingTheUnprocessedBatch`.
- Both polls stay **tracked**, but the claim below does not depend on it: `OutboxMessageConfiguration` marks `ProcessedFromOutboxAtUtc` with `IsConcurrencyToken()`, so EF emits the entry's *original* value as a `WHERE ProcessedFromOutboxAtUtc IS NULL` predicate on the claiming update, and `UpdateProcessedDate` states that original as `null` itself rather than relying on the value a tracked message was loaded with. A message emits the same predicate whether it arrives tracked or detached.
- Before it publishes, a drain **claims the row** through `TryClaimForDispatch`: one set-based `ExecuteUpdateAsync` moving `NextAttemptAtUtc` one backoff ahead, matched on `Id = @id AND ProcessedFromOutboxAtUtc IS NULL AND NextAttemptAtUtc = @observed` — `@observed` being the instant that drain's own poll read. It neither opens a transaction nor saves, so it enlists in the drain's unit of work: the claim is **invisible to every other connection until that unit of work commits, and discarded by its rollback**. That is what arbitrates two hosts polling one database. The losing host's claim **waits on the winner's row lock for the winner's whole publish and then matches zero rows**, so that drain publishes nothing, stamps nothing and spends no attempt. Previously both hosts published and only the stamp conflicted, after the fact. Pinned by the eight `WhenClaimingForDispatch` facts and, over a real SQL Server with `READ_COMMITTED_SNAPSHOT` both ON and OFF and with the wait read from `sys.dm_exec_requests` rather than from elapsed time, by `Integration/WhenArbitratingOutboxDrainsOnSqlServer.MustMakeASecondDrainsClaimWaitOnTheWinningDrainsRowLock`, `.MustDenyTheWaitingDrainsClaimOnceTheWinningDrainCommits`, `.MustGrantTheWaitingDrainsClaimOnceTheWinningDrainRollsBack` and `.MustLeaveADifferentRowClaimableWhileADrainIsBlockedOnTheRacedRow`; the decision is recorded in [ADR-0035](https://github.com/brenpike/Chatter/blob/master/docs/adr/0035-the-outbox-drain-claims-the-row-it-is-about-to-publish-inside-the-transaction-that-publishes-it.md).
  - The lock is confined to **the one row the drain is publishing**, because the claim seeks it by primary key, so a drain blocked on one row leaves every other row claimable *during* the block — that is what the last of those four facts measures. Everything measured covers that single-row primary-key seek only.
  - **If a publish outruns the `DbContext` command timeout** — 30 seconds by the provider's default, not a value this package sets — the waiting drain's claim statement throws instead, and that drain spends a dispatch attempt it never used. Nothing is given up: `OutboxMaxDispatchAttempts` has no value by default, so the row is simply deferred, which is what you want while the winner is publishing it. **No option was added for this**, here or elsewhere.
- After a row is dispatched, `UpdateProcessedDate` stages the `ProcessedFromOutboxAtUtc` stamp. This column is an **optimistic concurrency token**, so two processors racing on the same row produce a `DbUpdateConcurrencyException` when the surrounding unit of work commits; the outbox's `IUnitOfWork.ExecuteAsync`, which wraps that commit, logs the time the winner recorded, resyncs the losing entry against the stored row, and rethrows. The token's guarantee is narrower than "exactly once": it prevents two *stale, competing* updates from both committing, so only one racer's stamp survives. It is **not** what keeps two drains from publishing the same row: the stamp is written *after* the dispatch returns, so the token can only conflict once both have already published. The drain claim above is what arbitrates that, before either publishes. Delivery remains at-least-once and handlers are expected to be idempotent.

### Unit of Work / Persistance Transaction

`UnitOfWork<TContext>` coordinates a single atomic commit. Its `ExecuteAsync` runs your operation inside an EF execution strategy: it either **begins** a `ReadCommitted` transaction or **participates** in one already active on the context, runs the operation, and calls `SaveChangesAsync`, which flushes into whichever transaction is active regardless of who began it.

Commit and dispose are scoped to ownership, not merely to activity: `ExecuteAsync` commits and disposes only the transaction it began. If the caller opened the ambient transaction, the unit of work leaves it open on both the success path and the failure path — on failure the original exception propagates and it is the caller's own dispose that discards the work. A caller who opens their own transaction around dispatch must therefore commit or roll it back themselves; SQL Server holds locks for as long as they leave it open. That is the caller's choice, and the correct semantics for participating in someone else's unit of work.

**A rollback of a transaction the unit of work began also clears `TContext`'s change tracker.** EF accepts changes when it saves, not when it commits, so without this the entities a failed attempt had flushed stay tracked as though the write had landed, and a later read on the same scoped context resolves them out of the identity map ahead of the store. The tracker is cleared wholesale rather than entry by entry, because detaching one flushed entity leaves its companions tracked — the same wrong answer read through a different object. Ownership scopes this exactly as it scopes the rollback: **where the unit of work adopted the caller's transaction it leaves the tracker alone**, since the caller owns both that transaction and the state staged into it. The success path is untouched, because a commit that stood leaves a truthful tracker. If your application catches the failure outside the unit of work and keeps using the same `DbContext`, expect its entities to be detached — that transaction had already rolled back, so the work was gone either way. The reasoning is recorded in [ADR-0034](https://github.com/brenpike/Chatter/blob/master/docs/adr/0034-a-rolled-back-unit-of-work-reconciles-its-contexts-change-tracker.md).

Every terminal step runs through one guard that logs the failure at Warning and swallows it, so no terminal step can become the failure the caller is told about. On the failure path — rolling back and disposing — that keeps the exception which actually caused the failure the one that propagates. On the success path, where only the dispose remains, it keeps a provider whose disposal faults from turning a committed unit of work into a thrown exception: the commit has already stood by then, and reporting it as a failure would have the caller compensate for work that is durable. `SaveChangesAsync` and the commit itself stay inside the guarded region, so a save or commit failure still propagates. That cleanup runs under `CancellationToken.None`: honouring the token that failed the operation would make a cancelled operation skip exactly the rollback cancellation called for. Beginning the transaction stays outside the guarded region, so a failure to begin one propagates untouched, with no scope in existence to clean up.

> **The guard buys exception fidelity, not a closed transaction.** It keeps a failing rollback or dispose from replacing the causal exception. It does not resurrect the transaction: a provider whose own disposal aborts still leaves that transaction attached to the context, and nothing here can undo that.

> **If you have called `EnableRetryOnFailure(...)`, you get a throw.** `ExecuteAsync` reads `RetriesOnFailure` on the context's execution strategy and, when it is `true`, throws an `InvalidOperationException` before the strategy runs and before any transaction is begun — so re-execution is unreachable rather than handled. Re-execution cannot be made safe here: the first attempt's `SaveChangesAsync` accepts every tracked change, so a retry after a failed commit saves nothing and commits an empty transaction, leaving the caller looking at success with the writes gone. Only the application can supply the `verifySucceeded` predicate that would close that. **Either remove `EnableRetryOnFailure(...)` from that `DbContext`, or stop registering the unit of work for it** (`WithUnitOfWorkBehavior`, `WithInboxBehavior`, `WithOutboxProcessingBehavior`) — the exception message names both ways out. Nothing is lost by the refusal: Chatter's recovery pipeline and broker redelivery already retry at their own layers. EF Core's default SQL Server strategy does not retry, so this affects only consumers who opted in. The reasoning is recorded in [ADR-0025](https://github.com/brenpike/Chatter/blob/master/docs/adr/0025-the-unit-of-work-refuses-a-retrying-execution-strategy-rather-than-re-executing-the-handler.md).

The transaction itself is exposed through `IPersistanceTransaction`, implemented by `PersistanceTransaction`, which wraps EF's `IDbContextTransaction` and surfaces `TransactionId`, `CommitAsync`, and `RollbackAsync`. It refuses a null transaction at creation, and `CommitAsync` / `RollbackAsync` throw `ObjectDisposedException` once the handle has been disposed. The current transaction is also published into the `TransactionContext` container so the outbox can stamp each message's `BatchId` with the active transaction id.

`IUnitOfWork.CurrentTransaction` reads whatever transaction the context currently holds. When the context holds none, it hands back a **no-active-transaction** handle rather than a wrapper around `null`: its `TransactionId` is `Guid.Empty`, disposing it is a no-op, and `CommitAsync` / `RollbackAsync` throw `InvalidOperationException` telling you to begin a transaction or run the work through `ExecuteAsync` first. That handle is never published into the `TransactionContext` container — only a live transaction is — so an application that took a transaction out of the container still holds a real one. Both of these paths previously produced a `NullReferenceException`.

> Note: the type is intentionally spelled **`Persistance`** (and `IPersistanceTransaction`) in the codebase. The README uses the correct English spelling _persistence_ in prose, but you must use `Persistance` when referencing the actual type.

## Database Schema

The entity configurations map two tables (table names default to the `DbSet`/entity names unless you override them).

### Inbox — `InboxMessage`

| Column | Type | Constraints |
| --- | --- | --- |
| `MessageId` | `string` | Primary key, required |
| `ReceivedByInboxAtUtc` | `DateTime?` | Nullable; **concurrency token**; when the handler completed, and `null` while the id is only claimed |

### Outbox — `OutboxMessage`

| Column | Type | Constraints |
| --- | --- | --- |
| `Id` | `int` | Primary key, required, generated on add (identity) |
| `MessageId` | `string` | Required |
| `ProcessedFromOutboxAtUtc` | `DateTime?` | Nullable; **concurrency token**; `null` until dispatched |
| `SentToOutboxAtUtc` | `DateTime` | Required |
| `MessageBody` | `string` | Required; serialized message payload |
| `MessageContext` | `string` | Required; JSON-serialized message context |
| `MessageContentType` | `string` | Required |
| `Destination` | `string` | Required |
| `BatchId` | `Guid` | Required; the transaction id the message was written under |
| `DispatchAttempts` | `int` | Required; store default `0`; incremented in the database by `RecordDispatchAttempt` |
| `NextAttemptAtUtc` | `DateTime?` | Nullable; `null` means **due now**; advanced a backoff ahead by `RecordDispatchAttempt` |

The last two are the columns [Migration: the outbox attempt columns](#migration-the-outbox-attempt-columns) adds.

**Indexes.** `OutboxMessageConfiguration` declares none beyond the `Id` primary key, so the poll has no supporting index unless you ask for one. Applying the opt-in `OutboxMessagePollIndexConfiguration` adds a single non-unique, non-filtered index over (`ProcessedFromOutboxAtUtc`, `SentToOutboxAtUtc`) — see [Opt-in: the outbox poll index](#opt-in-the-outbox-poll-index). That index does not lead with `NextAttemptAtUtc`: extending it would force a second migration on anyone who has already applied it, so the index shape is carried by [#381](https://github.com/brenpike/Chatter/issues/381) instead. `InboxMessageConfiguration` declares no index beyond the `MessageId` primary key, which is also the key the inbox reads a marker by.

## Domain Language

See [CONTEXT.md](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers.Reliability.EntityFramework/CONTEXT.md) for the domain glossary (Brokered Message Inbox/Outbox, Unit of Work, Persistance Transaction, Inbox Deduplication Window, Retention Purge, Outbox Attempt State, Outbox Poll Index).

[← All Chatter modules](https://github.com/brenpike/Chatter/blob/master/README.md)
