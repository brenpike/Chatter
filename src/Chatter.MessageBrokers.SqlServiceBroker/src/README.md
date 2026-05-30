# <a name="chatter-sqlservicebroker"></a> Chatter.MessageBrokers.SqlServiceBroker

A SQL Server Service Broker transport for Chatter.MessageBrokers — send and receive brokered messages over SQL Service Broker dialogs.

## Overview

`Chatter.MessageBrokers.SqlServiceBroker` is a concrete, SQL Server Service Broker implementation of the technology-agnostic interfaces defined by [Chatter.MessageBrokers](../../Chatter.MessageBrokers/src/README.md#chatter-messagebrokers) (itself built on [Chatter.CQRS](../../Chatter.CQRS/src/README.md#chatter-cqrs)).

Where the core library defines `IMessagingInfrastructureReceiver`, `IMessagingInfrastructureDispatcher`, and the recovery abstractions (retry, circuit breaker), this package supplies the SQL Service Broker realizations:

- `SqlServiceBrokerReceiver` dequeues messages with `WAITFOR (RECEIVE ...)`.
- `SqlServiceBrokerSender` enqueues messages by opening a dialog (`BEGIN DIALOG`), `SEND ON CONVERSATION`, and (optionally) `END CONVERSATION`.
- `SqlCircuitBreakerExceptionPredicatesProvider` and `SqlRetryExceptionPredicatesProvider` teach the core recovery pipeline which SQL exceptions are transient.

It registers itself with Chatter through an `IChatterBuilder` extension, `AddSqlServiceBroker(...)`, chained off `AddMessageBrokers(...)` — the same way the sibling `Chatter.MessageBrokers.AzureServiceBus` package adds `AddAzureServiceBus(...)`.

## Installation

```bash
dotnet add package Chatter.MessageBrokers.SqlServiceBroker
```

## Getting Started

Register Chatter CQRS, add message brokers, then add the SQL Service Broker infrastructure with a connection string. The DI entry point is `AddSqlServiceBroker` (in the `Microsoft.Extensions.DependencyInjection` namespace), configured via the `SqlServiceBrokerOptionsBuilder`.

```csharp
using Microsoft.Extensions.DependencyInjection;

services.AddChatterCqrs(Configuration, builder =>
{
    builder
        .AddMessageBrokers()
        .AddSqlServiceBroker(ssb =>
        {
            ssb.AddSqlServiceBrokerOptions(
                    Configuration.GetValue<string>("ConnectionStrings:MyDatabase"))
               // register a receiver for a message type bound to a Service Broker queue
               .AddQueueReceiver<MyIntegrationEvent>("Chatter_Queue_MyIntegrationEvent");
        });
});
```

`AddQueueReceiver<TMessage>` binds a `Chatter.CQRS.IMessage` type to a Service Broker queue (the queue name is used as both the receive path and the message description). Optional parameters let you set an error/dead-letter service path, transaction mode, and `maxReceiveAttempts` (default `10`):

```csharp
ssb.AddQueueReceiver<MyIntegrationEvent>(
    queueName: "Chatter_Queue_MyIntegrationEvent",
    errorQueuePath: "Chatter_Service_DeadLetter",
    transactionMode: TransactionMode.FullAtomicityViaInfrastructure,
    deadLetterServicePath: "Chatter_Service_DeadLetter",
    maxReceiveAttempts: 8);
```

### Sending / publishing

Within a message handler, dispatch out through the broker using the standard Chatter context, or via the SQL Service Broker-specific dispatcher extension:

```csharp
using Chatter.CQRS.Context;

public class MyHandler : IMessageHandler<SomeCommand>
{
    public Task Handle(SomeCommand message, IMessageHandlerContext context)
    {
        // SqlServiceBroker() returns an ISqlServiceBrokerContextDispatcher
        return context.SqlServiceBroker()
                      .Send(new AnotherCommand(), "Chatter_Service_AnotherCommand");
    }
}
```

The `context.SqlServiceBroker()` extension returns an `ISqlServiceBrokerContextDispatcher`, which exposes `Send`, `Publish`, and `Forward` and pins the outbound message to the SQL Service Broker infrastructure. The `destinationPath` is the **target Service Broker service** the sender opens a dialog *TO*.

## Configuration

Options are modeled by `SqlServiceBrokerOptions` and assembled with `SqlServiceBrokerOptionsBuilder`. Configure them either by passing values to `AddSqlServiceBrokerOptions(...)` or by using the fluent `With...`/`Use...` methods.

| Property | Builder method | Default | Purpose |
| --- | --- | --- | --- |
| `ConnectionString` | `AddSqlServiceBrokerOptions(connectionString)` / `WithConnectionString(...)` | _(required)_ | SQL Server connection string used for all Service Broker communication. |
| `MessageBodyType` | `WithMessageBodyType(...)` / `WithJsonBodyType()` | `application/json; charset=utf-16` | Content type used to encode/decode the message body. |
| `ReceiverTimeoutInMilliseconds` | `WithReceiverTimeout(...)` | `-1` (wait indefinitely) | Timeout passed to `WAITFOR (RECEIVE ...)`. `-1` waits forever; an elapsed timeout yields an empty result. |
| `ConversationLifetimeInSeconds` | `WithConversationLifetime(...)` | `0` (no `LIFETIME`) | Maximum time a dialog stays open (`BEGIN DIALOG ... WITH LIFETIME`). |
| `ConversationEncryption` | `UseConversationEncryption()` | `false` | Whether dialog messages are encrypted when leaving the SQL Server instance. |
| `CompressMessageBody` | `WithMessageBodyCompression()` | `true` | Gzip the body on send via T-SQL `compress(...)`; the receiver auto-decompresses when it detects the `0x1F8B` gzip header. |
| `CleanupOnEndConversation` | `WithConversationCleanup()` | `false` | Issue `END CONVERSATION ... WITH CLEANUP` to forcibly drop conversations that cannot complete normally. |
| `EndConversationAfterDispatch` | `EndConversationAfterDispatch(bool)` | `true` | When `true`, the sender ends the dialog after each dispatch (emits an `EndDialog` message). |

`SqlServiceBrokerOptionsBuilder.Build()` throws if no options were configured, if the connection string is null/whitespace, or if the message body type is missing.

Recovery (retry and circuit breaker) is supplied automatically: `AddSqlServiceBroker` registers `SqlRetryExceptionPredicatesProvider` and `SqlCircuitBreakerExceptionPredicatesProvider`, which classify SQL failures as transient (e.g. `SqlException.IsTransient` on net8.0+, known transient error numbers, and error `208` "invalid object name") so the core Chatter recovery pipeline retries or trips the breaker appropriately.

## SQL Setup

This package is a **transport over existing SQL Service Broker objects** — it does not provision them. At runtime it only emits Service Broker DML against the objects you have already created:

| `Scripts/` command | T-SQL emitted | When |
| --- | --- | --- |
| `BeginDialogConversationCommand` | `BEGIN DIALOG ... FROM SERVICE ... TO SERVICE ... [ON CONTRACT ...] WITH ENCRYPTION = ON\|OFF [, LIFETIME ...]` | Sender, per outbound message. |
| `SendOnConversationCommand` | `SEND ON CONVERSATION @handle [MESSAGE TYPE ...] (@body)` (wrapped in `compress(...)` when compression is on) | Sender, after the dialog opens. |
| `EndDialogConversationCommand` | `END CONVERSATION @handle [WITH ERROR ... DESCRIPTION ...] [WITH CLEANUP]` | Sender (when `EndConversationAfterDispatch`), and receiver on ack/deadletter/`EndDialog`. |
| `ReceiveMessageFromQueueCommand` | `WAITFOR (RECEIVE TOP(1) ... FROM <queue>) [, TIMEOUT ...]` | Receiver, to dequeue. |

You are responsible for provisioning the Service Broker schema **manually** (or via your own migration tooling) before using this package. At minimum you need:

- `ALTER DATABASE [Db] SET ENABLE_BROKER;` on the target database.
- A **message type** and **contract** — the library tags Chatter-originated messages with message type `//Chatter/BrokeredMessage` and service contract `//Chatter` (see `ServicesMessageTypes`). It also accepts the built-in `DEFAULT` message type.
- The **queues** you reference in `AddQueueReceiver<T>(queueName, ...)` (the receive path) and any dead-letter queue/service path you configure.
- The **services** you send to (the `destinationPath` / target service supplied when sending).

On receive, the library filters by message type: only `//Chatter/BrokeredMessage` and `DEFAULT` are surfaced to handlers; `http://schemas.microsoft.com/SQL/ServiceBroker/EndDialog` messages are acknowledged and ended, and any other type is discarded. The receiver also handles dialog lifecycle (ack = `END CONVERSATION`, nack = transaction rollback, deadletter = resend to the configured dead-letter service path).

> Note: automatic provisioning of Service Broker objects (queues, services, contracts, message types, `ENABLE_BROKER`) lives in the separate `Chatter.SqlChangeFeed` package, not here.

## Domain Language

See the [domain glossary](../CONTEXT.md) for definitions of Service Broker Receiver, Service Broker Sender, Queue, Conversation, Setup Scripts, and Service Broker Options.

[← All Chatter modules](../../../README.md)
