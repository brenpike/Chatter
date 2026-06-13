# <a name="chatter-messagebrokers-rabbitmq"></a> Chatter.MessageBrokers.RabbitMQ

A RabbitMQ transport for Chatter.MessageBrokers — send and receive brokered messages over RabbitMQ exchanges and queues.

## Overview

`Chatter.MessageBrokers.RabbitMQ` is a concrete, RabbitMQ implementation of the technology-agnostic interfaces defined by [Chatter.MessageBrokers](../../Chatter.MessageBrokers/src/README.md#chatter-messagebrokers) (itself built on [Chatter.CQRS](../../Chatter.CQRS/src/README.md#chatter-cqrs)).

Where the core library defines `IMessagingInfrastructureReceiver`, `IMessagingInfrastructureDispatcher`, and the recovery abstractions (retry, circuit breaker), this package supplies the RabbitMQ realizations:

- `RabbitMqReceiver` bridges RabbitMQ's push-consumer model to the core's blocking-pull loop via an internal `Channel<T>` buffer, so the core is never busy-polling.
- `RabbitMqSender` publishes messages through the default exchange (routing key = queue name) or an explicit exchange/routing-key override, with publisher confirms enabled so a publish is only treated as sent once the broker confirms it.
- `RabbitMqRetryExceptionPredicatesProvider` and `RabbitMqCircuitBreakerExceptionPredicatesProvider` classify transient RabbitMQ faults so the shared retry/circuit-breaker recovery pipeline handles them correctly.

It registers itself with Chatter through an `IChatterBuilder` extension, `AddRabbitMq(...)`, chained off `AddMessageBrokers(...)` — the same way the sibling `Chatter.MessageBrokers.AzureServiceBus` and `Chatter.MessageBrokers.SqlServiceBroker` packages register their adapters.

## Installation

```bash
dotnet add package Chatter.MessageBrokers.RabbitMQ
```

## Getting Started

Register Chatter CQRS, add message brokers, then add the RabbitMQ infrastructure with a connection and at least one queue receiver. The DI entry point is `AddRabbitMq` (in the `Microsoft.Extensions.DependencyInjection` namespace), configured via the `RabbitMqOptionsBuilder`.

```csharp
using Microsoft.Extensions.DependencyInjection;

services.AddChatterCqrs(Configuration, builder =>
{
    builder
        .AddMessageBrokers()
        .AddRabbitMq(rmq =>
        {
            rmq.AddRabbitMqOptions(hostName: "localhost",
                                   userName: "guest",
                                   password: "guest")
               // register a receiver for a message type bound to a RabbitMQ queue
               .AddQueueReceiver<MyIntegrationEvent>("my-integration-event");
        });
});
```

Alternatively, connect via AMQP URI:

```csharp
rmq.AddRabbitMqOptions(uri: "amqp://guest:guest@localhost:5672/");
```

`AddQueueReceiver<TMessage>` binds a `Chatter.CQRS.IMessage` type to a RabbitMQ queue. Optional parameters let you set an error/dead-letter queue path, description, transaction mode, and `maxReceiveAttempts` (default `10`):

```csharp
rmq.AddQueueReceiver<MyIntegrationEvent>(
    queueName: "my-integration-event",
    errorQueuePath: "my-integration-event-error",
    transactionMode: TransactionMode.ReceiveOnly,
    deadLetterQueuePath: "my-integration-event-deadletter",
    maxReceiveAttempts: 5);
```

### Sending / publishing

Within a message handler, dispatch out through the broker using the standard Chatter context, selecting the RabbitMQ infrastructure with the `.RabbitMq()` context selector:

```csharp
using Chatter.CQRS.Context;

public class MyHandler : IMessageHandler<SomeCommand>
{
    public Task Handle(SomeCommand message, IMessageHandlerContext context)
    {
        return context.RabbitMq()
                      .Send(new AnotherCommand(), "another-command");
    }
}
```

`context.RabbitMq()` stamps the outbound message to route through the RabbitMQ infrastructure and returns an `IMessageBrokerContext`, which exposes `Send`, `Publish`, and `Forward`. The destination is the target **queue name** under the default-exchange convention (see [Addressing](#addressing)).

## Configuration

Options are modeled by `RabbitMqOptions` and assembled with `RabbitMqOptionsBuilder`. Configure them by calling `AddRabbitMqOptions(...)` or using the fluent `With...` methods.

### Connection

| Builder method | Purpose |
| --- | --- |
| `AddRabbitMqOptions(uri: "amqp://...")` | Connect via AMQP URI (takes precedence over discrete host/credential settings when both are set). |
| `AddRabbitMqOptions(hostName: "...", userName: "...", password: "...")` | Connect via discrete host and credentials. |
| `WithUri(string uri)` | Set the AMQP URI after initial configuration. |
| `WithHostName(string hostName)` | Set the broker host name after initial configuration. |
| `WithCredentials(string userName, string password)` | Set authentication credentials after initial configuration. |

`Build()` throws if neither a URI nor a host name is supplied, or if no `AddRabbitMqOptions(...)` call was made.

### Receiver options

| Property | Builder method | Default | Purpose |
| --- | --- | --- | --- |
| `Prefetch` | `WithPrefetch(int)` | `1` | Maximum number of unacknowledged messages the broker delivers to a receiver before waiting for acknowledgements. Size this `>= MaxConcurrentCalls` to keep all processing workers saturated. |
| `QueueType` | `WithQueueType(QueueType)` | `QueueType.Quorum` | Delivery-count strategy: `Quorum` reads the native `x-delivery-count` header; `Classic` uses a header-stamped republish counter. **Quorum is the documented recommendation** — see [Delivery counting](#delivery-counting). |
| `MessageBodyType` | `WithMessageBodyType(string)` / `WithJsonBodyType()` | `application/json; charset=utf-8` | Content type used to encode/decode the message body. |

## Addressing

The RabbitMQ adapter uses a **default-exchange convention**: a bare `Destination` names a queue, and the sender publishes to RabbitMQ's default exchange (`""`) with a routing key equal to that queue name. Routing key and queue name coincide only under this convention.

To route through a non-default exchange with an explicit routing key, use the `.WithRabbitMqRouting(exchange, routingKey)` extension on `OutboundBrokeredMessage`:

```csharp
using Chatter.CQRS.Context;

public class MyHandler : IMessageHandler<SomeCommand>
{
    public Task Handle(SomeCommand message, IMessageHandlerContext context)
    {
        var outbound = new OutboundBrokeredMessage(new AnotherCommand(), "my-destination");
        outbound.WithRabbitMqRouting("my.exchange", "my.routing.key");
        return context.RabbitMq().Send(outbound);
    }
}
```

When `.WithRabbitMqRouting(...)` is present on the outbound message, the sender publishes to the specified exchange with the specified routing key instead of the default-exchange convention.

## Delivery counting

Chatter's Max Receives Exceeded concept requires counting how many times a message has been delivered so a poison message can be routed to the dead-letter/error queue once the configured limit is reached. The adapter selects a counting strategy based on `QueueType`:

### Quorum queues (default, recommended)

On **quorum queues**, RabbitMQ maintains a native per-message `x-delivery-count` header that is incremented on every redelivery. The adapter reads this header directly. On failure, the message is negatively acknowledged with `requeue: true` and the broker redelivers it with an incremented count. There is no duplicate risk and no ordering change — quorum queues are the recommended choice.

### Classic queues

**Classic queues** do not expose a native delivery counter. The adapter implements a **header-stamped republish counter**: on retry it republishes the message to its own queue with an incremented `x-chatter-delivery-count` header (publisher-confirmed before the original is acknowledged), then acknowledges the original delivery.

This approach carries documented trade-offs (per [ADR 0001](../../../docs/adr/0001-rabbitmq-classic-queue-redelivery-counting-via-republish.md)):

- **Rare duplicate**: a crash between the confirmed republish and the acknowledgement of the original yields a duplicate delivery (never a loss). This is mitigated by publisher confirms and absorbed downstream by the Chatter Inbox (idempotent, once-only handling).
- **Loss of head-of-queue ordering**: the republished message lands at the tail of the queue.

Use `QueueType.Classic` only when quorum queues are not available and you accept these trade-offs.

```csharp
rmq.AddRabbitMqOptions(hostName: "localhost")
   .WithQueueType(QueueType.Classic);
```

## Dead-letter / error routing

When Max Receives Exceeded trips, the adapter **republishes** the message body directly to the attribute-declared `DeadletterQueueName` or `ErrorQueueName` path (an adapter-owned republish), then acknowledges the original delivery. This is authoritative over any broker-side dead-letter exchange (DLX) configured on the work queue — the declared name is the destination, regardless of broker DLX configuration.

Configure the dead-letter and error queue paths in `AddQueueReceiver<TMessage>(...)`:

```csharp
rmq.AddQueueReceiver<MyIntegrationEvent>(
    queueName: "my-integration-event",
    errorQueuePath: "my-integration-event-error",
    deadLetterQueuePath: "my-integration-event-deadletter");
```

Both queues must be provisioned externally before the application starts (see [Required topology](#required-topology)).

## Transactions

The adapter supports the broker-agnostic transaction modes that map cleanly onto AMQP:

| Mode | Support |
| --- | --- |
| `TransactionMode.None` | Supported. No transactional coupling between receive and handler. |
| `TransactionMode.ReceiveOnly` | Supported. Ack/nack scope the receive only. |
| `TransactionMode.FullAtomicityViaInfrastructure` | **Rejected at startup.** RabbitMQ offers no atomic receive-and-send across the consume and a downstream publish. Use the **Outbox** for transactional send. |

`FullAtomicityViaInfrastructure` is rejected at registration time — the application will not start — with a message directing you to `TransactionMode.None` or `TransactionMode.ReceiveOnly` and the Outbox.

## Known limitations

### Single RabbitMQ queue receiver per process (0.1.0)

0.1.0 supports exactly **one RabbitMQ queue receiver per process**. Registering more than one RabbitMQ queue receiver fails fast at startup with `NotSupportedException` — the connection source owns one receive channel and one consumer registration, so a second receiver would clobber the first and recovery would re-register only the last. There is no silent stall: the error surfaces immediately at `AddRabbitMq(...)` registration time, before the host starts.

Full multi-receiver support is tracked for a future minor release.

## Required topology

This package is a **transport over existing RabbitMQ topology** — it **provisions nothing**. All exchanges, queues, bindings, and dead-letter routing must be created and owned externally before the application starts. The adapter assumes they exist.

This mirrors the `Chatter.MessageBrokers.SqlServiceBroker` manual-provisioning stance.

### What you must provision

For each registered queue receiver, provision at minimum:

- A **work queue** matching the `queueName` passed to `AddQueueReceiver<TMessage>(...)`.
  - **Quorum queues are the documented recommendation** (`x-queue-type: quorum` argument at declaration).
  - Classic queues are supported but carry the ADR 0001 delivery-counting trade-offs.
- A **dead-letter queue** matching the `deadLetterQueuePath` / `errorQueuePath` configured on the receiver (the adapter republishes to these by name — broker-side DLX configuration on the work queue is ignored by the adapter).
- **Bindings** connecting any non-default exchange to the work queue when `.WithRabbitMqRouting(exchange, routingKey)` is used.

### Development topology

For local development, declare topology in a Dockerfile or `docker-compose.yml` using the RabbitMQ management definitions import or the `rabbitmq_management` plugin. A minimal `docker-compose.yml` fragment:

```yaml
services:
  rabbitmq:
    image: rabbitmq:4-management
    ports:
      - "5672:5672"
      - "15672:15672"
    volumes:
      - ./rabbitmq-definitions.json:/etc/rabbitmq/definitions.json
    environment:
      RABBITMQ_DEFAULT_USER: guest
      RABBITMQ_DEFAULT_PASS: guest
```

Declare your queues and bindings in `rabbitmq-definitions.json` and the management plugin will import them on startup.

### Production topology

Provision exchanges, queues, and bindings via your IaC tooling (Terraform, Pulumi, Ansible, etc.) before deploying the application.

## Infrastructure type selection

To target a specific outbound message at the RabbitMQ infrastructure when multiple brokers are registered, use either the `context.RabbitMq()` selector in a handler or the `InfrastructureTypes.RabbitMq()` extension when configuring a receiver:

```csharp
// In a handler — stamps the outbound message for RabbitMQ dispatch
context.RabbitMq().Send(new MyEvent(), "my-event-queue");

// In DI configuration — when constructing infrastructure explicitly
var infraType = new InfrastructureTypes().RabbitMq();
```

## Domain Language

See the [domain glossary](../CONTEXT.md) for definitions of RabbitMq Receiver, RabbitMq Sender, Exchange, Queue, Routing Key, Binding, Dead-Letter Exchange / Dead-letter Queue, Delivery Count Strategy, RabbitMq Options, and Topology Ownership.

[← All Chatter modules](../../../README.md)
