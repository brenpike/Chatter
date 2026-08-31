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

**Fill rule — every row below states when it is emitted; a blank condition cell is a defect, and `Always` is a positive claim that the emit site is unconditional rather than a default.** A condition on an attribute, event or metric-attribute row is stated *relative to its signal existing at all*: whether a span exists at all is the span table's **Started when**, and whether a measurement is taken at all is the instrument table's **Recorded when**.

**Span name rule.** A span is named `{messaging.operation.name} {messaging.destination.name}`, degrading to the bare operation name when no destination is set. A bare `send` is therefore the same span under this rule rather than a further one, and a name whose destination is resolved only at span stop is rewritten there.

**Spans**

<!-- Fill rule: every row states when it is emitted; a blank condition cell is a defect, and `Always` is a positive claim that the emit site is unconditional rather than a default. -->

| Span | Name | Kind | Started by | Started when |
| --- | --- | --- | --- | --- |
| send | `send {messaging.destination.name}`, per the span name rule | `ActivityKind.Producer` | `BrokeredMessageDispatcher`'s send and publish paths, `ForwardingRouter`, `ReplyRouter` | Once per dispatch call, however many messages that call carries — and only while a .NET `ActivityListener` is attached to the `Chatter.MessageBrokers` source and samples the span. |
| receive | `receive {messaging.destination.name}`, per the span name rule; the destination is the receiver path | `ActivityKind.Consumer` | `BrokeredMessageReceiver<TMessage>` at worker entry | Once per delivery, covering every Recovery attempt made for that delivery — and only while a .NET `ActivityListener` is attached to the `Chatter.MessageBrokers` source and samples the span. |

Those two are the whole span inventory; the name rule renames a span, it never adds one. `messaging.operation.type` also declares the semconv values `create`, `process` and `settle`, but Chatter emits none of them, so a query written against those values matches nothing.

**Span attributes**

An **unset** attribute below is an unconditional write of a null value, not a skipped write: .NET `Activity.SetTag` drops a tag whose value is null.

| Attribute | Span | Value | Emitted | Name origin |
| --- | --- | --- | --- | --- |
| `messaging.system` | send | The Messaging Infrastructure identifier the dispatch names — the infrastructure-type entry of the routing options' Message Context, or the outbound message's own on a forward or a reply. | Only when the dispatch carries that identifier; a dispatch carrying none leaves the attribute **unset**, and nothing is invented in its place. | semconv v1.30.0 |
| `messaging.system` | receive | The receiver's configured `ReceiverOptions.InfrastructureType` — **the empty string** when the receiver was configured without one, because that property's `""` default is never normalized. | Always, the empty-string case included. | semconv v1.30.0 |
| `messaging.operation.name` | send | `send` | Always. | semconv v1.30.0 |
| `messaging.operation.name` | receive | `receive` | Always. | semconv v1.30.0 |
| `messaging.operation.type` | send | `send` | Always. | semconv v1.30.0 |
| `messaging.operation.type` | receive | `receive` | Always. | semconv v1.30.0 |
| `messaging.destination.name` | send | The destination the call named, when it named one; otherwise the single destination every message of the batch resolved to. | Written at span **stop**, not at start, because an attribute-routed `Send` / `Publish` resolves its destination by the one enumeration the Router performs. **Unset** when the call named none and the batch resolved to more than one destination, or yielded nothing. Sampling is decided at start, so the start-time placeholder reaches no .NET `ActivityListener` and the rewritten name is what is read at `ActivityStopped`. | semconv v1.30.0 |
| `messaging.destination.name` | receive | The receiver path, as the Messaging Infrastructure's path builder resolved it. | Always, at span start — the path is resolved once at receiver startup, before any delivery. | semconv v1.30.0 |
| `messaging.batch.message_count` | send | How many messages the call handed to the Router: the number a `Send` / `Publish` actually yielded, and `1` for a forward or a reply. | Always. A forward or a reply sets it at span start; a `Send` / `Publish` writes it at span **stop**, the count being unknown until the Router's one enumeration ends, and sampling is decided at start, so the start-time `0` placeholder reaches no .NET `ActivityListener`. | semconv v1.30.0 |
| `messaging.message.id` | receive | The Messaging Infrastructure's own identifier for the delivered message. | Only when the infrastructure supplied a non-empty one. | semconv v1.30.0 |
| `chatter.messaging.receive.attempts` | receive | How many Recovery attempts ran for this delivery; `0` when the delivery failed before Recovery began, as a poisoned body does. | Always, written at span stop. | Chatter-native |
| `chatter.messaging.settlement` | receive | The settlement Chatter answered with: `ack` when handling completed and the worker token was not cancelled; `nack` when handling completed under a cancelled worker token, or a processing fault left the delivery count below `MaxReceiveAttempts`; `deadletter` on a poisoned body, or a processing fault whose delivery count has reached `MaxReceiveAttempts`. | Only on those branches of the worker's error ladder that choose a settlement, and recorded where the branch chooses it rather than after the settlement call, which is best-effort. A delivery that ended in a `CriticalReceiverException`, in a shutdown cancellation, or in a delivery-count probe that itself failed reaches no such branch and carries no settlement. | Chatter-native |
| `error.type` | send | The fully qualified exception type name. | Only when an exception ended the dispatch call. | semconv v1.30.0 |
| `error.type` | receive | The fully qualified exception type name; or `settlement_failed` when the Messaging Infrastructure *returned* a `Failed` Settlement Outcome without raising anything. | Only when a failure was retained for the delivery. A shutdown cancellation is deliberately not a failed receive — a clean restart would otherwise emit one failure per delivery in flight — and retains none. | semconv v1.30.0 |

