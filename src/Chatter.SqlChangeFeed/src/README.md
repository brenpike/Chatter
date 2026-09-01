# <a name="chatter-sqlchangefeed"></a> Chatter.SqlChangeFeed

Emit strongly-typed notifications whenever rows in a watched SQL Server table are inserted, updated, or deleted.

## Overview

`Chatter.SqlChangeFeed` turns row-level changes in a SQL Server table into a *change feed* of in-process messages, without polling. It provisions a SQL trigger on the watched table that publishes changes onto a SQL Server Service Broker queue; Chatter's Service Broker receiver picks those messages up and dispatches them to your handlers.

This package was originally named **Table Watcher** (`Chatter.TableWatcher`); it is now `Chatter.SqlChangeFeed`. The "watcher" terminology still appears throughout the domain language.

By default, each change is delivered to your code as one of three strongly-typed events:

- `RowInsertedEvent<TRowChangeData>`
- `RowUpdatedEvent<TRowChangeData>`
- `RowDeletedEvent<TRowChangeData>`

Alternatively you can opt out of that fan-out and handle the raw `ProcessChangeFeedCommand<TRowChangeData>` (a batch of `ChangeFeedItem<TRowChangeData>`) yourself.

## Installation

```
dotnet add package Chatter.SqlChangeFeed
```

This package builds on `Chatter.CQRS` and `Chatter.MessageBrokers.SqlServiceBroker`, which are pulled in transitively.

## Getting Started

### 1. Register the change feed

`AddSqlChangeFeed<TRowChangedData>` is the primary entry point. It is an extension on `IChatterBuilder`, so chain it off your existing Chatter registration. `TRowChangedData` is a type implementing `IMessage` whose properties map to the columns of the watched row.

```csharp
using Chatter.CQRS.DependencyInjection;
using Chatter.SqlChangeFeed.DependencyInjection;

public void ConfigureServices(IServiceCollection services)
{
    services.AddChatterCqrs(typeof(Startup).Assembly)
            .AddSqlChangeFeed<MyRow>(
                connectionString: Configuration.GetConnectionString("Chatter"),
                databaseName: "MyDatabase",   // optional; falls back to the connection string's Initial Catalog
                tableName: "MyTable",
                optionsBuilder: opts => opts
                    .WithSchema("dbo")
                    .WithTypesOfChangesToWatch(ChangeTypes.Insert | ChangeTypes.Update | ChangeTypes.Delete));
}

// The IMessage that maps to a row in MyTable
public class MyRow : Chatter.CQRS.IMessage
{
    public int Id { get; set; }
    public string Name { get; set; }
}
```

A non-generic overload, `AddSqlChangeFeed(Type rowChangedDataType, ...)`, is available when the row type is only known at runtime.

### 2. Provision the SQL dependencies

The Trigger, Stored Procedures, and Service Broker objects are **not** created at registration time. Call `UseChangeFeedSqlMigrationsAsync<TRowChangedData>` on the `IServiceProvider` during startup to run the Change Feed Migration:

```csharp
public static async Task ProvisionChangeFeedAsync(IHost host, CancellationToken token)
{
    await host.Services.UseChangeFeedSqlMigrationsAsync<MyRow>(token);
}
```

A blocking bridge, `UseChangeFeedSqlMigrations<TRowChangedData>`, is also available; it calls `GetAwaiter().GetResult()` on the same work, so prefer the `Async` form wherever the host lets you await. Non-generic overloads of both take the row type as a `Type` argument, for when it is only known at runtime.

