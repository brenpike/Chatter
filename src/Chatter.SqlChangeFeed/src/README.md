# Chatter.SqlChangeFeed

[![NuGet](https://img.shields.io/nuget/v/Chatter.SqlChangeFeed.svg)](https://www.nuget.org/packages/Chatter.SqlChangeFeed)
[![Downloads](https://img.shields.io/nuget/dt/Chatter.SqlChangeFeed.svg)](https://www.nuget.org/packages/Chatter.SqlChangeFeed)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![CI](https://github.com/brenpike/Chatter/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/brenpike/Chatter/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/brenpike/Chatter/blob/master/LICENSE)

**Strongly typed insert, update and delete notifications from a watched SQL Server table, delivered over SQL Server Service Broker.**

This package installs a Trigger on a table you choose. The Trigger sends the row changes you watch onto a SQL Server Service Broker queue, and a Brokered Message Receiver hands each change to your Chatter.CQRS handlers as `RowInsertedEvent<T>`, `RowUpdatedEvent<T>` or `RowDeletedEvent<T>`. Nothing polls the table. Part of the [Chatter](https://github.com/brenpike/Chatter) suite.

## Contents

- [Features](#features)
- [Installation](#installation)
- [Quick start](#quick-start)
- [Install requirements](#install-requirements)
- [Handling changes](#handling-changes)
- [Configuration](#configuration)
- [Object names](#object-names)
- [How it works](#how-it-works)
- [Re-running the migration](#re-running-the-migration)
- [Known limitations](#known-limitations)
- [Diagnostics](#diagnostics)
- [Upgrading](#upgrading)
- [Related packages](#related-packages)
- [Learn more](#learn-more)
- [License](#license)

## Features

- **Typed row events**: each change arrives as `RowInsertedEvent<T>`, `RowUpdatedEvent<T>` (old and new values) or `RowDeletedEvent<T>`, where `T` is your row type.
- **No polling**: a Trigger on the watched table pushes changes onto SQL Server Service Broker as they commit.
- **Ordinary handlers**: you handle changes with Chatter.CQRS `IMessageHandler<T>`, and can send or publish Brokered Messages from those handlers.
- **Change type filter**: watch any combination of inserts, updates and deletes.
- **Manual mode**: handle the raw batch as `ProcessChangeFeedCommand<T>` instead of per-row events.
- **Re-runnable Change Feed Migration**: one startup call installs the Service Broker objects, the Trigger and the install and uninstall Stored Procedures, and later runs reconcile rather than repeat.
- **Schema drift repair**: re-running the migration rebuilds the Trigger when the watched table's columns change and leaves it alone when they do not.
- **Safe refusals**: missing tables, tables without a primary key, unsupported platforms and diverged topology are refused with a named error before any Service Broker object or Trigger is created.

## Installation

```shell
dotnet add package Chatter.SqlChangeFeed
```

Targets .NET 10 (`net10.0`).

Dependencies: Chatter.MessageBrokers.SqlServiceBroker, Microsoft.Data.SqlClient 7.0.1.

Chatter.MessageBrokers and Chatter.CQRS come in transitively. `AddSqlChangeFeed` registers the SQL Server Service Broker transport for you, so you do not call `AddSqlServiceBroker` yourself.

## Quick start

The samples use `Host.CreateApplicationBuilder(args)` (`builder.Services`, `builder.Configuration`) with implicit usings enabled. `WebApplication.CreateBuilder(args)` works the same way, and so does any `IServiceCollection` with an `IConfiguration`. Read [Install requirements](#install-requirements) before you point this at a live database.

### 1. Define a row type

The row type is a class that implements `IMessage` and has a public parameterless constructor. Its properties are named after the watched table's columns; names match without regard to case.

```csharp
using Chatter.CQRS;

public class OrderRow : IMessage
{
    public int Id { get; set; }
    public string CustomerId { get; set; }
    public decimal Total { get; set; }
    public string Status { get; set; }
}
```

### 2. Register the change feed

Chain `AddSqlChangeFeed<T>` after `AddChatterCqrs` and `AddMessageBrokers`. The change feed's receiver depends on services that `AddMessageBrokers` registers.

```csharp
using Chatter.SqlChangeFeed;
using Chatter.SqlChangeFeed.DependencyInjection;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddChatterCqrs(builder.Configuration, typeof(Program).Assembly)
    .AddMessageBrokers()
    .AddSqlChangeFeed<OrderRow>(
        builder.Configuration.GetConnectionString("Orders"),
        databaseName: null,
        tableName: "Orders",
        optionsBuilder: o => o
            .WithSchema("dbo")
            .WithTypesOfChangesToWatch(ChangeTypes.Insert | ChangeTypes.Update | ChangeTypes.Delete));
```

`databaseName: null` uses the `Initial Catalog` of the connection string. `AddChatterCqrs` scans the assembly you pass for handlers, so put your handlers there.

### 3. Run the Change Feed Migration

Registration only wires up dependency injection; it creates nothing in SQL Server. Run the Change Feed Migration once at startup, after `Build()` and before the host starts:

```csharp
using var host = builder.Build();

await host.Services.UseChangeFeedSqlMigrationsAsync<OrderRow>();

await host.RunAsync();
```

With `WebApplication`, call `await app.Services.UseChangeFeedSqlMigrationsAsync<OrderRow>();` before `app.Run()`. Both methods also accept a `CancellationToken`. Running the migration on every startup is safe; see [Re-running the migration](#re-running-the-migration).

### 4. Handle row changes

Implement `IMessageHandler<T>` for the events you care about:

```csharp
using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.SqlChangeFeed;

public class OrderRowChangedHandler :
    IMessageHandler<RowInsertedEvent<OrderRow>>,
    IMessageHandler<RowUpdatedEvent<OrderRow>>,
    IMessageHandler<RowDeletedEvent<OrderRow>>
{
    private readonly ILogger<OrderRowChangedHandler> _logger;

    public OrderRowChangedHandler(ILogger<OrderRowChangedHandler> logger) => _logger = logger;

    public Task Handle(RowInsertedEvent<OrderRow> message, IMessageHandlerContext context)
    {
        _logger.LogInformation("Order {OrderId} inserted", message.Inserted.Id);
        return Task.CompletedTask;
    }

    public Task Handle(RowUpdatedEvent<OrderRow> message, IMessageHandlerContext context)
    {
        _logger.LogInformation("Order {OrderId} status {Old} -> {New}",
            message.NewValue.Id, message.OldValue.Status, message.NewValue.Status);
        return Task.CompletedTask;
    }

    public Task Handle(RowDeletedEvent<OrderRow> message, IMessageHandlerContext context)
    {
        _logger.LogInformation("Order {OrderId} deleted", message.Deleted.Id);
        return Task.CompletedTask;
    }
}
```

Insert, update or delete a row in `dbo.Orders` and the matching handler runs.

## Install requirements

The Change Feed Migration runs DDL against your own database. Check these before you run it.

### Supported platforms

| Target | Supported | Reason |
| --- | --- | --- |
| SQL Server (on-premises, VM or container) | Yes | Full SQL Server Service Broker. |
| Azure SQL Managed Instance | Yes | Full SQL Server Service Broker. |
| Azure SQL Database | No | The engine has no Service Broker. |

On Azure SQL Database the install Stored Procedure refuses with a named error, naming the engine edition and the watched table, before it creates any Service Broker object. The minimum server version is SQL Server 2016 SP1, because the Stored Procedures are created with `CREATE OR ALTER`.

### Privileges

The principal in the connection string needs `ALTER` on the target database. It does not need `sysadmin`, and the migration does not change the database owner.

### Enabling Service Broker disconnects other sessions

When Service Broker is disabled on the target database, the migration runs:

```sql
ALTER DATABASE [YourDatabase] SET ENABLE_BROKER WITH ROLLBACK IMMEDIATE;
```

> **Warning:** `WITH ROLLBACK IMMEDIATE` rolls back other sessions' open transactions on that database and disconnects them. This happens on the first install only; once Service Broker is enabled the statement is skipped. Run the first install in a maintenance window, or enable Service Broker yourself beforehand.

### Watched-table preconditions

Before it creates any Service Broker object, the install Stored Procedure refuses with a named error when:

- the watched table does not exist, or
- the watched table has no `PRIMARY KEY`. A `UNIQUE` constraint is not enough, because the Trigger joins `INSERTED` to `DELETED` on the primary key columns.

A refused install leaves no partly created queue, service or Trigger behind.

## Handling changes

### Row events

By default each changed row becomes one event, all in namespace `Chatter.SqlChangeFeed`:

| Event | Properties | Raised when the Change Feed Item has |
| --- | --- | --- |
| `RowInsertedEvent<T>` | `Inserted` | an inserted row only |
| `RowUpdatedEvent<T>` | `NewValue`, `OldValue` | both an inserted and a deleted row |
| `RowDeletedEvent<T>` | `Deleted` | a deleted row only |

These are `IEvent`s, so any number of handlers can handle each one, and you only implement the ones you need. A statement that changes several rows arrives as one message; the receiver dispatches one event per row, in order, each awaited before the next.

When a handler throws, the whole message fails, including rows already handled from that statement. The message is then received again under the Chatter.MessageBrokers [Recovery](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers/src/README.md#recovery) rules, so write handlers that tolerate seeing a row change more than once.

### Watching only some changes

`WithTypesOfChangesToWatch` sets which operations the Trigger fires on. Changes you do not watch never leave the database.

```csharp
.AddSqlChangeFeed<OrderRow>(connectionString, databaseName: null, tableName: "Orders",
    optionsBuilder: o => o.WithTypesOfChangesToWatch(ChangeTypes.Insert))
```

### Sending and publishing from a handler

Change feed handlers run inside a Brokered Message Receiver, so the Chatter.MessageBrokers handler-context extensions work there. For example, publish an integration event when an order row is inserted:

```csharp
public Task Handle(RowInsertedEvent<OrderRow> message, IMessageHandlerContext context)
    => context.Publish(new OrderPlaced { OrderId = message.Inserted.Id });
```

`OrderPlaced` is an `IEvent` mapped to a broker path, as described in the Chatter.MessageBrokers [Quick start](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers/src/README.md#quick-start).

### Processing the batch yourself

Call `ProcessTableChangesManually()` to skip the row events and receive each message as a `ProcessChangeFeedCommand<T>`. Its `Changes` property holds one `ChangeFeedItem<T>` per changed row, each with `Inserted` and `Deleted` values.

```csharp
builder.Services.AddChatterCqrs(builder.Configuration, typeof(Program).Assembly)
    .AddMessageBrokers()
    .AddSqlChangeFeed<OrderRow>(
        builder.Configuration.GetConnectionString("Orders"),
        databaseName: null,
        tableName: "Orders",
        optionsBuilder: o => o.ProcessTableChangesManually());
```

```csharp
using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.SqlChangeFeed;

public class OrderRowBatchHandler : IMessageHandler<ProcessChangeFeedCommand<OrderRow>>
{
    public Task Handle(ProcessChangeFeedCommand<OrderRow> message, IMessageHandlerContext context)
    {
        foreach (ChangeFeedItem<OrderRow> change in message.Changes)
        {
            // Inserted only: insert. Deleted only: delete. Both: update (Inserted is the new value).
        }

        return Task.CompletedTask;
    }
}
```

`ProcessChangeFeedCommand<T>` is a Command, so register exactly one handler for it.

## Configuration

Configuration is fluent only; this package reads no `appsettings.json` section.

### AddSqlChangeFeed arguments

`AddSqlChangeFeed<TRowChangedData>` extends `IChatterBuilder` (namespace `Chatter.SqlChangeFeed.DependencyInjection`). `TRowChangedData` must be a class that implements `IMessage` and has a public parameterless constructor. A non-generic overload, `AddSqlChangeFeed(Type rowChangedDataType, ...)`, takes the row type at runtime.

| Argument | Description |
| --- | --- |
| `connectionString` | Connection to the SQL Server that hosts the watched table. Required. |
| `databaseName` | Database that contains the table. Pass `null` to use the connection string's `Initial Catalog`. |
| `tableName` | Table to watch, without its schema. Required. |
| `optionsBuilder` | Optional `Action<SqlChangeFeedOptionsBuilder>` for everything else. |

`AddSqlChangeFeed` throws `ArgumentNullException` for a blank connection string or table name. It throws `InvalidOperationException` when neither `databaseName` nor the connection string names a database, and `ChangeFeedObjectNameCollisionException` for colliding names (see [Object names](#object-names)).

### Options reference

All methods are on `SqlChangeFeedOptionsBuilder` (namespace `Chatter.SqlChangeFeed.Configuration`) and return the builder.

| Option | Type | Default | Description |
| --- | --- | --- | --- |
| `WithNameOfDatabaseToWatch` | `string` | the `databaseName` argument | Sets the database that contains the watched table. |
| `WithSchema` | `string` | `dbo` | Schema of the watched table. The change feed's queues, Trigger and Stored Procedures are created in this schema too. |
| `WithTypesOfChangesToWatch` | `ChangeTypes` | `Insert \| Update \| Delete` | Operations the Trigger fires on. |
| `EmitRowChangeEvents` | — | on | Dispatch `RowInsertedEvent<T>`, `RowUpdatedEvent<T>` and `RowDeletedEvent<T>`. |
| `ProcessTableChangesManually` | — | off | Dispatch `ProcessChangeFeedCommand<T>` instead of row events. |
| `WithChangeFeedQueueName` | `string` | `Chatter_Queue_<RowType>` | Queue the migration creates and the receiver reads. Must be a single unqualified identifier. |
| `WithChangeFeedDeadLetterServiceName` | `string` | `Chatter_DeadLetterService_<RowType>` | Dead-letter service the migration creates and the receiver deadletters to. |
| `WithErrorQueueName` | `string` | — | Error Queue for messages that fail on every receive attempt. |
| `WithTransactionMode` | `TransactionMode` | `FullAtomicityViaInfrastructure` | Transaction mode of the change feed's receiver. |
| `WithMaxReceiveAttempts` | `int` | `10` | Has no effect; the receiver always uses `10`. See [Known limitations](#known-limitations). |
| `WithReceiverTimeoutInMilliseconds` | `int` | `-1` (wait indefinitely) | How long each Service Broker receive waits for a message before it is issued again. |
| `WithMessageBodyType` | `string` | `application/json; charset=utf-16` | Content type the transport uses to read and write bodies. The Trigger writes UTF-16 JSON, so keep the default. |
| `WithApplicationJsonUtf16CharsetMessageBodyType` | — | — | Resets the body type to `application/json; charset=utf-16`. |
| `WithConversationLifetimeInSeconds` | `int` | `int.MaxValue` | Dialog lifetime for messages Chatter sends over Service Broker. |
| `EnableConversationEncryption` / `DisableConversationEncryption` | — | disabled | Dialog encryption for messages Chatter sends over Service Broker. |
| `WithCompressedMessageBody` / `WithUncompressedMessageBody` | — | compressed | Body compression for messages Chatter sends over Service Broker. |

The last three rows configure the SQL Server Service Broker transport that `AddSqlChangeFeed` registers, which applies to messages your application sends over Service Broker. They do not change the Trigger: it always sends compressed, unencrypted messages with no lifetime, and the receiver decompresses them automatically. `TransactionMode` is in namespace `Chatter.MessageBrokers.Receiving`.

### Common configurations

A dedicated schema, explicit names, an Error Queue and a finite receiver timeout:

```csharp
using Chatter.MessageBrokers.Receiving;
using Chatter.SqlChangeFeed;
using Chatter.SqlChangeFeed.DependencyInjection;

builder.Services.AddChatterCqrs(builder.Configuration, typeof(Program).Assembly)
    .AddMessageBrokers()
    .AddSqlChangeFeed<OrderRow>(
        builder.Configuration.GetConnectionString("Orders"),
        databaseName: "Sales",
        tableName: "Orders",
        optionsBuilder: o => o
            .WithSchema("sales")
            .WithTypesOfChangesToWatch(ChangeTypes.Insert | ChangeTypes.Update)
            .WithChangeFeedQueueName("OrdersChangeFeed")
            .WithChangeFeedDeadLetterServiceName("OrdersChangeFeedDeadLetter")
            .WithErrorQueueName("OrdersChangeFeedErrors")
            .WithTransactionMode(TransactionMode.FullAtomicityViaInfrastructure)
            .WithReceiverTimeoutInMilliseconds(5000));
```

Watching two tables takes one `AddSqlChangeFeed` call and one migration call per row type:

```csharp
builder.Services.AddChatterCqrs(builder.Configuration, typeof(Program).Assembly)
    .AddMessageBrokers()
    .AddSqlChangeFeed<OrderRow>(connectionString, databaseName: null, tableName: "Orders")
    .AddSqlChangeFeed<CustomerRow>(connectionString, databaseName: null, tableName: "Customers");

using var host = builder.Build();

await host.Services.UseChangeFeedSqlMigrationsAsync<OrderRow>();
await host.Services.UseChangeFeedSqlMigrationsAsync<CustomerRow>();
```

Give each row type a distinct class name; see [Object names](#object-names).

## Object names

The migration installs seven objects per row type. Each default name is a fixed prefix plus the row type's class name, without its namespace.

| Object | Default name | Override |
| --- | --- | --- |
| Conversation queue | `Chatter_Queue_<RowType>` | `WithChangeFeedQueueName` |
| Conversation service | `Chatter_Service_<RowType>` | — |
| Dead-letter queue | `Chatter_DeadLetterQueue_<RowType>` | — |
| Dead-letter service | `Chatter_DeadLetterService_<RowType>` | `WithChangeFeedDeadLetterServiceName` |
| Trigger | `Chatter_ChangeFeedTrigger_<RowType>` | — |
| Install Stored Procedure | `Chatter_InstallChangeFeed_<RowType>` | — |
| Uninstall Stored Procedure | `Chatter_UninstallChangeFeed_<RowType>` | — |

Rules for configured names:

- Configured names are used for the installed objects and by the receiver. The conversation service keeps its derived name and is created on the configured queue.
- A queue name must be a single unqualified identifier, with no dot and no brackets. The migration creates it as one object inside the configured schema, while the receiver reads a dot as a schema separator, so a dotted or bracketed name leaves the receiver polling a queue that does not exist.
- Queues, the Trigger and the Stored Procedures share one schema-scoped namespace, so `WithChangeFeedQueueName` may not equal the dead-letter queue, Trigger or either Stored Procedure name. Services share a database-wide namespace, so `WithChangeFeedDeadLetterServiceName` may not equal the conversation service name.
- A collision throws `ChangeFeedObjectNameCollisionException` from `AddSqlChangeFeed`. Names are compared without regard to case, matching SQL Server's default collation. A queue and a service may share a name.

## How it works

`UseChangeFeedSqlMigrationsAsync<T>` installs, in order:

1. **Service Broker objects.** It enables Service Broker if needed, then creates the message type, the contract, the conversation queue and service, and the dead-letter queue and service.
2. **The Trigger.** An `AFTER INSERT, UPDATE, DELETE` Trigger on the watched table, limited to the change types you watch. It serializes the `INSERTED` and `DELETED` rows to JSON and sends them to the conversation service as one compressed message per statement.
3. **The install and uninstall Stored Procedures.** The install procedure checks the preconditions and creates or refreshes the Trigger. The uninstall procedure removes the Trigger, the queues, the services and both procedures.

The Trigger sends a message only when SQL Server fires it for a change type you watch. `TRUNCATE TABLE`, a bulk load run without `FIRE_TRIGGERS`, and changes made while the Trigger is disabled or the conversation service does not exist send nothing.

At runtime a Brokered Message Receiver reads the conversation queue and deserializes each message into a `ProcessChangeFeedCommand<T>`. With row events on, it classifies each `ChangeFeedItem<T>`: inserted only is an insert, deleted only is a delete, and both is an update. It then dispatches the matching event.

## Re-running the migration

Re-running the Change Feed Migration is safe, and it is how you repair a change feed after the watched table's schema changes. Service Broker objects that already exist are kept, and the Stored Procedures are replaced in place. The Trigger is reconciled against the table's current columns:

- Each run hashes the watched table's column set into a fingerprint and stores it in a comment in the Trigger.
- When the fingerprint matches the installed Trigger's, the Trigger is left untouched.
- When it differs or is missing, the Trigger is dropped and recreated from the current columns.

Only a Trigger on the watched table is refreshed. A same-named trigger on another table is left alone, and the install fails on the duplicate name.

### Diverged topology is refused

If the installed objects do not match your configuration, for example after you change `WithChangeFeedQueueName`, the install Stored Procedure refuses the run with a named error before it creates or alters any Service Broker object. It refuses when the conversation service is bound to a queue other than the configured one, or when a service other than the configured dead-letter service is bound to the dead-letter queue.

The refusal does not repair anything; nothing is rebound, renamed or dropped. To fix it, run the uninstall Stored Procedure that is already installed, then run the migration again:

```sql
EXEC [dbo].[Chatter_UninstallChangeFeed_OrderRow];
```

A refused or failed run never replaces the installed uninstall Stored Procedure, so it always removes the objects your last successful run installed. Running it drops the queues, which discards any notifications still on them.

## Known limitations

- **`WithMaxReceiveAttempts` has no effect.** The value is recorded but never reaches the receiver, which always allows `10` receive attempts before a message is deadlettered. Tracked in [#531](https://github.com/brenpike/Chatter/issues/531).
- **The `WithTransactionMode` IntelliSense text names the wrong default.** Its XML documentation says `ReceiveOnly`; the actual default is `FullAtomicityViaInfrastructure`. Tracked in [#531](https://github.com/brenpike/Chatter/issues/531).
- **Default names use the row type's class name only.** Two row types with the same class name in different namespaces derive the same seven object names. When both tables are in the same schema, the install fails on the duplicate Trigger name. The two overridable names cannot fix this, because the other five still collide, so give the row types distinct class names.
- **Transport options are shared.** Each `AddSqlChangeFeed` call registers its own SQL Server Service Broker transport options, and the transport uses the last registration. When you register several change feeds, give them the same receiver timeout, body type, lifetime, encryption and compression settings. Tracked in [#531](https://github.com/brenpike/Chatter/issues/531).
- **No trace context.** Change feed messages carry no headers; see [Diagnostics](#diagnostics).
- **Azure SQL Database is not supported.** It has no Service Broker; see [Install requirements](#install-requirements).

## Diagnostics

This package emits no telemetry of its own. The change feed's receiver is a Chatter.MessageBrokers Brokered Message Receiver, so with diagnostics on it emits the receive spans and metrics described in the Chatter.MessageBrokers [Diagnostics](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers/src/README.md#diagnostics) section.

Change feed messages carry no headers. They come from the Trigger, not from a Chatter sender, so no W3C trace context (`traceparent`, `tracestate`) or other Message Context travels with them. Handling a row change therefore starts a new trace instead of continuing the trace of whatever wrote the row. See [Trace context propagation](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers/src/README.md#trace-context-propagation) and the SQL Server Service Broker [Header propagation](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers.SqlServiceBroker/src/README.md#header-propagation) notes.

## Upgrading

Each item below lists what to change in your application. The [changelog](https://github.com/brenpike/Chatter/blob/master/src/Chatter.SqlChangeFeed/src/Chatter.SqlChangeFeed/CHANGELOG.md) gives the release for each one.

### IApplicationBuilder migration overloads were removed

The `UseChangeFeedSqlMigrations` and `UseChangeFeedSqlMigrationsAsync` overloads on `IApplicationBuilder` no longer exist, and the package no longer references `Microsoft.AspNetCore.Http.Abstractions`. Call the `IServiceProvider` form instead:

```csharp
// Before
app.UseChangeFeedSqlMigrations<OrderRow>();

// After
await app.ApplicationServices.UseChangeFeedSqlMigrationsAsync<OrderRow>();
```

Await the call from an async startup path; discarding the task lets the application start before the migration finishes.

### The synchronous migration is obsolete

`UseChangeFeedSqlMigrations<T>` and `UseChangeFeedSqlMigrations(Type)` on `IServiceProvider` are marked `[Obsolete]` at warning level and still work. They run the migration on the thread pool and block until it completes, so they do not deadlock under a `SynchronizationContext`. Move to `UseChangeFeedSqlMigrationsAsync` wherever you can await.

### Configured names reach the installed objects

`WithChangeFeedQueueName` and `WithChangeFeedDeadLetterServiceName` apply to the objects the migration installs, not only to the receiver. If you set either one on an existing install, the objects created under the default names are not renamed or dropped, and the migration refuses the run because the installed topology diverges. Run the installed `Chatter_UninstallChangeFeed_<RowType>` Stored Procedure, then run the migration again.

A configured name that collides with another object name throws `ChangeFeedObjectNameCollisionException` at registration. Choose a distinct name.

### Triggers are refreshed on schema change

A Trigger installed by an older version carries no column fingerprint, so the next migration run drops and recreates it once. After that, re-running the migration rebuilds the Trigger only when the watched table's columns change.

## Related packages

- [Chatter.CQRS](https://www.nuget.org/packages/Chatter.CQRS): The in-process Commands, Queries, Events and handlers that receive row changes.
- [Chatter.MessageBrokers](https://www.nuget.org/packages/Chatter.MessageBrokers): Brokered Message Receivers, sending and publishing, and Recovery.
- [Chatter.MessageBrokers.SqlServiceBroker](https://www.nuget.org/packages/Chatter.MessageBrokers.SqlServiceBroker): The SQL Server Service Broker transport this package runs on.

## Learn more

- [Domain glossary (CONTEXT.md)](https://github.com/brenpike/Chatter/blob/master/src/Chatter.SqlChangeFeed/CONTEXT.md)
- [Changelog](https://github.com/brenpike/Chatter/blob/master/src/Chatter.SqlChangeFeed/src/Chatter.SqlChangeFeed/CHANGELOG.md)
- [Context map of all Chatter modules](https://github.com/brenpike/Chatter/blob/master/CONTEXT-MAP.md)
- [Chatter suite README](https://github.com/brenpike/Chatter/blob/master/README.md)

## License

Licensed under the [MIT License](https://github.com/brenpike/Chatter/blob/master/LICENSE).