**Span events**

| Event | Span | Attributes | Emitted |
| --- | --- | --- | --- |
| `chatter.messaging.receive.retry` | receive | `chatter.messaging.receive.attempts`, carrying the number of the attempt this event records. | On every Recovery attempt after the first, and only while `Activity.IsAllDataRequested` is true, so a sampled-out or recording-only span pays nothing to construct it. |
| `exception` | send | Provenance-split by target framework: on `net10.0` the base class library's `Activity.AddException` writes them; on `net8.0` Chatter writes the `exception.*` set itself. The event name is `exception` either way. | Only when an exception ended the dispatch call and `Activity.IsAllDataRequested` is true. |
| `exception` | receive | Provenance-split by target framework, exactly as on the send span: `Activity.AddException` on `net10.0`, Chatter-written `exception.*` tags on `net8.0`. | Only when an exception ended the delivery and `Activity.IsAllDataRequested` is true. A `Failed` Settlement Outcome the infrastructure returned without raising carries no event, deliberately: there is no exception, and a never-thrown marker exception would attach a synthetic stack trace as false evidence about something that never happened. A shutdown cancellation likewise carries none. |

**Metrics**

| Instrument | Type | Unit | Records | Recorded when |
| --- | --- | --- | --- | --- |
| `messaging.client.operation.duration` | `Histogram<double>` | `s` | The elapsed time of one dispatch call, or of one delivery. | Once per dispatch call and once per delivery, on the failing path as well as the succeeding one, and only while a .NET `MeterListener` has enabled this instrument. |
| `messaging.client.sent.messages` | `Counter<long>` | `{message}` | The number of messages the dispatch call handed to broker infrastructure: the number a `Send` / `Publish` yielded, `1` for a forward, and for a reply `1` once the Router has been called or `0` when the call failed before that. | Once per dispatch call, on the failing path as well as the succeeding one, and only while a .NET `MeterListener` has enabled this instrument. |
| `messaging.client.consumed.messages` | `Counter<long>` | `{message}` | One message per delivery. "Consumed" is the pinned specification's wire spelling for what this module calls receiving. | Once per delivery, on the failing path as well as the succeeding one, and only while a .NET `MeterListener` has enabled this instrument. |

**Metric attributes**

| Attribute | Instruments | Value | Emitted |
| --- | --- | --- | --- |
| `messaging.system` | all three | On a send measurement, the Messaging Infrastructure identifier the dispatch names — **null** when it names none. On a receive measurement, the receiver's configured `ReceiverOptions.InfrastructureType` — the empty string when the receiver was configured without one. | Always, as a key, whatever the value. |
| `messaging.operation.name` | all three | `send` on a send measurement, `receive` on a receive measurement. | Always, as a key. |
| `messaging.operation.type` | all three | `send` on a send measurement, `receive` on a receive measurement. | Always, as a key. |
| `messaging.destination.name` | all three | On a send measurement, the destination the call named or the single destination the batch resolved to — **null** when the call named none and the batch resolved to more than one, or yielded nothing. On a receive measurement, the receiver path. | Always, as a key, whatever the value. |
| `error.type` | all three | The fully qualified exception type name; or `settlement_failed` for a `Failed` Settlement Outcome the Messaging Infrastructure returned without raising anything. | Only when a non-blank error type was resolved for the operation; an operation that did not fail carries no `error.type` key at all. |

Where a span leaves an attribute **unset**, the instruments still carry that attribute as a key with a null value. Query the spans for a missing attribute; query the instruments for a null one.

**Metric attribute names are a strict subset of the span attribute names.** A rate broken down by settlement outcome, by message id or by attempt count therefore cannot be built from these instruments — that breakdown has to come from the spans.

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
