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

## Domain Language

Terminology used throughout this module (Brokered Message, Receiver, Dispatcher, Router/Forwarder, Inbox/Outbox, Recovery, Circuit Breaker, Critical Failure, Error Queue, Max Receives Exceeded, Body Converter) is defined in the [domain glossary](../CONTEXT.md).

[← All Chatter modules](../../../README.md)
