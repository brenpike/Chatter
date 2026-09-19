# <a name="chatter-reliability-entityframework"></a> Chatter.MessageBrokers.Reliability.EntityFramework

Durable EF Core inbox/outbox and unit-of-work for [Chatter.MessageBrokers](#chatter-messagebrokers).

## Overview

`Chatter.MessageBrokers.Reliability.EntityFramework` is the EF Core implementation of the reliability ports defined by [Chatter.MessageBrokers](#chatter-messagebrokers): the brokered message **inbox**, **outbox**, and **unit of work**. Out of the box Chatter wires these ports to in-memory defaults; registering this package **replaces those in-memory defaults with durable, relational storage** backed by your application's `DbContext`.

This gives you:

- **Idempotent (once-only) message handling** via a persisted inbox of processed message ids.
- **Reliable publish** via the transactional outbox pattern — outgoing messages are written to your database in the *same* transaction as your domain state, then dispatched separately.
- **Atomic units of work** that commit your domain changes and inbox/outbox writes together (or roll them all back) through a single EF transaction.

Because the inbox, outbox, and unit of work all run against the same `DbContext`, your business state and the messaging bookkeeping share one transaction and one commit.

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

`BrokeredMessageInbox<TContext>` enforces **once-only handling**. When a message arrives, the inbox reads the marker for its `MessageId` by key:

- If a marker is present and was received within the deduplication window, the handler is skipped and the suppression is logged at Information with the message id.
- If a marker is present but was received longer ago than the window, it is treated as spent: the handler runs, and the marker's `ReceivedByInboxAtUtc` is refreshed in place. `MessageId` is the primary key, so refreshing the existing row is the only shape available — a second row for the same id would not insert.
- If no marker is present, the handler runs and on success an `InboxMessage` row is added recording the id and `ReceivedByInboxAtUtc`.

If the incoming message has no message id, the inbox simply executes the handler (no idempotency tracking is possible). Whether the marker is added or refreshed, it participates in the surrounding unit of work, so the handler's effects and the inbox record commit together — the inbox never saves on its own. The registration guard in [Reliability Behavior Order](#reliability-behavior-order) is what makes every relational reliability participant share one `DbContext`, so the transaction the unit of work commits is the one holding the marker.

#### The deduplication window

`EntityFrameworkReliabilityOptions.InboxDeduplicationWindow` is **`null` — disabled — by default**, and `WithReliabilityRetention<TContext>(...)` is what sets it. With no window configured, nothing expires: every existing marker suppresses its id however old that marker is. A finite default was deliberately not chosen, because it would change the behavior of a host already running this package — a late redelivery its inbox suppresses today would start being handled again.

When a window *is* configured, expiry is decided at receive rather than left to the purge, so the period an id is actually suppressed is the window itself and not the window plus however long the next purge pass takes to arrive. A marker carrying no `ReceivedByInboxAtUtc` has no age to compare against: it is never treated as expired, and the purge never deletes it either.

Size the window **at or above your worst-case redelivery horizon**. `MessageId` is a wire value chosen by whatever produced the message, so a forged or merely reused id suppresses a legitimate message for as long as its marker stays live; a window sized that way spends its suppression on genuine redeliveries and releases the id afterwards.

Deduplication covers redelivery, not concurrency. Two deliveries racing on the same expired id can both read the marker, both find it expired, and both run the handler before either commits. That accepted residual — and the reasoning behind the disabled default — is recorded in [ADR-0026](https://github.com/brenpike/Chatter/blob/master/docs/adr/0026-the-relational-inbox-decides-expiry-at-receive-so-purge-timing-cannot-suppress-a-legitimate-message.md).

### Outbox (reliable publish)

`BrokeredMessageOutbox<TContext>` implements the transactional outbox. Outgoing messages are serialized and staged as `OutboxMessage` rows via `SendToOutbox`. Each row captures the serialized body, message context (JSON), destination, content type, send time, and a `BatchId` (the current transaction id). `SendToOutbox` participates in the surrounding unit of work, so the staged row and the work that produced it commit together — the outbox never saves on its own, and a message is never published unless the local state change commits. An enqueue with no surrounding unit of work is staged, not persisted. The registration guard in [Reliability Behavior Order](#reliability-behavior-order) is what makes every relational reliability participant share one `DbContext`, so the transaction the unit of work commits is the one holding the staged rows.

Processing then drains the outbox separately:

- `GetUnprocessedMessagesFromOutbox` returns **at most `ReliabilityOptions.OutboxPollBatchSize` rows (default 100), oldest-staged first** — it translates to `WHERE ProcessedFromOutboxAtUtc IS NULL ORDER BY SentToOutboxAtUtc` with the batch size applied as the row limit, so one poll never drags the whole backlog into memory. The bound comes from the constructor taking `ReliabilityOptions`, which is the one the container resolves and which refuses a batch size below 1 with an `ArgumentOutOfRangeException`. The older two-argument constructor remains as the uncapped path for a caller that builds the outbox by hand.
- `BrokeredMessageOutboxProcessor` then **drains rather than trickles**: within one processing interval it re-polls for as long as a poll comes back full, and stops when a poll returns fewer rows than the batch size or returns the same rows as the previous poll. The repeat check compares the `(Id, MessageId)` pair of each row, and it is what keeps a full batch that cannot be dispatched from spinning against the store — dispatch failures are logged and swallowed, so such a batch is otherwise re-fetched identically forever.
- `GetUnprocessedBatch(batchId)` is deliberately left **uncapped**: its caller dispatches one unit of work's staged messages with no re-poll behind it, so a cap there would drop the remainder permanently rather than defer it.
- Both polls stay **tracked**. Tracking is necessary but not sufficient for the claim below: `OutboxMessageConfiguration` marks `ProcessedFromOutboxAtUtc` with `IsConcurrencyToken()`, and it is that marking *plus* tracking that makes EF carry the loaded `null` as the original value and emit it as a `WHERE ProcessedFromOutboxAtUtc IS NULL` predicate on the claiming update. Reading the poll with `AsNoTracking` would leave original and current value both stamped, matching no row and failing every drain.
- After a row is dispatched, `UpdateProcessedDate` stages the `ProcessedFromOutboxAtUtc` stamp. This column is an **optimistic concurrency token**, so two processors racing on the same row produce a `DbUpdateConcurrencyException` when the surrounding unit of work commits; the outbox's `IUnitOfWork.ExecuteAsync`, which wraps that commit, logs the time the winner recorded, resyncs the losing entry against the stored row, and rethrows. The token's guarantee is narrower than "exactly once": it prevents two *stale, competing* updates from both committing, so only one racer's stamp survives. It does **not** prevent a duplicate publish: `OutboxProcessor` dispatches the row to the broker *before* it stamps, so both racers have already published by the time either commit conflicts. Duplicate delivery is the accepted cost of this at-least-once drain, and handlers are expected to be idempotent.

### Unit of Work / Persistance Transaction

`UnitOfWork<TContext>` coordinates a single atomic commit. Its `ExecuteAsync` runs your operation inside an EF execution strategy: it either **begins** a `ReadCommitted` transaction or **participates** in one already active on the context, runs the operation, and calls `SaveChangesAsync`, which flushes into whichever transaction is active regardless of who began it.

Commit and dispose are scoped to ownership, not merely to activity: `ExecuteAsync` commits and disposes only the transaction it began. If the caller opened the ambient transaction, the unit of work leaves it open on both the success path and the failure path — on failure the original exception propagates and it is the caller's own dispose that discards the work. A caller who opens their own transaction around dispatch must therefore commit or roll it back themselves; SQL Server holds locks for as long as they leave it open. That is the caller's choice, and the correct semantics for participating in someone else's unit of work.

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
| `ReceivedByInboxAtUtc` | `DateTime?` | When the message was recorded in the inbox |

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

**Indexes.** `OutboxMessageConfiguration` declares none beyond the `Id` primary key, so the poll has no supporting index unless you ask for one. Applying the opt-in `OutboxMessagePollIndexConfiguration` adds a single non-unique, non-filtered index over (`ProcessedFromOutboxAtUtc`, `SentToOutboxAtUtc`) — see [Opt-in: the outbox poll index](#opt-in-the-outbox-poll-index). `InboxMessageConfiguration` declares no index beyond the `MessageId` primary key, which is also the key the inbox reads a marker by.

## Domain Language

See [CONTEXT.md](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers.Reliability.EntityFramework/CONTEXT.md) for the domain glossary (Brokered Message Inbox/Outbox, Unit of Work, Persistance Transaction, Inbox Deduplication Window, Retention Purge, Outbox Poll Index).

[← All Chatter modules](https://github.com/brenpike/Chatter/blob/master/README.md)
