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
        ┌────────────┬─────────┤               └──────────┬──────────┐
        ▼            ▼         ▼                           ▼          ▼
 AzureServiceBus  RabbitMQ  SqlServiceBroker   Reliability.        Reliability.
   (+ .Auth AAD)   (AMQP)     (SQL Server)      EntityFramework     Cosmos
                                               (durable EF          (Cosmos DB
                                                inbox/outbox)        document tier)

 Chatter.SqlChangeFeed — emits SQL Server row-change notifications (Service Broker)
```

Chatter.MessageBrokers defines the transport interfaces; you pick a concrete implementation (**Azure Service Bus**, **RabbitMQ**, or **SQL Server Service Broker**) and, optionally, durable reliability storage (relational **Entity Framework** or document-tier **Cosmos DB**).

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

### [Chatter.MessageBrokers.RabbitMQ](./src/Chatter.MessageBrokers.RabbitMQ/src/README.md#chatter-messagebrokers-rabbitmq)
`dotnet add package Chatter.MessageBrokers.RabbitMQ`

A RabbitMQ transport for Chatter.MessageBrokers — sends and receives brokered messages over RabbitMQ exchanges and queues.

- `AddQueueReceiver<TMessage>` binds a message type to a queue, with an error/dead-letter queue path and `maxReceiveAttempts` (default `10`).
- Default-exchange addressing (routing key = destination queue name), overridable with `.WithRabbitMqRouting(exchange, routingKey)`.
- Delivery counting by queue type: **Quorum** queues read the native `x-delivery-count` (the recommended default), **Classic** queues use a header-stamped republish counter — trade-offs in the module's ADR 0001.
- **Provisions no topology** — exchanges, queues, bindings, and dead-letter routing are created externally, mirroring the SqlServiceBroker manual-provisioning stance.
- `TransactionMode.FullAtomicityViaInfrastructure` is rejected at startup — use the **Outbox** for transactional send.
- Entry point: `IChatterBuilder.AddRabbitMq(...)` (chained off `AddMessageBrokers`).

### [Chatter.MessageBrokers.Reliability.EntityFramework](./src/Chatter.MessageBrokers.Reliability.EntityFramework/src/README.md#chatter-reliability-entityframework)
`dotnet add package Chatter.MessageBrokers.Reliability.EntityFramework`

EF Core implementation of the Chatter.MessageBrokers reliability ports — durable inbox, transactional outbox, and unit of work backed by your own `DbContext`, replacing the in-memory defaults.

- Idempotent inbox keyed on `MessageId`; transactional outbox writing in the same DB transaction as domain state.
- Atomic `UnitOfWork<TContext>` over EF execution strategies, exposed via `IPersistanceTransaction`.
- Ships `IEntityTypeConfiguration` types applied in your `DbContext.OnModelCreating` — messaging tables live alongside domain tables.
- Entry point: `CommandPipelineBuilder.WithInboxBehavior<TContext>()` / `WithOutboxProcessingBehavior<TContext>()` / `WithUnitOfWorkBehavior<TContext>()`.

### [Chatter.MessageBrokers.Reliability.Cosmos](./src/Chatter.MessageBrokers.Reliability.Cosmos/src/README.md#chatter-reliability-cosmos)
`dotnet add package Chatter.MessageBrokers.Reliability.Cosmos`

Document-tier (NoSQL) implementation of the Chatter.MessageBrokers reliability ports, backed by Azure Cosmos DB — the stage-then-commit sibling of the relational Entity Framework tier.

- Document-tier **stage-then-commit** reliability over a Cosmos `TransactionalBatch`: the framework opens the batch, the handler contributes its own aggregate writes, and the batch executes once as the single commit point.
- Per-command **participation registry** (`WithCosmosDocumentReliability<TCommand>(...)`) with per-command database/container/lease, enabling multi-container support — unregistered commands bypass the document tier untouched.
- Co-resident **outbox** staged atomically with the aggregate write, plus a co-resident **inbox** marker for TOCTOU-free idempotent dedup (confirmed-duplicate marker-409 fails the batch atomically).
- **Change-feed outbox relay**: a hosted `ChangeFeedProcessor` drains co-resident pending outbox documents, publishes each through the broker at-least-once, then marks delivered with a TTL self-purge.
- **Standalone outbox relay** (`AddCosmosOutboxRelay`): the same change-feed relay registered as its own `IHostedService`, independent of the command pipeline and repeatable per monitored container, carrying the `IOutboxBodyResolver` drain-time body-resolution seam.
- **Standalone inbox** (`WithCosmosInbox`): a lease-less redelivery-dedup gate for a service with no aggregate write, outbox, or lease container — it skips the handler on a confirmed *completed* marker, but does not serialize genuinely-concurrent deliveries of the same id (that mutual exclusion stays the transport's responsibility).
- Entry points: `CommandPipelineBuilder.WithCosmosDocumentReliability<TCommand>(...)` (document tier), `services.AddCosmosOutboxRelay(...)` (standalone relay), `CommandPipelineBuilder.WithCosmosInbox(...)` (standalone inbox).

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
2. To exchange messages across services, add **Chatter.MessageBrokers** plus a transport — **AzureServiceBus**, **RabbitMQ**, or **SqlServiceBroker**.
3. For durable reliability, add **Reliability.EntityFramework** (relational — apply its entity configurations to your `DbContext`) or **Reliability.Cosmos** (document-tier — register a `CosmosClient` and a per-command participation entry).

Each module's README (linked above) has installation, configuration, and worked examples.

## Diagnostics (optional, opt-in)

Chatter emits OpenTelemetry-compatible **tracing** and **metrics**, and both are **off until you opt in**. Chatter takes **no dependency on any `OpenTelemetry.*` NuGet package** — the instrumentation is built on the .NET base class library only: `System.Diagnostics.ActivitySource` for spans and `System.Diagnostics.Metrics.Meter` for instruments. You choose the collector.

The `ActivitySource` and the `Meter` are named **per emitting assembly** — `Chatter.CQRS` and `Chatter.MessageBrokers` — so each module can be sampled and filtered on its own. Opt in on your own OpenTelemetry provider with a prefix wildcard, or by naming both scopes exactly:

```csharp
services.AddOpenTelemetry()
        .WithTracing(t => t.AddSource("Chatter.*"))    // or .AddSource("Chatter.CQRS", "Chatter.MessageBrokers")
        .WithMetrics(m => m.AddMeter("Chatter.*"));    // or .AddMeter("Chatter.CQRS", "Chatter.MessageBrokers")
```

**When nothing subscribes to the Chatter sources, nothing is emitted and nothing extra goes on the wire.** Each instrumented operation first checks whether Chatter's own source has a subscriber and returns before building a span name, a tag collection, or a `traceparent` header — so an application that never opts in pays no per-operation cost and its messages are byte-identical to the un-instrumented ones. The guarantee is per-operation: constructing the `ActivitySource` and `Meter` themselves is a one-time static initialization per process, which is unavoidable for any `ActivitySource`-based design.

> **Telemetry attribute names are data, not compile-time API.** Chatter's broker-boundary attribute names track the pinned **OpenTelemetry semantic conventions v1.30.0**, and **may change in a minor release** when that pin advances. Dashboards and alert queries that hard-code attribute names should expect to be revisited on a pin bump; the bump is announced in the affected package's CHANGELOG.

Design rationale, the propagation scope, and the off-guard rules are recorded in [ADR-0010](./docs/adr/0010-optional-bcl-only-telemetry-per-assembly-sources-and-the-off-guard.md).

## Domain language

Chatter's ubiquitous language is documented per bounded context — see [CONTEXT-MAP.md](./CONTEXT-MAP.md) and the `CONTEXT.md` in each module directory.

## Building & testing

```
dotnet test
```
