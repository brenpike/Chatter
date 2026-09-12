# <a name="chatter-cqrs"></a> Chatter.CQRS

A lightweight CQRS framework for .NET that dispatches Commands, Queries, and Events to their handlers via the mediator pattern.

## Overview

Chatter.CQRS is the foundational module of the Chatter library suite. It implements **Command Query Responsibility Segregation (CQRS)** using an in-process **mediator** pattern: instead of calling handler classes directly, your code dispatches messages and the framework routes each one to the registered handler(s).

The module distinguishes three kinds of message, all of which derive from the `IMessage` marker interface:

- **Commands** (`ICommand`) — instruct an aggregate to change state; dispatched to exactly **one** handler.
- **Queries** (`IQuery<T>`) — retrieve a read model without mutating state; dispatched to exactly one handler that returns a result.
- **Events** (`IEvent`) — announce that something happened; fanned out to **zero or many** handlers.

Handlers are discovered automatically by assembly scanning (powered by [Scrutor](https://github.com/khellang/Scrutor)) and registered into the standard `Microsoft.Extensions.DependencyInjection` container. Only closed handler types are scanned, and a discovered handler is registered only under the closed `IMessageHandler<TMessage>` / `IQueryHandler<TQuery,TResult>` interface(s) it implements, not under any other interface the handler class may implement (see the command registration contract below). An open-generic handler class is not discovered by the scan and must be registered manually. Commands additionally flow through an optional **command pipeline** of cross-cutting behaviors.

## Installation

```bash
dotnet add package Chatter.CQRS
```

## Getting Started

### 1. Register Chatter.CQRS with DI

The entry point is the `AddChatterCqrs` extension method on `IServiceCollection`. It scans the supplied assemblies for handlers and wires up the dispatchers. Several overloads control how handler assemblies are located:

- The zero-argument `AddChatterCqrs(configuration)` overload — no marker types, no explicit assemblies, no namespace selector — scans every loaded assembly in the current `AppDomain`.
- Marker types or explicit assemblies, with no namespace selector configured, scan **only** those assemblies — nothing else in the `AppDomain` is scanned.
- A namespace selector, whether used alone or alongside marker types / explicit assemblies, widens the scan to every assembly the selector matches, in addition to any explicit assemblies supplied.

```csharp
using Microsoft.Extensions.DependencyInjection;

// Locate handler assemblies via marker types — ONLY the assemblies these types live in are scanned
services.AddChatterCqrs(configuration, typeof(CreateOrderHandler), typeof(SomeOtherHandler));

// ...or pass explicit assemblies — ONLY the assemblies passed are scanned
services.AddChatterCqrs(configuration, typeof(Program).Assembly);

// ...or select assemblies by namespace/assembly-name with '*' and '?' wildcards
services.AddChatterCqrs(configuration, "MyApp.*");

// ...or widen a marker-type/explicit-assembly scan back to the whole AppDomain with a '*' namespace selector
services.AddChatterCqrs(
    configuration,
    messageHandlerSourceBuilder: source => source.WithMarkerTypes(typeof(CreateOrderHandler)).WithNamespaceSelector("*"));

// ...or use the full builder form with a command pipeline + assembly source filter
services.AddChatterCqrs(
    configuration,
    pipelineBuilder: pipeline => pipeline.WithBehavior(typeof(LoggingBehavior<>)),
    messageHandlerSourceBuilder: source => source.WithMarkerTypes(typeof(CreateOrderHandler)));
```

`AddChatterCqrs` returns an `IChatterBuilder`, which exposes the `Services`, `Configuration`, and `AssemblySourceFilter` used by other Chatter modules (such as the message brokers) to extend the registration.

#### Failing composition when two handlers claim one command

`ThrowOnDuplicateCommandHandlers()` is an opt-in check on the returned `IChatterBuilder`. It re-reads the same assembly source filter `AddChatterCqrs` scanned, and throws a single `InvalidOperationException` naming every command that more than one scanned handler handles, together with all of that command's competing handler types:

```csharp
services.AddChatterCqrs(configuration, typeof(CreateOrderHandler))
        .ThrowOnDuplicateCommandHandlers();
```

The check is **off unless you call it**: no `AddChatterCqrs` overload invokes it, so an application that never calls it composes exactly as it did before. A handler registered by hand before `AddChatterCqrs`, or registered by another module after it, is not compared against the scanned ones. Duplicate **event** handlers are not reported either: event handlers are appended rather than replaced, so several handlers for one event all register and all run. Duplicate **query** handlers need no such flag — query handlers are registered with a *throw* strategy, so a second registration for the same closed `IQueryHandler<TQuery, TResult>` fails the scan itself.

Why the check is opt-in, and why turning it on by default would be a major-version change, is recorded in [ADR-0017](https://github.com/brenpike/Chatter/blob/master/docs/adr/0017-opt-in-strict-command-handler-registration.md).

### 2. Define a command and its handler

```csharp
using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using System.Threading.Tasks;

public class CreateOrder : ICommand
{
    public string CustomerId { get; set; }
    public decimal Total { get; set; }
}

public class CreateOrderHandler : IMessageHandler<CreateOrder>
{
    public Task Handle(CreateOrder message, IMessageHandlerContext context)
    {
        // ... change aggregate state
        return Task.CompletedTask;
    }
}
```

### 3. Dispatch the command

Inject `IMessageDispatcher` and dispatch:

```csharp
public class OrdersController
{
    private readonly IMessageDispatcher _dispatcher;

    public OrdersController(IMessageDispatcher dispatcher)
        => _dispatcher = dispatcher;

    public Task Post(CreateOrder command)
        => _dispatcher.Dispatch(command);
}
```

## Core Concepts

### Messages

`IMessage` is the root marker interface. `ICommand` and `IEvent` both extend it. Queries use the separate `IQuery` / `IQuery<T>` markers.

### Commands

A `ICommand` is dispatched through `IMessageDispatcher` to a single `IMessageHandler<TCommand>`. During scanning, command handlers are registered with a *replace* strategy: when two scanned types handle the same command, the last one scanned is the registration that survives and the earlier one is displaced with no error and no log. Scan order is derived from assembly load order and the order an assembly defines its types, neither of which is specified. Dispatch then resolves that single surviving registration. Call `ThrowOnDuplicateCommandHandlers()` to fail composition instead.

```csharp
Task Dispatch<TMessage>(TMessage message) where TMessage : IMessage;
Task Dispatch<TMessage>(TMessage message, IMessageHandlerContext messageHandlerContext) where TMessage : IMessage;
```

### Queries and Read Models

A query implements `IQuery<TResult>`, where `TResult` is the read model returned. Query handlers implement `IQueryHandler<TQuery, TResult>` and are dispatched through `IQueryDispatcher`:

```csharp
public class GetOrderById : IQuery<OrderReadModel>
{
    public string OrderId { get; set; }
}

public class GetOrderByIdHandler : IQueryHandler<GetOrderById, OrderReadModel>
{
    public Task<OrderReadModel> Handle(GetOrderById query, IQueryHandlerContext context)
        => Task.FromResult(new OrderReadModel(/* ... */));
}
```

```csharp
public class OrdersService
{
    private readonly IQueryDispatcher _queries;
    public OrdersService(IQueryDispatcher queries) => _queries = queries;

    public Task<OrderReadModel> Get(string id)
        => _queries.Query(new GetOrderById { OrderId = id });
}
```

`IQueryDispatcher` offers overloads that take the query alone or with an explicit `IQueryHandlerContext`, and strongly-typed `Query<TQuery, TResult>` forms.

Query scanning discovers only closed handler types. An open-generic query handler — `class MyHandler<TQuery, TResult> : IQueryHandler<TQuery, TResult>` — is not discovered and must be registered manually after `AddChatterCqrs`:

```csharp
services.AddTransient(typeof(IQueryHandler<,>), typeof(MyHandler<,>));
```

#### Caching and retention

The invoker cache is a process-lifetime static with no eviction, and every entry strongly roots the caller-supplied query `Type`. Its entry count is bounded by the number of distinct `(runtime query type, result type)` pairs ever dispatched — not by traffic.

Do not dispatch a query type defined in an `AssemblyLoadContext` you intend to unload through `Query<TResult>(IQuery<TResult>)`. Dispatch it through `Query<TQuery, TResult>`, which never touches the cache, or accept that the load context will not unload. The hatch only reaches callers that can name `TQuery` at compile time; a reflective host holding an arbitrary `IQuery<TResult>` cannot use it.

Design rationale — and why the cache is left static and documented rather than weakened — is recorded in [ADR-0013](https://github.com/brenpike/Chatter/blob/master/docs/adr/0013-query-invoker-cache-process-lifetime-retention-documented-not-weakened.md).

### Events: Domain vs Integration

An `IEvent` is dispatched through the same `IMessageDispatcher` but is fanned out to **all** registered `IMessageHandler<TEvent>` handlers (event handlers are appended during scanning rather than replaced). Handlers are invoked sequentially, and the fan-out stops at the first handler that throws — see below.

- A **Domain Event** is handled in-process within the originating domain.
- An **Integration Event** is published outward to other services. Cross-service publishing requires broker infrastructure provided by the Chatter Message Brokers modules.

```csharp
public class OrderCreated : IEvent
{
    public string OrderId { get; set; }
}

public class SendConfirmationEmail : IMessageHandler<OrderCreated>
{
    public Task Handle(OrderCreated message, IMessageHandlerContext context)
        => /* ... */ Task.CompletedTask;
}
```

#### When a handler throws

Event handlers are not isolated from one another. What follows separates what the dispatcher itself does, which holds wherever it runs, from what the host around it settles.

**What the dispatcher does.** The first handler that throws ends the dispatch: the dispatcher logs the exception once and rethrows it **unchanged** — never wrapped, never joined to another handler's exception — and no subsequent handler is invoked. The dispatcher has no compensation step, and nothing it does undoes a handler that already ran. Handlers are invoked in the order their registrations were added to the `IServiceCollection`, and that invocation order is stable for the life of a process.

Where that order comes from is a registration concern rather than a dispatch one. An application that has not deliberately ordered its registrations gets assembly-scan order, which it did not choose and which can differ across builds. Ordering your own `Add*` calls relative to `AddChatterCqrs` moves a hand-registered handler, but re-registering a handler the assembly scan already found does not reposition it — event handler registration appends unconditionally, so the handler ends up registered twice and runs twice per event.

**What your host determines.** The dispatcher's guarantees end at the dispatch boundary: that it undoes nothing is a fact about the dispatcher, not a promise that an earlier handler's effects survive, and not a claim about what a later delivery does. Those are settled by whatever your host wraps around the dispatch, and the mechanisms named here are the ones Chatter itself ships rather than an exhaustive account of what a host can add.

*Whether earlier handlers' effects survive.* When the dispatch runs inside an ambient transaction, the throw leaves that transaction uncompleted and whatever those handlers enlisted in it is rolled back with the rest. A broker receiver running under `TransactionMode.FullAtomicityViaInfrastructure` does exactly that — it opens a `TransactionScope` around the delivery and completes it only after the dispatch returns and the message is acknowledged. Effects that never enlisted — an outbound HTTP call, a write to a store outside the transaction — stand either way.

*Which handlers are skipped on a given delivery.* Registration order fixes the order handlers are invoked in, not the set that ends up skipped: the skipped set is whatever follows the handler that actually threw, so it is decided per delivery. A handler whose failure is transient or conditional lets its siblings run on a later delivery, where a different handler may end the dispatch instead.

*What a redelivery re-runs.* A redelivered event re-runs the fan-out from the start, including handlers that already succeeded: the reliability inbox is command-scoped and does not deduplicate events. Event handlers must therefore be idempotent or roll back.

*How many times Chatter logs a failure.* The dispatcher's one `LogError` call is not always the only one Chatter makes for a failure. When the event arrived through a `BrokeredMessageReceiver`, the receiver logs the rethrown exception again before rethrowing it in turn, so the exception from a failed dispatch of a broker-delivered event is passed to `LogError` at least twice: once by `EventDispatcher` and at least once more by `BrokeredMessageReceiver`. When an event is dispatched directly through `IMessageDispatcher`, with no receiver around the dispatch, the dispatcher's one `LogError` call is the only one Chatter makes for that dispatch. These count the calls Chatter makes, not the records an application sees: whether a call produces a record, and how many, is decided by the log levels and logging providers the application configures.

*Whether a subscriber can run apart from its siblings.* Handlers are resolved from the service provider by event type, not by the delivery that triggered the dispatch. A second broker subscription or queue for the same event in the same host therefore does not isolate one subscriber: each delivery dispatches into the same handler set in the same order, stopping at the first handler that throws, so every delivery that reaches a sibling handler invokes it, duplicating its side effects across those deliveries. A separate delivery is necessary but not sufficient. A subscriber runs independently of its siblings only when it has its own delivery — its own broker subscription or queue — and is dispatched by a separate endpoint or host whose service provider registers that subscriber as the only handler for the event. Within one dispatch, a handler that must not be able to strand its siblings has to contain its own failures.

The decision to keep this behavior, the survey of comparable libraries behind it, and the cost it accepts are recorded in [ADR-0012](https://github.com/brenpike/Chatter/blob/master/docs/adr/0012-event-fan-out-failure-semantics.md).

### Message Context

Every dispatch carries a context object that flows alongside the message through the pipeline and into handlers:

- `IMessageHandlerContext` (concrete: `MessageHandlerContext`) is passed to message handlers and exposes a `CancellationToken`.
- `IQueryHandlerContext` (concrete: `QueryHandlerContext`) is passed to query handlers.

Both implement `IContainContext`, which exposes a `ContextContainer Container`. The container is an extensible, type-keyed bag for attaching arbitrary contextual data:

```csharp
public Task Handle(CreateOrder message, IMessageHandlerContext context)
{
    // store
    context.Container.Include(new TenantInfo("acme"));

    // retrieve
    var tenant = context.Container.Get<TenantInfo>();
    if (context.Container.TryGet<TenantInfo>(out var t)) { /* ... */ }

    // get-or-create helpers
    var settings = context.Container.GetOrNew<Settings>();

    return Task.CompletedTask;
}
```

A `ContextContainer` can be created with an inherited container, in which case lookups fall through to the parent. When you dispatch without supplying a context, `MessageDispatcher` creates a fresh `MessageHandlerContext` and seeds the container with the active `IMessageDispatcher` and `IExternalDispatcher`.

Lookups are type-checked: a value is found only when it is present under the key **and** assignable to `T`. A present value that is not assignable to `T` reads as `false` from `TryGet`, leaving the `out` parameter at `default(T)`, and throws `InvalidCastException` from `Get`, naming the key, the stored type and `T`; `Get` keeps throwing `KeyNotFoundException` when the key is absent, so the two failures stay distinguishable. A stored `null` is a **present** value when `T` is a reference or nullable type, and a mismatch when `T` is a non-nullable value type. A key present in the local container is answered from it whatever its type, so a local mismatch does not fall through to the inherited container.

`GetOrAdd`, `GetOrDefault` and `GetOrNew` all gate on `TryGet<T>`, so a value of another type stored under the `typeof(T).FullName` key is reported absent and is **overwritten** by the value each of them then creates and stores. `Include<T>(string, T)` will write any value under any string, including one equal to some `typeof(T).FullName`.

Why `TryGet` reports a mismatch while `Get` throws on one is recorded in [ADR-0018](https://github.com/brenpike/Chatter/blob/master/docs/adr/0018-context-container-lookup-is-type-checked.md).

#### Threading

A `ContextContainer` is **not** synchronized. `Include`, `Get`/`TryGet` and the `GetOrAdd`/`GetOrDefault`/`GetOrNew` helpers all read and mutate a plain dictionary, and the type takes no lock. Never use one container from two threads at the same time: concurrent use is undefined and can corrupt the underlying dictionary.

A dispatch that is handed an existing context reuses that context's container: `MessageDispatcher.Dispatch(message, context)` seeds the container it is given rather than creating a new one. `Chatter.MessageBrokers`' `context.InMemory()` is that path — it forwards the caller's own context straight through — so a handler that starts two `context.InMemory()` dispatches without awaiting the first before starting the second runs both against one container. Await every nested dispatch before starting the next. A nested dispatch against the caller's own container is expected and is safe once awaited — the hazard is simultaneity, not reuse.

Note also that `GetOrAdd` treats a stored `null` as a **present** value and returns it rather than invoking the factory; `GetOrNew<T>()` is the deliberate exception — it always returns an instance.

Design rationale — and why the container is left unsynchronized and documented rather than guarded — is recorded in [ADR-0011](https://github.com/brenpike/Chatter/blob/master/docs/adr/0011-context-container-unsynchronized-documented-not-guarded.md).

### Dispatch

`IMessageDispatcher` is the unified entry point for both commands and events. Internally it resolves the correct `IDispatchMessages` implementation (`CommandDispatcher` for `ICommand`, `EventDispatcher` for `IEvent`) via the `IMessageDispatcherProvider`, based on the message type. `IQueryDispatcher` handles queries separately.

The default in-memory dispatchers are registered automatically by `AddChatterCqrs` (and are also available via `AddInMemoryMessageDispatchers()` / `AddInMemoryQueryDispatcher()`).

## Command Pipeline

Commands can be wrapped in an ordered chain of cross-cutting **behaviors** — the equivalent of middleware for command handling (e.g. logging, validation, transactions). A behavior implements `ICommandBehavior<TMessage>`:

```csharp
using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.Pipeline;
using System.Threading.Tasks;

public class LoggingBehavior<TMessage> : ICommandBehavior<TMessage> where TMessage : ICommand
{
    public async Task Handle(TMessage message, IMessageHandlerContext context, CommandHandlerDelegate next)
    {
        // before the handler / next behavior
        await next();
        // after
    }
}
```

`next` is a `CommandHandlerDelegate` that invokes the next behavior in the chain, ending with the actual command handler. Behaviors execute in registration order (the pipeline composes them so the first-registered behavior is the outermost).

Add behaviors when registering, via the `CommandPipelineBuilder`:

```csharp
services.AddChatterCqrs(
    configuration,
    pipelineBuilder: pipeline =>
    {
        // open-generic behavior applied to ALL commands
        pipeline.WithBehavior(typeof(LoggingBehavior<>));
        // closed-generic behavior scoped to one command
        pipeline.WithBehavior(typeof(ValidateCreateOrderBehavior));
    },
    messageHandlerSourceBuilder: source => source.WithMarkerTypes(typeof(CreateOrderHandler)));
```

Under the hood the pipeline is `ICommandBehaviorPipeline<TMessage>` (default implementation `CommandBehaviorPipeline<TMessage>`). When a command is dispatched, `CommandDispatcher` runs the pipeline if one exists; otherwise it invokes the handler directly.

You can also register behaviors directly on the `IServiceCollection`:

```csharp
services.AddPipelineBehavior(typeof(LoggingBehavior<>)); // open generic → all commands
services.AddPipelineBehavior(typeof(ValidateCreateOrderBehavior)); // closed generic → one command
```

## Diagnostics (optional, opt-in)

Command and Event dispatch are instrumented with OpenTelemetry-compatible tracing and metrics. The instrumentation is **off until an application opts in**, and `Chatter.CQRS` takes **no dependency on any `OpenTelemetry.*` NuGet package** — it is built on the .NET base class library only: `System.Diagnostics.ActivitySource` for spans and `System.Diagnostics.Metrics.Meter` for instruments.

### Turning it on

The `ActivitySource` and the `Meter` are both named after the emitting assembly — **`Chatter.CQRS`**. Subscribe on your own OpenTelemetry provider with a prefix wildcard, or name the scopes exactly:

```csharp
services.AddOpenTelemetry()
        .WithTracing(t => t.AddSource("Chatter.*"))    // or .AddSource("Chatter.CQRS")
        .WithMetrics(m => m.AddMeter("Chatter.*"));    // or .AddMeter("Chatter.CQRS")
```

`Chatter.MessageBrokers` emits under its own separate scope, also named after its assembly, so the two modules can be sampled and filtered independently. The `"Chatter.*"` form above subscribes to both.

Any .NET `ActivityListener` / `MeterListener` works just as well — an OpenTelemetry provider merely subscribes to these base-class-library primitives, it is not a prerequisite for them.

### What is emitted

Every row below states when its signal is emitted: a blank condition cell is a defect, and `Always` is a positive claim that the emit site is unconditional, not a default. One row per facet — no comma-joined lists.

**Spans.** At most one span per Command dispatch and per Event dispatch.

<!-- FILL RULE: every row states when it is emitted; a blank condition cell is a defect; `Always` is a positive claim that the emit site is unconditional, not a default. One row per facet - no comma-joined lists. -->

| Span | Name | Kind | Started by | Started when |
| --- | --- | --- | --- | --- |
| `dispatch` | `dispatch ` followed by the **short** name of the compile-time `TMessage` type argument, not the runtime type — a variable declared `SubmitOrder` gives `dispatch SubmitOrder`, a variable declared `ICommand` gives `dispatch ICommand` | `ActivityKind.Internal` | `CommandDispatcher.Dispatch<TMessage>` and `EventDispatcher.Dispatch<TMessage>` | A .NET `ActivityListener` is attached to the `Chatter.CQRS` source **and** samples this dispatch; when it is not, no span exists and only the metric below can be recorded |

| Attribute | Span | Value | Emitted | Name origin |
| --- | --- | --- | --- | --- |
| `chatter.message.type` | `dispatch` | The **fully qualified** name of the compile-time `TMessage` type argument, not the runtime type — a variable declared `SubmitOrder` gives `Acme.Ordering.SubmitOrder` (span name `dispatch SubmitOrder`), a variable declared `ICommand` gives `Chatter.CQRS.Commands.ICommand` (span name `dispatch ICommand`) | Always — set unconditionally on every started span | Chatter-native |
| `chatter.dispatch.kind` | `dispatch` | `command` on the Command dispatch path, `event` on the Event dispatch path | Always — set unconditionally on every started span | Chatter-native |
| `error.type` | `dispatch` | The fully qualified exception type name | Failure only — when the dispatch threw | OpenTelemetry semantic convention |
| Status — the span's own status field, not a tag | `dispatch` | `Error`, with the exception's message as the status description | Failure only — when the dispatch threw | `Activity.SetStatus`, .NET base class library |

The `exception` event carries the same name **and the same default attribute values** on both target frameworks. Which code writes them still differs — on `net8.0` Chatter populates the attributes itself, on `net10.0` the .NET base class library's `Activity.AddException` does — so the table below states one canonical value per attribute rather than one value per target framework.

`exception.type` carries `Type.ToString()`, the spelling `Activity.AddException` writes, which does not assembly-qualify a generic type's arguments. `error.type` deliberately keeps `Type.FullName`, which does: that value is already identical on both target frameworks and is the one consumer dashboards key on, so moving it would be a larger telemetry break than the one being fixed. For a **generic** exception type the two attributes therefore carry different strings by design — `exception.type` reads ``Acme.Ordering.NotFound`1[System.String]`` where `error.type` reads ``Acme.Ordering.NotFound`1[[System.String, System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]]``. For a non-generic exception type they are identical.

One `net10.0`-only behaviour is **not** emulated and cannot be. On `net9.0` and later, `Activity.AddException` first notifies an `ActivityListener.ExceptionRecorder` callback registered by a subscribed listener and lets that callback supply or override the exception tags before the event is added; `net8.0` has no such API and this package does not grow one for it. A consumer that registers an `ExceptionRecorder` therefore still sees different behaviour per target framework even though the default attribute values now match — `net10.0` simply has the richer surface. Every value in the table below is therefore the **default**: what Chatter emits when no `ExceptionRecorder` supplies or overrides it. Register one and any of the three attributes can carry whatever that callback writes on `net10.0`, while `net8.0`, having no such callback, keeps the default.

| Event | Span | Attributes | Emitted |
| --- | --- | --- | --- |
| `exception` | `dispatch` | `exception.type` — the exception type rendered by `Type.ToString()`, the same **default** value on both target frameworks; generic type arguments are not assembly-qualified, so for a generic exception type this differs from `error.type` | Failure only, and only when the .NET `ActivityListener` requested all data (`Activity.IsAllDataRequested`) |
| `exception` | `dispatch` | `exception.message` — `Exception.Message`, the same **default** value on both target frameworks | Failure only, and only when the .NET `ActivityListener` requested all data (`Activity.IsAllDataRequested`) |
| `exception` | `dispatch` | `exception.stacktrace` — the stringified exception, carrying its type, message and stack trace, the same **default** value on both target frameworks | Failure only, and only when the .NET `ActivityListener` requested all data (`Activity.IsAllDataRequested`) |

**Metrics.**

| Instrument | Type | Unit | Advised buckets | Records | Recorded when |
| --- | --- | --- | --- | --- | --- |
| `chatter.cqrs.dispatch.duration` | `Histogram<double>` | `s` (seconds) | `0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10` — published as instrument advice on `net10.0` only; on `net8.0` the instrument carries none. See **Histogram bucket boundaries** below. | Elapsed time of one Command or Event dispatch, measured from before the span is started until the handler — or, for an Event, the last handler — returns or throws | Once per dispatch, on success and on failure alike, and only while the instrument itself is enabled on a .NET `MeterListener`; a dispatch that ran with diagnostics off records nothing |

| Attribute | Instruments | Value | Emitted |
| --- | --- | --- | --- |
| `chatter.message.type` | `chatter.cqrs.dispatch.duration` | The fully qualified name of the compile-time `TMessage` type argument — the same value the span attribute above carries | Always |
| `chatter.dispatch.kind` | `chatter.cqrs.dispatch.duration` | `command` on the Command dispatch path, `event` on the Event dispatch path | Always |
| `error.type` | `chatter.cqrs.dispatch.duration` | The fully qualified exception type name, resolved by the same code that sets the span's `error.type`, so the two signals cannot disagree | Failure only — when the dispatch threw |

Query dispatch is not instrumented.

### Histogram bucket boundaries

`chatter.cqrs.dispatch.duration` records **seconds**. The OpenTelemetry .NET SDK's default explicit histogram boundaries are millisecond-sized (`0, 5, 10, 25, ... 10000`), so a collector that applies them puts every realistic measurement in the first bucket and P50, P90 and P99 all report the same number forever. `Chatter.CQRS` therefore publishes seconds-sized bucket boundaries on the instrument itself. The boundary set is the one the OpenTelemetry messaging semantic conventions specify for duration histograms — borrowed for its shape, since no semantic convention covers in-process CQRS dispatch itself — and is listed in the Metrics table above.

**They are advice, not a setting.** The boundaries are published as instrument *advice* — a **default** that an application's own view **overrides**. An application that already registers a view for `chatter.cqrs.dispatch.duration` keeps winning exactly as it did before; nothing it configured changes. Advice is the right layer for this precisely because it cannot take that choice away from the application.

**Advice is published on `net10.0` only.** The base class library type that carries instrument advice does not exist in the `net8.0` shared framework, and this package takes no package dependency to reach it. On `net8.0` the instrument therefore ships with no advice at all, and the collector falls back to its own millisecond-sized defaults.

**On `net8.0`, configure the equivalent view in your own application.** `AddView` and `ExplicitBucketHistogramConfiguration` are `OpenTelemetry.Metrics` types that come from *your* application's OpenTelemetry packages — this package still takes **no dependency on any `OpenTelemetry.*` NuGet package**, and the snippet below adds none to it:

```csharp
using OpenTelemetry.Metrics;

services.AddOpenTelemetry()
        .WithMetrics(m => m
            .AddMeter("Chatter.CQRS")
            .AddView("chatter.cqrs.dispatch.duration", new ExplicitBucketHistogramConfiguration
            {
                Boundaries = new double[] { 0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10 }
            }));
```

The same view is harmless on `net10.0`: it overrides advice that already carries these boundaries. This `net8.0` caveat retires when `net8.0` is dropped and the package single-targets `net10.0` after .NET 8 reaches end of life on 2026-11-10 — tracked in [issue #395](https://github.com/brenpike/Chatter/issues/395).

### Off means off

**When nothing subscribes to the `Chatter.CQRS` source or meter, nothing is emitted and no work is done.** Every entry point checks whether Chatter's own source has a subscriber as its first statement and returns before a span name, a tag collection, or a timestamp is constructed, so no diagnostics work and no diagnostics allocation is added on the un-instrumented path. What this does NOT claim, as of 0.14.0: command dispatch is `async` on BOTH paths, because a command handler that faults after an `await` has to reach the dispatcher's `catch` to be logged at all. That state machine belongs to the dispatch itself rather than to the instrumentation — it is there whether or not you opt in — so it is not something the un-instrumented path avoids. ADR-0010 R4's "no async state machine" MECHANISM is withdrawn; the cost guarantee it existed to state is intact. The guarantee is per-dispatch: constructing the `ActivitySource` and `Meter` themselves is a one-time static initialization per process, which is unavoidable for any `ActivitySource`-based design.

The guard is Chatter's own subscriber check — never `Activity.Current`, which is non-null in any host running unrelated instrumentation and therefore does not mean Chatter diagnostics are on.

### Attribute names are data, not API

Attributes prefixed `chatter.` are Chatter-native: no OpenTelemetry semantic convention covers in-process CQRS dispatch. The remaining names (`error.type`, `exception.*`) are OpenTelemetry semantic conventions pinned to **v1.30.0**. Because telemetry attributes are emitted data rather than a compile-time type surface, **they may change in a minor release** when that pin advances. Dashboards and alert queries that hard-code attribute names should expect to be revisited on a pin bump; the bump is announced in this package's CHANGELOG.

Design rationale and the off-guard rules are recorded in [ADR-0010](https://github.com/brenpike/Chatter/blob/master/docs/adr/0010-optional-bcl-only-telemetry-per-assembly-sources-and-the-off-guard.md).

## Domain Language

Terms such as Command, Query, Read Model, Event (Domain vs Integration), Aggregate, Command Pipeline, Message Context, and Dispatcher follow the project's ubiquitous language. See [../CONTEXT.md](https://github.com/brenpike/Chatter/blob/master/src/Chatter.CQRS/CONTEXT.md) for the full glossary and relationships.

[← All Chatter modules](https://github.com/brenpike/Chatter/blob/master/README.md)
