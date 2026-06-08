# Chatter

Chatter is a suite of modular .NET libraries for building domain-driven Web APIs and microservices. It pairs an in-process **CQRS + mediator** core with **technology-agnostic message broker** infrastructure, so the same Command/Event handlers serve both internal dispatch and cross-service integration over the transport of your choice.

## Architecture at a glance

```
                        ┌─────────────────────────────┐
                        │        Chatter.CQRS         │  in-process mediator:
                        │  Commands · Queries · Events │  dispatch & handle
                        │      + Command Pipeline      │
                        └──────────────┬──────────────┘
                                       │ built on
                        ┌──────────────▼──────────────┐
                        │    Chatter.MessageBrokers    │  technology-agnostic
                        │  receive · send · route ·    │  brokered messaging
                        │  inbox/outbox · recovery     │
                        └──────┬───────────────┬───────┘
              implements       │               │      reliability port
        ┌────────────────┬─────┘               └─────────────┐
        ▼                ▼                                    ▼
 AzureServiceBus   SqlServiceBroker              Reliability.EntityFramework
   (+ .Auth AAD)     (SQL Server)                 (durable EF inbox/outbox)

 Chatter.SqlChangeFeed — emits SQL Server row-change notifications (Service Broker)
```

Chatter.MessageBrokers defines the transport interfaces; you pick a concrete implementation (**Azure Service Bus** or **SQL Server Service Broker**) and, optionally, durable reliability storage (**Entity Framework**).

## Modules

### [Chatter.CQRS](./src/Chatter.CQRS/src/README.md#chatter-cqrs)
`dotnet add package Chatter.CQRS`

A lightweight CQRS framework that dispatches Commands, Queries, and Events to their handlers via an in-process mediator, with automatic assembly-scanning registration and an optional command behavior pipeline.

- Commands → a single `IMessageHandler<TCommand>`; Events → fan-out to many handlers; Queries → `IQueryHandler<TQuery,TResult>`.
- Marker-based message model (`IMessage`, `ICommand`, `IEvent`, `IQuery<T>`) with automatic handler discovery.
- Composable cross-cutting **Command Pipeline** via `ICommandBehavior<TMessage>`.
- Extensible per-dispatch **Message Context**.
- Entry point: `services.AddChatterCqrs(...)`.

### [Chatter.MessageBrokers](./src/Chatter.MessageBrokers/src/README.md#chatter-messagebrokers)
`dotnet add package Chatter.MessageBrokers`

Technology-agnostic brokered messaging built on Chatter.CQRS — receiving, sending/publishing/forwarding, routing, reliability, and recovery. Requires a concrete broker implementation for the transport.

- Single-instance background-service receiver per message type marked with `[BrokeredMessage(...)]`, dispatching to your existing CQRS handlers.
- Unified outbound `IBrokeredMessageDispatcher` (Send / Publish / Forward) + in-memory dispatch.
- **Inbox** (idempotent once-only handling) and **Outbox** (reliable publish) patterns.
- **Recovery**: retry, circuit breaker, max-receives-exceeded → Error Queue, Critical Failure events.
- **Routing Slips** for itinerary-style choreography.
- Entry point: `IChatterBuilder.AddMessageBrokers(...)`.

### [Chatter.MessageBrokers.AzureServiceBus](./src/Chatter.MessageBrokers.AzureServiceBus/src/README.md#chatter-azureservicebus)
`dotnet add package Chatter.MessageBrokers.AzureServiceBus`

The Azure Service Bus transport for Chatter.MessageBrokers — concrete senders and receivers (queues for commands, topic subscriptions for events) wired into the broker abstraction.

- `AddQueueReceiver<TMessage>` (commands) and `AddTopicSubscription<TMessage>` (events), each with error-queue path and max-receive attempts.
- Options in code or from config (`Chatter:Infrastructure:AzureServiceBus`): connection, concurrency, prefetch, retry policy.
- ASB-aware transient-exception detection feeding the core retry/circuit-breaker recovery.
- Entry point: `IChatterBuilder.AddAzureServiceBus(...)` (chained off `AddMessageBrokers`).

### [Chatter.MessageBrokers.AzureServiceBus.Auth](./src/Chatter.MessageBrokers.AzureServiceBus.Auth/src/README.md#chatter-azureservicebus-auth)
`dotnet add package Chatter.MessageBrokers.AzureServiceBus.Auth`

