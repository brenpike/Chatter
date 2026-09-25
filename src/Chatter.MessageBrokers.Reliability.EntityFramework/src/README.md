# Chatter.MessageBrokers.Reliability.EntityFramework

[![NuGet](https://img.shields.io/nuget/v/Chatter.MessageBrokers.Reliability.EntityFramework.svg)](https://www.nuget.org/packages/Chatter.MessageBrokers.Reliability.EntityFramework)
[![Downloads](https://img.shields.io/nuget/dt/Chatter.MessageBrokers.Reliability.EntityFramework.svg)](https://www.nuget.org/packages/Chatter.MessageBrokers.Reliability.EntityFramework)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![CI](https://github.com/brenpike/Chatter/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/brenpike/Chatter/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/brenpike/Chatter/blob/master/LICENSE)

**EF Core Inbox, Outbox and Unit of Work for Chatter.MessageBrokers, stored in your own DbContext.**

This package replaces the in-memory Inbox and Outbox of [Chatter.MessageBrokers](https://www.nuget.org/packages/Chatter.MessageBrokers) with durable tables in your application's `DbContext`. Your domain changes, the Inbox marker and the staged Outbox messages commit in one EF Core transaction, or none of them do. There is no separate Chatter context: you apply the shipped entity configurations in your own `OnModelCreating` and own the migrations. Part of the [Chatter](https://github.com/brenpike/Chatter) suite.

## Contents

- [Features](#features)
- [Installation](#installation)
- [Quick start](#quick-start)
- [Pipeline registration](#pipeline-registration)
- [Inbox](#inbox)
- [Outbox](#outbox)
- [Unit of Work](#unit-of-work)
- [Retention purge](#retention-purge)
- [Database schema](#database-schema)
- [Configuration](#configuration)
- [Diagnostics](#diagnostics)
- [Upgrading](#upgrading)
- [Related packages](#related-packages)
- [Learn more](#learn-more)
- [License](#license)

## Features

- **Transactional Outbox**: messages your handler sends or publishes are staged as rows and commit with your domain changes, then are published after the commit.
- **Once-only Inbox**: a received command's message id is claimed before the handler runs and stamped after it, in the same transaction as the handler's work.
- **Unit of Work**: one `ReadCommitted` transaction per command, committed only if it was begun here; an existing transaction is joined, not replaced.
- **Your DbContext, your schema**: `IEntityTypeConfiguration` types for both tables, applied in your `OnModelCreating` and migrated with your own EF Core migrations.
- **Fixed behavior order**: the Outbox wraps the Unit of Work, which wraps the Inbox, whatever order you register them in.
- **Safe multi-host draining**: a drain claims the row it publishes inside its own transaction, so two hosts polling one database do not both publish it.
- **Retention purge**: an optional hosted service deletes expired Inbox markers and processed Outbox rows in bounded batches.
- **Opt-in poll index**: a separate configuration adds an index for the Outbox poll when you want one.

## Installation

```shell
dotnet add package Chatter.MessageBrokers.Reliability.EntityFramework
```

Targets .NET 10 (`net10.0`).

Dependencies: Chatter.MessageBrokers, Microsoft.EntityFrameworkCore 10.0.0, Microsoft.EntityFrameworkCore.Relational 10.0.0.

The database provider is yours to add, along with a transport for Chatter.MessageBrokers. For example, SQL Server and Azure Service Bus:

```shell
dotnet add package Microsoft.EntityFrameworkCore.SqlServer
dotnet add package Chatter.MessageBrokers.AzureServiceBus
```

To generate migrations you also need the EF Core design package and the `dotnet-ef` tool:

```shell
dotnet add package Microsoft.EntityFrameworkCore.Design
dotnet tool install --global dotnet-ef
```

## Quick start

The samples use `WebApplication.CreateBuilder(args)` (`builder.Services`, `builder.Configuration`) with implicit usings enabled. Any `IServiceCollection` with an `IConfiguration` works the same way.

### 1. Map the Inbox and Outbox in your DbContext

```csharp
using Chatter.MessageBrokers.Reliability.EntityFramework;
using Chatter.MessageBrokers.Reliability.Inbox;
using Chatter.MessageBrokers.Reliability.Outbox;
using Microsoft.EntityFrameworkCore;

public class OrdersDbContext : DbContext
{
    public OrdersDbContext(DbContextOptions<OrdersDbContext> options) : base(options) { }

    // Your own aggregates live here too.
    public DbSet<Order> Orders { get; set; }

    public DbSet<InboxMessage> InboxMessages { get; set; }
    public DbSet<OutboxMessage> OutboxMessages { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfiguration(new InboxMessageConfiguration());
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());

        // Optional: index the Outbox poll (see Database schema).
        // modelBuilder.ApplyConfiguration(new OutboxMessagePollIndexConfiguration());
    }
}
```

The `DbSet` properties are optional; they name the tables `InboxMessages` and `OutboxMessages`. Without them EF Core names the tables after the entity types.

### 2. Register the behaviors

```csharp
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<OrdersDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("Orders")));

builder.Services.AddChatterCqrs(builder.Configuration,
        pipeline => pipeline
            .WithInboxBehavior<OrdersDbContext>()
            .WithOutboxProcessingBehavior<OrdersDbContext>()
            .WithReliabilityRetention<OrdersDbContext>(r =>
            {
                r.InboxDeduplicationWindow = TimeSpan.FromDays(7);
                r.ProcessedOutboxRetention = TimeSpan.FromDays(3);
            }),
        typeof(Program))
    .AddMessageBrokers(o => o.AddReliabilityOptions(r => r.WithOutboxPollingProcessor()))
    .AddAzureServiceBus(asb => asb.WithConnectionString(builder.Configuration.GetConnectionString("ServiceBus")));
```

The extension methods live in the `Microsoft.Extensions.DependencyInjection` namespace, class `Extensions`, and extend the `CommandPipelineBuilder` passed to `AddChatterCqrs`. `WithOutboxPollingProcessor()` re-drains any message whose immediate publish failed; keep it on whenever you use the Outbox. Any transport works in place of `AddAzureServiceBus`.

### 3. Create the tables

```shell
dotnet ef migrations add AddChatterReliability
dotnet ef database update
```

This package ships no migrations. The tables are part of your model, so every schema change goes through your own migrations.

### 4. Write the handler

```csharp
using Chatter.CQRS;
using Chatter.CQRS.Context;

public class PlaceOrderHandler : IMessageHandler<PlaceOrder>
{
    private readonly OrdersDbContext _db;

    public PlaceOrderHandler(OrdersDbContext db) => _db = db;

    public async Task Handle(PlaceOrder message, IMessageHandlerContext context)
    {
        _db.Orders.Add(new Order { Id = message.OrderId, Sku = message.Sku });

        await context.Publish(new OrderPlaced { OrderId = message.OrderId }, "order-events");
    }
}
```

You do not call `SaveChangesAsync`. The Unit of Work saves and commits the new `Order`, the Inbox marker for `PlaceOrder` and the staged `OrderPlaced` row together. After the commit, the Outbox publishes `OrderPlaced` to `order-events`; if the handler throws, nothing is saved and nothing is published.

## Pipeline registration

| Method | Description |
| --- | --- |
| `WithUnitOfWorkBehavior<TContext>()` | Registers `UnitOfWork<TContext>` as `IUnitOfWork` and adds `UnitOfWorkBehavior<>`. |
| `WithInboxBehavior<TContext>()` | Registers `BrokeredMessageInbox<TContext>` as `IBrokeredMessageInbox` and adds `InboxBehavior<>`, plus the Unit of Work. |
| `WithOutboxProcessingBehavior<TContext>()` | Registers `BrokeredMessageOutbox<TContext>` as `IBrokeredMessageOutbox`, routes sends and publishes to it through `OutboxBrokeredMessageRouter`, and adds `OutboxProcessingBehavior<>`, plus the Unit of Work. |
| `WithReliabilityRetention<TContext>(Action<EntityFrameworkReliabilityOptions>)` | Sets the retention options and adds the `ReliabilityRetentionPurgeService<TContext>` hosted service. Adds no behavior. |

Every method takes `TContext : DbContext` and returns the `CommandPipelineBuilder`, so calls chain. Calling a method more than once is harmless.

### Inbox and Outbox

Use the registration from [Quick start](#quick-start) step 2. Received commands are handled once per message id, and outgoing messages commit with the handler's work.

### Outbox only

```csharp
builder.Services.AddChatterCqrs(builder.Configuration,
        pipeline => pipeline.WithOutboxProcessingBehavior<OrdersDbContext>(),
        typeof(Program))
    .AddMessageBrokers(o => o.AddReliabilityOptions(r => r.WithOutboxPollingProcessor()))
    .AddAzureServiceBus(asb => asb.WithConnectionString(builder.Configuration.GetConnectionString("ServiceBus")));
```

Outgoing messages commit with the handler's work. Received messages are not deduplicated.

### Inbox only

```csharp
builder.Services.AddChatterCqrs(builder.Configuration,
        pipeline => pipeline.WithInboxBehavior<OrdersDbContext>(),
        typeof(Program))
    .AddMessageBrokers()
    .AddAzureServiceBus(asb => asb.WithConnectionString(builder.Configuration.GetConnectionString("ServiceBus")));
```

Received commands are handled once per message id. Messages the handler sends or publishes go straight to the broker as it calls `Send` or `Publish`, not with the commit.

### Unit of Work only

```csharp
builder.Services.AddChatterCqrs(builder.Configuration,
        pipeline => pipeline.WithUnitOfWorkBehavior<OrdersDbContext>(),
        typeof(Program))
    .AddMessageBrokers();
```

Each command handler runs inside one transaction that is saved and committed when it returns. No Inbox or Outbox tables are used.

### Which messages the behaviors wrap

These are Command Pipeline behaviors, so they wrap Command handlers only; Event handlers run without them. The Inbox acts only on a command delivered by a Brokered Message Receiver, because it needs the received message id. A command dispatched in-process still runs inside the Unit of Work, and its sends and publishes still go through the Outbox.

### Behavior order

Whatever order you call the three behavior methods in, and however many times, they resolve to the same nesting: the Outbox wraps the Unit of Work, which wraps the Inbox. The Inbox marker commits with the handler's work, and the Outbox publishes only after that commit.

The guarantee covers descriptors added by `WithUnitOfWorkBehavior`, `WithInboxBehavior` and `WithOutboxProcessingBehavior`. A behavior of your own keeps the slot it was registered in, but may land on a different side of the reliability behaviors. Direct `WithBehavior` calls, and closed-generic, factory, keyed or decorated registrations of these behavior types, are not reordered.

### One DbContext per pipeline

All four methods must name the same `TContext`, because the Unit of Work commits the context that holds the Inbox marker, the Outbox rows and the rows retention purges. The first call records its `TContext`. A later call with a different one throws `InvalidOperationException` naming both contexts, and the refused call leaves the service collection exactly as it was.

## Inbox

`BrokeredMessageInbox<TContext>` keeps one `InboxMessage` row per message id. `ReceivedByInboxAtUtc` records its state: no value means the id is claimed and its handler has not completed; a value means the handler completed at that time.

When a command arrives, the Inbox reads the marker for its `MessageId`:

| Marker | Result |
| --- | --- |
| None | Fresh id: claim it and run the handler. |
| Stamped, inside the deduplication window (or any stamp when no window is set) | Skip the handler and log at Information. |
| Stamped, older than the deduplication window | Re-claim it and run the handler. |
| Not stamped | A claim no handler completed: re-claim it and run the handler. |

### Claim and stamp

The Inbox writes the claim and flushes it before the handler runs, then stamps it and flushes again after the handler returns. Both flushes go into the Unit of Work's transaction, so the marker and the handler's work commit together. The claim's flush takes the row lock, so a concurrent delivery of the same id waits behind it.

- **An ambient transaction is required.** Without one, `ReceiveViaInbox` throws `InvalidOperationException`. `WithInboxBehavior` registers the Unit of Work that supplies it.
- **A failing handler frees the id.** Its exception rolls back the claim, so the redelivery runs the handler again.
- **A swallowed failure reruns the handler.** If something catches the handler's exception and lets the Unit of Work commit, the claim commits without a stamp, and the next delivery runs the handler again.
- **No message id, no deduplication.** A message without a `MessageId` runs the handler directly.
- **`HasBeenReceived` answers for the handler.** It returns `true` only for a stamped marker, and only one inside the window when a window is set.

### Deduplication window

`InboxDeduplicationWindow` is `null` by default, so a marker suppresses its id forever. Set it through `WithReliabilityRetention` to let ids expire. Expiry is decided when a message is received, not by the purge, so an id is suppressed for exactly the window.

Size the window at or above your worst-case redelivery time. `MessageId` is chosen by whoever produced the message, so a forged or reused id suppresses a legitimate message for as long as its marker is inside the window.

Two deliveries racing on the same expired id are ordered by the claim. The one that loses the race gets a `DbUpdateConcurrencyException` and its handler does not run. The message is not lost: the exception reaches the receive pipeline, and the broker's redelivery finds the winner's stamp.

## Outbox

`BrokeredMessageOutbox<TContext>` stores outgoing messages as `OutboxMessage` rows. `WithOutboxProcessingBehavior` routes every send and publish to it, so a handler's messages are staged rather than sent.

1. The handler calls `Send` or `Publish`. The Outbox adds a row, tagged with the current transaction id as its `BatchId`, and does not save it.
2. The Unit of Work saves and commits the row together with your domain changes.
3. `OutboxProcessingBehavior` then publishes the rows staged under that transaction.
4. Each row is claimed, published, and stamped with `ProcessedFromOutboxAtUtc`.

> **Warning:** A send or publish made outside a Unit of Work, for example from a controller through `IBrokeredMessageDispatcher`, is added to the `DbContext` but not saved. Dispatch a command and send from its handler, or call `SaveChangesAsync` yourself.

### Failed publishes and the polling processor

A failed publish is logged and does not fail your command, which has already committed. The Outbox records the attempt with one database-side update: it increments `DispatchAttempts` and moves `NextAttemptAtUtc` one backoff ahead. Only the polling processor from Chatter.MessageBrokers publishes that row again, so enable it with `WithOutboxPollingProcessor()`.

Each poll selects rows that are unprocessed and due (`NextAttemptAtUtc` is null or past), oldest `SentToOutboxAtUtc` first, capped at `OutboxPollBatchSize`. When `OutboxMaxDispatchAttempts` is set, rows that have used up their attempts are skipped and stay in the table. The immediate publish after a commit takes every row of its own batch, with no cap, due check or attempt ceiling.

### Tuning the polling processor

The processor and its settings belong to Chatter.MessageBrokers:

```csharp
.AddMessageBrokers(o => o
    .AddReliabilityOptions(r => r
        .WithOutboxPollingProcessor(5000)      // poll every 5 seconds after a short batch
        .WithOutboxPollBatchSize(100)          // at most 100 rows per poll
        .WithOutboxDispatchBackoff(5, 60)      // wait 5 s after a failure, doubling to 60 s
        .WithOutboxMaxDispatchAttempts(20)))   // optional: stop polling a row after 20 failures
```

The same values can come from `appsettings.json`, where a configured key wins over the fluent call:

```json
{
  "Chatter": {
    "MessageBrokers": {
      "Reliability": {
        "EnableOutboxPollingProcessor": true,
        "OutboxProcessingIntervalInMilliseconds": 5000,
        "OutboxPollBatchSize": 100,
        "OutboxDispatchBackoffBaseInSeconds": 5,
        "OutboxDispatchBackoffCapInSeconds": 60,
        "OutboxMaxDispatchAttempts": null
      }
    }
  }
}
```

After a full batch the processor polls again at once; after a shorter one it waits the interval. See the [Chatter.MessageBrokers configuration](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers/src/README.md#configuration) for every option and the values it refuses.

### Several hosts on one database

Before publishing a row, a drain claims it with one update keyed on the row's `Id`, matching the `NextAttemptAtUtc` value its poll read. The claim runs inside the drain's transaction, so no other connection sees it until that transaction commits. A second host that polled the same row waits on that row's lock for the whole publish, then matches nothing and publishes nothing. The lock covers only that row, so other rows stay claimable.

If a publish takes longer than the `DbContext` command timeout (30 seconds by the provider default), the waiting drain's claim fails instead. That drain spends one dispatch attempt and the row is deferred, which is harmless while the first host publishes it.

### Delivery guarantee

`ProcessedFromOutboxAtUtc` is a concurrency token, so if two processors stamp the same row only one stamp survives and the other commit fails with a logged `DbUpdateConcurrencyException`. The stamp is written after the publish, so delivery is at-least-once: a publish followed by a failed stamp publishes the message again later. Put handlers that are not naturally idempotent behind the Inbox.

## Unit of Work

`UnitOfWork<TContext>.ExecuteAsync` runs the rest of the pipeline inside an EF Core execution strategy. It begins a `ReadCommitted` transaction, or joins the one already open on the context, then calls `SaveChangesAsync`.

- **It commits and disposes only what it began.** A transaction you opened yourself is saved into but left open, on success and on failure; you commit or roll it back.
- **A rollback clears the change tracker, for its own transaction only.** After rolling back a transaction it began, it clears `TContext`'s change tracker so no entity looks saved when it was not. If you catch the failure and keep using the same `DbContext`, expect its entities to be detached. It leaves the tracker alone when it joined your transaction.
- **Cleanup never hides the real error.** A rollback or dispose that fails is logged at Warning and swallowed, so the exception that caused the failure is the one you see. A dispose failure after a successful commit does not turn the commit into an error.

> **Important:** The Unit of Work refuses a `DbContext` configured with `EnableRetryOnFailure(...)`. `ExecuteAsync` throws `InvalidOperationException` before any transaction begins, because re-running a failed commit would commit an empty transaction and lose your writes. Remove `EnableRetryOnFailure(...)` from that context, or stop registering the Unit of Work for it. Chatter's Recovery and broker redelivery already retry at their own layers.

### Persistance Transaction

The transaction is exposed as `IPersistanceTransaction` (namespace `Chatter.MessageBrokers.Reliability`), with `TransactionId`, `CommitAsync` and `RollbackAsync`. The type is spelled `Persistance` in code; use that spelling when you reference it. `CommitAsync` and `RollbackAsync` throw `ObjectDisposedException` once the handle is disposed.

`IUnitOfWork.CurrentTransaction` returns the context's current transaction. With none open, it returns a handle whose `TransactionId` is `Guid.Empty` and whose `CommitAsync` and `RollbackAsync` throw `InvalidOperationException`. Only a live transaction is placed in the `TransactionContext`, which is where the Outbox reads the `BatchId` from.

## Retention purge

By default nothing is deleted: Inbox markers and processed Outbox rows stay forever. Call `WithReliabilityRetention` to set a window for either table and start the purge:

```csharp
pipeline.WithReliabilityRetention<OrdersDbContext>(r =>
{
    r.InboxDeduplicationWindow = TimeSpan.FromDays(7);    // also how long an id is suppressed
    r.ProcessedOutboxRetention = TimeSpan.FromDays(3);
    r.PurgeInterval = TimeSpan.FromMinutes(5);
});
```

`ReliabilityRetentionPurgeService<TContext>` runs one pass per `PurgeInterval`. A table is purged only when its window is set.

- **Bounded passes.** Each pass issues one `DELETE` per table for the 1,000 oldest eligible rows. That caps reclamation at 288,000 rows per table per day at the 5-minute default. If a table gains eligible rows faster, shorten `PurgeInterval`.
- **Inbox.** Markers stamped longer ago than the window, and markers claimed but never stamped, are deleted.
- **Outbox.** Only rows stamped processed longer ago than the retention are deleted; a row still waiting to publish is never deleted.
- **Errors.** A failed pass is logged at Error and retried on the next pass; it does not stop the host.

The last `WithReliabilityRetention` call wins, and the purge service is added once however often you call it.

## Database schema

Table names follow your `DbSet` property names, or the entity type names when there is no `DbSet`.

### InboxMessage table

| Column | Type | Constraints |
| --- | --- | --- |
| `MessageId` | `string` | Primary key, required. |
| `ReceivedByInboxAtUtc` | `DateTime?` | Nullable; concurrency token. When the handler completed; `null` while the id is only claimed. |

### OutboxMessage table

| Column | Type | Constraints |
| --- | --- | --- |
| `Id` | `int` | Primary key, required, generated on add. |
| `MessageId` | `string` | Required. |
| `ProcessedFromOutboxAtUtc` | `DateTime?` | Nullable; concurrency token. `null` until published. |
| `SentToOutboxAtUtc` | `DateTime` | Required. |
| `MessageBody` | `string` | Required. The serialized message. |
| `MessageContext` | `string` | Required. The JSON-serialized Message Context. |
| `MessageContentType` | `string` | Required. |
| `Destination` | `string` | Required. |
| `BatchId` | `Guid` | Required. The transaction id the row was staged under. |
| `DispatchAttempts` | `int` | Required; store default `0`. Failed publishes so far. |
| `NextAttemptAtUtc` | `DateTime?` | Nullable. `null` means due now; otherwise the earliest next attempt. |

### Outbox poll index

Neither configuration declares an index beyond the primary keys. To index the Outbox poll, apply `OutboxMessagePollIndexConfiguration` and generate a migration for it:

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);

    modelBuilder.ApplyConfiguration(new InboxMessageConfiguration());
    modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
    modelBuilder.ApplyConfiguration(new OutboxMessagePollIndexConfiguration());
}
```

It adds one non-unique, unfiltered index over (`ProcessedFromOutboxAtUtc`, `SentToOutboxAtUtc`), which works on any provider. It does not include `NextAttemptAtUtc`.

## Configuration

`EntityFrameworkReliabilityOptions` (namespace `Chatter.MessageBrokers.Reliability.EntityFramework`) is set in code through `WithReliabilityRetention`. It has no configuration section of its own.

| Option | Type | Default | Description |
| --- | --- | --- | --- |
| `InboxDeduplicationWindow` | `TimeSpan?` | `null` | How long a stamped Inbox marker suppresses its id and is kept. `null` suppresses and keeps forever. |
| `ProcessedOutboxRetention` | `TimeSpan?` | `null` | How long a processed Outbox row is kept. `null` keeps it forever. |
| `PurgeInterval` | `TimeSpan` | 5 minutes | Wait between purge passes. |

A zero or negative value for any of the three throws `ArgumentOutOfRangeException` at registration, as does a `PurgeInterval` longer than `Task.Delay` accepts (about 49.7 days).

To read the values from configuration, bind them inside the delegate:

```csharp
pipeline.WithReliabilityRetention<OrdersDbContext>(r =>
    builder.Configuration.GetSection("Orders:Retention").Bind(r));
```

```json
{
  "Orders": {
    "Retention": {
      "InboxDeduplicationWindow": "7.00:00:00",
      "ProcessedOutboxRetention": "3.00:00:00",
      "PurgeInterval": "00:05:00"
    }
  }
}
```

`Bind` is the `ConfigurationBinder` extension (namespace `Microsoft.Extensions.Configuration`) from Microsoft.Extensions.Configuration.Binder. The Chatter packages this package depends on already reference it, so you add no package for it.

The Outbox polling, batch size, backoff and attempt ceiling are `ReliabilityOptions` in Chatter.MessageBrokers; see [Tuning the polling processor](#tuning-the-polling-processor) and the [Chatter.MessageBrokers configuration](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers/src/README.md#configuration).

## Diagnostics

This package emits no spans or metrics of its own; see [Chatter.MessageBrokers diagnostics](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers/src/README.md#diagnostics). An Outbox row stores the trace context of the code that staged it. With diagnostics on, the drain publishes the row under a Chatter.MessageBrokers send span parented to that stored context, so the trace reads write, drain, receive (see [Trace context propagation](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers/src/README.md#trace-context-propagation)).

Log categories are the generic types, such as `BrokeredMessageInbox<TContext>`, `UnitOfWork<TContext>` and `ReliabilityRetentionPurgeService<TContext>`. Notable entries: a suppressed duplicate at Information, a failed transaction cleanup at Warning, and a failed purge pass at Error.

## Upgrading

Your application owns the schema, so each change below arrives as a model change you migrate yourself. Apply every row between your current version and the new one.

| Upgrading past | Change | What to do |
| --- | --- | --- |
| 0.9.0 | Outbox gains `DispatchAttempts` and `NextAttemptAtUtc`. | Generate a migration, apply it, then deploy the new binaries (schema first). |
| 0.9.0 | A `DbContext` with `EnableRetryOnFailure` is refused; mixing `TContext` types is refused. | Remove `EnableRetryOnFailure` from the context; name one `TContext` in every call. |
| 0.10.0 | `ReceivedByInboxAtUtc` becomes a concurrency token (a model annotation, no DDL). | Generate and commit the migration even though it is empty. Optionally backfill hand-written rows. |
| 0.11.0 | The Outbox drain claims rows before publishing. | Nothing. No schema change; deploy in either order. |
| 0.12.0 | Targets `net10.0` only. | Build your application on .NET 10. |

**Outbox attempt columns (0.9.0).** Against SQL Server, the generated migration emits:

```sql
ALTER TABLE [OutboxMessages] ADD [NextAttemptAtUtc] datetime2 NULL;
ALTER TABLE [OutboxMessages] ADD [DispatchAttempts] int NOT NULL DEFAULT 0;
```

Both are additive and need no backfill: existing rows get `0` attempts and a `null` next attempt, which means due now. The previous binaries ignore the new columns, so apply the migration first. New binaries against an unmigrated database fail the Outbox poll with a provider error.

**Inbox concurrency token (0.10.0).** `dotnet ef migrations add <name>` produces a migration with empty `Up()` and `Down()` whose model snapshot records the annotation. Commit it: EF Core 10 raises `PendingModelChangesWarning` as an error when the model differs from the last snapshot. Existing markers are all stamped and keep suppressing. If you wrote Inbox rows by hand with a null `ReceivedByInboxAtUtc`, they read as unfinished claims and do not suppress; this restores them:

```sql
UPDATE [InboxMessages] SET [ReceivedByInboxAtUtc] = SYSUTCDATETIME() WHERE [ReceivedByInboxAtUtc] IS NULL;
```

Replace `OutboxMessages` and `InboxMessages` with your table names.

## Related packages

- [Chatter.CQRS](https://www.nuget.org/packages/Chatter.CQRS): The Commands, Events and Command Pipeline the behaviors plug into.
- [Chatter.MessageBrokers](https://www.nuget.org/packages/Chatter.MessageBrokers): The Inbox, Outbox, polling processor and Recovery this package makes durable.
- [Chatter.MessageBrokers.AzureServiceBus](https://www.nuget.org/packages/Chatter.MessageBrokers.AzureServiceBus): Azure Service Bus transport.
- [Chatter.MessageBrokers.RabbitMQ](https://www.nuget.org/packages/Chatter.MessageBrokers.RabbitMQ): RabbitMQ transport.
- [Chatter.MessageBrokers.SqlServiceBroker](https://www.nuget.org/packages/Chatter.MessageBrokers.SqlServiceBroker): SQL Server Service Broker transport.
- [Chatter.MessageBrokers.Reliability.Cosmos](https://www.nuget.org/packages/Chatter.MessageBrokers.Reliability.Cosmos): Azure Cosmos DB reliability for the same abstractions.

## Learn more

- [Domain glossary (CONTEXT.md)](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers.Reliability.EntityFramework/CONTEXT.md)
- [Changelog](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/CHANGELOG.md)
- [Context map of all Chatter modules](https://github.com/brenpike/Chatter/blob/master/CONTEXT-MAP.md)
- [Chatter suite README](https://github.com/brenpike/Chatter/blob/master/README.md)

## License

Licensed under the [MIT License](https://github.com/brenpike/Chatter/blob/master/LICENSE).
