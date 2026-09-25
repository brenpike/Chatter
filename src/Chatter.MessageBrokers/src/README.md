# Chatter.MessageBrokers

[![NuGet](https://img.shields.io/nuget/v/Chatter.MessageBrokers.svg)](https://www.nuget.org/packages/Chatter.MessageBrokers)
[![Downloads](https://img.shields.io/nuget/dt/Chatter.MessageBrokers.svg)](https://www.nuget.org/packages/Chatter.MessageBrokers)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![CI](https://github.com/brenpike/Chatter/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/brenpike/Chatter/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/brenpike/Chatter/blob/master/LICENSE)

**Technology-agnostic brokered messaging built on Chatter.CQRS: Brokered Message Receivers, sending and publishing, routing, Inbox/Outbox reliability and Recovery.**

This package receives messages from a message broker and dispatches them to your existing Chatter.CQRS command and event handlers. It also sends, publishes and forwards messages back out, so your domain code never depends on a specific broker. It defines the abstractions and orchestration only; a transport package (Azure Service Bus, RabbitMQ or SQL Server Service Broker) supplies the wire. Part of the [Chatter](https://github.com/brenpike/Chatter) suite.

## Contents

- [Features](#features)
- [Installation](#installation)
- [Quick start](#quick-start)
- [Receiving](#receiving)
- [Sending and publishing](#sending-and-publishing)
- [Routing](#routing)
- [Serialization](#serialization)
- [Reliability](#reliability)
- [Recovery](#recovery)
- [Configuration](#configuration)
- [Diagnostics](#diagnostics)
- [Related packages](#related-packages)
- [Learn more](#learn-more)
- [License](#license)

## Features

- **Brokered Message Receivers**: mark a command or event with `[BrokeredMessage]` and a receiver starts as a hosted background service, one per message type.
- **Sending and publishing**: `IBrokeredMessageDispatcher` and handler-context extensions send commands and publish events without broker-specific code.
- **Transport independence**: the same code runs over Azure Service Bus, RabbitMQ or SQL Server Service Broker, and one application can use several at once.
- **Routing slips**: a message can carry its own itinerary of destinations and advance through it step by step.
- **Outbox**: route outgoing messages through a store and drain it with a background poller, with dispatch backoff and an optional attempt ceiling.
- **Inbox**: skip redelivered messages by message id so handlers run once per deduplication window.
- **Recovery**: retry with no, constant or exponential delay, a circuit breaker, an Error Queue for messages past their receive limit, and deadlettering for poisoned bodies.
- **Configuration binding**: every option can come from `appsettings.json`, and bad values are refused at startup with a named exception.
- **Opt-in diagnostics**: OpenTelemetry-compatible spans and metrics plus W3C trace context propagation, with no `OpenTelemetry.*` dependency.

## Installation

```shell
dotnet add package Chatter.MessageBrokers
```

Targets .NET 10 (`net10.0`).

Dependencies: Chatter.CQRS, Microsoft.Extensions.Configuration.Abstractions 10.0.0, Microsoft.Extensions.Hosting 10.0.0.

This package ships no transport. Add one of these:

```shell
dotnet add package Chatter.MessageBrokers.AzureServiceBus
dotnet add package Chatter.MessageBrokers.RabbitMQ
dotnet add package Chatter.MessageBrokers.SqlServiceBroker
```

For a durable Inbox and Outbox, add [Chatter.MessageBrokers.Reliability.EntityFramework](https://www.nuget.org/packages/Chatter.MessageBrokers.Reliability.EntityFramework) or [Chatter.MessageBrokers.Reliability.Cosmos](https://www.nuget.org/packages/Chatter.MessageBrokers.Reliability.Cosmos).

## Quick start

The samples use `WebApplication.CreateBuilder(args)` (`builder.Services`, `builder.Configuration`) with implicit usings enabled. Any `IServiceCollection` with an `IConfiguration` works the same way.

### 1. Register Chatter, the message brokers and a transport

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddChatterCqrs(builder.Configuration, typeof(Program).Assembly)
    .AddMessageBrokers()
    .AddAzureServiceBus(asb => asb.WithConnectionString(builder.Configuration.GetConnectionString("ServiceBus")));
```

`AddMessageBrokers` extends the `IChatterBuilder` that `AddChatterCqrs` returns. Any transport works in place of `AddAzureServiceBus`; see that transport's README.

### 2. Map a message to broker paths

```csharp
using Chatter.CQRS.Commands;
using Chatter.CQRS.Events;
using Chatter.MessageBrokers;

[BrokeredMessage(sendingPath: "orders", receivingPath: "orders")]
public class PlaceOrder : ICommand
{
    public Guid OrderId { get; set; }
    public string Sku { get; set; }
}

public class OrderPlaced : IEvent
{
    public Guid OrderId { get; set; }
}
```

Because `receivingPath` is set, `AddMessageBrokers` starts a Brokered Message Receiver for `PlaceOrder` on the `orders` queue.

### 3. Handle it and publish an event

```csharp
using Chatter.CQRS;
using Chatter.CQRS.Context;

public class PlaceOrderHandler : IMessageHandler<PlaceOrder>
{
    public async Task Handle(PlaceOrder message, IMessageHandlerContext context)
    {
        // save the order ...

        await context.Publish(new OrderPlaced { OrderId = message.OrderId }, "order-events");
    }
}
```

The receiver deserializes the body and dispatches it to your handler exactly as an in-process dispatch would.

### 4. Send from your API

```csharp
using Chatter.MessageBrokers.Sending;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("orders")]
public class OrdersController : ControllerBase
{
    private readonly IBrokeredMessageDispatcher _dispatcher;

    public OrdersController(IBrokeredMessageDispatcher dispatcher) => _dispatcher = dispatcher;

    [HttpPost]
    public async Task<IActionResult> Place(PlaceOrder command)
    {
        await _dispatcher.Send(command); // destination comes from [BrokeredMessage] sendingPath
        return Accepted();
    }
}
```

## Receiving

### What AddMessageBrokers registers

`AddMessageBrokers` registers:

- a Brokered Message Receiver for every `[BrokeredMessage]` type that sets `receivingPath`
- `IBrokeredMessageDispatcher`, which replaces the no-op `IExternalDispatcher` from Chatter.CQRS
- the routers, the Recovery strategy and the Error Queue action
- an in-memory Inbox and Outbox, each a process-lifetime singleton
- the JSON and plain-text Body Converters

It scans the assemblies you gave `AddChatterCqrs` unless you pass your own:

| Overload | Receiver assemblies |
| --- | --- |
| `AddMessageBrokers(options => { })` | the assemblies `AddChatterCqrs` scanned |
| `AddMessageBrokers(options => { }, typeof(PlaceOrder))` | the assemblies containing the marker types |
| `AddMessageBrokers(options => { }, typeof(PlaceOrder).Assembly)` | the assemblies listed |
| `AddMessageBrokers("MyCompany.Orders.*", options => { })` | assemblies matched by the namespace selector |
| `AddMessageBrokers(options => { }, source => { })` | an `AssemblySourceFilterBuilder` you configure |

The options delegate is optional on every overload.

### The BrokeredMessage attribute

```csharp
[BrokeredMessage(sendingPath: "orders",
                 receivingPath: "orders",
                 errorQueueName: "orders-errors",
                 messageDescription: "Place an order",
                 infrastructureType: "",
                 deadletterQueueName: "orders-deadletter")]
public class PlaceOrder : ICommand { /* ... */ }
```

| Parameter | Description |
| --- | --- |
| `sendingPath` | Destination used when you send or publish without naming one. |
| `receivingPath` | When set, a receiver starts for this type on this path. |
| `errorQueueName` | Where a message goes once it exceeds its receive limit. |
| `messageDescription` | Free-text description of the message. |
| `infrastructureType` | Which transport the receiver uses. Empty means the default transport. |
| `deadletterQueueName` | Deadletter destination, for transports that use one. |

You must supply `sendingPath` or `receivingPath`; the attribute throws `ArgumentException` when both are blank.

### How a receiver runs

Each receiver runs inside its own `IHostedService`, and only one receiver instance runs per message type. For each delivery it deserializes the body, dispatches to your handler in a fresh DI scope under Recovery, then settles the delivery. A handler that completes acknowledges the message. A failing handler leaves the message for redelivery until its delivery count reaches `MaxReceiveAttempts`, and then it is deadlettered and sent to the Error Queue (see [Recovery](#recovery)).

Every receiver carries `ReceiverOptions`:

| Option | Type | Default | Description |
| --- | --- | --- | --- |
| `MaxReceiveAttempts` | `int` | `10` | Deliveries allowed before the message is deadlettered and sent to the Error Queue. |
| `MaxConcurrentCalls` | `int` | `1` | Deliveries handled at once. A value below `1` fails when the receiver starts. |
| `TransactionMode` | `TransactionMode?` | — | Unset means the `MessageBrokerOptions.TransactionMode` value applies. |

`TransactionMode` is one of `None`, `ReceiveOnly` (the default) or `FullAtomicityViaInfrastructure`. Transport packages expose further receiver tuning, such as concurrency; see each transport's README.

### Registering a receiver without the attribute

Register a receiver in code when the message type lives in an assembly you cannot decorate, or when you need a different receive limit:

```csharp
using Chatter.MessageBrokers.Receiving;

builder.Services.AddChatterCqrs(builder.Configuration, typeof(Program).Assembly)
    .AddMessageBrokers(options => options
        .AddReceiver<PlaceOrder>("orders",
                                 errorQueuePath: "orders-errors",
                                 transactionMode: TransactionMode.ReceiveOnly,
                                 maxReceiveAttempts: 5));
```

`AddReceiver<TMessage>` also accepts `description`, `senderPath`, `infrastructureType` and `deadletterQueuePath`. It throws `InvalidOperationException` if the type already carries `[BrokeredMessage]`, because that type's receiver is already registered.

### Reading the inbound message

Inside a handler invoked by a receiver, the context is an `IMessageBrokerContext`:

```csharp
using Chatter.CQRS.Context;

var inbound = context.GetInboundBrokeredMessage();      // null outside a received message
var messageId = inbound?.MessageId;
var correlationId = inbound?.CorrelationId;
var transaction = context.GetTransactionContext();
```

### Inbound header trust

Chatter does not authenticate an inbound message's Message Context. The receiving transport overwrites a few entries with values only it knows, such as which transport the message arrived on, but every other entry is whatever the sender wrote: the correlation id, subject, group id, reply-to address, Routing Slip, trace context and your own entries alike. Use these values for correlation and diagnostics, never as an authorization input or as proof of who sent the message. Grant send rights on the queues, topics and services you receive from only to principals you already trust to invoke your handlers.

Inbound values also travel onward. `context.Send`, `context.Publish` and every `IBrokeredMessageDispatcher` overload that takes an `IMessageHandlerContext` copy the entire inbound Message Context onto each outbound message, and options you pass override inherited entries key by key. `Forward` and `IReplyRouter` re-send the inbound message itself, carrying its Message Context. A dispatcher call that passes a `TransactionContext` or no context carries only the options you pass, and so does a Routing Slip send, plus the slip.

## Sending and publishing

`IBrokeredMessageDispatcher` is the single outbound surface. It combines `IBrokeredMessageSender` (`Send` a command), `IBrokeredMessagePublisher` (`Publish` one event or a batch) and `IBrokeredMessageForwarder` (`Forward` a received message to a new destination). Inject it anywhere:

```csharp
await dispatcher.Send(new PlaceOrder { OrderId = id });                 // path from [BrokeredMessage]
await dispatcher.Send(new PlaceOrder { OrderId = id }, "orders-priority");
await dispatcher.Publish(new OrderPlaced { OrderId = id }, "order-events");
await dispatcher.Publish(new[] { firstEvent, secondEvent });           // batch: each path from [BrokeredMessage]
```

Without a destination, the message type's `sendingPath` is used; if there is none, the call throws `ArgumentNullException`. A batch publish always takes each message's path from its attribute.

### From a handler

Handlers use extensions on `IMessageHandlerContext` (namespace `Chatter.CQRS.Context`). They send over the same transaction context as the delivery being handled:

```csharp
await context.Send(new ChargePayment { OrderId = message.OrderId }, "payments");
await context.Publish(new OrderPlaced { OrderId = message.OrderId }, "order-events");
await context.InMemory().Dispatch(new ReserveStock { OrderId = message.OrderId });
```

`context.Send` and `context.Publish` copy the entire inbound Message Context onto each outbound message, and options you pass win over inherited entries; see [Inbound header trust](#inbound-header-trust). They do nothing when the context holds no brokered dispatcher, which happens only if `AddMessageBrokers` was not called. `context.InMemory().Dispatch(...)` dispatches in-process on the caller's own Message Context, so await each nested dispatch before starting the next.

### Send options

```csharp
using Chatter.MessageBrokers.Routing.Options;

var options = new SendOptions { MessageId = order.Id.ToString() }
    .WithSubject("place-order")
    .WithGroupId(order.CustomerId.ToString())
    .WithTimeToLiveInMinutes(30);
options.SetCorrelationId(correlationId);
options.SetReplyToAddress("order-replies");
options.WithMessageContext("tenant", tenantId);

await dispatcher.Send(command, options: options);
```

| Method | Description |
| --- | --- |
| `MessageId` | Sets the message id. When unset, the registered message id generator creates one. |
| `ContentType` | Selects the Body Converter. Defaults to `application/json`. |
| `SetCorrelationId(string)` | Sets the correlation id carried with the message. |
| `WithSubject(string)` | Sets the subject (`SendOptions` only). |
| `WithGroupId(string)` | Sets the group id, used for sessions where the transport supports them (`SendOptions` only). |
| `WithTimeToLiveInMinutes(int)` | Sets a time to live (`SendOptions` only). |
| `SetReplyToAddress(string)` / `SetReplyToGroupId(string)` | Sets reply routing (`SendOptions` only). |
| `WithMessageContext(string, object)` | Adds a custom Message Context entry. |
| `UseMessagingInfrastructure(Func<InfrastructureTypes, string>)` | Chooses the transport for this send. |

`PublishOptions` supports the shared members (`MessageId`, `ContentType`, `SetCorrelationId`, `WithMessageContext`, `UseMessagingInfrastructure`).

### Choosing a transport

With more than one transport registered, the first one registered is the default. Pick another per send:

```csharp
var options = new SendOptions();
options.UseMessagingInfrastructure(t => t.RabbitMq());
await dispatcher.Send(command, "billing", options: options);
```

Each transport package adds an extension on `InfrastructureTypes`: `AzureServiceBus()`, `RabbitMq()` or `SqlServiceBroker()`. Inside a handler, `context.AzureServiceBus()`, `context.RabbitMq()` and `context.SqlServiceBroker()` return the context bound to that transport. They return null outside a handler invoked by a Brokered Message Receiver.

### Message ids

The default generator creates a random GUID. Choose another on the options builder:

```csharp
using Chatter.MessageBrokers.Configuration;

builder.Services.AddChatterCqrs(builder.Configuration, typeof(Program).Assembly)
    .AddMessageBrokers(options => options.UseHashedBodyGuidMessageIdGenerator());
```

| Method | Description |
| --- | --- |
| `UseGuidMessageIdGenerator()` | A random GUID (the default). |
| `UseCombGuidMessageIdGenerator()` | A sequential "comb" GUID, which indexes well in relational stores. |
| `UseHashedBodyGuidMessageIdGenerator()` | A GUID derived from a SHA-256 hash of the body, so identical bodies get the same id. |

## Routing

### Routing slips

A Routing Slip is an itinerary the message carries with it. Each step is a receiving path. The service at each step receives the same command, runs its handler, and then `RoutingSlipBehavior` sends the command on to the next step.

Add the behavior to the Command Pipeline in every service on the route:

```csharp
builder.Services.AddChatterCqrs(builder.Configuration,
        pipeline => pipeline.WithRoutingSlipBehavior(),
        typeof(Program))
    .AddMessageBrokers()
    .AddAzureServiceBus(asb => asb.WithConnectionString(builder.Configuration.GetConnectionString("ServiceBus")));
```

Build a slip and send the command to its first step:

```csharp
using Chatter.MessageBrokers.Routing.Slips;

var slip = RoutingSlipBuilder.NewRoutingSlip(Guid.NewGuid())
    .WithRoute("validate-order")
    .WithRoute("charge-payment")
    .WithRoute("ship-order")
    .Build();

await dispatcher.Send(new PlaceOrder { OrderId = id }, slip);
```

Inside a handler, `context.Send(command, slip)` does the same, `context.Forward(slip)` forwards the inbound message to the slip's next step, and `context.TryGetRoutingSlip(out var slip)` reads the slip and its `Attachments`. `SendOptions.WithRoutingSlip(slip)` attaches a slip to options you already have.

`RoutingSlipBehavior` follows the slip an inbound message carries exactly as the sender wrote it, and `context.TryGetRoutingSlip` returns that inbound slip ahead of any slip your application added. The behavior sends the handled command to the step that slip names next, using your application's own broker credentials. Add it only in services whose queues accept messages solely from senders you trust to choose that destination; see [Inbound header trust](#inbound-header-trust).

### Forwarding and replies

`Forward` re-sends a received message to a new destination unchanged; forwarding is a specialization of routing. `SetReplyToAddress` and `SetReplyToGroupId` carry reply routing with a message. On the receiving side, `IReplyRouter` routes a reply for an inbound message over the transport that message arrived on.

## Serialization

Body Converters read and write message bodies. JSON bodies use one shared `System.Text.Json` configuration inside this package. To produce bytes the library reads back identically, or to read bytes it wrote, use `ChatterJson`:

```csharp
using Chatter.MessageBrokers;

string json = ChatterJson.Serialize(new PlaceOrder { OrderId = id, Sku = "ABC-1" });
PlaceOrder roundTripped = ChatterJson.Deserialize<PlaceOrder>(json);
```

`Serialize` writes the members of the type you declare, not the runtime type, so a base-typed variable holding a derived instance writes the base members only. The shared configuration itself is not public. To use a different wire format, implement `IBrokeredMessageBodyConverter` with its own `JsonSerializerOptions`; `IBodyConverterFactory` selects it by content type.

### Lenient reading of enums and booleans

Reading is deliberately as tolerant as Newtonsoft.Json, so two versions of an application can share a queue during a rolling deploy. Writing is strict: an enum is written as its number and a boolean as a bare `true` / `false`.

For an enum-typed property, all of these read:

| JSON value | Read as |
| --- | --- |
| `{"Status":1}` | the member whose value is `1` |
| `{"Status":"Booked"}` | the member named `Booked` |
| `{"Status":"booked"}` | the same member; names match case-insensitively |
| `{"Status":999}` | `999`, whether or not a member has that value |
| `{"Status":"999"}` | the same, quoted as a string |
| `{"Status":"-1"}` | `-1`, for an enum with a signed underlying type |
| `{"Status":"Booked,Cancelled"}` | both members combined, whether or not the enum is `[Flags]` |

A string that names no member and is not a number, such as `{"Status":"NotARealStatus"}`, throws `JsonException`.

For a `bool` or `bool?` property, all of these read:

| JSON value | Read as |
| --- | --- |
| `{"Enabled":true}` | `true` |
| `{"Enabled":"true"}` | `true`; case-insensitive, so `"TRUE"` and `"False"` read too |
| `{"Enabled":" true "}` | `true`; surrounding whitespace is trimmed |
| `{"Enabled":"1"}` / `{"Enabled":"0"}` | `true` / `false` |
| `{"Enabled":1}` / `{"Enabled":0}` | `true` / `false`, as bare numbers |

A `bool?` reads JSON `null` as `null`. Any other quoted value, such as `{"Enabled":"notabool"}`, throws `JsonException`.

> **Important:** Deserialization does not validate enum membership. `{"Status":999}` reaches your handler as an undefined value, so check it with `Enum.IsDefined` or a domain guard before branching on it. This leniency is intentional and will not be tightened, because a stricter read would turn one version's message into a poison message for another.

## Reliability

Two stores make brokered messaging reliable. The **Outbox** records outgoing messages so they are published alongside your local state change. The **Inbox** records received message ids so a redelivery is skipped. This package ships in-memory versions of both, registered as process-lifetime singletons, for development and single-node hosts.

For durable stores, use [Chatter.MessageBrokers.Reliability.EntityFramework](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers.Reliability.EntityFramework/src/README.md) or [Chatter.MessageBrokers.Reliability.Cosmos](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers.Reliability.Cosmos/src/README.md).

### Outbox

Route sends through the Outbox and drain it with the background poller:

```csharp
builder.Services.AddChatterCqrs(builder.Configuration, typeof(Program).Assembly)
    .AddMessageBrokers(options => options
        .AddReliabilityOptions(r => r
            .WithOutboxRouting()                  // Send and Publish write to the Outbox
            .WithOutboxPollingProcessor(5000)     // drain every 5 seconds
            .WithOutboxPollBatchSize(100)         // at most 100 messages per poll
            .WithOutboxDispatchBackoff(5, 60)     // wait 5 s after a failed publish, doubling to 60 s
            .WithOutboxMaxDispatchAttempts(20)))  // stop after 20 failed publishes
    .AddAzureServiceBus(asb => asb.WithConnectionString(builder.Configuration.GetConnectionString("ServiceBus")));
```

`WithOutboxRouting()` makes `Send` and `Publish` write to the Outbox instead of the broker. `WithOutboxPollingProcessor(...)` registers the background poller that publishes from the Outbox. `WithInMemoryOutboxTimeToLive(minutes)` sets how long the in-memory Outbox keeps rows it has already published.

**Draining.** A poll takes at most `OutboxPollBatchSize` messages that are due, oldest first. After a full batch the poller polls again right away; after a shorter batch it waits the polling interval. A drain also stops when a poll returns no message it has not already seen, or after it has seen 10,000 messages; the next interval picks up the rest.

**Backoff and giving up.** A failed publish leaves the message in the Outbox and schedules its next attempt one backoff ahead: 5 s, then 10 s, 20 s and so on up to 60 s. The backoff always applies. No attempt ceiling applies unless you set `OutboxMaxDispatchAttempts`; with it set, a message that has failed that many times is skipped by every later poll and stays in the store.

**Delivery is at-least-once.** A publish that succeeds followed by a failure to mark the row can publish the same message twice. Put handlers that are not naturally idempotent behind the Inbox. The in-memory Outbox throws `InvalidOperationException` if you add a message whose `MessageId` it already holds.

**Several hosts draining one Outbox.** Before publishing a row, a drain claims it by moving the row's next-attempt time forward, and publishes only if the claim succeeds. Over a relational store the claim is part of the publishing transaction, so a second drain waits on that row's lock and then publishes nothing. A publish slower than the `DbContext` command timeout (30 seconds by the provider default) makes the waiting drain give up and defer the row. Over the in-memory store the claim lasts one backoff, so a publish longer than one backoff can be duplicated, never lost.

#### Implementing a custom outbox store

Register one type as `IBrokeredMessageOutbox` before calling `AddMessageBrokers`. The poller casts that same instance to `IPollableOutboxStore`, so the type must implement both, or the poll fails with `InvalidCastException`. `GetUnprocessedMessagesFromOutbox` should return at most `OutboxPollBatchSize` due messages, oldest `SentToOutboxAtUtc` first.

`RecordDispatchAttempt` and `TryClaimForDispatch` have default implementations that record nothing and always grant the claim; implement them to get backoff and claim arbitration. Each row must keep the same identity across fetches, or the poller cannot tell a re-fetched row from new work.

### Inbox

Add `InboxBehavior<>` to the Command Pipeline to deduplicate received commands:

```csharp
using Chatter.MessageBrokers.Reliability.Inbox;

builder.Services.AddChatterCqrs(builder.Configuration,
        pipeline => pipeline.WithBehavior(typeof(InboxBehavior<>)),
        typeof(Program))
    .AddMessageBrokers(options => options
        .AddReliabilityOptions(r => r
            .WithInMemoryInboxDeduplicationWindow(60)   // minutes
            .WithInMemoryInboxMaxEntries(200000)));
```

The Inbox reserves the message id before your handler runs. A concurrent delivery of the same id is skipped without running the handler and without throwing. If the handler throws, the reservation is released so a retry runs the handler again.

The in-memory Inbox has two settings:

- **Deduplication window** (`InMemoryInboxDeduplicationWindowInMinutes`, default `60`, minimum `1`). After a message completes, a redelivery of its id is skipped for this long. The window is also the lease on an in-flight reservation, so a handler still running when the window ends loses its reservation and a redelivery can run concurrently. A handler that can run longer than the window must be idempotent; size the window above your slowest handler.
- **Maximum entries** (`InMemoryInboxMaxEntries`, default `200000`, minimum `1`). This is a memory safety valve, not the deduplication guarantee. At the cap the store first drops entries whose window has ended, and only then entries still inside their window, logging a warning once when it does so. It never drops a reservation for a handler still inside its window.

An entry costs roughly 200 bytes, so keep the cap above deduplication window multiplied by peak receive rate. The defaults hold about 55 receipts per second for 60 minutes in roughly 40 MB. Expiry is decided when an id is looked up, not by the periodic sweep, which only reclaims memory.

## Recovery

Recovery wraps receiving with retry and a circuit breaker, and decides what happens to a message that keeps failing. A common setup retries transient failures with exponential delay and sends exhausted messages to the Error Queue:

```csharp
builder.Services.AddChatterCqrs(builder.Configuration, typeof(Program).Assembly)
    .AddMessageBrokers(options => options
        .AddRecoveryOptions(r => r
            .UseExponentialDelayRecovery(10)
            .RetryWhen<TimeoutException>()
            .RetryWhen(e => e is HttpRequestException)
            .UseRouteToErrorQueueRecoveryAction()
            .WithCircuitBreaker(cb => cb
                .SetNumberOfFailuresBeforeOpen(5)
                .SetOpenToHalfOpenWaitTime(15)
                .IsTrippedBy<TimeoutException>())));
```

### Retry

| Method | Description |
| --- | --- |
| `UseNoDelayRecovery()` | Retry immediately (the default). |
| `UseConstantDelayRecovery(int)` | Wait the same number of milliseconds between attempts. |
| `UseExponentialDelayRecovery(int)` | Wait longer after each attempt. Also sets `MaxRetryAttempts`, capped at 15. |
| `WithMaxRetryAttempts(int)` | Attempts per delivery. Default `5`. |
| `RetryWhen(params Predicate<Exception>[])` / `RetryWhen<TException>()` | Adds exception types that should be retried. |

An exception is retried when any registered predicate matches it. The default set has one predicate, matching a transient `BrokeredMessageReceiverException`; `RetryWhen` adds to that set rather than replacing it. Predicates match on exception type; no default predicate reads the exception message.

Neither the receiver nor the retry and circuit breaker policies wrap your handler's exception before testing it, so `RetryWhen<TimeoutException>()` and `IsTrippedBy<TimeoutException>()` match a `TimeoutException` your handler throws.

### Circuit breaker

The circuit breaker stops processing after repeated failures and admits trial calls once it has cooled. For each call it receives one decision, called an Admission: `Refused` (throws `CircuitBreakerOpenException` without running the action), `Execute` (run against a closed circuit) or `Trial` (run as one half-open trial). It trips on its own exception set, which by default also matches a transient `BrokeredMessageReceiverException`; `IsTrippedBy` and `IsTrippedBy<TException>` add to it.

| Method | Option | Default | Description |
| --- | --- | --- | --- |
| `SetOpenToHalfOpenWaitTime(int)` | `OpenToHalfOpenWaitTimeInSeconds` | `15` | Seconds the circuit stays open before a trial is admitted. |
| `SetConcurrentHalfOpenAttempts(int)` | `ConcurrentHalfOpenAttempts` | `1` | Trials admitted at once while half-open. |
| `SetNumberOfFailuresBeforeOpen(int)` | `NumberOfFailuresBeforeOpen` | `5` | Failures that open the circuit. |
| `SetNumberOfHalfOpenSuccessesBeforeClose(int)` | `NumberOfHalfOpenSuccessesToClose` | `3` | Successful trials that close it. |
| `SetTimeOpenBeforeCriticalEvent(int)` | `SecondsOpenBeforeCriticalFailureNotification` | `1800` | Seconds open before a Critical Failure is raised. |

Circuit state lives in `ICircuitBreakerStateStore` (in-memory by default). A custom store implements `AdmitAsync`, `RecordSuccessAsync` and `RecordFailureAsync`, makes each state transition itself under its own synchronization, and measures the cooling interval with a monotonic clock rather than the wall clock.

### Failure outcomes

- **Max Receives Exceeded.** When a failing message's delivery count reaches `MaxReceiveAttempts`, the receiver deadletters it and runs `IMaxReceivesExceededAction`. The default, `ErrorQueueDispatcher`, forwards the message to its Error Queue.
- **Poisoned message.** A body that cannot be deserialized raises `PoisonedMessageException`, and the message is deadlettered.
- **Critical Failure.** A `CriticalReceiverException` stops the receiver and raises a Critical Failure through `ICriticalFailureNotifier`. The default dispatches a `CriticalFailureEvent` in-process, so implement `IMessageHandler<CriticalFailureEvent>` to alert or restart.
- **Settlement.** Each acknowledge, reject or deadletter reports a Settlement Outcome of `Settled`, `NotRequired` or `Failed`. A transport with nothing to acknowledge reports `NotRequired`, which is not the same as `Failed`.

## Configuration

### Fluent configuration

This sample sets the transaction mode, the Outbox and Recovery together:

```csharp
using Chatter.MessageBrokers.Receiving;

builder.Services.AddChatterCqrs(builder.Configuration, typeof(Program).Assembly)
    .AddMessageBrokers(options => options
        .WithTransactionMode(TransactionMode.ReceiveOnly)
        .AddReliabilityOptions(r => r
            .WithOutboxRouting()
            .WithOutboxPollingProcessor(5000)
            .WithOutboxPollBatchSize(100)
            .WithOutboxDispatchBackoff(5, 60)
            .WithOutboxMaxDispatchAttempts(20))
        .AddRecoveryOptions(r => r
            .UseExponentialDelayRecovery(10)
            .RetryWhen<TimeoutException>()
            .UseRouteToErrorQueueRecoveryAction()
            .WithCircuitBreaker(cb => cb
                .SetNumberOfFailuresBeforeOpen(5)
                .SetOpenToHalfOpenWaitTime(15))))
    .AddAzureServiceBus(asb => asb.WithConnectionString(builder.Configuration.GetConnectionString("ServiceBus")));
```

### appsettings.json

`AddMessageBrokers` binds the `Chatter:MessageBrokers` section of the `IConfiguration` you passed to `AddChatterCqrs`; no extra call is needed. Every bindable key at its default:

```json
{
  "Chatter": {
    "MessageBrokers": {
      "TransactionMode": "ReceiveOnly",
      "Reliability": {
        "RouteMessagesToOutbox": false,
        "MinutesToLiveInMemory": 10,
        "EnableOutboxPollingProcessor": false,
        "OutboxProcessingIntervalInMilliseconds": 5000,
        "InMemoryInboxDeduplicationWindowInMinutes": 60,
        "InMemoryInboxMaxEntries": 200000,
        "OutboxPollBatchSize": 100,
        "OutboxDispatchBackoffBaseInSeconds": 5,
        "OutboxDispatchBackoffCapInSeconds": 60,
        "OutboxMaxDispatchAttempts": null
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

Each section name is a constant on its builder:

| Section | Constant |
| --- | --- |
| `Chatter:MessageBrokers` | `MessageBrokerOptionsBuilder.MessageBrokerSectionName` |
| `Chatter:MessageBrokers:Reliability` | `ReliabilityOptionsBuilder.ReliabilityOptionsSectionName` |
| `Chatter:MessageBrokers:Recovery` | `RecoveryOptionsBuilder.RecoveryOptionsSectionName` |
| `Chatter:MessageBrokers:Recovery:CircuitBreaker` | `CircuitBreakerOptionsBuilder.CircuitBreakerOptionsSectionName` |

The key paths are the same when you build a sub-builder on its own with `FromConfig(services, configuration)`.

### Options reference

`Chatter:MessageBrokers`

| Option | Type | Default | Description |
| --- | --- | --- | --- |
| `TransactionMode` | `TransactionMode` | `ReceiveOnly` | `None`, `ReceiveOnly` or `FullAtomicityViaInfrastructure`. Fluent: `WithTransactionMode`. |

`Chatter:MessageBrokers:Reliability`

| Option | Type | Default | Description |
| --- | --- | --- | --- |
| `RouteMessagesToOutbox` | `bool` | `false` | Send and publish through the Outbox. Fluent: `WithOutboxRouting`. |
| `MinutesToLiveInMemory` | `double` | `10` | How long the in-memory Outbox keeps published rows; `0` or less keeps them. Fluent: `WithInMemoryOutboxTimeToLive`. |
| `EnableOutboxPollingProcessor` | `bool` | `false` | Run the background Outbox poller. Fluent: `WithOutboxPollingProcessor`. |
| `OutboxProcessingIntervalInMilliseconds` | `int` | `5000` | Wait after a short batch. Fluent: `WithOutboxPollingProcessor(int)`. |
| `OutboxPollBatchSize` | `int` | `100` | Most messages per poll. Fluent: `WithOutboxPollBatchSize`. |
| `OutboxDispatchBackoffBaseInSeconds` | `int` | `5` | Wait after the first failed publish. Fluent: `WithOutboxDispatchBackoff`. |
| `OutboxDispatchBackoffCapInSeconds` | `int` | `60` | Longest wait between publish attempts. Fluent: `WithOutboxDispatchBackoff`. |
| `OutboxMaxDispatchAttempts` | `int?` | `null` | Failed publishes before a message stops being polled; `null` retries forever. Fluent: `WithOutboxMaxDispatchAttempts`. |
| `InMemoryInboxDeduplicationWindowInMinutes` | `int` | `60` | In-memory Inbox window and reservation lease. Fluent: `WithInMemoryInboxDeduplicationWindow`. |
| `InMemoryInboxMaxEntries` | `int` | `200000` | In-memory Inbox memory cap. Fluent: `WithInMemoryInboxMaxEntries`. |

`Chatter:MessageBrokers:Recovery`

| Option | Type | Default | Description |
| --- | --- | --- | --- |
| `MaxRetryAttempts` | `int` | `5` | Attempts per delivery. Fluent: `WithMaxRetryAttempts`, `UseExponentialDelayRecovery`. |

The circuit breaker keys are in the [Circuit breaker](#circuit-breaker) table. The delay strategy, the exception predicates and the Error Queue action are fluent only and have no configuration key.

### Precedence: configuration wins

Fluent values are applied first and configuration is bound over them, so a key present in configuration wins over both the default and an explicit fluent call. A key absent from configuration keeps the fluent value. Chatter.MessageBrokers.AzureServiceBus uses the opposite rule, where an explicit fluent call wins, so check which module you are configuring.

```csharp
// With "Chatter:MessageBrokers:TransactionMode": "FullAtomicityViaInfrastructure" in appsettings,
// the configured value wins over this fluent call. Remove the key to let the fluent value stand.
.AddMessageBrokers(options => options.WithTransactionMode(TransactionMode.ReceiveOnly))
```

### Refused values

A value of the wrong type, such as `"MinutesToLiveInMemory": "abc"`, fails in the configuration binder with an `InvalidOperationException` naming the full key path. A value of the right type that is out of range is refused at startup with `ConfiguredValueRefusedException` (namespace `Chatter.MessageBrokers.Exceptions`). It carries `OptionName`, `RefusedValue`, `RequiredBound` and `ConfigurationPath` (null when no section was bound).

| Option | Refused when |
| --- | --- |
| `MessageBrokerOptions.TransactionMode` | the enum does not define the value |
| `ReliabilityOptions.OutboxProcessingIntervalInMilliseconds` | below `0`, when the Outbox poller is enabled |
| `ReliabilityOptions.MinutesToLiveInMemory` | `NaN` or infinite |
| `ReliabilityOptions.OutboxPollBatchSize` | below `1` |
| `ReliabilityOptions.OutboxDispatchBackoffBaseInSeconds` | below `1` |
| `ReliabilityOptions.OutboxDispatchBackoffCapInSeconds` | below `1` |
| `ReliabilityOptions.OutboxMaxDispatchAttempts` | below `1`, when set |
| `ReliabilityOptions.InMemoryInboxDeduplicationWindowInMinutes` | below `1` |
| `ReliabilityOptions.InMemoryInboxMaxEntries` | below `1` |
| `RecoveryOptions.MaxRetryAttempts` | below `1` |
| `CircuitBreakerOptions.ConcurrentHalfOpenAttempts` | below `1` |
| `CircuitBreakerOptions.OpenToHalfOpenWaitTimeInSeconds` | negative, or longer than `Task.Delay` can wait |
| `CircuitBreakerOptions.SecondsOpenBeforeCriticalFailureNotification` | negative, or longer than `Timer.Change` can schedule |

These edge values are accepted on purpose:

- `OutboxProcessingIntervalInMilliseconds` of `0` polls without waiting.
- `MinutesToLiveInMemory` of `0` or less turns off cleanup of published in-memory Outbox rows.
- `OpenToHalfOpenWaitTimeInSeconds` of `0` admits a trial on the first call after the circuit opens.
- `NumberOfFailuresBeforeOpen` or `NumberOfHalfOpenSuccessesToClose` of `0` or `1` opens or closes on the first failure or success.

`UseConstantDelayRecovery(int)` is not validated. `ReceiverOptions.MaxConcurrentCalls` below `1` fails when that receiver starts, not at build time.

### Every injection style resolves the same options instance

`MessageBrokerOptions`, `ReliabilityOptions`, `RecoveryOptions` and `CircuitBreakerOptions` are each registered as the concrete type and as `IOptions<T>`, `IOptionsSnapshot<T>` and `IOptionsMonitor<T>`. All four resolve the one instance the builder produced, with fluent values and configuration applied. A `services.Configure<T>(...)` of your own is not consulted for these four types; use the fluent builder or configuration instead. `IOptionsMonitor<T>` is for resolution only: options are bound once, never reload, and every options name resolves the same instance.

## Diagnostics

The broker boundary emits OpenTelemetry-compatible spans and metrics and propagates W3C trace context. Nothing is emitted until your application subscribes. This package takes no dependency on any `OpenTelemetry.*` package; it uses `System.Diagnostics.ActivitySource` and `System.Diagnostics.Metrics.Meter` from the base class library.

### Turning it on

The `ActivitySource` and the `Meter` are both named `Chatter.MessageBrokers` (`BrokerDiagnostics.ActivitySourceName` and `BrokerDiagnostics.MeterName`). Chatter.CQRS emits under its own name, so you can subscribe to either or both:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource("Chatter.*"))     // or .AddSource("Chatter.CQRS", "Chatter.MessageBrokers")
    .WithMetrics(m => m.AddMeter("Chatter.*"));     // or .AddMeter("Chatter.CQRS", "Chatter.MessageBrokers")
```

Any .NET `ActivityListener` or .NET `MeterListener` works as well.

**Off means off.** When nothing subscribes to the `Chatter.MessageBrokers` source or meter, each emit site returns before building a span name, tags or a `traceparent` header. Your messages are then byte-identical to uninstrumented ones. A `traceparent` is written only when Chatter itself started a span, never from an unrelated ambient `Activity.Current`.

### What is emitted

Two spans, one Chatter-native span event plus the standard `exception` event, and three instruments. Names starting `chatter.` are Chatter-native; `messaging.*`, `error.type` and `exception.*` follow the OpenTelemetry semantic conventions v1.30.0. Every row says when it is emitted, and `Always` means the emit site is unconditional.

**Span name rule.** A span is named `{messaging.operation.name} {messaging.destination.name}`, or just the operation name when no destination is set. When the destination is resolved only at span stop, the name is rewritten there.

**Spans**

| Span | Name | Kind | Started by | Started when |
| --- | --- | --- | --- | --- |
| send | `send {messaging.destination.name}`, per the span name rule | `ActivityKind.Producer` | The dispatcher's send and publish paths, the forwarding router, the reply router, the Outbox drain, and the Cosmos Outbox Relay's drain | Once per dispatch call that reaches the send path, however many messages it carries, and once per row the Outbox drain publishes, while a .NET `ActivityListener` samples the `Chatter.MessageBrokers` source. A forward with a blank destination and a reply with no reply routing context start no span. A reply whose construction throws does start one, and reports a failed send. |
| receive | `receive {messaging.destination.name}`, per the span name rule; the destination is the receiver path | `ActivityKind.Consumer` | The Brokered Message Receiver at worker entry | Once per delivery, covering every Recovery attempt for that delivery, while a .NET `ActivityListener` samples the `Chatter.MessageBrokers` source. |

These two are the whole span inventory. Chatter emits none of the semconv `messaging.operation.type` values `create`, `process` or `settle`.

**Span attributes**

An **unset** attribute below is written as null, which .NET `Activity.SetTag` drops.

| Attribute | Span | Value | Emitted | Name origin |
| --- | --- | --- | --- | --- |
| `messaging.system` | send | The Messaging Infrastructure identifier the dispatch names: from the routing options on a send or publish, the outbound message on a forward, the inbound message on a reply, or the row on an Outbox drain. | Only when the identifier is non-blank. A dispatch naming none, or naming the default infrastructure (whose identifier is empty), leaves it **unset**. | semconv v1.30.0 |
| `messaging.system` | receive | The receiver's `ReceiverOptions.InfrastructureType`; a blank value leaves it **unset**. | Only when the receiver was configured with one. | semconv v1.30.0 |
| `messaging.operation.name` | send | `send` | Always. | semconv v1.30.0 |
| `messaging.operation.name` | receive | `receive` | Always. | semconv v1.30.0 |
| `messaging.operation.type` | send | `send` | Always. | semconv v1.30.0 |
| `messaging.operation.type` | receive | `receive` | Always. | semconv v1.30.0 |
| `messaging.destination.name` | send | The destination the call named; otherwise the single destination every message in the batch resolved to. | A forward or reply sets it at span start and never rewrites it. A send or publish with an explicit destination sets it at start and writes the same value again at stop. An attribute-routed send or publish first writes it at span **stop**, renaming the span there, and leaves it **unset** when the batch resolved to more than one destination or to none. | semconv v1.30.0 |
| `messaging.destination.name` | receive | The receiver path, as the transport's path builder resolved it. | Always, at span start. | semconv v1.30.0 |
| `messaging.batch.message_count` | send | Messages the call handed to the router: the number a send or publish yielded, `1` for a forward, `1` for a drained Outbox row, and for a reply `1` once routed or `0` if it failed first. Matches `messaging.client.sent.messages` for the call. | Always. A forward and a drain set it at start; a send, publish or reply writes it at span **stop**, replacing the starting `0`. | semconv v1.30.0 |
| `messaging.message.id` | receive | The transport's own identifier for the delivered message. | Only when the transport supplied a non-empty one. | semconv v1.30.0 |
| `chatter.messaging.receive.attempts` | receive | Recovery attempts made for this delivery; `0` when it failed before Recovery began, as a poisoned body does. | Always, at span stop. | Chatter-native |
| `chatter.messaging.settlement` | receive | `ack` when handling completed and the worker was not cancelled; `nack` when handling completed under cancellation, or a failure left the delivery count below `MaxReceiveAttempts`; `deadletter` on a poisoned body or a failure that reached `MaxReceiveAttempts`. | Only on the branches that choose a settlement, recorded when the branch chooses it. A delivery ending in a `CriticalReceiverException`, a Shutdown Cancellation, or a failed delivery-count check carries none. | Chatter-native |
| `error.type` | send | The fully qualified exception type name. | Only when an exception ended the dispatch call. | semconv v1.30.0 |
| `error.type` | receive | The fully qualified exception type name, or `settlement_failed` when the transport returned a `Failed` Settlement Outcome without throwing. | Only when a failure was retained for the delivery. A Shutdown Cancellation is not a failed receive and retains none. | semconv v1.30.0 |
| Status (the span status, not a tag) | send | `Error`, with the exception message as the description. | Set together with this span's `error.type`, so the two always agree. | `Activity.SetStatus`, .NET base class library |
| Status (the span status, not a tag) | receive | `Error`, described by the exception message, or on the `settlement_failed` path by what did not settle. | Set together with this span's `error.type`, including on the `settlement_failed` path. | `Activity.SetStatus`, .NET base class library |

**Span events**

| Event | Span | Attributes | Emitted |
| --- | --- | --- | --- |
| `chatter.messaging.receive.retry` | receive | `chatter.messaging.receive.attempts`, the number of the attempt this event records. | On every Recovery attempt after the first, and only while `Activity.IsAllDataRequested` is true. |
| `exception` | send | The `exception.*` set, written by `Activity.AddException`. | Only when an exception ended the dispatch call and `Activity.IsAllDataRequested` is true. |
| `exception` | receive | The `exception.*` set, written by `Activity.AddException`. | Only when an exception ended the delivery and `Activity.IsAllDataRequested` is true. A `Failed` Settlement Outcome returned without an exception carries no event, and neither does a Shutdown Cancellation. |

**A Shutdown Cancellation is logged at `Debug`, not `Error`.** A Shutdown Cancellation is an `OperationCanceledException` or `ObjectDisposedException` that ends a delivery while the receiver is stopping. The receiver logs it once at `Debug` and rethrows it unchanged. The same exception while the receiver is still running is a failed dispatch and is logged at `Error`.

**Metrics**

| Instrument | Type | Unit | Advised buckets | Records | Recorded when |
| --- | --- | --- | --- | --- | --- |
| `messaging.client.operation.duration` | `Histogram<double>` | `s` | `0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10`; see [Histogram bucket boundaries](#histogram-bucket-boundaries) | Elapsed time of one dispatch call, one Outbox drain publish, or one delivery. | Once per dispatch call that reaches the send path, once per row the Outbox drain publishes, and once per delivery, on failure as well as success, while a .NET `MeterListener` has enabled the instrument. The blank-destination forward and the reply with no routing context record nothing; a reply whose construction throws records here. |
| `messaging.client.sent.messages` | `Counter<long>` | `{message}` | — | Messages the dispatch call handed to the transport: the number a send or publish yielded, `1` for a forward, `1` per drained Outbox row, and for a reply `1` once routed or `0` if it failed first. | Once per dispatch call that reaches the send path and once per row the Outbox drain publishes, on failure as well as success, while a .NET `MeterListener` has enabled the instrument. The blank-destination forward and the reply with no routing context record nothing. |
| `messaging.client.consumed.messages` | `Counter<long>` | `{message}` | — | One message per delivery. "Consumed" is the specification's name for what this package calls receiving. | Once per delivery, on failure as well as success, while a .NET `MeterListener` has enabled the instrument. |

**Metric attributes**

| Attribute | Instruments | Value | Emitted |
| --- | --- | --- | --- |
| `messaging.system` | all three | On a send, the infrastructure identifier the dispatch names, or **null** when it names none or the default. A reply takes it from the inbound message before building the reply; a drain uses the value stored with the row. On a receive, the receiver's `ReceiverOptions.InfrastructureType`, or **null** when not configured. | Always, as a key, whatever the value. |
| `messaging.operation.name` | all three | `send` or `receive`. | Always, as a key. |
| `messaging.operation.type` | all three | `send` or `receive`. | Always, as a key. |
| `messaging.destination.name` | all three | On a send, the named destination or the single destination the batch resolved to, or **null** when the call named none and the batch resolved to several or none. On a receive, the receiver path. | Always, as a key, whatever the value. |
| `error.type` | all three | The fully qualified exception type name, or `settlement_failed` for a `Failed` Settlement Outcome returned without an exception. | Only when the operation failed; a successful operation has no `error.type` key. |

An attribute unset on the span still appears on the instruments as a key with a null value. Query spans for a missing attribute and instruments for a null one.

Metric attribute names are a subset of the span attribute names. A breakdown by settlement, message id or attempt count must come from the spans.

### Histogram bucket boundaries

`messaging.client.operation.duration` records seconds. The OpenTelemetry .NET SDK's default boundaries are sized for milliseconds, which would put every measurement in the first bucket. This package therefore publishes seconds-sized boundaries, matching the OpenTelemetry messaging conventions, as instrument advice.

Advice is a default, and a view registered by your application overrides it. To choose other boundaries, register a view; `AddView` and `ExplicitBucketHistogramConfiguration` come from your application's OpenTelemetry packages:

```csharp
using OpenTelemetry.Metrics;

builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m
        .AddMeter("Chatter.MessageBrokers")
        .AddView("messaging.client.operation.duration", new ExplicitBucketHistogramConfiguration
        {
            Boundaries = new double[] { 0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10 }
        }));
```

### Attribute names are data, not API

The semantic-convention attributes (`messaging.system`, `messaging.operation.name`, `messaging.operation.type`, `messaging.destination.name`, `messaging.message.id`, `messaging.batch.message_count`, `error.type`) follow semantic conventions **v1.30.0**. Telemetry attributes are emitted data, not a compile-time API, so **they may change in a minor release** when that version advances. Dashboards and alerts that hard-code attribute names should expect to be revisited; any change is announced in this package's CHANGELOG.

### Trace context propagation

Trace context travels in the Message Context as the standard `traceparent` and `tracestate` headers (`TraceContextHeaders.TraceParent` and `TraceContextHeaders.TraceState`). It survives wherever the whole Message Context survives. It is separate from `CorrelationId`, which is an application-facing identity.

| Path | Trace context |
| --- | --- |
| Azure Service Bus | Flows both ways, as application properties. |
| RabbitMQ | Flows both ways, as a message header. |
| EntityFramework Outbox | Stored with the row and restored on drain, where the drain reparents it. |
| Cosmos Outbox | Stored with the document and restored by the Outbox Relay, which reparents it the same way. |
| Outbox replay in general | A `traceparent` round-trips as a string. |
| SQL Server Service Broker, `DEFAULT` message type and deadletter paths | Does not flow; these paths build fresh headers. The Chatter envelope path does carry it. |
| Chatter.SqlChangeFeed | Does not flow; its messages come from a SQL trigger and carry no headers. |

**Outbox drains reparent.** With diagnostics on, the EntityFramework Outbox drain and the Cosmos Outbox Relay each publish a drained message under a new send span whose parent is the stored `traceparent`, then write that span's context onto the outgoing message. The trace reads write, drain, receive. A row stored without trace context starts a new root, with the drain's ambient activity attached as a link. With diagnostics off, or the drain span sampled out, the stored `traceparent` goes out unchanged.

The Cosmos Outbox Relay uses this package's send span and emits no span of its own; it emits its own metrics, described in the [Chatter.MessageBrokers.Reliability.Cosmos README](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers.Reliability.Cosmos/src/README.md#diagnostics). The SQL Server Service Broker and SqlChangeFeed gaps are limitations of those receive paths and affect every header alike.

## Related packages

- [Chatter.CQRS](https://www.nuget.org/packages/Chatter.CQRS): The in-process Commands, Queries, Events and Command Pipeline this package builds on.
- [Chatter.MessageBrokers.AzureServiceBus](https://www.nuget.org/packages/Chatter.MessageBrokers.AzureServiceBus): Azure Service Bus transport.
- [Chatter.MessageBrokers.AzureServiceBus.Auth](https://www.nuget.org/packages/Chatter.MessageBrokers.AzureServiceBus.Auth): Microsoft Entra ID authentication for the Azure Service Bus transport.
- [Chatter.MessageBrokers.RabbitMQ](https://www.nuget.org/packages/Chatter.MessageBrokers.RabbitMQ): RabbitMQ transport.
- [Chatter.MessageBrokers.SqlServiceBroker](https://www.nuget.org/packages/Chatter.MessageBrokers.SqlServiceBroker): SQL Server Service Broker transport.
- [Chatter.MessageBrokers.Reliability.EntityFramework](https://www.nuget.org/packages/Chatter.MessageBrokers.Reliability.EntityFramework): EF Core Inbox, Outbox and Unit of Work.
- [Chatter.MessageBrokers.Reliability.Cosmos](https://www.nuget.org/packages/Chatter.MessageBrokers.Reliability.Cosmos): Azure Cosmos DB Inbox and Outbox Relay.
- [Chatter.SqlChangeFeed](https://www.nuget.org/packages/Chatter.SqlChangeFeed): Typed change notifications from a SQL Server table.

## Learn more

- [Domain glossary (CONTEXT.md)](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers/CONTEXT.md)
- [Changelog](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/CHANGELOG.md)
- [Context map of all Chatter modules](https://github.com/brenpike/Chatter/blob/master/CONTEXT-MAP.md)
- [Chatter suite README](https://github.com/brenpike/Chatter/blob/master/README.md)

## License

Licensed under the [MIT License](https://github.com/brenpike/Chatter/blob/master/LICENSE).
