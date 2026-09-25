# Chatter.MessageBrokers.SqlServiceBroker

[![NuGet](https://img.shields.io/nuget/v/Chatter.MessageBrokers.SqlServiceBroker.svg)](https://www.nuget.org/packages/Chatter.MessageBrokers.SqlServiceBroker)
[![Downloads](https://img.shields.io/nuget/dt/Chatter.MessageBrokers.SqlServiceBroker.svg)](https://www.nuget.org/packages/Chatter.MessageBrokers.SqlServiceBroker)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![CI](https://github.com/brenpike/Chatter/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/brenpike/Chatter/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/brenpike/Chatter/blob/master/LICENSE)

**SQL Server Service Broker transport for Chatter.MessageBrokers: send and receive Brokered Messages over Service Broker conversations.**

This package connects the Chatter.MessageBrokers abstractions to SQL Server Service Broker. A Service Broker Receiver reads a queue with `WAITFOR (RECEIVE ...)` and dispatches each message to your handlers, and the Service Broker Sender opens a conversation to a target service for every message you send or publish. The package provisions nothing: you create the message type, contract, queues and services yourself before the application starts. Part of the [Chatter](https://github.com/brenpike/Chatter) suite.

## Contents

- [Features](#features)
- [Installation](#installation)
- [Quick start](#quick-start)
- [SQL setup](#sql-setup)
- [Receiving](#receiving)
- [Sending](#sending)
- [Transactions](#transactions)
- [Configuration](#configuration)
- [Recovery](#recovery)
- [Header propagation](#header-propagation)
- [Diagnostics](#diagnostics)
- [Related packages](#related-packages)
- [Learn more](#learn-more)
- [License](#license)

## Features

- **Queue receivers**: bind a Command or Event type to a Service Broker queue with one `AddQueueReceiver<T>` call, or through the `[BrokeredMessage]` attribute.
- **Chatter envelope**: messages sent with the `//Chatter/BrokeredMessage` message type carry their message id and full Message Context end to end.
- **Interop with plain T-SQL**: the receiver also accepts the built-in `DEFAULT` message type, so a stored procedure or trigger can send to your handlers.
- **Three transaction modes**: receive without a transaction, receive in a SQL transaction, or commit the receive and the handler's Service Broker sends together.
- **Deadlettering**: failed and poisoned messages are resent to a dead-letter service you choose.
- **Body compression**: bodies are compressed with T-SQL `COMPRESS` on send and detected and decompressed on receive.
- **SQL-aware recovery**: transient SQL errors feed the Chatter.MessageBrokers retry and circuit breaker, and a missing queue stops the receiver instead of retrying forever.
- **Safe error logging**: Service Broker `Error` messages are logged with a parsed error code and a sanitized description.

## Installation

```shell
dotnet add package Chatter.MessageBrokers.SqlServiceBroker
```

Targets .NET 10 (`net10.0`).

Dependencies: `Chatter.MessageBrokers`, `Microsoft.Data.SqlClient` 7.0.1, `Microsoft.Extensions.Hosting` 10.0.0.

Service Broker is available in SQL Server and Azure SQL Managed Instance. Azure SQL Database has no Service Broker.

Companion packages:

- [Chatter.SqlChangeFeed](https://www.nuget.org/packages/Chatter.SqlChangeFeed) for typed insert, update and delete notifications from a watched table, built on this transport.
- [Chatter.MessageBrokers.Reliability.EntityFramework](https://www.nuget.org/packages/Chatter.MessageBrokers.Reliability.EntityFramework) for a durable Inbox and Outbox in your own `DbContext`.

## Quick start

The samples use `WebApplication.CreateBuilder(args)` (`builder.Services`, `builder.Configuration`). Any `IServiceCollection` with an `IConfiguration` works the same way.

### 1. Define a message

```csharp
using Chatter.CQRS.Commands;

public class PlaceOrder : ICommand
{
    public Guid OrderId { get; set; }
}
```

### 2. Handle it

```csharp
using Chatter.CQRS;
using Chatter.CQRS.Context;

public class PlaceOrderHandler : IMessageHandler<PlaceOrder>
{
    public Task Handle(PlaceOrder message, IMessageHandlerContext context)
    {
        // place the order
        return Task.CompletedTask;
    }
}
```

### 3. Create the Service Broker objects

Run the script in [SQL setup](#sql-setup) against your database. It creates the `Orders_Initiator_Service` you send from, the `Orders_Service` and `Orders_Queue` that `PlaceOrder` travels through, and the `Orders_DeadLetter_Service` for failed messages.

### 4. Register the transport and its receiver

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddChatterCqrs(builder.Configuration, typeof(Program).Assembly)
    .AddMessageBrokers()
    .AddSqlServiceBroker(ssb => ssb
        .AddSqlServiceBrokerOptions(builder.Configuration.GetConnectionString("Orders"))
        .AddQueueReceiver<PlaceOrder>("Orders_Queue", deadLetterServicePath: "Orders_DeadLetter_Service"));
```

`AddChatterCqrs` registers your handlers, `AddMessageBrokers` adds the Brokered Message Receiver infrastructure, and `AddSqlServiceBroker` adds this transport. The receiver reads the queue named `Orders_Queue`.

### 5. Add the connection string

```json
{
  "ConnectionStrings": {
    "Orders": "Server=<server>;Database=Orders;Integrated Security=true;TrustServerCertificate=true"
  }
}
```

### 6. Send a command from your API

```csharp
using Chatter.MessageBrokers.Routing.Options;
using Chatter.MessageBrokers.Sending;
using Chatter.MessageBrokers.SqlServiceBroker;

app.MapPost("/orders", async (PlaceOrder command, IBrokeredMessageDispatcher dispatcher) =>
{
    var options = new SendOptions();
    options.WithMessageContext(SSBMessageContext.ServiceName, "Orders_Initiator_Service");
    options.WithMessageContext(SSBMessageContext.ServiceContractName, ServicesMessageTypes.ChatterServiceContract);
    options.WithMessageContext(SSBMessageContext.MessageTypeName, ServicesMessageTypes.ChatterBrokeredMessageType);

    await dispatcher.Send(command, "Orders_Service", options: options);
    return Results.Accepted();
});
```

The destination is the target service, `Orders_Service`, not the queue. The three Message Context keys select the initiator service, the `//Chatter` contract and the Chatter envelope message type; see [Sending](#sending).

## SQL setup

This package issues only runtime Dialog Commands (`BEGIN DIALOG`, `SEND`, `RECEIVE`, `END CONVERSATION`). Before the application starts, the database must have Service Broker enabled and must contain the message type, contract, queues and services your receivers and senders name.

### Provisioning script

This script creates everything the [Quick start](#quick-start) uses. Rename the database, queues and services to suit your application, and keep the message type and contract names exactly as shown.

```sql
USE [master];
GO

IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE name = N'Orders' AND is_broker_enabled = 1)
    ALTER DATABASE [Orders] SET ENABLE_BROKER WITH ROLLBACK IMMEDIATE;
GO

USE [Orders];
GO

-- The Chatter envelope message type and the contract that carries it
CREATE MESSAGE TYPE [//Chatter/BrokeredMessage] VALIDATION = NONE;
CREATE CONTRACT [//Chatter] ([//Chatter/BrokeredMessage] SENT BY ANY);

-- The service your application sends from
CREATE QUEUE [Orders_Initiator_Queue];
CREATE SERVICE [Orders_Initiator_Service] ON QUEUE [Orders_Initiator_Queue] ([//Chatter]);

-- The service PlaceOrder is sent to, and the queue its receiver reads
CREATE QUEUE [Orders_Queue] WITH POISON_MESSAGE_HANDLING (STATUS = OFF);
CREATE SERVICE [Orders_Service] ON QUEUE [Orders_Queue] ([//Chatter]);

-- The service failed messages are deadlettered to
CREATE QUEUE [Orders_DeadLetter_Queue];
CREATE SERVICE [Orders_DeadLetter_Service] ON QUEUE [Orders_DeadLetter_Queue] ([//Chatter]);
GO
```

> **Warning:** `ALTER DATABASE ... SET ENABLE_BROKER WITH ROLLBACK IMMEDIATE` disconnects every other session in the database and rolls back its open transactions. Run it in a maintenance window, or ask your DBA to enable Service Broker ahead of time.

> **Important:** By default SQL Server disables a queue after five consecutive rolled-back receives. A failed handler rolls back its receive under `ReceiveOnly` and `FullAtomicityViaInfrastructure`, and Chatter redelivers it up to `maxReceiveAttempts` times (default `10`) before deadlettering. Create every queue you receive from with `POISON_MESSAGE_HANDLING (STATUS = OFF)`, as above, or keep `maxReceiveAttempts` at `5` or lower.

### What each object is for

- **`ENABLE_BROKER`**: turns Service Broker on for the database.
- **`//Chatter/BrokeredMessage` message type and `//Chatter` contract**: the names the Chatter envelope uses, available as `ServicesMessageTypes.ChatterBrokeredMessageType` and `ServicesMessageTypes.ChatterServiceContract`.
- **A queue per receiver**: the name you pass to `AddQueueReceiver<T>`.
- **A service per destination**: the name you pass as the destination when sending. A service must list every contract it accepts.
- **An initiator service**: the `FROM SERVICE` of the conversations your application opens.
- **A dead-letter service**: the name you pass as `deadLetterServicePath`.

To receive `DEFAULT` messages from plain T-SQL as well, list the `DEFAULT` contract on the target service:

```sql
CREATE SERVICE [Orders_Service] ON QUEUE [Orders_Queue] ([//Chatter], [DEFAULT]);
```

Run the application as the owner of these objects, or grant its database user the permissions Service Broker requires to begin dialogs on, send to and receive from them.

### Queue names

The queue name is bracket-quoted into the `RECEIVE` statement, never interpolated raw.

- A dotted name such as `dbo.Orders_Queue` means `schema.queue`, and each part is quoted separately (`[dbo].[Orders_Queue]`).
- A one-part name that contains a dot must be bracketed in your registration, for example `[orders.queue]`; it is then used whole.
- An already-bracketed name (`[Orders_Queue]` or `[dbo].[Orders_Queue]`) is used as written.
- A name with an empty part, such as `dbo.` or `.Orders_Queue`, throws `ArgumentException` when the receive command is built.

Service names are single identifiers and are never split on dots, so a URL-shaped service name works as written.

### Dialog Commands

| Command | T-SQL emitted | When it runs |
| --- | --- | --- |
| `BeginDialogConversationCommand` | `BEGIN DIALOG ... FROM SERVICE ... TO SERVICE ... [ON CONTRACT ...] WITH ENCRYPTION = ON\|OFF [, LIFETIME = ...]` | Sender, once per outbound message. |
| `SendOnConversationCommand` | `SEND ON CONVERSATION ... [MESSAGE TYPE ...] (...)`, with the body wrapped in `COMPRESS(...)` when compression is on | Sender, after the dialog opens. |
| `EndDialogConversationCommand` | `END CONVERSATION ... [WITH ERROR = ... DESCRIPTION = ...] [WITH CLEANUP]` | Sender after each dispatch when `EndConversationAfterDispatch` is on; receiver on acknowledge, deadletter, `EndDialog` and every discarded message. |
| `ReceiveMessageFromQueueCommand` | `WAITFOR (RECEIVE TOP(1) ... FROM <queue>) [, TIMEOUT ...]` | Receiver, to take the next message. |

### Inspecting the dead-letter queue

Deadlettered messages wait on the dead-letter queue until you read them. With compression on (the default), decompress the body to see the Chatter envelope as JSON:

```sql
SELECT message_type_name,
       CAST(DECOMPRESS(message_body) AS NVARCHAR(MAX)) AS envelope
FROM [Orders_DeadLetter_Queue];
```

[Chatter.SqlChangeFeed](https://www.nuget.org/packages/Chatter.SqlChangeFeed) creates Service Broker objects only for its own change feeds. It does not create objects for receivers you register with this package.

## Receiving

### Registering a queue receiver

Register receivers inside the `AddSqlServiceBroker(ssb => ...)` delegate:

```csharp
AddQueueReceiver<TMessage>(string queueName,
                           string errorQueuePath = null,
                           string description = null,
                           TransactionMode? transactionMode = null,
                           string deadLetterServicePath = null,
                           int maxReceiveAttempts = 10)
    where TMessage : class, IMessage
```

| Parameter | Type | Default | Description |
| --- | --- | --- | --- |
| `queueName` | `string` | — (required) | Queue the receiver reads. See [Queue names](#queue-names). |
| `errorQueuePath` | `string` | `null` | Service the message is forwarded to after it is deadlettered for exceeding `maxReceiveAttempts`. `null` skips the forward. |
| `description` | `string` | `null` | Free-text description of the receiver. |
| `transactionMode` | `TransactionMode?` | global `TransactionMode` | Overrides the Chatter.MessageBrokers transaction mode for this receiver. See [Transactions](#transactions). |
| `deadLetterServicePath` | `string` | `null` | Service failed and poisoned messages are resent to. Set it on every receiver; deadlettering has nowhere to send without it. |
| `maxReceiveAttempts` | `int` | `10` | Deliveries before a failing message is deadlettered. |

```csharp
using Chatter.MessageBrokers.Receiving;

builder.Services.AddChatterCqrs(builder.Configuration, typeof(Program).Assembly)
    .AddMessageBrokers()
    .AddSqlServiceBroker(ssb => ssb
        .AddSqlServiceBrokerOptions(builder.Configuration.GetConnectionString("Orders"))
        .AddQueueReceiver<PlaceOrder>(
            "Orders_Queue",
            transactionMode: TransactionMode.FullAtomicityViaInfrastructure,
            deadLetterServicePath: "Orders_DeadLetter_Service",
            maxReceiveAttempts: 5));
```

A message type registered with `AddQueueReceiver` must not also carry the `[BrokeredMessage]` attribute; registration throws `InvalidOperationException` when it does. Each receiver processes one message at a time.

### Receivers declared with an attribute

Receivers found by the Chatter.MessageBrokers `[BrokeredMessage]` assembly scan also run on Service Broker when it is the default infrastructure. The sending path is the target service, the receiving path is the queue, and `deadletterQueueName` is the dead-letter service.

```csharp
using Chatter.CQRS.Commands;
using Chatter.MessageBrokers;

[BrokeredMessage("Orders_Service", "Orders_Queue", deadletterQueueName: "Orders_DeadLetter_Service")]
public class PlaceOrder : ICommand
{
    public Guid OrderId { get; set; }
}
```

### Message types

The receiver takes one message at a time and decides what to do from its Service Broker message type.

| Message type | What the receiver does |
| --- | --- |
| `//Chatter/BrokeredMessage` | Unwraps the Chatter envelope and dispatches its body to your handler with the envelope's message id and Message Context. |
| `DEFAULT` | Dispatches the raw body to your handler. The message id is the conversation handle. With the default `MessageBodyType`, the body must be the message as UTF-16 JSON. |
| `http://schemas.microsoft.com/SQL/ServiceBroker/EndDialog` | Ends its side of the conversation. Nothing is dispatched. |
| `http://schemas.microsoft.com/SQL/ServiceBroker/Error` | Logs the error at `Error` level and ends the conversation. Nothing is dispatched. |
| Any other type, or a `//Chatter/BrokeredMessage` or `DEFAULT` message with no body | Discards the message and ends the conversation. |

A discarded message has its conversation ended as part of settling it. Under a transactional mode the `END CONVERSATION` runs on the `RECEIVE`'s own transaction, so both commit together. Under `TransactionMode.None` the `RECEIVE` has already autocommitted and the `END CONVERSATION` is a separate autocommit, so a process that stops between the two can leave that conversation endpoint open.

A logged Service Broker error carries the error code and description as their own structured fields; see [Diagnostics](#diagnostics). An idle `WAITFOR` that times out returns no message, and the receiver simply receives again.

### Dialog lifecycle

| Outcome | What happens on the conversation |
| --- | --- |
| Acknowledge | `END CONVERSATION`, then the receive transaction commits. |
| Negative acknowledge | The receive transaction rolls back, so the message returns to the queue and is received again. Under `TransactionMode.None` there is nothing to roll back and the message is not redelivered. |
| Deadletter | `END CONVERSATION`, then the message is resent to `deadLetterServicePath`, then the receive transaction commits. |

The dead-letter message is a Chatter envelope that wraps the original body. Its Message Context holds the failure description, failure details and receive attempts, and none of the original headers.

Receive attempts are counted in memory by the receiving process, per conversation. A restart starts the count again.

### Reading Service Broker details in a handler

The receiver adds these `SSBMessageContext` keys to every dispatched message's Message Context: `ConversationGroupId`, `ConversationHandle`, `MessageSequenceNumber`, `ServiceName`, `ServiceContractName` and `MessageTypeName`.

```csharp
using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.SqlServiceBroker;

public class PlaceOrderHandler : IMessageHandler<PlaceOrder>
{
    public Task Handle(PlaceOrder message, IMessageHandlerContext context)
    {
        if (context.GetInboundBrokeredMessage()?.MessageContext.TryGetValue(SSBMessageContext.ConversationHandle, out var handle) == true)
        {
            // handle is the Guid of the conversation this message arrived on
        }

        return Task.CompletedTask;
    }
}
```

## Sending

### Destination and envelope keys

The destination of a send or publish is the name of the target service, never the queue. For each message, the Service Broker Sender begins a dialog to that service, sends the message and, by default, ends its side of the conversation.

Three `SSBMessageContext` keys, set with `WithMessageContext` on `SendOptions` or `PublishOptions`, shape the dialog. `WithMessageContext` returns `RoutingOptions`, so call it as its own statement.

| Key | Value | Effect |
| --- | --- | --- |
| `SSBMessageContext.ServiceName` | Initiator service name | `FROM SERVICE`. When unset, the target service is used. |
| `SSBMessageContext.ServiceContractName` | Contract name, usually `ServicesMessageTypes.ChatterServiceContract` | `ON CONTRACT`. When unset, the clause is omitted and Service Broker uses the `DEFAULT` contract. |
| `SSBMessageContext.MessageTypeName` | Message type, usually `ServicesMessageTypes.ChatterBrokeredMessageType` | `MESSAGE TYPE`. Only `//Chatter/BrokeredMessage` sends the Chatter envelope; any other value, or none, sends the raw message body. |

Send with all three keys set, as in the [Quick start](#quick-start), so the receiver gets the envelope with its message id and Message Context. A send without them uses the `DEFAULT` contract and message type, which the target service must list.

### From a handler

A send or publish from a handler inherits the inbound Message Context, including these keys. Without explicit keys, it begins its dialog from the service the message arrived on, with the same contract and message type. Options you supply win over inherited values.

```csharp
using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Routing.Options;
using Chatter.MessageBrokers.SqlServiceBroker;

public class PlaceOrderHandler : IMessageHandler<PlaceOrder>
{
    public Task Handle(PlaceOrder message, IMessageHandlerContext context)
    {
        var options = new SendOptions();
        options.WithMessageContext(SSBMessageContext.ServiceName, "Orders_Service");
        options.WithMessageContext(SSBMessageContext.ServiceContractName, ServicesMessageTypes.ChatterServiceContract);
        options.WithMessageContext(SSBMessageContext.MessageTypeName, ServicesMessageTypes.ChatterBrokeredMessageType);

        return context.Send(new ShipOrder { OrderId = message.OrderId }, "Shipping_Service", options);
    }
}
```

`Shipping_Service` must exist and list the `//Chatter` contract.

### Choosing the infrastructure

When your application registers more than one broker, `context.SqlServiceBroker()` marks the inbound message for Service Broker and returns the `IMessageBrokerContext`, whose `Send`, `Publish` and `Forward` go out over Service Broker. It returns `null` when the handler was not invoked by a Brokered Message Receiver. Outside a handler, select Service Broker per send with `options.UseMessagingInfrastructure(t => t.SqlServiceBroker())`.

### Sending from T-SQL

A stored procedure or trigger can send to a Chatter receiver with the `DEFAULT` message type. The target service must list the `DEFAULT` contract, and the body must be the message serialized as JSON in UTF-16 (`NVARCHAR`).

```sql
DECLARE @handle UNIQUEIDENTIFIER;

BEGIN DIALOG @handle
    FROM SERVICE [Orders_Initiator_Service]
    TO SERVICE 'Orders_Service'
    ON CONTRACT [DEFAULT]
    WITH ENCRYPTION = OFF;

SEND ON CONVERSATION @handle (CAST(N'{"OrderId":"3f2b8c1e-5d4a-4b7e-9c61-0a2f4e8d7b10"}' AS VARBINARY(MAX)));

END CONVERSATION @handle;
```

## Transactions

The transaction mode comes from the receiver's `transactionMode` argument, or from the global Chatter.MessageBrokers `TransactionMode` (see [Chatter.MessageBrokers configuration](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers/src/README.md#configuration)). All three modes are supported.

| Mode | Receive | Handler's Service Broker sends |
| --- | --- | --- |
| `None` | `RECEIVE` autocommits. Acknowledging ends the conversation as a separate autocommit, and a failed message is not redelivered. | Each send runs on its own connection and transaction. |
| `ReceiveOnly` (default) | `RECEIVE` runs in a SQL transaction that commits on acknowledge and rolls back on negative acknowledge. | Each send runs on its own connection and transaction, independent of the receive. |
| `FullAtomicityViaInfrastructure` | As `ReceiveOnly`. | Sends enlist in the receive transaction, on the same connection, so the receive, its `END CONVERSATION` and the sends commit or roll back together. |

Only Service Broker sends join the receive transaction. Sends to another transport, and sends made outside a handler, are not part of it.

### Using the receive transaction in a handler

Under `ReceiveOnly` and `FullAtomicityViaInfrastructure`, the receive's `SqlTransaction` and `SqlConnection` are in the handler's transaction context. Run your own SQL on them to make it atomic with the receive.

```csharp
using Chatter.CQRS;
using Chatter.CQRS.Context;
using Microsoft.Data.SqlClient;

public class PlaceOrderHandler : IMessageHandler<PlaceOrder>
{
    public async Task Handle(PlaceOrder message, IMessageHandlerContext context)
    {
        var transactionContext = context.GetTransactionContext();
        if (transactionContext != null && transactionContext.Container.TryGet<SqlTransaction>(out var transaction))
        {
            using var command = transaction.Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO dbo.Orders (OrderId) VALUES (@orderId);";
            command.Parameters.AddWithValue("@orderId", message.OrderId);
            await command.ExecuteNonQueryAsync();
        }
    }
}
```

## Configuration

Options are built from the `AddSqlServiceBroker(ssb => ...)` delegate only; nothing is bound from `appsettings.json`. Read values from `IConfiguration` yourself and pass them in. Call an `AddSqlServiceBrokerOptions` overload first, because the `With...` and `Use...` methods change the options it creates.

```csharp
builder.Services.AddChatterCqrs(builder.Configuration, typeof(Program).Assembly)
    .AddMessageBrokers()
    .AddSqlServiceBroker(ssb => ssb
        .AddSqlServiceBrokerOptions(builder.Configuration.GetConnectionString("Orders"))
        .WithReceiverTimeout(5000)
        .WithConversationLifetime(3600)
        .UseConversationEncryption()
        .AddQueueReceiver<PlaceOrder>("Orders_Queue", deadLetterServicePath: "Orders_DeadLetter_Service"));
```

### Options

| Option | Type | Default | Description |
| --- | --- | --- | --- |
| `ConnectionString` | `string` | — (required) | SQL Server connection string for all Service Broker work. Every connection is opened with `MultipleActiveResultSets` on. |
| `MessageBodyType` | `string` | `application/json; charset=utf-16` | Content type that selects the body converter for the Chatter envelope and for `DEFAULT` bodies. This package registers `JsonUnicodeBodyConverter` for the default; keep it unless you register your own `IBrokeredMessageBodyConverter`. |
| `ReceiverTimeoutInMilliseconds` | `int` | `-1` | `TIMEOUT` of `WAITFOR (RECEIVE ...)`. `-1`, or any value of `0` or below, waits indefinitely. |
| `ConversationLifetimeInSeconds` | `int` | `0` | `LIFETIME` of each dialog. `0` or below omits the clause, and SQL Server's maximum applies. |
| `ConversationEncryption` | `bool` | `false` | `WITH ENCRYPTION = ON` for dialogs whose messages leave the SQL Server instance. |
| `CompressMessageBody` | `bool` | `true` | Wraps each sent body in T-SQL `COMPRESS`. The receiver decompresses any body that starts with the gzip header `0x1F8B`, whatever this setting. |
| `CleanupOnEndConversation` | `bool` | `false` | Adds `WITH CLEANUP` to every `END CONVERSATION` this package issues. The other side is not notified. |
| `EndConversationAfterDispatch` | `bool` | `true` | Ends the sender's side of the conversation after each message. When off, your application must end the initiator side of each conversation, or its endpoints accumulate. |

### Builder methods

| Method | Description |
| --- | --- |
| `AddSqlServiceBrokerOptions(string connectionString, ...)` | Creates the options from a connection string and optional parameters; see below. |
| `AddSqlServiceBrokerOptions(SqlServiceBrokerOptions)` | Uses an options instance you built. |
| `AddSqlServiceBrokerOptions(Func<SqlServiceBrokerOptions>)` | Uses the instance a factory returns. The factory runs immediately. |
| `WithConnectionString(string)` | Sets `ConnectionString`. |
| `WithMessageBodyType(string)` | Sets `MessageBodyType`. |
| `WithJsonBodyType()` | Sets `MessageBodyType` to `application/json; charset=utf-16`. |
| `WithReceiverTimeout(int)` | Sets `ReceiverTimeoutInMilliseconds`. |
| `WithConversationLifetime(int)` | Sets `ConversationLifetimeInSeconds`. |
| `UseConversationEncryption()` | Sets `ConversationEncryption` to `true`. |
| `WithMessageBodyCompression()` | Sets `CompressMessageBody` to `true`. |
| `WithConversationCleanup()` | Sets `CleanupOnEndConversation` to `true`. |
| `EndConversationAfterDispatch(bool)` | Sets `EndConversationAfterDispatch`. |

The connection-string overload takes optional parameters named after the options: `messageBodyType`, `receiverTimeoutInMilliseconds`, `conversationLifetimeInSeconds`, `compressMessageBody`, `cleanupOnEndConversation` and `endConversationAfterDispatch`. Its encryption parameter is spelled `coversationEncryption`; call `UseConversationEncryption()` instead of passing it by name. To turn compression off, pass `compressMessageBody: false`:

```csharp
ssb.AddSqlServiceBrokerOptions(builder.Configuration.GetConnectionString("Orders"),
                               receiverTimeoutInMilliseconds: 5000,
                               compressMessageBody: false);
```

The `SqlServiceBrokerOptions` constructor takes the same parameters, except that `messageBodyType` is required and `conversationLifetimeInSeconds` defaults to `int.MaxValue`.

### Validation

`AddSqlServiceBroker` builds the options at registration and throws `ArgumentNullException` when no `AddSqlServiceBrokerOptions` overload was called, when the connection string is null or whitespace, or when the message body type is missing. The built `SqlServiceBrokerOptions` is registered as a singleton.

## Recovery

Failed receives and handlers are retried by the Chatter.MessageBrokers retry and circuit breaker policies (see [Recovery](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers/src/README.md#recovery)). This package tells those policies which SQL errors are transient: a `SqlException` whose `IsTransient` is `true`, or whose error number is on a list of known transient errors such as `1205` (deadlock victim), `40501` (service busy) and `40613` (database unavailable).

Two error numbers are terminal and never retried: `208` (invalid object name) and `102` (incorrect syntax). A receive that fails with either raises a `CriticalReceiverException` naming the queue, so a host started before its queue exists stops the receiver instead of retrying forever. A connection string the SQL client rejects when opening also raises a `CriticalReceiverException`.

A poisoned body, one that cannot be deserialized into the message type, is deadlettered on its first delivery without running your handler.

## Header propagation

Only the Chatter envelope carries the Message Context across Service Broker. Two receive paths build a fresh, empty header dictionary instead, so every upstream header is dropped:

- **`DEFAULT` message type.** The body carries no envelope, so there is no Message Context to restore. Context survives only when the sender uses the `//Chatter/BrokeredMessage` message type.
- **Deadletter.** The dead-letter message gets a fresh dictionary holding only the failure details.

This applies to every header alike: correlation id, group id, your own headers and the W3C `traceparent` and `tracestate` trace-context headers. A distributed trace continues across this transport on the Chatter envelope path and starts a new trace on the `DEFAULT` path. See [Trace context propagation](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers/src/README.md#trace-context-propagation).

## Diagnostics

This package emits no telemetry of its own. Broker spans and metrics come from the `Chatter.MessageBrokers` `ActivitySource` and `Meter`; see [Chatter.MessageBrokers diagnostics](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers/src/README.md#diagnostics). Trace context crosses Service Broker only on the Chatter envelope path; see [Header propagation](#header-propagation).

When a Service Broker `Error` message arrives, the receiver logs it at `Error` level with the structured fields `ConversationHandle`, `ServiceName`, `ServiceBrokerErrorCode` and `ServiceBrokerErrorDescription`:

- The code is parsed from the error body as a 32-bit integer and logged as that integer, never as raw peer text.
- The description keeps only printable letters, marks, numbers, punctuation, symbols and spaces; any other character becomes a space. It is truncated at 3000 characters.
- A missing body logs `<no error payload>` for both fields. A body that is not valid UTF-16, whose code is not a 32-bit integer, or that cannot otherwise be read logs `<unreadable error payload>` for both.

Other discarded messages are logged at `Trace` level.

## Related packages

| Package | Description |
| --- | --- |
| [Chatter.CQRS](https://www.nuget.org/packages/Chatter.CQRS) | In-process Commands, Queries, Events and the Command Pipeline. |
| [Chatter.MessageBrokers](https://www.nuget.org/packages/Chatter.MessageBrokers) | The broker abstractions this transport implements: receivers, routing, Inbox/Outbox and Recovery. |
| [Chatter.SqlChangeFeed](https://www.nuget.org/packages/Chatter.SqlChangeFeed) | Typed insert, update and delete notifications from a watched SQL Server table, delivered over this transport. |
| [Chatter.MessageBrokers.Reliability.EntityFramework](https://www.nuget.org/packages/Chatter.MessageBrokers.Reliability.EntityFramework) | EF Core Inbox, Outbox and Unit of Work. |
| [Chatter.MessageBrokers.AzureServiceBus](https://www.nuget.org/packages/Chatter.MessageBrokers.AzureServiceBus) | Azure Service Bus transport. |
| [Chatter.MessageBrokers.RabbitMQ](https://www.nuget.org/packages/Chatter.MessageBrokers.RabbitMQ) | RabbitMQ transport. |

## Learn more

- [SQL Server Service Broker domain glossary (CONTEXT.md)](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers.SqlServiceBroker/CONTEXT.md)
- [Changelog](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers.SqlServiceBroker/src/Chatter.MessageBrokers.SqlServiceBroker/CHANGELOG.md)
- [Context map of all Chatter modules](https://github.com/brenpike/Chatter/blob/master/CONTEXT-MAP.md)
- [Chatter suite README](https://github.com/brenpike/Chatter/blob/master/README.md)

## License

Licensed under the [MIT License](https://github.com/brenpike/Chatter/blob/master/LICENSE).