Azure Active Directory token authentication for the Azure Service Bus broker — connect with AAD bearer tokens (or `DefaultAzureCredential`) instead of a connection-string shared key.

- Opt-in builder extensions: client-secret, client-certificate (X509 thumbprint), and interactive auth.
- Automatic fallback to `DefaultAzureCredential` (managed identity, env, Azure CLI) when no explicit credential is given.
- Applied only when the connection string carries no SAS key — additive by design.
- Entry point: `ServiceBusOptionsBuilder.UseAadTokenProviderWith...` (inside `AddAzureServiceBus`).

### [Chatter.MessageBrokers.SqlServiceBroker](./src/Chatter.MessageBrokers.SqlServiceBroker/src/README.md#chatter-sqlservicebroker)
`dotnet add package Chatter.MessageBrokers.SqlServiceBroker`

A SQL Server Service Broker transport for Chatter.MessageBrokers — sends and receives brokered messages over Service Broker dialogs, with no external broker dependency.

- `SqlServiceBrokerReceiver`/`SqlServiceBrokerSender` over `BEGIN DIALOG` / `SEND` / `WAITFOR RECEIVE`.
- Fluent options: connection, `WAITFOR` timeout, conversation lifetime/encryption, gzip body compression, dead-letter routing.
- SQL-aware transient-exception predicates feeding the core retry/circuit-breaker recovery.
- **Does not auto-provision** Service Broker objects — queues, services, contracts, and `ENABLE_BROKER` are set up manually.
- Entry point: `IChatterBuilder.AddSqlServiceBroker(...)` (chained off `AddMessageBrokers`).

### [Chatter.MessageBrokers.Reliability.EntityFramework](./src/Chatter.MessageBrokers.Reliability.EntityFramework/src/README.md#chatter-reliability-entityframework)
`dotnet add package Chatter.MessageBrokers.Reliability.EntityFramework`

EF Core implementation of the Chatter.MessageBrokers reliability ports — durable inbox, transactional outbox, and unit of work backed by your own `DbContext`, replacing the in-memory defaults.

- Idempotent inbox keyed on `MessageId`; transactional outbox writing in the same DB transaction as domain state.
- Atomic `UnitOfWork<TContext>` over EF execution strategies, exposed via `IPersistanceTransaction`.
- Ships `IEntityTypeConfiguration` types applied in your `DbContext.OnModelCreating` — messaging tables live alongside domain tables.
- Entry point: `CommandPipelineBuilder.WithInboxBehavior<TContext>()` / `WithOutboxProcessingBehavior<TContext>()` / `WithUnitOfWorkBehavior<TContext>()`.

### [Chatter.SqlChangeFeed](./src/Chatter.SqlChangeFeed/src/README.md#chatter-sqlchangefeed)
`dotnet add package Chatter.SqlChangeFeed`

Emits strongly-typed notifications when rows in a watched SQL Server table are inserted, updated, or deleted — trigger-based via SQL Server Service Broker, no polling. (Formerly *Table Watcher*.)

- Default fan-out to `RowInsertedEvent<T>` / `RowUpdatedEvent<T>` / `RowDeletedEvent<T>`, handled through `IMessageHandler<T>`.
- Opt-in manual mode delivering the raw `ProcessChangeFeedCommand<T>` batch.
- Selectable change types (`Insert | Update | Delete`), schema/database overrides, dead-letter and compression options.
- Manual, idempotent SQL provisioning via `UseChangeFeedSqlMigrations<T>`.
- Entry point: `IChatterBuilder.AddSqlChangeFeed<TRowChangedData>(...)`.

## Getting started

1. Install **Chatter.CQRS** and register it: `services.AddChatterCqrs(...)`.
2. To exchange messages across services, add **Chatter.MessageBrokers** plus a transport — **AzureServiceBus** or **SqlServiceBroker**.
3. For durable reliability, add **Reliability.EntityFramework** and apply its entity configurations to your `DbContext`.

Each module's README (linked above) has installation, configuration, and worked examples.

## Domain language

Chatter's ubiquitous language is documented per bounded context — see [CONTEXT-MAP.md](./CONTEXT-MAP.md) and the `CONTEXT.md` in each module directory.

## Building & testing

```
dotnet test
```
