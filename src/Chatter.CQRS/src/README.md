# Chatter.CQRS

[![NuGet](https://img.shields.io/nuget/v/Chatter.CQRS.svg)](https://www.nuget.org/packages/Chatter.CQRS)
[![Downloads](https://img.shields.io/nuget/dt/Chatter.CQRS.svg)](https://www.nuget.org/packages/Chatter.CQRS)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![CI](https://github.com/brenpike/Chatter/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/brenpike/Chatter/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/brenpike/Chatter/blob/master/LICENSE)

**In-process CQRS for .NET: dispatch Commands, Queries and Events to their handlers, with assembly-scanned registration and a composable Command Pipeline.**

Your application dispatches a message, and Chatter.CQRS routes it to the handler or handlers registered for it. A Command goes to one handler, a Query goes to one handler and returns a Read Model, and an Event fans out to every handler registered for it. Handlers are found by scanning your assemblies and are resolved from `Microsoft.Extensions.DependencyInjection`. Part of the [Chatter](https://github.com/brenpike/Chatter) suite.

## Contents

- [Features](#features)
- [Installation](#installation)
- [Quick start](#quick-start)
- [Registering handlers](#registering-handlers)
- [Commands](#commands)
- [Queries](#queries)
- [Events](#events)
- [Command Pipeline](#command-pipeline)
- [Message Context](#message-context)
- [Configuration](#configuration)
- [Diagnostics](#diagnostics)
- [Related packages](#related-packages)
- [Learn more](#learn-more)
- [License](#license)

## Features

- **Commands, Queries and Events**: `ICommand` goes to exactly one handler, `IQuery<TResult>` returns a Read Model from one handler, and `IEvent` fans out to zero or many handlers.
- **Assembly-scanned registration**: one `AddChatterCqrs` call finds your handlers by marker type, assembly or namespace selector. Handlers may be `public` or `internal`.
- **Command Pipeline**: wrap every Command, or one Command, in ordered behaviors such as logging, validation or transactions.
- **Message Context**: every dispatch carries a context with your `CancellationToken` and a type-keyed Context Container.
- **Caller-requested cancellation**: a dispatch you cancel is logged at `Debug` and is not reported as a failure.
- **Opt-in diagnostics**: spans and a duration histogram on the .NET base class library `ActivitySource` and `Meter`, with no dependency on any `OpenTelemetry.*` package.
- **Broker-ready**: the `IExternalDispatcher` seam is a no-op by default, and [Chatter.MessageBrokers](https://www.nuget.org/packages/Chatter.MessageBrokers) replaces it to send and publish Brokered Messages.

## Installation

```shell
dotnet add package Chatter.CQRS
```

Targets .NET 10 (`net10.0`).

Dependencies: Microsoft.Extensions.Configuration.Abstractions 10.0.0, Microsoft.Extensions.Logging.Abstractions 10.0.0, Microsoft.Extensions.Hosting 10.0.0, Scrutor 7.0.0.

Add [Chatter.MessageBrokers](https://www.nuget.org/packages/Chatter.MessageBrokers) and a transport package when Commands and Events must cross process boundaries.

## Quick start

The samples use `WebApplication.CreateBuilder(args)` (`builder.Services`, `builder.Configuration`) with implicit usings enabled. Any `IServiceCollection` with an `IConfiguration` works the same way.

### 1. Register Chatter.CQRS

In `Program.cs`:

```csharp
using Chatter.CQRS;
using Chatter.CQRS.Context;

var builder = WebApplication.CreateBuilder(args);

// Scans only the assembly that contains Program for handlers.
builder.Services.AddChatterCqrs(builder.Configuration, typeof(Program).Assembly);
```

`AddChatterCqrs` lives in the `Microsoft.Extensions.DependencyInjection` namespace, so it needs no extra `using`.

### 2. Define a Command and its handler

In its own file:

```csharp
using Chatter.CQRS;
using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;

public sealed record PlaceOrder(Guid OrderId, string CustomerId, decimal Total) : ICommand;

public sealed class PlaceOrderHandler : IMessageHandler<PlaceOrder>
{
    public Task Handle(PlaceOrder message, IMessageHandlerContext context)
    {
        // Change the state of the Order aggregate here.
        return Task.CompletedTask;
    }
}
```

### 3. Dispatch the Command

Back in `Program.cs`:

```csharp
var app = builder.Build();

app.MapPost("/orders", async (PlaceOrder command, IMessageDispatcher dispatcher, CancellationToken cancellationToken) =>
{
    await dispatcher.Dispatch(command, new MessageHandlerContext(cancellationToken));
    return Results.Accepted($"/orders/{command.OrderId}");
});

app.Run();
```

Passing a `MessageHandlerContext` built from the request token lets the handler observe cancellation. `dispatcher.Dispatch(command)` also works; it dispatches with a fresh context whose token is never signalled.

## Registering handlers

`AddChatterCqrs` scans a set of assemblies, registers every handler it finds, and registers the dispatchers. It returns an `IChatterBuilder` that exposes `Services`, `Configuration` and `AssemblySourceFilter`, which other Chatter modules use to extend the registration.

| Method | Description |
| --- | --- |
| `AddChatterCqrs(IConfiguration, Action<CommandPipelineBuilder> pipelineBuilder = null, Action<AssemblySourceFilterBuilder> messageHandlerSourceBuilder = null)` | Full form: configure the Command Pipeline and the assembly source filter. |
| `AddChatterCqrs(IConfiguration, Action<CommandPipelineBuilder> pipelineBuilder = null, params Type[] markerTypesForRequiredAssemblies)` | Configure the Command Pipeline and scan the assemblies that contain the marker types. |
| `AddChatterCqrs(IConfiguration, params Type[] markerTypesForRequiredAssemblies)` | Scan the assemblies that contain the marker types. |
| `AddChatterCqrs(IConfiguration, params Assembly[] handlerAssemblies)` | Scan the assemblies passed. |
| `AddChatterCqrs(IConfiguration, string handlerNamespaceSelector)` | Scan loaded assemblies that match a namespace selector. |

### Which assemblies are scanned

- `AddChatterCqrs(configuration)` with no marker types, no assemblies and no namespace selector scans every loaded, non-dynamic assembly in the current `AppDomain`.
- Marker types or explicit assemblies, with no namespace selector, scan only those assemblies.
- A namespace selector, alone or together with marker types or assemblies, widens the scan to every loaded assembly it matches, plus any explicit assemblies.

A namespace selector matches an assembly when the namespace of one of its types, or the assembly's full name, matches the pattern. The match is case-insensitive, and `*` matches any run of characters while `?` matches one.

```csharp
// Only the assemblies that contain these types.
builder.Services.AddChatterCqrs(builder.Configuration, typeof(PlaceOrderHandler), typeof(GetOrderHandler));

// Only the assemblies passed.
builder.Services.AddChatterCqrs(builder.Configuration, typeof(Program).Assembly);

// Every loaded assembly whose type namespaces or name match the selector.
builder.Services.AddChatterCqrs(builder.Configuration, "Contoso.Ordering.*");

// A marker type, widened by a selector that is built step by step.
builder.Services.AddChatterCqrs(
    builder.Configuration,
    messageHandlerSourceBuilder: source => source
        .WithMarkerTypes(typeof(PlaceOrderHandler))
        .WithNamespaceSelector(ns => ns.Append("Contoso.Ordering.").AppendWildcard()));
```

### What the scan registers

- Only closed handler types are scanned. A handler is registered only under the closed `IMessageHandler<TMessage>` or `IQueryHandler<TQuery, TResult>` interfaces it implements, never under any other interface the class implements.
- Command handlers replace one another, Event handlers append, and a second handler for the same Query makes the scan throw. [Commands](#duplicate-command-handlers) and [Events](#events) describe what that means for you.
- Handlers and behaviors are registered as transient. `IMessageDispatcher`, `IQueryDispatcher` and `IExternalDispatcher` are registered as scoped.

An open-generic handler is not found by the scan. Register it yourself after `AddChatterCqrs`:

```csharp
using Chatter.CQRS.Queries;

builder.Services.AddTransient(typeof(IQueryHandler<,>), typeof(CachingQueryHandler<,>));
```

Because the dispatchers are scoped, code that runs outside a request, such as a `BackgroundService`, resolves them from a scope it creates:

```csharp
using Chatter.CQRS;
using Chatter.CQRS.Context;

public sealed class OrderImportWorker(IServiceScopeFactory scopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IMessageDispatcher>();

        await dispatcher.Dispatch(
            new PlaceOrder(Guid.NewGuid(), "customer-42", 99.95m),
            new MessageHandlerContext(stoppingToken));
    }
}
```

## Commands

A Command implements `ICommand` and asks an aggregate to change state. Dispatch it through `IMessageDispatcher` to its single `IMessageHandler<TCommand>`.

| Method | Description |
| --- | --- |
| `Dispatch<TMessage>(TMessage message)` | Dispatches with a fresh `MessageHandlerContext` whose token is never signalled. |
| `Dispatch<TMessage>(TMessage message, IMessageHandlerContext messageHandlerContext)` | Dispatches with the context you supply. |

`IMessageDispatcher` handles both Commands and Events; it picks the Command or Event path from the message type. When a Command Pipeline is configured, the Command passes through its behaviors before it reaches the handler.

### Duplicate command handlers

The scan registers Command handlers with a replace strategy. When two scanned classes handle the same Command, the last one scanned wins, silently, and the scan order is not specified. To fail at startup instead, chain `ThrowOnDuplicateCommandHandlers()`:

```csharp
builder.Services
    .AddChatterCqrs(builder.Configuration, typeof(Program).Assembly)
    .ThrowOnDuplicateCommandHandlers();
```

It throws one `InvalidOperationException` that names every Command handled by more than one scanned handler, with all of the competing handler types.

- It is opt-in. No `AddChatterCqrs` overload calls it.
- Call it after your last `AddChatterCqrs` call; it checks the assemblies scanned by every `AddChatterCqrs` call made on the same service collection before it.
- It checks only scanned assemblies. A handler you register by hand, or that another module registers, is not compared.
- Event handlers are not checked, because every Event handler runs. Queries need no check, because a second Query handler already fails the scan.

## Queries

A Query implements `IQuery<TResult>`, where `TResult` is the Read Model it returns. Its handler implements `IQueryHandler<TQuery, TResult>`, and you dispatch it through `IQueryDispatcher`.

```csharp
using Chatter.CQRS.Context;
using Chatter.CQRS.Queries;

public sealed record OrderSummary(Guid OrderId, string Status, decimal Total);

public sealed record GetOrder(Guid OrderId) : IQuery<OrderSummary>;

public sealed class GetOrderHandler : IQueryHandler<GetOrder, OrderSummary>
{
    public Task<OrderSummary> Handle(GetOrder query, IQueryHandlerContext context)
        => Task.FromResult(new OrderSummary(query.OrderId, "Placed", 99.95m));
}
```

With `using Chatter.CQRS.Queries;` added to `Program.cs`:

```csharp
app.MapGet("/orders/{id:guid}", async (Guid id, IQueryDispatcher queries, CancellationToken cancellationToken) =>
{
    var summary = await queries.Query(new GetOrder(id), new QueryHandlerContext(cancellationToken));
    return Results.Ok(summary);
});
```

| Method | Description |
| --- | --- |
| `Query<TResult>(IQuery<TResult> query)` | Dispatches with a fresh `QueryHandlerContext`. `TResult` is inferred from the Query. |
| `Query<TResult>(IQuery<TResult> query, IQueryHandlerContext queryHandlerContext)` | Dispatches with the context you supply. |
| `Query<TQuery, TResult>(TQuery query)` | Strongly typed form; resolves `IQueryHandler<TQuery, TResult>` directly. |
| `Query<TQuery, TResult>(TQuery query, IQueryHandlerContext queryHandlerContext)` | Strongly typed form with the context you supply. |

A Query type must be a class. Two scanned handlers for the same `IQueryHandler<TQuery, TResult>` make `AddChatterCqrs` throw.

### Query types in unloadable assemblies

`Query<TResult>(IQuery<TResult>)` caches one invoker per distinct pair of runtime Query type and result type. The cache lives for the whole process, is never evicted, and holds a strong reference to each Query `Type`. Its size is bounded by the number of distinct pairs you dispatch, not by traffic.

If a Query type comes from a collectible `AssemblyLoadContext` that you intend to unload, dispatch it through `Query<TQuery, TResult>`, which does not use the cache. Otherwise that load context cannot unload.

## Events

An Event implements `IEvent` and announces that something happened. Dispatch it through the same `IMessageDispatcher`; every `IMessageHandler<TEvent>` registered for it runs.

```csharp
using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.CQRS.Events;

public sealed record OrderPlaced(Guid OrderId, string CustomerId) : IEvent;

public sealed class SendOrderConfirmation : IMessageHandler<OrderPlaced>
{
    public Task Handle(OrderPlaced message, IMessageHandlerContext context)
    {
        // Email the customer.
        return Task.CompletedTask;
    }
}

public sealed class ReserveStock : IMessageHandler<OrderPlaced>
{
    public Task Handle(OrderPlaced message, IMessageHandlerContext context)
    {
        // Reserve inventory for the order.
        return Task.CompletedTask;
    }
}
```

A Command handler can raise the Event through an injected `IMessageDispatcher`, passing its own context through so the Event handlers see the same cancellation token:

```csharp
public sealed class PlaceOrderHandler(IMessageDispatcher dispatcher) : IMessageHandler<PlaceOrder>
{
    public async Task Handle(PlaceOrder message, IMessageHandlerContext context)
    {
        // Change the state of the Order aggregate here, then announce it.
        await dispatcher.Dispatch(new OrderPlaced(message.OrderId, message.CustomerId), context);
    }
}
```

A Domain Event is handled in-process, inside the domain that raised it. An Integration Event is published to other services through `IExternalDispatcher`, which is a no-op until [Chatter.MessageBrokers](https://www.nuget.org/packages/Chatter.MessageBrokers) replaces it.

### When an event handler throws

Event handlers are not isolated from one another. The dispatcher guarantees the following:

- Handlers run one at a time, in the order their registrations were added to the `IServiceCollection`. For scanned handlers that is scan order, which you do not choose and which can differ between builds.
- The first handler that throws ends the dispatch. Its exception is rethrown unchanged, never wrapped, and later handlers are skipped.
- There is no compensation. Nothing undoes a handler that already ran.
- Registering a scanned handler again by hand does not move it; it registers the handler twice, and it runs twice.

Your host decides the rest:

- **Effects of earlier handlers**: inside an ambient `TransactionScope`, the throw rolls back work those handlers enlisted in it. A Brokered Message Receiver using `TransactionMode.FullAtomicityViaInfrastructure` opens that scope. Work that did not enlist, such as an HTTP call, stands.
- **Redelivery**: a redelivered Event runs every handler again, including those that succeeded. The Inbox deduplicates Commands, not Events, so Event handlers must be idempotent.
- **Logging**: `EventDispatcher` logs the failure once at `Error`. When the Event came through a Brokered Message Receiver, the receiver logs it again, so expect at least two `Error` entries. A cancellation you requested is logged at `Debug` instead.
- **Isolating a subscriber**: handlers are resolved by Event type, not by delivery, so a second queue or subscription in the same host still runs every sibling handler. A subscriber runs apart from its siblings only when it has its own delivery and a separate host whose only handler for the Event is that subscriber.

Within one dispatch, a handler that must not stop its siblings has to catch its own failures.

## Command Pipeline

A behavior implements `ICommandBehavior<TMessage>` and wraps Command handling. Call `next()` to run the next behavior, or the handler after the last behavior.

```csharp
using Chatter.CQRS.Commands;
using Chatter.CQRS.Context;
using Chatter.CQRS.Pipeline;
using Microsoft.Extensions.Logging;

public sealed class LoggingBehavior<TMessage>(ILogger<LoggingBehavior<TMessage>> logger)
    : ICommandBehavior<TMessage> where TMessage : ICommand
{
    public async Task Handle(TMessage message, IMessageHandlerContext messageHandlerContext, CommandHandlerDelegate next)
    {
        logger.LogInformation("Handling {Command}", typeof(TMessage).Name);
        await next();
        logger.LogInformation("Handled {Command}", typeof(TMessage).Name);
    }
}
```

Register behaviors through the `pipelineBuilder` argument of `AddChatterCqrs`:

```csharp
builder.Services.AddChatterCqrs(
    builder.Configuration,
    pipelineBuilder: p => p.WithBehavior(typeof(LoggingBehavior<>)),
    messageHandlerSourceBuilder: s => s.WithExplicitAssemblies(typeof(Program).Assembly));
```

- An open-generic behavior, such as `typeof(LoggingBehavior<>)`, applies to every Command.
- A behavior closed over one Command, such as `p.WithBehavior<ValidationBehavior<PlaceOrder>>()`, applies to that Command only. The class must have exactly one type parameter, the Command.
- The first-registered behavior is the outermost.
- Behaviors apply to Commands only. Events and Queries do not pass through the pipeline.

You can also register a behavior directly on the service collection:

```csharp
using Chatter.CQRS.DependencyInjection;

builder.Services.AddPipelineBehavior(typeof(LoggingBehavior<>));
builder.Services.AddPipelineBehavior(typeof(ValidationBehavior<PlaceOrder>));
```

A behavior is registered only under the `ICommandBehavior<TMessage>` interfaces it implements, or the open `ICommandBehavior<>` for an open-generic behavior.

## Message Context

Every dispatch carries a context that flows through the pipeline into the handler.

- `IMessageHandlerContext` (concrete `MessageHandlerContext`) is passed to Command and Event handlers.
- `IQueryHandlerContext` (concrete `QueryHandlerContext`) is passed to Query handlers.

Both expose a `CancellationToken` and a `ContextContainer Container`. Construct them with `new MessageHandlerContext(cancellationToken)` or `new QueryHandlerContext(cancellationToken)`.

### Context Container

The Context Container is a type-keyed bag of values. By default a value is stored under `typeof(T).FullName`.

```csharp
public Task Handle(PlaceOrder message, IMessageHandlerContext context)
{
    context.Container.Include(new TenantInfo("contoso"));

    var tenant = context.Container.Get<TenantInfo>();

    if (context.Container.TryGet<TenantInfo>(out var found))
    {
        // Use found.
    }

    var audit = context.Container.GetOrNew<AuditTrail>();
    return Task.CompletedTask;
}
```

| Method | Description |
| --- | --- |
| `Include<T>(T)` / `Include<T>(string, T)` | Stores a value under `typeof(T).FullName` or under the key you pass, replacing any value already there. |
| `Get<T>()` / `Get<T>(string)` | Returns the value. Throws `KeyNotFoundException` when absent and `InvalidCastException` when the stored value is not a `T`. |
| `TryGet<T>(out T)` / `TryGet<T>(string, out T)` | Returns `false` when absent or when the stored value is not a `T`. |
| `GetOrAdd<T>(Func<T>)` | Returns the value, or stores and returns the factory's result. |
| `GetOrDefault<T>()` | Returns the value, or stores and returns `default(T)`. |
| `GetOrNew<T>()` | Returns the value, or stores and returns `new T()`. Never returns `null`. |

- A stored `null` counts as present when `T` is a reference or nullable type, and as a mismatch when `T` is a non-nullable value type. `GetOrAdd` returns a stored `null` without calling the factory.
- `GetOrAdd`, `GetOrDefault` and `GetOrNew` treat a value of another type as absent and overwrite it.
- `new ContextContainer(inheritedContainer)` chains to a parent: a key missing locally is looked up in the parent. A key present locally is answered locally whatever its type, so a local mismatch does not fall through.
- A dispatch without a context gets a fresh `MessageHandlerContext`. The dispatcher seeds its container with the current `IMessageDispatcher` and `IExternalDispatcher`.

### Threading

A `ContextContainer` is not synchronized; it reads and writes a plain dictionary without a lock. Never use one container from two threads at the same time, because concurrent use is undefined and can corrupt it.

A dispatch that is given an existing context reuses that context's container. The `context.InMemory()` dispatch from Chatter.MessageBrokers passes your handler's own context through, so two nested dispatches started without awaiting the first share one container at once. Await each nested dispatch before starting the next; reuse is safe, simultaneous use is not.

### Caller-requested cancellation

A dispatch that ends in an `OperationCanceledException`, including a `TaskCanceledException`, while the token on the context you dispatched with is signalled, is cancelled, not failed. The Command, Event and Query dispatchers log it at `Debug` with the exception attached, rethrow the same exception, and do not record it as a failure in [Diagnostics](#diagnostics).

- A cancellation raised while that token is not signalled, such as an `HttpClient` timeout inside a handler, is still a failure.
- An `ObjectDisposedException` is always a failure, whatever the token says.
- A dispatch without a context has a token that is never signalled, so it never counts as cancelled. Dispatch with a context that carries your token.
- For a message delivered by a Brokered Message Receiver, the token is the receiver's shutdown token, so dispatches cut short by a clean shutdown are not reported as failed.

## Configuration

Chatter.CQRS reads no configuration keys. The `IConfiguration` you pass to `AddChatterCqrs` is carried on `IChatterBuilder` for other Chatter modules. You configure this package in code, through the builders below.

`AssemblySourceFilterBuilder` (the `messageHandlerSourceBuilder` argument):

| Method | Description |
| --- | --- |
| `WithMarkerTypes(params Type[] markerTypes)` | Adds the assemblies that contain the marker types. |
| `WithExplicitAssemblies(params Assembly[] assemblies)` | Adds the assemblies passed. |
| `WithNamespaceSelector(string namespaceSelector)` | Sets a `*` / `?` pattern matched against type namespaces and assembly names. Widens the scan to matching loaded assemblies. |
| `WithNamespaceSelector(Action<NamespaceSelectorBuilder> namespaceSelector)` | Builds the pattern with `Append`, `AppendWildcard` (`*`), `AppendSymbolWildcard` (`?`) and `AppendEscape` (`\`). |

`CommandPipelineBuilder` (the `pipelineBuilder` argument):

| Method | Description |
| --- | --- |
| `WithBehavior<TCommandBehavior>()` | Adds a behavior closed over one Command. |
| `WithBehavior(Type behaviorType)` | Adds an open-generic behavior for every Command, or a closed one for one Command. |

Extension methods:

| Method | Description |
| --- | --- |
| `IChatterBuilder.ThrowOnDuplicateCommandHandlers()` | Fails composition when a Command has more than one scanned handler. See [Duplicate command handlers](#duplicate-command-handlers). |
| `IServiceCollection.AddPipelineBehavior(Type behaviorType)` | Registers a behavior without the pipeline builder. Namespace `Chatter.CQRS.DependencyInjection`. |
| `IServiceCollection.AddInMemoryMessageDispatchers()` | Registers `IMessageDispatcher` and the in-memory Command and Event dispatchers. `AddChatterCqrs` calls it for you. |
| `IServiceCollection.AddInMemoryQueryDispatcher()` | Registers `IQueryDispatcher`. `AddChatterCqrs` calls it for you. |

## Diagnostics

Command and Event dispatch emit spans and a duration histogram. Nothing is emitted until your application subscribes. Chatter.CQRS takes no dependency on any `OpenTelemetry.*` package; it uses `System.Diagnostics.ActivitySource` and `System.Diagnostics.Metrics.Meter`.

### Enabling diagnostics

The `ActivitySource` and the `Meter` are both named `Chatter.CQRS` (`ChatterDiagnostics.ActivitySourceName` and `ChatterDiagnostics.MeterName`). With the OpenTelemetry .NET SDK in your application, subscribe by name or with a wildcard:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource("Chatter.*"))   // or "Chatter.CQRS"
    .WithMetrics(m => m.AddMeter("Chatter.*"));   // or "Chatter.CQRS"
```

Chatter.MessageBrokers emits under its own scope, so the two can be sampled and filtered separately; `"Chatter.*"` subscribes to both. Any .NET `ActivityListener` or .NET `MeterListener` works as well.

### Spans

At most one span per Command dispatch and per Event dispatch.

| Span | Name | Kind | Started by | Started when |
| --- | --- | --- | --- | --- |
| `dispatch` | `dispatch ` followed by the short name of the compile-time `TMessage` type argument, not the runtime type. A variable declared `PlaceOrder` gives `dispatch PlaceOrder`; a variable declared `ICommand` gives `dispatch ICommand`. | `ActivityKind.Internal` | `IMessageDispatcher.Dispatch` of a Command or an Event | A .NET `ActivityListener` is attached to the `Chatter.CQRS` source and samples this dispatch. Otherwise no span exists and only the metric can be recorded. |

| Attribute | Span | Value | Emitted | Name origin |
| --- | --- | --- | --- | --- |
| `chatter.message.type` | `dispatch` | The fully qualified name of the compile-time `TMessage` type argument, not the runtime type. A variable declared `PlaceOrder` gives `Contoso.Ordering.PlaceOrder`; a variable declared `ICommand` gives `Chatter.CQRS.Commands.ICommand`. | Always, on every started span | Chatter-native |
| `chatter.dispatch.kind` | `dispatch` | `command` on the Command path, `event` on the Event path | Always, on every started span | Chatter-native |
| `error.type` | `dispatch` | The fully qualified exception type name | Failure only: the dispatch threw and it was not a [caller-requested cancellation](#caller-requested-cancellation) | OpenTelemetry semantic convention |
| Status (the span's status field, not a tag) | `dispatch` | `Error`, with the exception message as the description | Failure only: the dispatch threw and it was not a caller-requested cancellation | `Activity.SetStatus`, .NET base class library |

The `exception` event is written by the base class library's `Activity.AddException`. An `ActivityListener.ExceptionRecorder` callback on a subscribed .NET `ActivityListener` can supply or override its tags, so the values below are the defaults.

| Event | Span | Attributes | Emitted |
| --- | --- | --- | --- |
| `exception` | `dispatch` | `exception.type`: the exception type rendered by `Type.ToString()` | Failure only, and only when the .NET `ActivityListener` requested all data (`Activity.IsAllDataRequested`) |
| `exception` | `dispatch` | `exception.message`: `Exception.Message` | Failure only, and only when the .NET `ActivityListener` requested all data (`Activity.IsAllDataRequested`) |
| `exception` | `dispatch` | `exception.stacktrace`: the stringified exception, with its type, message and stack trace | Failure only, and only when the .NET `ActivityListener` requested all data (`Activity.IsAllDataRequested`) |

`exception.type` uses `Type.ToString()`, while `error.type` uses `Type.FullName`. For a generic exception type they differ: `exception.type` reads ``Contoso.Ordering.OrderNotFound`1[System.String]`` where `error.type` reads ``Contoso.Ordering.OrderNotFound`1[[System.String, System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]]``. For a non-generic exception type they are identical.

### Metrics

| Instrument | Type | Unit | Advised buckets | Records | Recorded when |
| --- | --- | --- | --- | --- | --- |
| `chatter.cqrs.dispatch.duration` | `Histogram<double>` | `s` (seconds) | `0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10`; see [Histogram bucket boundaries](#histogram-bucket-boundaries) | Elapsed time of one Command or Event dispatch, from before the span starts until the handler (for an Event, the last handler) returns or throws | Once per dispatch, on success and on failure, and only while the instrument is enabled on a .NET `MeterListener`. A dispatch that ran with diagnostics off records nothing. |

| Attribute | Instruments | Value | Emitted |
| --- | --- | --- | --- |
| `chatter.message.type` | `chatter.cqrs.dispatch.duration` | The fully qualified name of the compile-time `TMessage` type argument, the same value as the span attribute | Always |
| `chatter.dispatch.kind` | `chatter.cqrs.dispatch.duration` | `command` on the Command path, `event` on the Event path | Always |
| `error.type` | `chatter.cqrs.dispatch.duration` | The fully qualified exception type name, resolved by the same code as the span's `error.type` | Failure only: the dispatch threw and it was not a caller-requested cancellation |

A caller-requested cancellation leaves the span status `Unset`, adds no `error.type` and no `exception` event, and still records `chatter.cqrs.dispatch.duration` once, without `error.type`. An alert on `error.type` equal to `System.OperationCanceledException` or `System.Threading.Tasks.TaskCanceledException` does not see these dispatches.

Query dispatch is not instrumented.

### Histogram bucket boundaries

`chatter.cqrs.dispatch.duration` records seconds, and the instrument publishes seconds-sized bucket advice:

`0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10`

This is the boundary set the OpenTelemetry messaging semantic conventions specify for duration histograms. Without it, a collector applying the SDK's millisecond-sized defaults (`0, 5, 10, 25, ... 10000`) puts every realistic measurement in the first bucket.

The boundaries are advice, not a setting: a view registered by your application overrides them. To choose other boundaries, register a view with your own OpenTelemetry packages; this snippet adds no OpenTelemetry dependency to Chatter.CQRS:

```csharp
using OpenTelemetry.Metrics;

builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m
        .AddMeter("Chatter.CQRS")
        .AddView("chatter.cqrs.dispatch.duration", new ExplicitBucketHistogramConfiguration
        {
            Boundaries = new double[] { 0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10 }
        }));
```

### Off means off

When nothing subscribes to the `Chatter.CQRS` source or meter, nothing is emitted. Each dispatch first checks whether Chatter's own source or instrument has a subscriber, and returns to the plain path before building a span name, tags or a timestamp. The check is Chatter's own subscriber check, never `Activity.Current`, which is non-null in any host running unrelated instrumentation.

The guarantee is per dispatch. Creating the `ActivitySource` and `Meter` is a one-time static initialization per process. Command and Event dispatch are `async` whether diagnostics are on or off, because the dispatcher must await a faulting handler to log it.

### Attribute names are data, not API

Attributes prefixed `chatter.` are Chatter-native, because no OpenTelemetry semantic convention covers in-process CQRS dispatch. `error.type` and `exception.*` follow the OpenTelemetry semantic conventions pinned to v1.30.0. Telemetry attributes are emitted data, not a compile-time API, so they may change in a minor release when that pin advances. Each change is announced in this package's CHANGELOG, so review dashboards and alerts that hard-code attribute names when you upgrade.

## Related packages

- [Chatter.MessageBrokers](https://www.nuget.org/packages/Chatter.MessageBrokers): Brokered Message Receivers, sending and publishing, routing, Inbox/Outbox reliability and Recovery. Chain `.AddMessageBrokers()` after `AddChatterCqrs`.
- [Chatter.MessageBrokers.AzureServiceBus](https://www.nuget.org/packages/Chatter.MessageBrokers.AzureServiceBus): Azure Service Bus transport.
- [Chatter.MessageBrokers.RabbitMQ](https://www.nuget.org/packages/Chatter.MessageBrokers.RabbitMQ): RabbitMQ transport.
- [Chatter.MessageBrokers.SqlServiceBroker](https://www.nuget.org/packages/Chatter.MessageBrokers.SqlServiceBroker): SQL Server Service Broker transport.
- [Chatter.MessageBrokers.Reliability.EntityFramework](https://www.nuget.org/packages/Chatter.MessageBrokers.Reliability.EntityFramework): EF Core Inbox, Outbox and Unit of Work.
- [Chatter.MessageBrokers.Reliability.Cosmos](https://www.nuget.org/packages/Chatter.MessageBrokers.Reliability.Cosmos): Azure Cosmos DB reliability.
- [Chatter.SqlChangeFeed](https://www.nuget.org/packages/Chatter.SqlChangeFeed): Typed notifications from a watched SQL Server table.

## Learn more

- [Domain glossary (CONTEXT.md)](https://github.com/brenpike/Chatter/blob/master/src/Chatter.CQRS/CONTEXT.md)
- [Changelog](https://github.com/brenpike/Chatter/blob/master/src/Chatter.CQRS/src/Chatter.CQRS/CHANGELOG.md)
- [Context map of all Chatter modules](https://github.com/brenpike/Chatter/blob/master/CONTEXT-MAP.md)
- [Chatter suite README](https://github.com/brenpike/Chatter/blob/master/README.md)

## License

Licensed under the [MIT License](https://github.com/brenpike/Chatter/blob/master/LICENSE).
