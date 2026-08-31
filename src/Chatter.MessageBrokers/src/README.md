# <a name="chatter-messagebrokers"></a> Chatter.MessageBrokers

Technology-agnostic brokered messaging for .NET, built on Chatter.CQRS.

## Overview

`Chatter.MessageBrokers` adds brokered (out-of-process) messaging on top of [Chatter.CQRS](../../Chatter.CQRS/src/README.md). It lets you receive messages from a broker and dispatch them to your existing `IMessageHandler<TMessage>` commands and events, and send/publish/forward messages back out — all without coupling your domain code to a specific broker technology.

The package defines the abstractions and the orchestration (receiving loop, dispatching, routing, reliability, recovery) but ships **no concrete transport**. The broker-facing interfaces (`IMessagingInfrastructureReceiver`, `IMessagingInfrastructureDispatcher`, `IBrokeredMessagePathBuilder`, etc.) are implemented by a sibling package. Pick one:

- **Chatter.MessageBrokers.AzureServiceBus** — Azure Service Bus queues/topics.
- **Chatter.MessageBrokers.SqlServiceBroker** — SQL Server Service Broker.

You register `Chatter.MessageBrokers` plus one infrastructure package, and the core wires everything together.

## Installation

```bash
dotnet add package Chatter.MessageBrokers
```

Then add a concrete broker, e.g.:

```bash
dotnet add package Chatter.MessageBrokers.AzureServiceBus
```

## Getting Started

### 1. Register with DI

`Chatter.MessageBrokers` extends the Chatter.CQRS builder. The primary entry point is `AddMessageBrokers` on `IChatterBuilder`:

```csharp
using Microsoft.Extensions.DependencyInjection;

services.AddChatterCqrs(configuration)
        .AddMessageBrokers(options =>
        {
            options.WithTransactionMode(TransactionMode.ReceiveOnly);
        })
        // then add a concrete infrastructure, e.g.:
        .AddAzureServiceBus(/* ... */);
```

`AddMessageBrokers` scans your assemblies for message types decorated with `BrokeredMessageAttribute`, and for every type whose `receivingPath` is set it auto-registers a receiver as a hosted background service. It also registers the dispatcher, routers, recovery strategy, in-memory inbox/outbox, and the default body converters (`JsonBodyConverter`, `TextPlainBodyConverter`).

There are several overloads to control which assemblies are scanned for receivers — by marker type, explicit `Assembly[]`, or a namespace wildcard selector:

```csharp
.AddMessageBrokers("MyApp.Messages.*", options => { /* ... */ });
.AddMessageBrokers(options => { /* ... */ }, typeof(SomeMarkerType));
```

### 2. Mark a message

Decorate a Chatter.CQRS `ICommand` or `IEvent` with `[BrokeredMessage(...)]` to map it to broker paths. Supplying `receivingPath` tells Chatter to start a receiver for that message automatically.

```csharp
using Chatter.CQRS.Commands;
using Chatter.MessageBrokers;

[BrokeredMessage(sendingPath: "orders.out", receivingPath: "orders.in")]
public class PlaceOrder : ICommand
{
    public Guid OrderId { get; set; }
    public string Sku { get; set; }
}
```

`BrokeredMessageAttribute` also accepts `errorQueueName`, `messageDescription`, `infrastructureType`, and `deadletterQueueName`. Either `sendingPath` or `receivingPath` is required.

### 3. Handle the received message

Write a normal Chatter.CQRS handler. When the message arrives on the broker, the receiver deserializes the body and dispatches it to your handler, passing an `IMessageBrokerContext` (which is an `IMessageHandlerContext`).

```csharp
using Chatter.CQRS;
using Chatter.CQRS.Context;

public class PlaceOrderHandler : IMessageHandler<PlaceOrder>
{
    public async Task Handle(PlaceOrder message, IMessageHandlerContext context)
    {
        // ... do work ...

        // reply / send / publish back out over the same infrastructure:
        await context.Publish(new OrderPlaced { OrderId = message.OrderId });
    }
}
```

### 4. Send / publish without a handler

Inject `IBrokeredMessageDispatcher` anywhere to send commands or publish events directly:

