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

## Inbox & Outbox

### Inbox (idempotency)

`BrokeredMessageInbox<TContext>` enforces **once-only handling**. When a message arrives, the inbox checks for its `MessageId`:

- If the id is already present, the handler is skipped (the message was already processed).
- Otherwise the handler runs, and on success an `InboxMessage` row is added recording the id and `ReceivedByInboxAtUtc`.

If the incoming message has no message id, the inbox simply executes the handler (no idempotency tracking is possible). The inbox add participates in the surrounding unit of work, so the handler's effects and the inbox record commit together.

### Outbox (reliable publish)

`BrokeredMessageOutbox<TContext>` implements the transactional outbox. Outgoing messages are serialized and written as `OutboxMessage` rows *inside the same transaction* as the work that produced them, so a message is never published unless the local state change commits. Each row captures the serialized body, message context (JSON), destination, content type, send time, and a `BatchId` (the current transaction id).

Processing then drains the outbox separately:

- `GetUnprocessedMessagesFromOutbox` / `GetUnprocessedBatch(batchId)` return rows whose `ProcessedFromOutboxAtUtc` is `null`.
- After a row is dispatched, `UpdateProcessedDate` stamps `ProcessedFromOutboxAtUtc`. This column is an **optimistic concurrency token**, so two processors racing on the same row produce a `DbUpdateConcurrencyException` — the loser logs and treats the row as already processed rather than double-publishing.

### Unit of Work / Persistance Transaction

`UnitOfWork<TContext>` coordinates a single atomic commit. Its `ExecuteAsync` runs your operation inside an EF execution strategy: it begins a `ReadCommitted` transaction (reusing the ambient one if a transaction is already active), runs the operation, calls `SaveChangesAsync`, and commits — rolling back on any exception.

The transaction itself is exposed through `IPersistanceTransaction`, implemented by `PersistanceTransaction`, which wraps EF's `IDbContextTransaction` and surfaces `TransactionId`, `CommitAsync`, and `RollbackAsync`. The current transaction is also published into the `TransactionContext` container so the outbox can stamp each message's `BatchId` with the active transaction id.

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

## Domain Language

See [CONTEXT.md](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers.Reliability.EntityFramework/CONTEXT.md) for the domain glossary (Brokered Message Inbox/Outbox, Unit of Work, Persistance Transaction).

[← All Chatter modules](https://github.com/brenpike/Chatter/blob/master/README.md)
