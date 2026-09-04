# <a name="chatter-messagebrokers"></a> Chatter.MessageBrokers

Technology-agnostic brokered messaging for .NET, built on Chatter.CQRS.

## Overview

`Chatter.MessageBrokers` adds brokered (out-of-process) messaging on top of [Chatter.CQRS](https://github.com/brenpike/Chatter/blob/master/src/Chatter.CQRS/src/README.md). It lets you receive messages from a broker and dispatch them to your existing `IMessageHandler<TMessage>` commands and events, and send/publish/forward messages back out — all without coupling your domain code to a specific broker technology.

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

## Configuration (appsettings)

Everything configured fluently above can also come from configuration. `AddMessageBrokers` reads the `Chatter:MessageBrokers` section **automatically**: it resolves the section from the `IConfiguration` the Chatter builder already holds, so no extra call is required. Earlier versions never resolved the section on this entry point, so every key underneath it was discarded — which is why configuration appeared to be ignored.

Four sections are bindable. Each section name is a constant on its builder, so it can be referenced instead of retyped:

| Section | Constant |
| --- | --- |
| `Chatter:MessageBrokers` | `MessageBrokerOptionsBuilder.MessageBrokerSectionName` |
| `Chatter:MessageBrokers:Reliability` | `ReliabilityOptionsBuilder.ReliabilityOptionsSectionName` |
| `Chatter:MessageBrokers:Recovery` | `RecoveryOptionsBuilder.RecoveryOptionsSectionName` |
| `Chatter:MessageBrokers:Recovery:CircuitBreaker` | `CircuitBreakerOptionsBuilder.CircuitBreakerOptionsSectionName` |

The last three are children of the first, so the key paths are identical whether the options are built through `AddMessageBrokers` or through a standalone `FromConfig(services, configuration)` on one of the sub-builders. Note that the circuit breaker key is spelled `CircuitBreaker` even though the property it binds onto is named `CircuitBreakerOptions`: `RecoveryOptions.CircuitBreakerOptions` is annotated `[ConfigurationKeyName("CircuitBreaker")]` precisely so that the one documented key works from both entry points.

### Bindable keys and their defaults

The default in each row is the value the fluent builder seeds before configuration is bound, so it is also the value a key keeps when configuration omits it.

`Chatter:MessageBrokers`

| Key | Type | Default |
| --- | --- | --- |
| `TransactionMode` | `None` / `ReceiveOnly` / `FullAtomicityViaInfrastructure` | `ReceiveOnly` |

`Chatter:MessageBrokers:Reliability`

| Key | Type | Default |
| --- | --- | --- |
| `RouteMessagesToOutbox` | `bool` | `false` |
| `MinutesToLiveInMemory` | `double` | `10` |
| `EnableOutboxPollingProcessor` | `bool` | `false` |
| `OutboxProcessingIntervalInMilliseconds` | `int` | `5000` (must be `0` or greater — see [Invalid reliability options fail at build time](#invalid-reliability-options-fail-at-build-time)) |

`Chatter:MessageBrokers:Recovery`

| Key | Type | Default |
| --- | --- | --- |
| `MaxRetryAttempts` | `int` | `5` |

`Chatter:MessageBrokers:Recovery:CircuitBreaker`

| Key | Type | Default |
| --- | --- | --- |
| `OpenToHalfOpenWaitTimeInSeconds` | `int` | `15` |
| `ConcurrentHalfOpenAttempts` | `int` | `1` |
| `NumberOfFailuresBeforeOpen` | `int` | `5` |
| `NumberOfHalfOpenSuccessesToClose` | `int` | `3` |
| `SecondsOpenBeforeCriticalFailureNotification` | `int` | `1800` |

A worked `appsettings.json`, showing every bindable key at its default:

```json
{
  "Chatter": {
    "MessageBrokers": {
      "TransactionMode": "ReceiveOnly",
      "Reliability": {
        "RouteMessagesToOutbox": false,
        "MinutesToLiveInMemory": 10,
        "EnableOutboxPollingProcessor": false,
        "OutboxProcessingIntervalInMilliseconds": 5000
      },
      "Recovery": {
        "MaxRetryAttempts": 5,
        "CircuitBreaker": {
          "OpenToHalfOpenWaitTimeInSeconds": 15,
          "ConcurrentHalfOpenAttempts": 1,
          "NumberOfFailuresBeforeOpen": 5,
          "NumberOfHalfOpenSuccessesToClose": 3,
          "SecondsOpenBeforeCriticalFailureNotification": 1800
        }
      }
    }
  }
}
```

The table above is the whole configurable surface. The remaining fluent calls — the retry delay strategy (`UseNoDelayRecovery`, `UseConstantDelayRecovery`, `UseExponentialDelayRecovery`), the retry and circuit-breaker exception predicates (`RetryWhen`, `IsTrippedBy`), and the max-receives-exceeded action (`UseRouteToErrorQueueRecoveryAction`) — register services rather than set option values and have no configuration equivalent.

### Precedence: configuration wins

Builder defaults are applied first and configuration is bound over them last, so a key present in configuration wins — over the builder default and over an explicit fluent call alike — while a key absent from configuration keeps the builder default. Configuration is bound into the options instance the builder already created, with non-public binding enabled, and never replaces that instance.

The honest reason an explicit fluent call loses is that these builders carry no nullable sentinel: they cannot distinguish an option that was never set fluently from one that was set to the same value as the default, so there is nothing for the bind to skip over.

`Chatter.MessageBrokers.AzureServiceBus` applies the opposite rule: its builder holds each fluent value in a nullable sentinel (`int?`, `bool?`, `TimeSpan?`), can therefore tell "never called" from "called with the default value", and lets an explicit fluent call win over configuration. The divergence is deliberate, and an application that configures both modules needs to know that the same-looking fluent call is authoritative in one module and overridable in the other.

In practice this means a `TransactionMode` in configuration overrides `WithTransactionMode(...)`, and an `OutboxProcessingIntervalInMilliseconds` in configuration overrides `WithOutboxPollingProcessor(2000)`:

```csharp
// Chatter:MessageBrokers:TransactionMode = "FullAtomicityViaInfrastructure" in appsettings
// wins over the fluent call below; remove the key to let the fluent value stand.
.AddMessageBrokers(options =>
{
    options.WithTransactionMode(TransactionMode.ReceiveOnly);
});
```

Precedence is settled once, while the options are being built, and the settled result is the only thing any injection style can see: `IOptions<T>`, `IOptionsSnapshot<T>` and `IOptionsMonitor<T>` resolve the same instance as injecting the concrete options type — see [Every injection style resolves the same options instance](#every-injection-style-resolves-the-same-options-instance).

### Invalid circuit breaker options fail at build time

An out-of-range circuit breaker value is rejected while the options are being built, by `CircuitBreakerOptionsValidationException` (namespace `Chatter.MessageBrokers.Recovery.CircuitBreaker`). The exception's message, and its `Violations` list, name **every** invalid option in one go, so an operator does not pay a deployment per invalid value. The minimums are what `CircuitBreaker` itself can run with: `ConcurrentHalfOpenAttempts`, `NumberOfFailuresBeforeOpen` and `NumberOfHalfOpenSuccessesToClose` must be at least `1`; `OpenToHalfOpenWaitTimeInSeconds` and `SecondsOpenBeforeCriticalFailureNotification` must be at least `0`.

Validation runs on the finalized options — after configuration has been bound over the fluent defaults — and from every entry point, so a bad value reaches it whether it arrived through `WithCircuitBreaker(...)`, through `Chatter:MessageBrokers:Recovery:CircuitBreaker`, or through the parent `Chatter:MessageBrokers` section. Previously an invalid value survived the build and surfaced much later as a bare `ArgumentOutOfRangeException` from the `new SemaphoreSlim(0, 0)` in the `CircuitBreaker` constructor, when the breaker was first resolved.

Validation is also unavoidable, because every single-instance resolution — the concrete `CircuitBreakerOptions`, `IOptions<CircuitBreakerOptions>`, `IOptionsSnapshot<CircuitBreakerOptions>` and `IOptionsMonitor<CircuitBreakerOptions>` — returns the validated instance. There is no longer a second, unvalidated instance sitting behind `IOptions<CircuitBreakerOptions>` for a bad value to survive in. The concrete registration is appended rather than replaced, so a second builder pass on the same `IServiceCollection` leaves the earlier instances reachable through `IEnumerable<CircuitBreakerOptions>` — but each of those was seeded and validated by its own `Build()`, so an enumeration cannot surface an unseeded or unvalidated object either.

### Invalid reliability options fail at build time

An `OutboxProcessingIntervalInMilliseconds` below zero is rejected while the options are being built, by `ReliabilityOptionsValidationException` (namespace `Chatter.MessageBrokers.Reliability.Configuration`). Its message, and its `Violations` list, name every invalid option in one go, on the same pattern as `CircuitBreakerOptionsValidationException` above.

The floor is what `BrokeredMessageOutboxProcessor` can actually poll with. It awaits `Task.Delay(OutboxProcessingIntervalInMilliseconds, token)` outside the `try`/`catch` that guards a poll pass, so a value `Task.Delay` rejects faults the whole background service rather than costing one pass. `0` is allowed — polling that aggressively is the operator's call to make. `-1` is rejected even though `Task.Delay` accepts it as `Timeout.Infinite`: an enabled processor that waits forever after its first pass is a disable by inference, and disabling the processor is expressible only through `EnableOutboxPollingProcessor`.

Validation is source-agnostic and runs on the finalized options, so a bad value reaches it whether it arrived through `WithOutboxPollingProcessor(-1)`, through `Chatter:MessageBrokers:Reliability`, or through the parent `Chatter:MessageBrokers` section. It also runs when `EnableOutboxPollingProcessor` is `false`: what is validated is the instance that was built, not whether anything currently reads it. Guarding validation on the processor being enabled is precisely how an invalid value survives to the deployment that later turns the processor on.

**Where each newly-bound key is checked.** The configuration in this section only started taking effect in `0.19.0` — before that the `Chatter:MessageBrokers` section did not bind (see the changelog). Every key it newly binds that can carry a value this module cannot run with ends in exactly one of three places, and you can check any key against the list:

1. **Rejected while the options are built.** The five `Recovery:CircuitBreaker` thresholds, and `Reliability:OutboxProcessingIntervalInMilliseconds`. The host does not start.
2. **Known, deferred, and tracked in issue #298.** Four values are accepted today that arguably should not be: a `Recovery:MaxRetryAttempts` of `0`, which disables retry by inference rather than through an opt-in; a `TransactionMode` string that names no member of the enum, which surfaces as a raw configuration-binder exception rather than a named one; a `MinutesToLiveInMemory` of `0`, which switches the in-memory outbox's purge off and lets it grow without bound; and a `MinutesToLiveInMemory` of `NaN`, which passes the purge's `ttl <= 0` guard (no comparison against `NaN` succeeds), then throws from `DateTime.AddMinutes(NaN)` inside the outbox processor's own `catch`, costing one logged poll pass each time rather than faulting the host.
3. **Checked where the value is used, not where it is bound.** `ReceiverOptions.MaxConcurrentCalls` is checked when a receiver initializes and raises an `InvalidOperationException` naming the receiver and the offending value when it is below `1`. The Azure Service Bus package's non-retry knobs are the same shape — see [that package's README](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers.AzureServiceBus/src/README.md).

The list is a claim about where each key is checked, not a claim that nothing can go wrong. Category 2 exists precisely because four known values are still accepted.

### Every injection style resolves the same options instance

`MessageBrokerOptions`, `ReliabilityOptions`, `RecoveryOptions` and `CircuitBreakerOptions` are each registered twice over the one instance their builder finished: once as the concrete type, and once behind `IOptions<T>`, `IOptionsSnapshot<T>` and `IOptionsMonitor<T>`. Injecting `IOptions<RecoveryOptions>` therefore hands back exactly the object `RecoveryOptionsBuilder.Build()` produced — fluent defaults applied, configuration bound over them, validation run — and so does injecting `RecoveryOptions` directly. Every default in the tables above, and the build-time `CircuitBreakerOptionsValidationException`, now hold on every injection style rather than only on direct injection of the concrete type.

Previously the three facets went to the container's own options factory instead, which built a fresh object from the configuration section alone. That object had seen neither the fluent defaults nor validation, so an `IOptions<CircuitBreakerOptions>` reader saw `ConcurrentHalfOpenAttempts` as `0` where the built instance held `1`, an `IOptions<RecoveryOptions>` reader saw `MaxRetryAttempts` as `0` and a null `CircuitBreakerOptions` where the built instance held `5` and a populated one, and an `IOptions<ReliabilityOptions>` reader saw `OutboxProcessingIntervalInMilliseconds` as `0` where the built instance held `5000`. The parent options were the worst of them: `IOptions<MessageBrokerOptions>` returned an all-default instance whose `TransactionMode` was `None` and whose `Reliability` and `Recovery` were both `null`, so reading `.Recovery.MaxRetryAttempts` off it raised a `NullReferenceException` rather than returning a wrong number — not one configured key landed.

Nothing inside this package injected those facets. `BrokeredMessageOutboxProcessor`, `RetryStrategy`, `RetryWithCircuitBreakerStrategy` and `CircuitBreaker` all inject the concrete options types, which were always correctly seeded, so the zeroed values were reachable only by an application that resolved a facet itself.

`IOptionsMonitor<T>` is supported for resolution only. Configuration is bound once, while the options are being built, so the built options never reload and the change callback is inert. Named options are not a concept in this package either — every name, including none, resolves the same built instance.

**Behaviour change:** a `services.Configure<MessageBrokerOptions>(...)`, `Configure<ReliabilityOptions>(...)`, `Configure<RecoveryOptions>(...)` or `Configure<CircuitBreakerOptions>(...)` registration of your own is **no longer consulted**. The facets are bound directly to the built instance and never go through the options factory, which is precisely what makes the built instance authoritative, so a post-configure of these four types silently stops applying. The narrowing is intentional, and no known application relies on it, but it is a public behaviour change: configure these options through the fluent builder or through the `Chatter:MessageBrokers` section instead.

The same rule is applied to `ServiceBusOptions` by `Chatter.MessageBrokers.AzureServiceBus`. It is not applied across the whole suite: `Chatter.MessageBrokers.RabbitMQ` and `Chatter.MessageBrokers.SqlServiceBroker` still register only the concrete options singleton, deliberately — neither registers a `Configure<T>`, so neither has anything divergent to close.

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

**Fill rule — every row below states when it is emitted; a blank condition cell is a defect, and `Always` is a positive claim that the emit site is unconditional rather than a default.** A condition on an attribute, event or metric-attribute row is stated *relative to its signal existing at all*: whether a span exists at all is the span table's **Started when**, and whether a measurement is taken at all is the instrument table's **Recorded when**. One row per facet — no comma-joined lists.

**Span name rule.** A span is named `{messaging.operation.name} {messaging.destination.name}`, degrading to the bare operation name when no destination is set. A bare `send` is therefore the same span under this rule rather than a further one, and a name whose destination is resolved only at span stop is rewritten there.

**Spans**

<!-- Fill rule: every row states when it is emitted; a blank condition cell is a defect, and `Always` is a positive claim that the emit site is unconditional rather than a default. One row per facet - no comma-joined lists. -->

| Span | Name | Kind | Started by | Started when |
| --- | --- | --- | --- | --- |
| send | `send {messaging.destination.name}`, per the span name rule | `ActivityKind.Producer` | `BrokeredMessageDispatcher`'s send and publish paths, `ForwardingRouter`, `ReplyRouter`, `OutboxProcessor`'s drain, and the Cosmos change-feed relay's drain (from the sibling reliability package) | Once per dispatch call that reaches the send path, however many messages that call carries, and once per row the outbox drain publishes — and only while a .NET `ActivityListener` is attached to the `Chatter.MessageBrokers` source and samples the span. Two shapes reach no span at all: `ForwardingRouter` returns before any diagnostics when the forward destination is blank, and `ReplyRouter` does the same when the reply routing context is null. A reply whose `BuildReply` throws does start one — the span opens before the reply is built, so a reply that could not be constructed is reported as a failed send rather than as a metric with no span beside it. |
| receive | `receive {messaging.destination.name}`, per the span name rule; the destination is the receiver path | `ActivityKind.Consumer` | `BrokeredMessageReceiver<TMessage>` at worker entry | Once per delivery, covering every Recovery attempt made for that delivery — and only while a .NET `ActivityListener` is attached to the `Chatter.MessageBrokers` source and samples the span. |

Those two are the whole span inventory; the name rule renames a span, it never adds one. `messaging.operation.type` also declares the semconv values `create`, `process` and `settle`, but Chatter emits none of them, so a query written against those values matches nothing.

**Span attributes**

An **unset** attribute below is an unconditional write of a null value, not a skipped write: .NET `Activity.SetTag` drops a tag whose value is null.

| Attribute | Span | Value | Emitted | Name origin |
| --- | --- | --- | --- | --- |
| `messaging.system` | send | The Messaging Infrastructure identifier the dispatch names — the infrastructure-type entry of the routing options' Message Context, the outbound message's own on a forward, the inbound message's on a reply (the reply aliases that same context, so it is the identity the reply carries and it is known before the reply is built), or the entry persisted with the row on an outbox drain. | Only when the dispatch carries a non-blank identifier; a dispatch carrying none — or one that names the default infrastructure, whose identifier is the empty string — leaves the attribute **unset**, and nothing is invented in its place. | semconv v1.30.0 |
| `messaging.system` | receive | The receiver's configured `ReceiverOptions.InfrastructureType`, normalized so that a blank value leaves the attribute **unset**. | Only when the Brokered Message Receiver was configured with one; a receiver configured without one leaves the attribute unset. | semconv v1.30.0 |
| `messaging.operation.name` | send | `send` | Always. | semconv v1.30.0 |
| `messaging.operation.name` | receive | `receive` | Always. | semconv v1.30.0 |
| `messaging.operation.type` | send | `send` | Always. | semconv v1.30.0 |
| `messaging.operation.type` | receive | `receive` | Always. | semconv v1.30.0 |
| `messaging.destination.name` | send | The destination the call named, when it named one; otherwise the single destination every message of the batch resolved to. | A forward or a reply sets it at span start, from the outbound message's own destination, and never rewrites it. A `Send` / `Publish` given an explicit destination also sets it at span start, then rewrites the same value in the dispatch call's `finally`. An attribute-routed `Send` / `Publish` starts with none and first writes it at span **stop** — in that same `finally`, the destination being resolved by the one enumeration the Router performs — rewriting the span name with it there. **Unset** on that attribute-routed shape alone, when the batch resolved to more than one destination or yielded nothing. | semconv v1.30.0 |
| `messaging.destination.name` | receive | The receiver path, as the Messaging Infrastructure's path builder resolved it. | Always, at span start — the path is resolved once at receiver startup, before any delivery. | semconv v1.30.0 |
| `messaging.batch.message_count` | send | How many messages the call handed to the Router: the number a `Send` / `Publish` actually yielded, `1` for a forward, `1` for a drained outbox row, and for a reply `1` once the Router has been called or `0` when the call failed before that — the same number `messaging.client.sent.messages` records for that call. | Always. A forward and a drain set it at span start; a `Send` / `Publish` and a reply write it at span **stop** — in the call's `finally`, the count being unknown until the Router's one enumeration ends, or until the reply has actually been handed to the Router — overwriting the `0` the span started with. | semconv v1.30.0 |
| `messaging.message.id` | receive | The Messaging Infrastructure's own identifier for the delivered message. | Only when the infrastructure supplied a non-empty one. | semconv v1.30.0 |
| `chatter.messaging.receive.attempts` | receive | How many Recovery attempts ran for this delivery; `0` when the delivery failed before Recovery began, as a poisoned body does. | Always, written at span stop. | Chatter-native |
| `chatter.messaging.settlement` | receive | The settlement Chatter answered with: `ack` when handling completed and the worker token was not cancelled; `nack` when handling completed under a cancelled worker token, or a processing fault left the delivery count below `MaxReceiveAttempts`; `deadletter` on a poisoned body, or a processing fault whose delivery count has reached `MaxReceiveAttempts`. | Only on those branches of the worker's error ladder that choose a settlement, and recorded where the branch chooses it rather than after the settlement call, which is best-effort. A delivery that ended in a `CriticalReceiverException`, in a shutdown cancellation, or in a delivery-count probe that itself failed reaches no such branch and carries no settlement. | Chatter-native |
| `error.type` | send | The fully qualified exception type name. | Only when an exception ended the dispatch call. | semconv v1.30.0 |
| `error.type` | receive | The fully qualified exception type name; or `settlement_failed` when the Messaging Infrastructure *returned* a `Failed` Settlement Outcome without raising anything. | Only when a failure was retained for the delivery. A shutdown cancellation is deliberately not a failed receive — a clean restart would otherwise emit one failure per delivery in flight — and retains none. | semconv v1.30.0 |
| Status — the span's own status field, not a tag | send | `Error`, with the exception's message as the status description. | Recorded by the same `ActivityOutcome.RecordFailure` call that writes this span's `error.type` above, so the status and the attribute cannot disagree. | `Activity.SetStatus`, .NET base class library |
| Status — the span's own status field, not a tag | receive | `Error`; the description is the exception's message on an exception-shaped failure, and on the non-exception `settlement_failed` path the description of what did not settle. | Recorded by the same `ActivityOutcome.RecordFailure` call that writes this span's `error.type` above, so the status and the attribute cannot disagree — the `settlement_failed` path included, which sets both without raising anything. | `Activity.SetStatus`, .NET base class library |

**Span events**

| Event | Span | Attributes | Emitted |
| --- | --- | --- | --- |
| `chatter.messaging.receive.retry` | receive | `chatter.messaging.receive.attempts`, carrying the number of the attempt this event records. | On every Recovery attempt after the first, and only while `Activity.IsAllDataRequested` is true, so a sampled-out or recording-only span pays nothing to construct it. |
| `exception` | send | Provenance-split by target framework: on `net10.0` the base class library's `Activity.AddException` writes them; on `net8.0` Chatter writes the `exception.*` set itself. The event name is `exception` either way. | Only when an exception ended the dispatch call and `Activity.IsAllDataRequested` is true. |
| `exception` | receive | Provenance-split by target framework, exactly as on the send span: `Activity.AddException` on `net10.0`, Chatter-written `exception.*` tags on `net8.0`. | Only when an exception ended the delivery and `Activity.IsAllDataRequested` is true. A `Failed` Settlement Outcome the infrastructure returned without raising carries no event, deliberately: there is no exception, and a never-thrown marker exception would attach a synthetic stack trace as false evidence about something that never happened. A shutdown cancellation likewise carries none. |

**Metrics**

| Instrument | Type | Unit | Advised buckets | Records | Recorded when |
| --- | --- | --- | --- | --- | --- |
| `messaging.client.operation.duration` | `Histogram<double>` | `s` | `0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10` — published as instrument advice on `net10.0` only; on `net8.0` the instrument carries none. See **Histogram bucket boundaries** below. | The elapsed time of one dispatch call, of one outbox drain publish, or of one delivery. | Once per dispatch call that reaches the send path, once per row the outbox drain publishes, and once per delivery, on the failing path as well as the succeeding one, and only while a .NET `MeterListener` has enabled this instrument. The two no-op routes that start no span — a blank forward destination, a null reply routing context — record nothing either; a reply whose `BuildReply` throws records here and on the send span alongside it. |
| `messaging.client.sent.messages` | `Counter<long>` | `{message}` | Not applicable — a `Counter<long>` has no buckets. | The number of messages the dispatch call handed to broker infrastructure: the number a `Send` / `Publish` yielded, `1` for a forward, `1` for each row the outbox drain publishes, and for a reply `1` once the Router has been called or `0` when the call failed before that. | Once per dispatch call that reaches the send path, once per row the outbox drain publishes, on the failing path as well as the succeeding one, and only while a .NET `MeterListener` has enabled this instrument. The two no-op routes that start no span — a blank forward destination, a null reply routing context — record nothing here either. |
| `messaging.client.consumed.messages` | `Counter<long>` | `{message}` | Not applicable — a `Counter<long>` has no buckets. | One message per delivery. "Consumed" is the pinned specification's wire spelling for what this module calls receiving. | Once per delivery, on the failing path as well as the succeeding one, and only while a .NET `MeterListener` has enabled this instrument. |

**Metric attributes**

| Attribute | Instruments | Value | Emitted |
| --- | --- | --- | --- |
| `messaging.system` | all three | On a send measurement, the Messaging Infrastructure identifier the dispatch names — **null** when it names none, or names the default infrastructure, whose identifier is the empty string. A reply resolves it off the inbound message before the reply is built, so a reply whose `BuildReply` throws still carries the inbound infrastructure's identifier rather than null; a drain carries the identifier persisted with the row. On a receive measurement, the receiver's configured `ReceiverOptions.InfrastructureType` — **null** when the receiver was configured without one. | Always, as a key, whatever the value. |
| `messaging.operation.name` | all three | `send` on a send measurement, `receive` on a receive measurement. | Always, as a key. |
| `messaging.operation.type` | all three | `send` on a send measurement, `receive` on a receive measurement. | Always, as a key. |
| `messaging.destination.name` | all three | On a send measurement, the destination the call named or the single destination the batch resolved to — **null** when the call named none and the batch resolved to more than one, or yielded nothing. On a receive measurement, the receiver path. | Always, as a key, whatever the value. |
| `error.type` | all three | The fully qualified exception type name; or `settlement_failed` for a `Failed` Settlement Outcome the Messaging Infrastructure returned without raising anything. | Only when a non-blank error type was resolved for the operation; an operation that did not fail carries no `error.type` key at all. |

Where a span leaves an attribute **unset**, the instruments still carry that attribute as a key with a null value. Query the spans for a missing attribute; query the instruments for a null one.

**Metric attribute names are a strict subset of the span attribute names.** A rate broken down by settlement outcome, by message id or by attempt count therefore cannot be built from these instruments — that breakdown has to come from the spans.

### Histogram bucket boundaries

`messaging.client.operation.duration` records **seconds**. The OpenTelemetry .NET SDK's default explicit histogram boundaries are millisecond-sized (`0, 5, 10, 25, ... 10000`), so a collector that applies them puts every realistic measurement in the first bucket and P50, P90 and P99 all report the same number forever. `Chatter.MessageBrokers` therefore publishes seconds-sized bucket boundaries on the duration histogram itself; the two counters alongside it have no buckets to advise. The boundaries match the OpenTelemetry messaging semantic conventions and are listed in the Metrics table above.

**They are advice, not a setting.** The boundaries are published as instrument *advice* — a **default** that an application's own view **overrides**. An application that already registers a view for `messaging.client.operation.duration` keeps winning exactly as it did before; nothing it configured changes. Advice is the right layer for this precisely because it cannot take that choice away from the application.

**Advice is published on `net10.0` only.** The base class library type that carries instrument advice does not exist in the `net8.0` shared framework, and this package takes no package dependency to reach it. On `net8.0` the instrument therefore ships with no advice at all, and the collector falls back to its own millisecond-sized defaults.

**On `net8.0`, configure the equivalent view in your own application.** `AddView` and `ExplicitBucketHistogramConfiguration` are `OpenTelemetry.Metrics` types that come from *your* application's OpenTelemetry packages — this package still takes **no dependency on any `OpenTelemetry.*` NuGet package**, and the snippet below adds none to it:

```csharp
using OpenTelemetry.Metrics;

services.AddOpenTelemetry()
        .WithMetrics(m => m
            .AddMeter("Chatter.MessageBrokers")
            .AddView("messaging.client.operation.duration", new ExplicitBucketHistogramConfiguration
            {
                Boundaries = new double[] { 0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10 }
            }));
```

The same view is harmless on `net10.0`: it overrides advice that already carries these boundaries. This `net8.0` caveat retires when `net8.0` is dropped and the package single-targets `net10.0` after .NET 8 reaches end of life on 2026-11-10 — tracked in [issue #395](https://github.com/brenpike/Chatter/issues/395).

### Attribute names are data, not API

Broker-boundary spans carry OpenTelemetry semantic-convention attributes pinned to **v1.30.0** (`messaging.system`, `messaging.operation.name`, `messaging.operation.type`, `messaging.destination.name`, `messaging.message.id`, `messaging.batch.message_count`, `error.type`). Because telemetry attributes are emitted data rather than a compile-time type surface, **they may change in a minor release** when that pin advances. Dashboards and alert queries that hard-code attribute names should expect to be revisited on a pin bump; the bump is announced in this package's CHANGELOG.

### Propagation scope

Trace context rides the **Message Context** as the ordinary `traceparent` / `tracestate` headers, so it survives anywhere the whole context survives. Scope is deliberately partial and stated honestly.

**Trace context flows for:**

| Path | Notes |
| --- | --- |
| Azure Service Bus | Both directions — the context is projected onto the message's application properties on send and read back on receive. |
| RabbitMQ | Both directions, as a preserved non-core header. |
| The EntityFramework outbox | Persisted with the context and rehydrated on drain — and, with diagnostics on, **reparented** there, as below. |
| The Cosmos outbox | Same shape — serialized on stage, materialized on relay — and, with diagnostics on, **reparented** there as well, exactly as the EntityFramework outbox is. |
| Outbox replay generally | A `traceparent` round-trips as a string through context materialization. |

**The outbox drain reparents; it does not break the chain.** Both drains behave the same way here: `OutboxProcessor` polling the EntityFramework outbox, and the Cosmos change-feed relay draining the Cosmos outbox. With diagnostics on, each publishes the drained row or document under a fresh send span parented to the `traceparent` persisted with that row or document, then writes **that span's** context onto the outgoing message. A downstream receive therefore parents to the drain span rather than directly to the write span, and the trace reads write → drain → receive: one extra hop, and it is the hop that actually put the message on the broker, minutes after the write and in another process. A row or document that carries no persisted context — one written while diagnostics were off, or received over a path that propagates none — starts a fresh root instead, with the ambient activity of the drain loop or of the change feed attached as a link rather than promoted to parent, because neither the poll nor the feed caused the message.

**The Cosmos drain opens this module's send span; it declares none of its own.** `Chatter.MessageBrokers.Reliability.Cosmos` emits its own drain metrics — lag, per-document outcome, batch size and batch count — under its own assembly-named scope, and no span at all: it publishes each drained document under the `Chatter.MessageBrokers` send span described above and never re-emits that span under its own scope, so one drained document is never reported by two send spans. Its instruments are documented in [that package's README](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers.Reliability.Cosmos/src/README.md).

The reparenting happens **only when diagnostics are opted into**, on either drain. With them off, on the metrics-only path, and when the drain span is sampled out, nothing is written and the persisted `traceparent` rides out unchanged.

**Trace context does NOT flow for:**

- **`Chatter.MessageBrokers.SqlServiceBroker`'s `DEFAULT`-message-type receive path.** That path builds a fresh header dictionary, so all upstream context is dropped. Only the Chatter envelope path — taken when the sending application supplies the Chatter brokered-message type — round-trips the context. The deadletter path likewise builds a fresh dictionary.
- **`Chatter.SqlChangeFeed`.** Its messages originate from a SQL trigger. There is no producer-side Chatter dispatch and no headers at all, so there is nothing to propagate and nothing to extract.

Both gaps are **pre-existing limitations that affect all headers alike** — they are not introduced by tracing, and closing them is a change to those receive paths, not to the instrumentation. Both are pinned by conformance tests, so a change that accidentally fixes or worsens either is visible.

Design rationale, the propagation scope, and the off-guard rules are recorded in [ADR-0010](https://github.com/brenpike/Chatter/blob/master/docs/adr/0010-optional-bcl-only-telemetry-per-assembly-sources-and-the-off-guard.md).

## Domain Language

Terminology used throughout this module (Brokered Message, Receiver, Dispatcher, Router/Forwarder, Inbox/Outbox, Recovery, Circuit Breaker, Critical Failure, Error Queue, Max Receives Exceeded, Body Converter) is defined in the [domain glossary](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers/CONTEXT.md).

[← All Chatter modules](https://github.com/brenpike/Chatter/blob/master/README.md)
