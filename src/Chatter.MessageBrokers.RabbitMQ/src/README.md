# Chatter.MessageBrokers.RabbitMQ

[![NuGet](https://img.shields.io/nuget/v/Chatter.MessageBrokers.RabbitMQ.svg)](https://www.nuget.org/packages/Chatter.MessageBrokers.RabbitMQ)
[![Downloads](https://img.shields.io/nuget/dt/Chatter.MessageBrokers.RabbitMQ.svg)](https://www.nuget.org/packages/Chatter.MessageBrokers.RabbitMQ)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![CI](https://github.com/brenpike/Chatter/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/brenpike/Chatter/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/brenpike/Chatter/blob/master/LICENSE)

**RabbitMQ transport for Chatter.MessageBrokers over externally provisioned exchanges and queues, with quorum-queue delivery counting.**

This package connects the Chatter.MessageBrokers abstractions to RabbitMQ. A queue receiver hands each delivery to your Command or Event handler, and your handlers send and publish through the same `IMessageHandlerContext` they use for in-process dispatch. It opens one AMQP connection per process and declares no topology: your exchanges, queues and bindings must exist before the application starts. Part of the [Chatter](https://github.com/brenpike/Chatter) suite.

## Contents

- [Features](#features)
- [Installation](#installation)
- [Quick start](#quick-start)
- [Receiving](#receiving)
- [Sending and publishing](#sending-and-publishing)
- [Addressing](#addressing)
- [Delivery counting](#delivery-counting)
- [Dead-letter and error routing](#dead-letter-and-error-routing)
- [Transactions](#transactions)
- [Configuration](#configuration)
- [TLS](#tls)
- [Required topology](#required-topology)
- [Known limitations](#known-limitations)
- [Diagnostics](#diagnostics)
- [Related packages](#related-packages)
- [Learn more](#learn-more)
- [License](#license)

## Features

- **Queue receiver**: bind a Command or Event type to a RabbitMQ queue with one registration call.
- **Default-exchange addressing**: a destination path names a queue; route through any exchange and routing key with `WithRabbitMqRouting`.
- **Publisher confirms**: a send completes only after the broker confirms it, and an unroutable message fails the send instead of vanishing.
- **Quorum-queue delivery counting**: the broker's `x-delivery-count` drives the receive attempt limit; classic queues are supported with a header-stamped counter.
- **Adapter-owned dead-lettering**: a message that exhausts its receive attempts is republished to your dead-letter or error queue, and both are checked for existence at startup.
- **Fail-fast startup**: unsupported transaction modes, a missing poison destination and a second queue receiver are rejected before the host starts receiving.
- **Strict TLS**: connect over `amqps://` or turn on TLS for host-based settings; certificate validation cannot be relaxed.
- **Connection recovery**: the connection recovers automatically and the receiver re-registers on the recovered channel.
- **Transient fault detection**: RabbitMQ connection faults feed the Chatter.MessageBrokers retry and circuit breaker recovery policies.

## Installation

```shell
dotnet add package Chatter.MessageBrokers.RabbitMQ
```

Targets .NET 10 (`net10.0`).

Dependencies: `Chatter.MessageBrokers`, `RabbitMQ.Client` 7.2.1.

Companion packages:

- [Chatter.MessageBrokers.Reliability.EntityFramework](https://www.nuget.org/packages/Chatter.MessageBrokers.Reliability.EntityFramework) or [Chatter.MessageBrokers.Reliability.Cosmos](https://www.nuget.org/packages/Chatter.MessageBrokers.Reliability.Cosmos) for a durable Inbox and Outbox.

## Quick start

The samples use `WebApplication.CreateBuilder(args)` (`builder.Services`, `builder.Configuration`) with implicit usings enabled. Any `IServiceCollection` with an `IConfiguration` works the same way.

### 1. Define your messages

```csharp
using Chatter.CQRS.Commands;
using Chatter.CQRS.Events;

public class PlaceOrder : ICommand
{
    public Guid OrderId { get; set; }
}

public class ShipOrder : ICommand
{
    public Guid OrderId { get; set; }
}

public class OrderPlaced : IEvent
{
    public Guid OrderId { get; set; }
}
```

### 2. Handle them

```csharp
using Chatter.CQRS;
using Chatter.CQRS.Context;

public class PlaceOrderHandler : IMessageHandler<PlaceOrder>
{
    public async Task Handle(PlaceOrder message, IMessageHandlerContext context)
    {
        // place the order, then send the next command to the "shipping" queue
        await context.Send(new ShipOrder { OrderId = message.OrderId }, "shipping");
    }
}
```

### 3. Register Chatter, the message brokers and RabbitMQ

The chain is always `AddChatterCqrs`, then `AddMessageBrokers`, then `AddRabbitMq`.

```csharp
builder.Services.AddChatterCqrs(builder.Configuration, typeof(Program).Assembly)
    .AddMessageBrokers()
    .AddRabbitMq(rmq => rmq
        .AddRabbitMqOptions(hostName: "localhost", userName: "guest", password: "guest")
        .AddQueueReceiver<PlaceOrder>("orders", deadLetterQueuePath: "orders-deadletter"));
```

`AddRabbitMq` lives in the `Microsoft.Extensions.DependencyInjection` namespace, so no extra `using` is needed.

### 4. Create the queues

This package creates nothing. Before the application starts, declare:

| Queue | Arguments | Why |
| --- | --- | --- |
| `orders` | `x-queue-type: quorum`, durable | The receiver's work queue. |
| `orders-deadletter` | durable | Where messages that exhaust their receive attempts are republished. Checked at startup. |
| `shipping` | `x-queue-type: quorum`, durable | The destination `PlaceOrderHandler` sends to. |

See [Required topology](#required-topology) for a definitions file, a Docker Compose setup and a C# provisioning sample.

### 5. Send a command from your API

```csharp
using Chatter.MessageBrokers.Sending;

app.MapPost("/orders", async (PlaceOrder command, IBrokeredMessageDispatcher dispatcher) =>
{
    await dispatcher.Send(command, "orders");
    return Results.Accepted();
});
```

The command is published to the default exchange with routing key `orders`, lands in the `orders` queue, and is handled by `PlaceOrderHandler`.

## Receiving

### Registering a queue receiver

Register the receiver inside the `AddRabbitMq(rmq => ...)` delegate. `TMessage` is any `class` implementing `IMessage`, so Commands and Events are both received from a queue.

```csharp
public RabbitMqOptionsBuilder AddQueueReceiver<TMessage>(string queueName,
                                                         string errorQueuePath = null,
                                                         string description = null,
                                                         TransactionMode? transactionMode = null,
                                                         string deadLetterQueuePath = null,
                                                         int maxReceiveAttempts = 10)
    where TMessage : class, IMessage
```

| Parameter | Type | Default | Description |
| --- | --- | --- | --- |
| `queueName` | `string` | — (required) | The work queue to receive from. |
| `errorQueuePath` | `string` | `null` | Error queue for failed messages. See [Dead-letter and error routing](#dead-letter-and-error-routing). |
| `description` | `string` | `null` | Free-text description of the receiver. |
| `transactionMode` | `TransactionMode?` | global `TransactionMode` | Overrides the Chatter.MessageBrokers transaction mode for this receiver. See [Transactions](#transactions). |
| `deadLetterQueuePath` | `string` | `null` | Dead-letter queue for messages that exhaust their receive attempts. |
| `maxReceiveAttempts` | `int` | `10` | Deliveries before the message is dead-lettered. See [Delivery counting](#delivery-counting). |

Unless the transaction mode is `None`, at least one of `deadLetterQueuePath` or `errorQueuePath` is required. Without either, the receiver throws `InvalidOperationException` at startup, naming the queue.

```csharp
using Chatter.MessageBrokers.Receiving;

builder.Services.AddChatterCqrs(builder.Configuration, typeof(Program).Assembly)
    .AddMessageBrokers()
    .AddRabbitMq(rmq => rmq
        .AddRabbitMqOptions(hostName: "localhost", userName: "guest", password: "guest")
        .AddQueueReceiver<PlaceOrder>(
            "orders",
            errorQueuePath: "orders-errors",
            transactionMode: TransactionMode.ReceiveOnly,
            deadLetterQueuePath: "orders-deadletter",
            maxReceiveAttempts: 5));
```

> **Important:** A process runs at most one RabbitMQ queue receiver. See [Known limitations](#known-limitations).

### Receivers declared with an attribute

A receiver found by the Chatter.MessageBrokers `[BrokeredMessage]` assembly scan also runs on RabbitMQ when RabbitMQ is the default infrastructure, that is, when `AddRabbitMq` registers the first broker. The receiving path is the queue name. A message type must use either the attribute or `AddQueueReceiver`, not both; registration throws `InvalidOperationException` when it carries both.

```csharp
using Chatter.CQRS.Commands;
using Chatter.MessageBrokers;

[BrokeredMessage("orders", "orders", deadletterQueueName: "orders-deadletter")]
public class PlaceOrder : ICommand
{
    public Guid OrderId { get; set; }
}
```

### Prefetch and concurrency

The receiver processes one message at a time; this package has no per-receiver concurrency setting. `Prefetch` (default `1`) sets how many unacknowledged deliveries RabbitMQ pushes ahead of the handler, and the receiver buffers that many. Values are clamped to the AMQP range `1` to `65535`.

### Connection recovery and transient faults

The RabbitMQ client recovers a dropped connection automatically. After recovery, the receiver registers itself again on a fresh channel. A delivery received before the drop cannot be settled on the new channel; RabbitMQ redelivers it instead.

The Chatter.MessageBrokers retry and circuit breaker policies treat these errors as transient: `BrokerUnreachableException`, `AlreadyClosedException`, `OperationInterruptedException`, `ConnectFailureException`, `SocketException` and `IOException`. See [Recovery](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers/src/README.md#recovery).

## Sending and publishing

### From a handler

Handlers send Commands and publish Events through the `Send` and `Publish` extensions on `IMessageHandlerContext` (namespace `Chatter.CQRS.Context`). The destination path is a queue name unless you route through an exchange (see [Addressing](#addressing)).

```csharp
using Chatter.CQRS;
using Chatter.CQRS.Context;

public class OrderPlacedHandler : IMessageHandler<OrderPlaced>
{
    public Task Handle(OrderPlaced message, IMessageHandlerContext context)
        => context.Send(new ShipOrder { OrderId = message.OrderId }, "shipping");
}
```

### From outside a handler

Inject `IBrokeredMessageDispatcher` (namespace `Chatter.MessageBrokers.Sending`) and call `Send` or `Publish` with a destination path, as in the [Quick start](#quick-start).

### Publishing to an exchange

Under the default-exchange convention, `Publish` delivers to one queue. To fan an Event out to several services, publish to a fanout or topic exchange with `WithRabbitMqRouting`:

```csharp
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Routing.Options;

await context.Publish(
    new OrderPlaced { OrderId = orderId },
    "order-events",
    new PublishOptions().WithRabbitMqRouting("orders.events", "orders.placed"));
```

The exchange and every binding must already exist. See [Required topology](#required-topology).

### Choosing the infrastructure

When your application registers more than one broker, `context.RabbitMq()` marks the inbound message for RabbitMQ and returns the `IMessageBrokerContext`, whose `Send`, `Publish` and `Forward` go out over RabbitMQ. It returns `null` when the handler was not invoked by a Brokered Message Receiver.

```csharp
await context.RabbitMq().Send(new ShipOrder { OrderId = message.OrderId }, "shipping");
```

Outside a handler, select RabbitMQ on the options. `UseMessagingInfrastructure` returns `void`, so call it as its own statement:

```csharp
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Routing.Options;

var options = new SendOptions();
options.UseMessagingInfrastructure(t => t.RabbitMq());

await dispatcher.Send(new PlaceOrder { OrderId = orderId }, "orders", options: options);
```

### Message properties

Every message is published as persistent. The message id, content type and correlation id travel in the native AMQP properties, and the correlation id is also copied to a header. A `TimeToLive` set on the message becomes the AMQP per-message `expiration`. Every other message context value travels as an AMQP header.

The body is encoded with the converter for `MessageBodyType` (JSON by default). On receipt, the delivered content type picks the converter; when a delivery has none, `MessageBodyType` is used.

## Addressing

A bare destination path names a queue. The sender publishes to the default exchange (`""`) with the routing key set to that queue name, so the routing key and the queue name are the same only under this convention.

`WithRabbitMqRouting(exchange, routingKey)` replaces that convention for one message. It exists on `SendOptions`, `PublishOptions` and `OutboundBrokeredMessage`, and lives in the `Chatter.CQRS.Context` namespace.

| Call | Exchange | Routing key |
| --- | --- | --- |
| no routing override | `""` (default) | the destination path |
| `WithRabbitMqRouting("orders.exchange", "orders.placed")` | `orders.exchange` | `orders.placed` |
| `WithRabbitMqRouting("", "orders-priority")` | `""` (default) | `orders-priority` |

```csharp
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Routing.Options;

await context.Send(
    new ShipOrder { OrderId = message.OrderId },
    "shipping",
    new SendOptions().WithRabbitMqRouting("orders.exchange", "orders.placed"));
```

Messages are published with the `mandatory` flag. When no binding routes a message to a queue, RabbitMQ returns it and the send fails with a `PublishException`; the message is not silently dropped.

A routing override applies only to the message it is set on. It is never copied from an inbound message to the messages a handler sends in reply.

## Delivery counting

Each receiver counts deliveries so it can dead-letter a message once `maxReceiveAttempts` is reached. The first delivery is attempt 1. The strategy follows `QueueType`, which must match how your work queue was declared.

### Quorum queues

Quorum is the default and the recommended choice. RabbitMQ increments the native `x-delivery-count` header on every redelivery, and the receiver reads it. A failed message is negatively acknowledged with requeue, so RabbitMQ redelivers it with a higher count. There is no duplicate and no ordering change.

> **Note:** From RabbitMQ 4.0, quorum queues have a broker-side `delivery-limit` of 20 by default. RabbitMQ drops or dead-letters a message past that limit itself, so keep `maxReceiveAttempts` below the queue's `delivery-limit`.

### Classic queues

Classic queues have no native delivery counter. On a failure, the receiver republishes the message to its own queue with an incremented `x-chatter-delivery-count` header, waits for the broker's confirm, and then acknowledges the original. This has two costs:

- **A rare duplicate, never a loss.** A crash between the confirmed republish and the acknowledgement leaves both copies. The Inbox absorbs the duplicate (see [Reliability](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers/src/README.md#reliability)).
- **Lost head-of-queue order.** The republished copy joins the tail of the queue.

Choose classic queues only when quorum queues are unavailable and you accept both costs.

```csharp
using Chatter.MessageBrokers.RabbitMQ.Configuration;

builder.Services.AddChatterCqrs(builder.Configuration, typeof(Program).Assembly)
    .AddMessageBrokers()
    .AddRabbitMq(rmq => rmq
        .AddRabbitMqOptions(hostName: "localhost", userName: "guest", password: "guest")
        .WithQueueType(QueueType.Classic)
        .AddQueueReceiver<PlaceOrder>("orders", deadLetterQueuePath: "orders-deadletter"));
```

The delivery-count headers are removed from the message context on receipt, so they never ride onto messages your handler sends.

## Dead-letter and error routing

When a message exhausts `maxReceiveAttempts`, the receiver republishes it to the dead-letter or error queue by name, through the default exchange, and then acknowledges the original. This happens in the adapter, so any dead-letter exchange (DLX) configured on the work queue in RabbitMQ is not used for it. The republished copy carries the failure reason in the `MessageContext.FailureDetails` and `MessageContext.FailureDescription` headers, and drops any per-message expiration so it does not expire in the dead-letter queue.

Which queue receives which copy depends on the paths you configure:

| Configured | Republished by the adapter to | Also written to the error queue |
| --- | --- | --- |
| `deadLetterQueuePath` only | the dead-letter queue | — |
| `errorQueuePath` only | the error queue | No. The adapter's copy is the only copy. |
| both | the dead-letter queue | Yes. Chatter.MessageBrokers forwards a copy to the error queue. |
| neither | — | Startup fails with `InvalidOperationException`, unless the mode is `None`. |

At startup, the receiver checks that each configured dead-letter and error queue exists, using a passive declare that creates nothing. A missing queue throws `InvalidOperationException` naming the queue and the option that configured it. Any other broker error, such as `ACCESS_REFUSED`, propagates unwrapped. `TransactionMode.None` receivers check nothing, because they never dead-letter.

## Transactions

The transaction mode comes from the receiver's `transactionMode` argument, or from the global Chatter.MessageBrokers `TransactionMode` (default `ReceiveOnly`; see [Chatter.MessageBrokers configuration](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers/src/README.md#configuration)).

| Mode | Support | Behaviour |
| --- | --- | --- |
| `None` | Supported, at-most-once | The receiver consumes with `autoAck`, so RabbitMQ removes each message as it is delivered. A handler failure or a process crash loses the message. No dead-letter or error queue is required. |
| `ReceiveOnly` | Supported | The message is acknowledged, requeued or dead-lettered after the handler runs. Sends from the handler are not part of the receive. |
| `FullAtomicityViaInfrastructure` | Rejected | RabbitMQ cannot receive and send in one atomic unit. `AddRabbitMq` throws `NotSupportedException` when the global mode or a RabbitMQ receiver's mode is `FullAtomicityViaInfrastructure`. |

For sends that commit with your local state change, use the Outbox (see [Reliability](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers/src/README.md#reliability)).

## Configuration

Options are set in code through `RabbitMqOptionsBuilder`. There is no configuration section binding: read values from your own configuration and pass them in.

### Connecting with a URI

```json
{
  "ConnectionStrings": {
    "RabbitMq": "amqp://orders-app:<password>@rabbitmq.internal:5672/orders"
  }
}
```

```csharp
builder.Services.AddChatterCqrs(builder.Configuration, typeof(Program).Assembly)
    .AddMessageBrokers()
    .AddRabbitMq(rmq => rmq
        .AddRabbitMqOptions(uri: builder.Configuration.GetConnectionString("RabbitMq"))
        .AddQueueReceiver<PlaceOrder>("orders", deadLetterQueuePath: "orders-deadletter"));
```

A URI sets the host, port, virtual host and credentials in one value. When a URI is set, the discrete host and credential settings are ignored.

### Connecting with host settings

```json
{
  "RabbitMq": {
    "HostName": "rabbitmq.internal",
    "UserName": "orders-app",
    "Password": "<password>"
  }
}
```

```csharp
builder.Services.AddChatterCqrs(builder.Configuration, typeof(Program).Assembly)
    .AddMessageBrokers()
    .AddRabbitMq(rmq => rmq
        .AddRabbitMqOptions(
            hostName: builder.Configuration["RabbitMq:HostName"],
            userName: builder.Configuration["RabbitMq:UserName"],
            password: builder.Configuration["RabbitMq:Password"],
            prefetch: 10)
        .AddQueueReceiver<PlaceOrder>("orders", deadLetterQueuePath: "orders-deadletter"));
```

The host settings have no port or virtual host option: they connect to the default virtual host `/` on port 5672, or 5671 with TLS. Use a URI for any other port or virtual host. An omitted user name or password falls back to the RabbitMQ client default.

### Options

| Option | Type | Default | Description |
| --- | --- | --- | --- |
| `Uri` | `string` | `null` | AMQP URI. Takes precedence over `HostName`, `UserName` and `Password`. |
| `HostName` | `string` | `null` | Broker host name. `Uri` or `HostName` is required. |
| `UserName` | `string` | `null` | User name for host-based settings. |
| `Password` | `string` | `null` | Password for host-based settings. |
| `MessageBodyType` | `string` | `application/json; charset=utf-8` | Content type used to encode and decode message bodies. Required. |
| `Prefetch` | `int` | `1` | Unacknowledged deliveries RabbitMQ pushes ahead of the handler. See [Prefetch and concurrency](#prefetch-and-concurrency). |
| `QueueType` | `QueueType` | `Quorum` | `Quorum` or `Classic`; selects the delivery counting strategy. See [Delivery counting](#delivery-counting). |
| `UseTls` | `bool` | `false` | TLS for host-based settings. See [TLS](#tls). |
| `TlsServerName` | `string` | `null` (uses `HostName`) | Server name the broker certificate is validated against. |

`QueueType` is in the `Chatter.MessageBrokers.RabbitMQ.Configuration` namespace.

### Builder methods

Call an `AddRabbitMqOptions` overload first. The `With...` methods change the options it created, and a later `AddRabbitMqOptions` call replaces them.

| Method | Description |
| --- | --- |
| `AddRabbitMqOptions(string uri = null, string hostName = null, string userName = null, string password = null, string messageBodyType = "application/json; charset=utf-8", int prefetch = 1, QueueType queueType = QueueType.Quorum)` | Creates the options from the arguments. |
| `AddRabbitMqOptions(RabbitMqOptions options)` | Uses an options instance you built. |
| `AddRabbitMqOptions(Func<RabbitMqOptions> optionsBuilder)` | Same, from a factory called immediately. |
| `WithUri(string uri)` | Sets `Uri`. |
| `WithHostName(string hostName)` | Sets `HostName`. |
| `WithCredentials(string userName, string password)` | Sets `UserName` and `Password`. |
| `WithMessageBodyType(string messageBodyType)` | Sets `MessageBodyType`. |
| `WithJsonBodyType()` | Sets `MessageBodyType` to `application/json; charset=utf-8`. |
| `WithPrefetch(int prefetch)` | Sets `Prefetch`. |
| `WithQueueType(QueueType queueType)` | Sets `QueueType`. |
| `WithTls(string serverName = null)` | Sets `UseTls` and `TlsServerName`. |
| `AddQueueReceiver<TMessage>(...)` | Registers the queue receiver. See [Receiving](#receiving). |

### Validation

`AddRabbitMq` builds the options immediately and throws when:

- no `AddRabbitMqOptions` call was made;
- neither `Uri` nor `HostName` is set;
- `MessageBodyType` is empty;
- TLS is requested while `Uri` is set to a URI that is not `amqps://`;
- a transaction mode is `FullAtomicityViaInfrastructure` (see [Transactions](#transactions));
- more than one RabbitMQ queue receiver is registered (see [Known limitations](#known-limitations)).

### Other APIs

| Method | Description |
| --- | --- |
| `context.RabbitMq()` | Routes the handler's outbound messages over RabbitMQ and returns `IMessageBrokerContext`; `null` outside a receiver-invoked handler. |
| `InfrastructureTypes.RabbitMq()` | The RabbitMQ infrastructure type, for `UseMessagingInfrastructure(t => t.RabbitMq())`. |
| `WithRabbitMqRouting(exchange, routingKey)` | Exchange and routing key override on `SendOptions`, `PublishOptions` or `OutboundBrokeredMessage`. See [Addressing](#addressing). |
| `IRabbitMqConnectionSource` | The connection seam. A replacement must be registered as a singleton before `AddRabbitMq`; any other lifetime throws `NotSupportedException`. |

## TLS

TLS can be turned on in two ways. Only one applies to a connection.

- **An `amqps://` URI.** The RabbitMQ client handles TLS for the URI.
- **`WithTls(serverName)` with host settings.** The certificate is validated against `serverName`, or `HostName` when you pass none, and the port becomes 5671.

With host settings:

```csharp
builder.Services.AddChatterCqrs(builder.Configuration, typeof(Program).Assembly)
    .AddMessageBrokers()
    .AddRabbitMq(rmq => rmq
        .AddRabbitMqOptions(hostName: "broker.example.com", userName: "orders-app", password: "<password>")
        .WithTls("broker.example.com")
        .AddQueueReceiver<PlaceOrder>("orders", deadLetterQueuePath: "orders-deadletter"));
```

With a URI:

```csharp
builder.Services.AddChatterCqrs(builder.Configuration, typeof(Program).Assembly)
    .AddMessageBrokers()
    .AddRabbitMq(rmq => rmq
        .AddRabbitMqOptions(uri: "amqps://orders-app:<password>@broker.example.com:5671/")
        .AddQueueReceiver<PlaceOrder>("orders", deadLetterQueuePath: "orders-deadletter"));
```

`WithTls` has no effect on a URI connection, and combining it with an `amqp://` URI throws at registration rather than connecting in the clear. Neither path can relax certificate validation. The host-settings path is stricter than the client's `amqps://` handling, which tolerates a certificate name mismatch: with `WithTls`, a server name that does not match the certificate fails the handshake.

## Required topology

This package provisions nothing. Exchanges, queues, bindings and any broker-side policies are created and owned outside your application, for example by infrastructure-as-code in production. The receiver's only broker check is the passive existence check on its dead-letter and error queues.

### What to declare

| Object | Declare it as | Needed when |
| --- | --- | --- |
| Work queue | durable, `x-queue-type: quorum` (or classic with `WithQueueType(QueueType.Classic)`) | Always; named by `AddQueueReceiver`'s `queueName`. |
| Dead-letter queue | durable queue | `deadLetterQueuePath` is set. |
| Error queue | durable queue | `errorQueuePath` is set. |
| Destination queues | durable queue | Your handlers send or publish to a queue name. |
| Exchanges and bindings | any exchange type, bound to the target queues | You use `WithRabbitMqRouting` with a non-default exchange. |

A broker-side DLX on the work queue is optional. It only applies to messages RabbitMQ itself dead-letters, such as those past a quorum queue's `delivery-limit`.

### Definitions file

This `definitions.json` declares the Quick start queues plus an exchange for [Publishing to an exchange](#publishing-to-an-exchange):

```json
{
  "queues": [
    { "name": "orders", "vhost": "/", "durable": true, "auto_delete": false, "arguments": { "x-queue-type": "quorum" } },
    { "name": "orders-deadletter", "vhost": "/", "durable": true, "auto_delete": false, "arguments": { "x-queue-type": "quorum" } },
    { "name": "shipping", "vhost": "/", "durable": true, "auto_delete": false, "arguments": { "x-queue-type": "quorum" } },
    { "name": "billing-order-events", "vhost": "/", "durable": true, "auto_delete": false, "arguments": { "x-queue-type": "quorum" } }
  ],
  "exchanges": [
    { "name": "orders.events", "vhost": "/", "type": "topic", "durable": true, "auto_delete": false, "internal": false, "arguments": {} }
  ],
  "bindings": [
    { "source": "orders.events", "vhost": "/", "destination": "billing-order-events", "destination_type": "queue", "routing_key": "orders.*", "arguments": {} }
  ]
}
```

### Local development with Docker Compose

```yaml
services:
  rabbitmq:
    image: rabbitmq:4-management
    ports:
      - "5672:5672"
      - "15672:15672"
    environment:
      RABBITMQ_DEFAULT_USER: guest
      RABBITMQ_DEFAULT_PASS: guest
    volumes:
      - ./definitions.json:/etc/rabbitmq/chatter-definitions.json:ro
```

Once the broker is up, import the file:

```shell
docker compose exec rabbitmq rabbitmqctl import_definitions /etc/rabbitmq/chatter-definitions.json
```

> **Note:** RabbitMQ can also import definitions at boot through `rabbitmq.conf`, but a fresh node that imports at boot does not create the default user or virtual host. Include users, virtual hosts and permissions in the file if you use that route.

### Declaring from code

For test fixtures or a deployment tool, declare the same objects with `RabbitMQ.Client`. Run this before your application starts, not inside it.

```csharp
using RabbitMQ.Client;

var factory = new ConnectionFactory { Uri = new Uri("amqp://guest:guest@localhost:5672/") };
await using var connection = await factory.CreateConnectionAsync();
await using var channel = await connection.CreateChannelAsync();

var quorum = new Dictionary<string, object> { ["x-queue-type"] = "quorum" };

await channel.QueueDeclareAsync("orders", durable: true, exclusive: false, autoDelete: false, arguments: quorum);
await channel.QueueDeclareAsync("orders-deadletter", durable: true, exclusive: false, autoDelete: false, arguments: quorum);
await channel.QueueDeclareAsync("shipping", durable: true, exclusive: false, autoDelete: false, arguments: quorum);

await channel.ExchangeDeclareAsync("orders.events", ExchangeType.Topic, durable: true, autoDelete: false);
await channel.QueueDeclareAsync("billing-order-events", durable: true, exclusive: false, autoDelete: false, arguments: quorum);
await channel.QueueBindAsync("billing-order-events", "orders.events", "orders.*");
```

### Permissions

The application's RabbitMQ user needs:

- **read** on the work queue, to receive;
- **write** on the default exchange, to send by queue name and to republish to dead-letter, error and classic work queues;
- **write** on every exchange you publish to with `WithRabbitMqRouting`;
- on RabbitMQ 4.3.1 and later, any one of **configure**, **write** or **read** on each dead-letter and error queue, for the startup existence check. Earlier versions need no permission for it. Without it, startup fails with `ACCESS_REFUSED`.

`TransactionMode.None` receivers skip the existence check and need no permission on poison queues.

## Known limitations

### One RabbitMQ queue receiver per process

A process supports exactly one RabbitMQ queue receiver. The connection owns one receive channel and one AMQP subscription, so a second receiver would displace the first. Registering a second one, whether through `AddQueueReceiver` or a `[BrokeredMessage]` receiver that resolves to RabbitMQ, makes `AddRabbitMq` throw `NotSupportedException` before the host starts. Split receivers across processes or services; multi-receiver support is tracked in [#195](https://github.com/brenpike/Chatter/issues/195).

## Diagnostics

This package emits no telemetry of its own. Broker spans and metrics come from the Chatter.MessageBrokers `ActivitySource` and `Meter`; see [Chatter.MessageBrokers diagnostics](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers/src/README.md#diagnostics). The W3C `traceparent` and `tracestate` values travel as AMQP headers in both directions, so a trace continues across the broker (see [Trace context propagation](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers/src/README.md#trace-context-propagation)).

## Related packages

- [Chatter.CQRS](https://www.nuget.org/packages/Chatter.CQRS): In-process Commands, Queries, Events and the Command Pipeline.
- [Chatter.MessageBrokers](https://www.nuget.org/packages/Chatter.MessageBrokers): The broker abstractions this transport implements: receivers, routing, Inbox/Outbox and Recovery.
- [Chatter.MessageBrokers.AzureServiceBus](https://www.nuget.org/packages/Chatter.MessageBrokers.AzureServiceBus): Azure Service Bus transport.
- [Chatter.MessageBrokers.SqlServiceBroker](https://www.nuget.org/packages/Chatter.MessageBrokers.SqlServiceBroker): SQL Server Service Broker transport.
- [Chatter.MessageBrokers.Reliability.EntityFramework](https://www.nuget.org/packages/Chatter.MessageBrokers.Reliability.EntityFramework): EF Core Inbox, Outbox and Unit of Work.
- [Chatter.MessageBrokers.Reliability.Cosmos](https://www.nuget.org/packages/Chatter.MessageBrokers.Reliability.Cosmos): Azure Cosmos DB Inbox and Outbox Relay.

## Learn more

- [Domain glossary (CONTEXT.md)](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers.RabbitMQ/CONTEXT.md)
- [Changelog](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers.RabbitMQ/src/Chatter.MessageBrokers.RabbitMQ/CHANGELOG.md)
- [Context map of all Chatter modules](https://github.com/brenpike/Chatter/blob/master/CONTEXT-MAP.md)
- [Chatter suite README](https://github.com/brenpike/Chatter/blob/master/README.md)

## License

Licensed under the [MIT License](https://github.com/brenpike/Chatter/blob/master/LICENSE).