```csharp
public class OrderApi
{
    private readonly IBrokeredMessageDispatcher _dispatcher;
    public OrderApi(IBrokeredMessageDispatcher dispatcher) => _dispatcher = dispatcher;

    public Task PlaceAsync(PlaceOrder cmd) => _dispatcher.Send(cmd);          // path from attribute
    public Task PlaceAsync(PlaceOrder cmd, string path) => _dispatcher.Send(cmd, path);
}
```

## Core Concepts

### Brokered Message
A message received from or sent to broker infrastructure. `OutboundBrokeredMessage` represents what you send; `InboundBrokeredMessage` represents what was received (available via `IMessageBrokerContext.BrokeredMessage`). A **Body Converter** (`IBrokeredMessageBodyConverter`, selected by `IBodyConverterFactory`) serializes/deserializes the body to/from your message type.

### Receiver
`BrokeredMessageReceiver<TMessage>` (interface `IBrokeredMessageReceiver<TMessage>`) consumes messages from the infrastructure in a loop. It runs inside `BrokeredMessageReceiverBackgroundService<TMessage>`, an `IHostedService`. Each decorated, receivable message type gets **its own background service**, and only **one receiver instance per message type** runs. The loop receives a message, deserializes it, dispatches it to the handler, then acks / nacks / deadletters based on the outcome. `ReceiverOptions` carries the receiver path, error/deadletter queue paths, `TransactionMode`, infrastructure type, and `MaxReceiveAttempts` (default 10).

### Dispatcher
`IReceivedMessageDispatcher` (`ScopedReceivedMessageDispatcher`) relays a received brokered message to the matching Chatter.CQRS handler in a fresh DI scope, bridging to the CQRS message dispatcher by message type.

### Sender / Publisher / Forwarder
`IBrokeredMessageDispatcher` is the unified outbound surface, composed of:

- `IBrokeredMessageSender` — `Send<TCommand>(...)` a command to one destination.
- `IBrokeredMessagePublisher` — `Publish<TEvent>(...)` an event (single or batch) to subscribers.
- `IBrokeredMessageForwarder` — `Forward(...)` a received `InboundBrokeredMessage` to a new destination.

The same operations are available as extension methods on `IMessageHandlerContext` (`context.Send(...)`, `context.Publish(...)`) so a handler can react over the same infrastructure that delivered the inbound message. `context.InMemory()` (`IInMemoryDispatcher`) provides in-process dispatch.

### Routing & Forwarding
`IRouteBrokeredMessages` (default `BrokeredMessageRouter`) resolves destinations and routes outbound messages to the infrastructure. `ForwardingRouter` (`IForwardMessages`) handles forwarding inbound messages; `ReplyRouter` (`IReplyRouter`) handles reply-to routing. Message IDs are produced by `IMessageIdGenerator` (default `GuidIdGenerator`; `CombGuidIdGenerator` and `HashedBodyGuidGenerator` are also provided).

## Reliability

### Outbox
The Outbox pattern records outgoing messages so they can be published reliably alongside local state changes. Enable it via `AddReliabilityOptions`:

```csharp
.AddMessageBrokers(options =>
{
    options.AddReliabilityOptions(r => r
        .WithOutboxRouting()                 // route outbound messages through the outbox
        .WithOutboxPollingProcessor(5000));  // BrokeredMessageOutboxProcessor drains it every 5s
});
```

`WithOutboxRouting()` swaps `IRouteBrokeredMessages` for `OutboxBrokeredMessageRouter`. `WithOutboxPollingProcessor(...)` registers `BrokeredMessageOutboxProcessor` (an `IHostedService`). The default store is `InMemoryBrokeredMessageOutbox`; `WithInMemoryOutboxTimeToLive(minutes)` controls its retention.

### Inbox
The Inbox pattern records received messages to enforce idempotent, once-only handling (`IBrokeredMessageInbox`, default `InMemoryBrokeredMessageInbox`, applied via `InboxBehavior`).

> **Persistence note:** the in-memory inbox/outbox are for development and single-node scenarios. Durable, transactional EF-backed implementations of `IBrokeredMessageInbox` / `IBrokeredMessageOutbox` (plus `IUnitOfWork` / `IPersistanceTransaction`) live in a sibling EntityFrameworkCore reliability package.