Read [Install Requirements](#install-requirements) before running this against a live database.

#### The `IApplicationBuilder` overloads are gone

The package no longer references `Microsoft.AspNetCore.Http.Abstractions`, and these four overloads were removed:

- `UseChangeFeedSqlMigrations<TRowChangedData>(this IApplicationBuilder, CancellationToken)`
- `UseChangeFeedSqlMigrations(this IApplicationBuilder, Type, CancellationToken)`
- `UseChangeFeedSqlMigrationsAsync<TRowChangedData>(this IApplicationBuilder, CancellationToken)`
- `UseChangeFeedSqlMigrationsAsync(this IApplicationBuilder, Type, CancellationToken)`

If one of those is now a compile error, reach the surviving `IServiceProvider` form through one property access:

```csharp
// before
app.UseChangeFeedSqlMigrations<MyRow>();

// after
app.ApplicationServices.UseChangeFeedSqlMigrations<MyRow>();
```

### 3. Handle the change notifications

With the default behavior (`ProcessChangeFeedCommandViaChatter = true`), implement `IMessageHandler<T>` for whichever events you care about:

```csharp
using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.SqlChangeFeed;

public class MyRowChangeHandler :
    IMessageHandler<RowInsertedEvent<MyRow>>,
    IMessageHandler<RowUpdatedEvent<MyRow>>,
    IMessageHandler<RowDeletedEvent<MyRow>>
{
    public Task Handle(RowInsertedEvent<MyRow> message, IMessageHandlerContext context)
    {
        var inserted = message.Inserted;
        // ...
        return Task.CompletedTask;
    }

    public Task Handle(RowUpdatedEvent<MyRow> message, IMessageHandlerContext context)
    {
        var before = message.OldValue;
        var after = message.NewValue;
        // ...
        return Task.CompletedTask;
    }

    public Task Handle(RowDeletedEvent<MyRow> message, IMessageHandlerContext context)
    {
        var deleted = message.Deleted;
        // ...
        return Task.CompletedTask;
    }
}
```

### Handling the raw change feed instead

If you call `ProcessTableChangesManually()` on the options builder, Chatter does **not** fan out to the row events. Instead, handle the batch command directly:

```csharp
public class MyChangeFeedHandler : IMessageHandler<ProcessChangeFeedCommand<MyRow>>
{
    public Task Handle(ProcessChangeFeedCommand<MyRow> message, IMessageHandlerContext context)
    {
        foreach (ChangeFeedItem<MyRow> change in message.Changes)
        {
            // change.Inserted and/or change.Deleted are populated depending on the operation
        }
        return Task.CompletedTask;
    }
}
```

## Install Requirements

The Change Feed Migration runs DDL against the consumer's own database. Read this before running it against a live one.

### Supported platforms

| Target | Supported | Why |
| --- | --- | --- |
| SQL Server (on-premises, VM, container) | Yes | Full SQL Server Service Broker. |
| Azure SQL Managed Instance | Yes | Full SQL Server Service Broker. |
| Azure SQL Database | **No** | The engine has no Service Broker at all. |

On Azure SQL Database (`SERVERPROPERTY('EngineEdition') = 5`) the install procedure refuses with a named error identifying the engine edition and the watched table, before creating any object — rather than failing obscurely part-way through.

**Server floor: SQL Server 2016 SP1.** The install and uninstall Stored Procedures are created with `CREATE OR ALTER`, so an upgraded package replaces a stale procedure body instead of silently keeping it. `CREATE OR ALTER` arrived in SQL Server 2016 SP1.

### Privileges

The installing principal must already hold `ALTER` on the target database. **Sysadmin is no longer required**, and install no longer transfers database ownership: the `ALTER AUTHORIZATION ON DATABASE::<db> TO [sa]` statement earlier versions emitted alongside `ENABLE_BROKER` is gone. Reassigning ownership to `sa` silently widened the privileges of every `EXECUTE AS OWNER` module in the consumer's database.

### Enabling Service Broker terminates other sessions

When the target database has Service Broker disabled, the migration issues:

```sql
ALTER DATABASE [YourDatabase] SET ENABLE_BROKER WITH ROLLBACK IMMEDIATE;
```

`WITH ROLLBACK IMMEDIATE` **rolls back other sessions' open transactions on that database and disconnects them.** Without it the statement waits for every other session on the database to close, so a first install behind a connection pool blocks indefinitely with no timeout and no diagnostic. This applies to the *first* install only — the statement sits behind an `is_broker_enabled = 0` guard, so once the broker is on it is skipped on every later run.

### Watched-table preconditions

Before any Service Broker object is created, the install procedure refuses — with an error naming the cause and the watched table — when:

- the watched table does not exist, or
- the watched table has no `PRIMARY KEY`. The Change Feed Trigger joins `INSERTED` to `DELETED` on the primary key columns, so a table carrying only a `UNIQUE` constraint is refused.

Because every precondition is checked first, a refused install leaves no partially created queue, service, or Trigger behind.

## Configuration

`AddSqlChangeFeed` takes the connection string, database, and table directly. Everything else is configured through the optional `Action<SqlChangeFeedOptionsBuilder>`, which produces a `SqlChangeFeedOptions`.

### Core arguments / `SqlChangeFeedOptions`

| Property | Meaning |
| --- | --- |
| `ConnectionString` | Connection to the SQL Server hosting the watched table. |
| `DatabaseName` | Database containing the table. Optional; defaults to the connection string's `Initial Catalog`. |
| `TableName` | The table to watch. |
| `SchemaName` | Schema of the table. Defaults to `dbo`. |
| `ChangeFeedTriggerTypes` | Which operations to watch (`ChangeTypes.Insert \| Update \| Delete`). Defaults to all three. |
| `ChangeFeedQueueName` | Name of the backing Service Broker queue. Drives the installed topology: the Change Feed Migration creates this queue and the receiver reads it. Defaults to a Chatter-generated name based on the row type. |
| `ChangeFeedDeadLetterServiceName` | Service Broker service to which dead-lettered messages are routed. Drives the installed topology: the Change Feed Migration creates this service. Defaults to a generated name. |
| `ProcessChangeFeedCommandViaChatter` | When `true` (default), Chatter fans changes out as `RowInserted`/`RowUpdated`/`RowDeleted` events. When `false`, you handle `ProcessChangeFeedCommand<T>` directly. |

### `SqlChangeFeedOptionsBuilder` methods

| Method | Effect |
| --- | --- |
| `WithNameOfDatabaseToWatch(string)` | Sets the database name. |
| `WithSchema(string)` | Sets the table schema (default `dbo`). |
| `WithTypesOfChangesToWatch(ChangeTypes)` | Restricts which operations raise notifications. |
| `EmitRowChangeEvents()` | Fan out to the row-change events (default). |
| `ProcessTableChangesManually()` | Deliver the raw `ProcessChangeFeedCommand<T>` instead. |
| `WithChangeFeedQueueName(string)` | Overrides the Service Broker queue name — both the queue the Change Feed Migration creates and the queue the receiver reads. |
| `WithChangeFeedDeadLetterServiceName(string)` | Overrides the dead-letter service name — both the dead-letter service the Change Feed Migration creates and the receiver's dead-letter path. |
| `WithErrorQueueName(string)` | Sets the error queue path for the receiver. |
| `WithTransactionMode(TransactionMode)` | Atomicity of the receiver (default `FullAtomicityViaInfrastructure`). |
| `WithMaxReceiveAttempts(int)` | Max receive attempts before recovery action (default `10`). |
| `WithReceiverTimeoutInMilliseconds(int)` | Receiver wait timeout (default `-1`, unlimited). |
| `WithConversationLifetimeInSeconds(int)` | Service Broker dialog lifetime. |
| `EnableConversationEncryption()` / `DisableConversationEncryption()` | Toggle Service Broker dialog encryption (default disabled). |
| `WithCompressedMessageBody()` / `WithUncompressedMessageBody()` | Toggle message-body compression (default compressed). |
| `WithMessageBodyType(string)` / `WithApplicationJsonUtf16CharsetMessageBodyType()` | Set the message body content type (default `application/json; charset=utf-16`). |

> **Configured names now reach the installed topology.** Previously `WithChangeFeedQueueName` bound only the receiver while the Change Feed Migration provisioned a default-named queue, so a consumer who set a queue name got a receiver reading a queue the migration never created. `WithChangeFeedDeadLetterServiceName` was dropped before it reached anything: the public `SqlChangeFeedOptions.ChangeFeedDeadLetterServiceName` property was never assigned during `Build()`, so both the migration and the receiver fell back to the generated name. Both configured names now flow through to the objects the migration installs, so **a consumer who already set either one will see the effective object name change** on the next migration run. The conversation *service* name stays derived from the row type in every case; that derived service is created on the configured queue, and the Trigger routes to the service rather than to the queue.

> **Known limitation — default names key on the row type's *simple* name.** Every default name `ChangeFeedObjectNames` derives — the conversation queue and service, the dead-letter queue and service, the Change Feed Trigger, and the install and uninstall Change Feed Stored Procedures — comes from the row type's **simple** name and never its namespace, so two row types sharing a simple name in different namespaces derive identical installed object names. When both watched tables live in the same schema the install **aborts** on the duplicate Change Feed Trigger name — that failure is the collision guard, not a defect. Configuration is not a full escape hatch: `WithChangeFeedQueueName` and `WithChangeFeedDeadLetterServiceName` override only 2 of those 7 names, so the other 5 still collide. The resolution is to give colliding row types **distinct simple names**.

## How It Works

When you call `UseChangeFeedSqlMigrations<T>`, `SqlDependencyManager<T>` runs a set of generated SQL scripts (via `ISqlDependencyManager`) against the target database. For each watched row type it provisions:

1. **Service Broker objects** — enables Service Broker on the database if needed (`ENABLE_BROKER WITH ROLLBACK IMMEDIATE`; see [Install Requirements](#install-requirements)) and creates the message type, contract, a conversation queue + service, and a dead-letter queue + service (`InstallAndConfigureSqlServiceBroker`). Names come from a single derivation, `ChangeFeedObjectNames`: the conversation queue and the dead-letter service honour `ChangeFeedQueueName` / `ChangeFeedDeadLetterServiceName` where configured, and every other name is derived from `ChatterServiceBrokerConstants` and the row type's simple name.
2. **A trigger on the watched table** (`CreateChangeFeedTrigger`) — an `AFTER INSERT/UPDATE/DELETE` trigger (scoped to `ChangeFeedTriggerTypes`) that serializes the affected `inserted`/`deleted` rows and `SEND`s them onto a Service Broker conversation as a compressed message.
3. **Install / uninstall Stored Procedures** (`CreateInstallationProcedure`, `CreateUninstallProcedure`) — both are emitted with `CREATE OR ALTER`, so re-running the migration replaces a stale procedure body rather than keeping it. The install procedure checks the preconditions, wires everything together, and creates or refreshes the Trigger; the uninstall procedure tears the Trigger, queues, services, and procedures back down.

At runtime the queue is drained by Chatter's SQL Service Broker receiver. A `ChangeFeedReceiver<T>` (a `BrokeredMessageReceiver`) deserializes each batch into a `ProcessChangeFeedCommand<T>` of `ChangeFeedItem<T>`. Each item carries an `Inserted` and/or `Deleted` snapshot, which the receiver uses to decide whether the change was an insert (inserted only), delete (deleted only), or update (both), dispatching the matching event — unless you chose `ProcessTableChangesManually()`.

**Provisioning is manual, not automatic.** Registration (`AddSqlChangeFeed`) only wires up DI; the SQL objects are created only when the Change Feed Migration is invoked at startup.

### Re-running the migration, and watched-table schema drift

Re-running the Change Feed Migration is safe, and it is also the repair path for a watched table whose schema changed. The Service Broker objects keep their `IF NOT EXISTS` guards, the Stored Procedures are replaced in place via `CREATE OR ALTER`, and the Trigger is reconciled against the watched table's *current* columns:

- On every run the install procedure re-derives the watched table's column set from `INFORMATION_SCHEMA` and hashes it into a fingerprint, which it embeds as a leading comment in the Trigger it creates.
- **Fingerprint matches** the one the installed Trigger carries — nothing changes. The Trigger is left completely untouched; its `object_id` and `modify_date` do not move.
- **Fingerprint differs, or is absent** (a Trigger installed by an earlier package version carries no marker) — the Trigger is dropped and recreated from the current column set.

Only a Trigger installed *on the watched table* is a refresh candidate. A same-named trigger on some other table is left alone, so the install fails loudly on the duplicate name rather than dropping an object the change feed does not own.

**The failure mode this replaces.** The Trigger's `SELECT` column list is fixed at the moment the Trigger is created. Previously a re-run early-returned as soon as the Trigger existed, so dropping or renaming a watched column left the Trigger referencing a column that no longer exists — and because the Trigger fires inside the consumer's own `INSERT`/`UPDATE`/`DELETE`, it aborted *their* writes to the watched table until someone manually uninstalled and reinstalled the change feed. Re-running the migration now repairs that instead.

## Header Propagation (including Trace Context)

Change-feed messages carry **no headers at all**. They originate from the SQL trigger this package provisions, which `SEND`s a `DEFAULT`-message-type message directly onto the Service Broker queue — there is no producer-side Chatter dispatch to stamp a Message Context, so there is nothing to propagate and nothing for a receiver to extract.

The consequence for the opt-in tracing added in [ADR-0010](https://github.com/brenpike/Chatter/blob/master/docs/adr/0010-optional-bcl-only-telemetry-per-assembly-sources-and-the-off-guard.md): W3C trace context (`traceparent` / `tracestate`) does **not** flow into a change-feed message, so handling a row change **starts a new trace** rather than continuing the trace of whatever wrote the row.

This is inherent to the change feed's trigger origin and is a **pre-existing property that affects all headers alike** — it is not something tracing introduced. It compounds with the receive side: because the trigger sends the `DEFAULT` message type, the queue is drained through the SQL Service Broker receiver's `DEFAULT` path, which itself builds a fresh header dictionary (see [Header Propagation in the SqlServiceBroker README](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers.SqlServiceBroker/src/README.md#header-propagation-including-trace-context)). Both ends are pinned by conformance tests, so a change that accidentally alters either is visible.

## Domain Language

See [`../CONTEXT.md`](https://github.com/brenpike/Chatter/blob/master/src/Chatter.SqlChangeFeed/CONTEXT.md) for the change-feed / Table Watcher glossary and relationships.

[← All Chatter modules](https://github.com/brenpike/Chatter/blob/master/README.md)
