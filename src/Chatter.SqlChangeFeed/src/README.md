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

The trigger, stored procedures, and Service Broker objects are **not** created at registration time. Call `UseChangeFeedSqlMigrations<TRowChangedData>` during startup to deploy them:

```csharp
public void Configure(IApplicationBuilder app)
{
    app.UseChangeFeedSqlMigrations<MyRow>();
}
```

(Overloads also exist on `IServiceProvider` for non-web hosts.)

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
| `ChangeFeedQueueName` | Name of the backing Service Broker queue. Defaults to a Chatter-generated name based on the row type. |
| `ChangeFeedDeadLetterServiceName` | Service Broker service to which dead-lettered messages are routed. Defaults to a generated name. |
| `ProcessChangeFeedCommandViaChatter` | When `true` (default), Chatter fans changes out as `RowInserted`/`RowUpdated`/`RowDeleted` events. When `false`, you handle `ProcessChangeFeedCommand<T>` directly. |

### `SqlChangeFeedOptionsBuilder` methods

| Method | Effect |
| --- | --- |
| `WithNameOfDatabaseToWatch(string)` | Sets the database name. |
| `WithSchema(string)` | Sets the table schema (default `dbo`). |
| `WithTypesOfChangesToWatch(ChangeTypes)` | Restricts which operations raise notifications. |
| `EmitRowChangeEvents()` | Fan out to the row-change events (default). |
| `ProcessTableChangesManually()` | Deliver the raw `ProcessChangeFeedCommand<T>` instead. |
| `WithChangeFeedQueueName(string)` | Overrides the Service Broker queue name. |
| `WithChangeFeedDeadLetterServiceName(string)` | Overrides the dead-letter service name. |
| `WithErrorQueueName(string)` | Sets the error queue path for the receiver. |
| `WithTransactionMode(TransactionMode)` | Atomicity of the receiver (default `FullAtomicityViaInfrastructure`). |
| `WithMaxReceiveAttempts(int)` | Max receive attempts before recovery action (default `10`). |
| `WithReceiverTimeoutInMilliseconds(int)` | Receiver wait timeout (default `-1`, unlimited). |
| `WithConversationLifetimeInSeconds(int)` | Service Broker dialog lifetime. |
| `EnableConversationEncryption()` / `DisableConversationEncryption()` | Toggle Service Broker dialog encryption (default disabled). |
| `WithCompressedMessageBody()` / `WithUncompressedMessageBody()` | Toggle message-body compression (default compressed). |
| `WithMessageBodyType(string)` / `WithApplicationJsonUtf16CharsetMessageBodyType()` | Set the message body content type (default `application/json; charset=utf-16`). |

## How It Works

When you call `UseChangeFeedSqlMigrations<T>`, `SqlDependencyManager<T>` runs a set of generated SQL scripts (via `ISqlDependencyManager`) against the target database. For each watched row type it provisions:

1. **Service Broker objects** — enables Service Broker on the database if needed and creates the message type, contract, a conversation queue + service, and a dead-letter queue + service (`InstallAndConfigureSqlServiceBroker`). Names are derived from `ChatterServiceBrokerConstants` and the row type name.
2. **A trigger on the watched table** (`CreateChangeFeedTrigger`) — an `AFTER INSERT/UPDATE/DELETE` trigger (scoped to `ChangeFeedTriggerTypes`) that serializes the affected `inserted`/`deleted` rows and `SEND`s them onto a Service Broker conversation as a compressed message.
3. **Install / uninstall stored procedures** (`CreateInstallationProcedure`, `CreateUninstallProcedure`) — the install procedure wires everything together; the uninstall procedure tears the trigger, queues, services, and procedures back down.

At runtime the queue is drained by Chatter's SQL Service Broker receiver. A `ChangeFeedReceiver<T>` (a `BrokeredMessageReceiver`) deserializes each batch into a `ProcessChangeFeedCommand<T>` of `ChangeFeedItem<T>`. Each item carries an `Inserted` and/or `Deleted` snapshot, which the receiver uses to decide whether the change was an insert (inserted only), delete (deleted only), or update (both), dispatching the matching event — unless you chose `ProcessTableChangesManually()`.

**Provisioning is manual, not automatic.** Registration (`AddSqlChangeFeed`) only wires up DI; the SQL objects are created only when `UseChangeFeedSqlMigrations` is invoked at startup. The scripts are idempotent (`IF NOT EXISTS` guards), so running them on each startup is safe.

## Domain Language

See [`../CONTEXT.md`](../CONTEXT.md) for the change-feed / Table Watcher glossary and relationships.

[← All Chatter modules](../../../README.md)