## Recovery

Receiving is wrapped by an `IRecoveryStrategy` — the default `RetryWithCircuitBreakerStrategy` combines Retry and Circuit Breaker. Configure it with `AddRecoveryOptions`:

```csharp
.AddMessageBrokers(options =>
{
    options.AddRecoveryOptions(r => r
        .UseExponentialDelayRecovery(maxRetryAttempts: 10)   // or UseConstantDelayRecovery(ms) / UseNoDelayRecovery()
        .RetryWhen<MyTransientException>()                   // restrict which exceptions are retried
        .UseRouteToErrorQueueRecoveryAction()                // IMaxReceivesExceededAction
        .WithCircuitBreaker(cb => { /* CircuitBreakerOptionsBuilder */ }));
});
```

- **Retry** — `IRetryStrategy` (`RetryStrategy`) with a pluggable `IRetryDelayStrategy`: `NoDelayRetry` (default), `ConstantDelayRetry`, `ExponentialDelayRetry`. Default max attempts is 5. `RetryWhen` / `RetryWhen<TException>` restrict which exceptions are retried.
- **Circuit Breaker** — `ICircuitBreaker` (`CircuitBreaker`) halts processing after repeated failures; state lives in `ICircuitBreakerStateStore` (default `InMemoryCircuitBreakerStateStore`). Throws `CircuitBreakerOpenException` when open.
- **Max Receives Exceeded** — when a message's delivery count reaches `MaxReceiveAttempts`, the receiver deadletters it and runs the `IMaxReceivesExceededAction` (default `ErrorQueueDispatcher`). `MaxReceiveAttemptsExceededException` / `MaxRetryAttemptsExceededException` signal the condition.
- **Critical Failure / Error Queue** — an unrecoverable receive error (`CriticalReceiverException`) stops the receiver loop and raises a Critical Failure via `ICriticalFailureNotifier` (default `CriticalFailureEventDispatcher`, which dispatches a `CriticalFailureEvent`). Failed messages are routed to the **Error Queue** (`ErrorQueueDispatcher`). Poison messages (`PoisonedMessageException`, e.g. a body that won't deserialize) are deadlettered.

## Routing Slips

A **Routing Slip** is a message that carries its own itinerary — an ordered list of destinations to visit. The receiver advances the slip to the next step as each handler completes, enabling itinerary-style choreography without a central orchestrator.

```csharp
using Chatter.MessageBrokers.Routing.Slips;

var slip = RoutingSlipBuilder.NewRoutingSlip(Guid.NewGuid())
    .WithRoute("validate.queue")
    .WithRoute("charge.queue")
    .WithRoute("ship.queue")
    .Build();
```

`RoutingSlipBehavior` advances the slip across the configured `RoutingStep`s; helper extensions (`MessageBrokerContextExtensions`, `SendOptionsExtensions`, `CommandPipelineBuilderExtensions`) attach and read the slip on the message/context.

## Diagnostics and Trace Context (optional, opt-in)

The brokered message boundary is instrumented with OpenTelemetry-compatible tracing and metrics, and W3C **trace context** is propagated across it. Both are **off until an application opts in**, and `Chatter.MessageBrokers` takes **no dependency on any `OpenTelemetry.*` NuGet package** — the instrumentation is built on the .NET base class library only: `System.Diagnostics.ActivitySource` for spans and `System.Diagnostics.Metrics.Meter` for instruments.

### Turning it on

The `ActivitySource` and the `Meter` are both named after the emitting assembly — **`Chatter.MessageBrokers`**. `Chatter.CQRS` emits under its own separate scope, named after *its* assembly, so send/receive and in-process dispatch can be sampled and filtered independently. Subscribe with a prefix wildcard to get both, or name the scopes exactly:

```csharp
services.AddOpenTelemetry()
        .WithTracing(t => t.AddSource("Chatter.*"))    // or .AddSource("Chatter.CQRS", "Chatter.MessageBrokers")
        .WithMetrics(m => m.AddMeter("Chatter.*"));    // or .AddMeter("Chatter.CQRS", "Chatter.MessageBrokers")
```

Any .NET `ActivityListener` / `MeterListener` works just as well — an OpenTelemetry provider merely subscribes to these base-class-library primitives, it is not a prerequisite for them.

### Off means off

**When nothing subscribes to the `Chatter.MessageBrokers` source or meter, nothing is emitted and nothing extra goes on the wire.** Each emit site checks whether Chatter's own source has a subscriber as its first statement and returns before a span name, a tag collection, or a `traceparent` header is constructed — so an application that never opts in pays no per-message cost and its messages are byte-identical to the un-instrumented ones. In particular, **no `traceparent` is written unless Chatter itself started a span**: the injection is a pure function of the span Chatter started, never of the ambient `Activity.Current`, which is non-null in any host running unrelated instrumentation. The guarantee is per-operation; constructing the `ActivitySource` and `Meter` themselves is a one-time static initialization per process, which is unavoidable for any `ActivitySource`-based design.

### What is emitted

Two spans, one Chatter-native span event alongside the standard `exception` event, and three instruments. Names prefixed `chatter.` are Chatter-native; `messaging.*`, `error.type` and `exception.*` are OpenTelemetry semantic conventions pinned to **v1.30.0**.

**Spans**

| Span name | Kind | Started by | Attributes |
| --- | --- | --- | --- |
| `send {messaging.destination.name}` | `ActivityKind.Producer` | `BrokeredMessageDispatcher`'s send/publish paths, `ForwardingRouter` and `ReplyRouter` — one span per dispatch call, however many messages it carries. | `messaging.system`, `messaging.operation.name` (`send`), `messaging.operation.type` (`send`), `messaging.destination.name`, `messaging.batch.message_count`, and `error.type` on failure. |
| `receive {receiver path}` | `ActivityKind.Consumer` | `BrokeredMessageReceiver<TMessage>` at worker entry — one span per delivery, covering every Recovery attempt for it. | `messaging.system`, `messaging.operation.name` (`receive`), `messaging.operation.type` (`receive`), `messaging.destination.name`, `messaging.message.id` (when the infrastructure supplied one), `chatter.messaging.receive.attempts`, `chatter.messaging.settlement` (`ack` / `nack` / `deadletter`), and `error.type` on failure. |

Those two are the whole span inventory. `messaging.operation.type` also declares the semconv values `create`, `process` and `settle`, but Chatter emits none of them, so a query written against those values matches nothing.

Two **span** attributes are deliberately absent in stated cases:

- An **attribute-routed dispatch** — the `Send` / `Publish` overloads that take no explicit destination — starts a bare `send` span with `messaging.destination.name` **unset**, because the destination is resolved by the one enumeration the Router performs. It is written, and the span name rewritten, at span stop, so what an exporter reads at stop is the resolved value rather than the start-time placeholder. A .NET `ActivityListener` that inspects the span at `ActivityStarted` does see the bare shape — the write happens after start — but sampling has already been decided by then, so the placeholder can never affect it. A batch whose messages resolve to different destinations has no single destination, so the attribute stays unset — at stop as well as at start — rather than being given the first message's value. `Forward` always takes an explicit destination, so a send span started by a forward never has this shape.
- `messaging.system` is left **unset** when the message carries no Messaging Infrastructure identifier. Nothing is invented in its place.

Both are absences **on the span**. The instruments below build one fixed attribute set, so `messaging.system` and `messaging.destination.name` are always present on a measurement as attribute *keys* and carry a **null value** in the two cases above rather than being omitted. Write "the attribute is missing" queries against the spans; against the metrics, write them against a null value.

**Span events**

| Event | On | When |
| --- | --- | --- |
| `chatter.messaging.receive.retry` | the receive span | One per Recovery attempt *after the first*, carrying `chatter.messaging.receive.attempts`. Added only when the subscriber asked for all data (`Activity.IsAllDataRequested`), so a sampled-out span pays nothing for it. |
| `exception` | either span | A failure an exception carried. On `net10.0` the event is produced by the base class library's `Activity.AddException`; on `net8.0` Chatter writes `exception.type`, `exception.message` and `exception.stacktrace` itself. The event name is `exception` either way. |

`chatter.messaging.receive.attempts` is written at span stop, so it is **always** present on a receive span; the retry *event* appears only from the second attempt onward. The tag is the attempt count, the event is the re-attempt — they are not the same signal.

**Metrics**

| Instrument | Type | Unit | Records |
| --- | --- | --- | --- |
| `messaging.client.operation.duration` | `Histogram<double>` | `s` | The duration of one send or one receive operation. |
| `messaging.client.sent.messages` | `Counter<long>` | `{message}` | Messages handed to broker infrastructure for delivery. |
| `messaging.client.consumed.messages` | `Counter<long>` | `{message}` | Messages broker infrastructure delivered. "Consumed" is the pinned specification's wire spelling for what this module calls receiving. |

All three carry exactly `messaging.system`, `messaging.operation.name`, `messaging.operation.type` and `messaging.destination.name`, plus `error.type` on failure.

**Metric attribute names are a strict subset of the span attribute names.** `messaging.batch.message_count`, `messaging.message.id`, `chatter.messaging.settlement` and `chatter.messaging.receive.attempts` are **span-only** and are not available as metric attributes. A rate broken down by settlement outcome, by message id, or by attempt count therefore cannot be built from these instruments — that breakdown has to come from the spans.

**`error.type` takes two shapes.** When an exception carried the failure it is the **fully qualified exception type name**. When the receiving infrastructure *returns* a `Failed` Settlement Outcome without throwing, it is **`settlement_failed`**. That second value carries no `exception` span event, deliberately: there is no exception, and a never-thrown marker exception would attach a synthetic stack trace as false evidence about something that never happened.

### Attribute names are data, not API

Broker-boundary spans carry OpenTelemetry semantic-convention attributes pinned to **v1.30.0** (`messaging.system`, `messaging.operation.name`, `messaging.operation.type`, `messaging.destination.name`, `messaging.message.id`, `messaging.batch.message_count`, `error.type`). Because telemetry attributes are emitted data rather than a compile-time type surface, **they may change in a minor release** when that pin advances. Dashboards and alert queries that hard-code attribute names should expect to be revisited on a pin bump; the bump is announced in this package's CHANGELOG.

### Propagation scope

Trace context rides the **Message Context** as the ordinary `traceparent` / `tracestate` headers, so it survives anywhere the whole context survives. Scope is deliberately partial and stated honestly.

**Trace context flows for:**

| Path | Notes |
| --- | --- |
| Azure Service Bus | Both directions — the context is projected onto the message's application properties on send and read back on receive. |
| RabbitMQ | Both directions, as a preserved non-core header. |
| The EntityFramework outbox | Persisted with the context and rehydrated on replay. |
| The Cosmos outbox | Same shape — serialized on stage, materialized on relay. |
| Outbox replay generally | A `traceparent` round-trips as a string through context materialization. |

**Trace context does NOT flow for:**

- **`Chatter.MessageBrokers.SqlServiceBroker`'s `DEFAULT`-message-type receive path.** That path builds a fresh header dictionary, so all upstream context is dropped. Only the Chatter envelope path — taken when the sending application supplies the Chatter brokered-message type — round-trips the context. The deadletter path likewise builds a fresh dictionary.
- **`Chatter.SqlChangeFeed`.** Its messages originate from a SQL trigger. There is no producer-side Chatter dispatch and no headers at all, so there is nothing to propagate and nothing to extract.

Both gaps are **pre-existing limitations that affect all headers alike** — they are not introduced by tracing, and closing them is a change to those receive paths, not to the instrumentation. Both are pinned by conformance tests, so a change that accidentally fixes or worsens either is visible.

Design rationale, the propagation scope, and the off-guard rules are recorded in [ADR-0010](../../../docs/adr/0010-optional-bcl-only-telemetry-per-assembly-sources-and-the-off-guard.md).

## Domain Language

Terminology used throughout this module (Brokered Message, Receiver, Dispatcher, Router/Forwarder, Inbox/Outbox, Recovery, Circuit Breaker, Critical Failure, Error Queue, Max Receives Exceeded, Body Converter) is defined in the [domain glossary](../CONTEXT.md).

[← All Chatter modules](../../../README.md)
