# <a name="chatter-cqrs"></a> Chatter.CQRS

A lightweight CQRS framework for .NET that dispatches Commands, Queries, and Events to their handlers via the mediator pattern.

## Overview

Chatter.CQRS is the foundational module of the Chatter library suite. It implements **Command Query Responsibility Segregation (CQRS)** using an in-process **mediator** pattern: instead of calling handler classes directly, your code dispatches messages and the framework routes each one to the registered handler(s).

The module distinguishes three kinds of message, all of which derive from the `IMessage` marker interface:

- **Commands** (`ICommand`) — instruct an aggregate to change state; dispatched to exactly **one** handler.
- **Queries** (`IQuery<T>`) — retrieve a read model without mutating state; dispatched to exactly one handler that returns a result.
- **Events** (`IEvent`) — announce that something happened; fanned out to **zero or many** handlers.

Handlers are discovered automatically by assembly scanning (powered by [Scrutor](https://github.com/khellang/Scrutor)) and registered into the standard `Microsoft.Extensions.DependencyInjection` container. Commands additionally flow through an optional **command pipeline** of cross-cutting behaviors.

## Installation

```bash
dotnet add package Chatter.CQRS
```

## Getting Started

### 1. Register Chatter.CQRS with DI

The entry point is the `AddChatterCqrs` extension method on `IServiceCollection`. It scans the supplied assemblies for handlers and wires up the dispatchers. Several overloads control how handler assemblies are located:

```csharp
using Microsoft.Extensions.DependencyInjection;

// Locate handler assemblies via marker types (the assembly each type lives in is scanned)
services.AddChatterCqrs(configuration, typeof(CreateOrderHandler), typeof(SomeOtherHandler));

// ...or pass explicit assemblies
services.AddChatterCqrs(configuration, typeof(Program).Assembly);

// ...or select assemblies by namespace/assembly-name with '*' and '?' wildcards
services.AddChatterCqrs(configuration, "MyApp.*");

// ...or use the full builder form with a command pipeline + assembly source filter
services.AddChatterCqrs(
    configuration,
    pipelineBuilder: pipeline => pipeline.WithBehavior(typeof(LoggingBehavior<>)),
    messageHandlerSourceBuilder: source => source.WithMarkerTypes(typeof(CreateOrderHandler)));
```

`AddChatterCqrs` returns an `IChatterBuilder`, which exposes the `Services`, `Configuration`, and `AssemblySourceFilter` used by other Chatter modules (such as the message brokers) to extend the registration.

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

A `ICommand` is dispatched through `IMessageDispatcher` to a single `IMessageHandler<TCommand>`. During scanning, command handlers are registered with a *replace* strategy, enforcing that a command resolves to exactly one handler.

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

### Events: Domain vs Integration

An `IEvent` is dispatched through the same `IMessageDispatcher` but is fanned out to **all** registered `IMessageHandler<TEvent>` handlers (event handlers are appended during scanning rather than replaced). Handlers are invoked sequentially.

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

## Domain Language

Terms such as Command, Query, Read Model, Event (Domain vs Integration), Aggregate, Command Pipeline, Message Context, and Dispatcher follow the project's ubiquitous language. See [../CONTEXT.md](../CONTEXT.md) for the full glossary and relationships.

[← All Chatter modules](../../../README.md)
