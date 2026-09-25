# Chatter.MessageBrokers.AzureServiceBus

[![NuGet](https://img.shields.io/nuget/v/Chatter.MessageBrokers.AzureServiceBus.svg)](https://www.nuget.org/packages/Chatter.MessageBrokers.AzureServiceBus)
[![Downloads](https://img.shields.io/nuget/dt/Chatter.MessageBrokers.AzureServiceBus.svg)](https://www.nuget.org/packages/Chatter.MessageBrokers.AzureServiceBus)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![CI](https://github.com/brenpike/Chatter/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/brenpike/Chatter/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/brenpike/Chatter/blob/master/LICENSE)

**Azure Service Bus transport for Chatter.MessageBrokers: queue receivers, topic subscriptions, sessions and cross-entity transactions.**

This package connects the Chatter.MessageBrokers abstractions to Azure Service Bus. Commands arrive on queues, Events arrive on topic subscriptions, and your handlers send and publish through the same `IMessageHandlerContext` they use for in-process dispatch. It uses one shared `ServiceBusClient` per namespace and creates no entities: queues, topics, subscriptions and session-enabled entities must already exist. Part of the [Chatter](https://github.com/brenpike/Chatter) suite.

## Contents

- [Features](#features)
- [Installation](#installation)
- [Quick start](#quick-start)
- [Receiving](#receiving)
- [Sending and publishing](#sending-and-publishing)
- [Sessions](#sessions)
- [Message lock renewal](#message-lock-renewal)
- [Transactions](#transactions)
- [Authentication](#authentication)
- [Configuration](#configuration)
- [Retry and circuit breaker](#retry-and-circuit-breaker)
- [Diagnostics](#diagnostics)
- [Related packages](#related-packages)
- [Learn more](#learn-more)
- [License](#license)

## Features

- **Queue receivers and topic subscriptions**: bind Commands to queues and Events to topic subscriptions with one registration call each, or through the `[BrokeredMessage]` attribute.
- **Per-receiver concurrency**: set `MaxConcurrentCalls` globally or per receiver; a value stated on the receiver wins.
- **Sessions**: FIFO delivery per session, several sessions held at once, the session id surfaced as the Group Id, and durable session state.
- **Message lock renewal**: PeekLock message locks are renewed while a handler runs, up to a configurable ceiling.
- **Cross-entity transactions**: settle the received message and the messages your handler sends in one Azure Service Bus transaction.
- **SAS or token authentication**: use a connection string with a shared access key, or any `TokenCredential` against an endpoint-only connection string.
- **Configuration or fluent setup**: bind every option from `appsettings.json`; an explicit fluent call wins over configuration.
- **Transient fault detection**: Azure Service Bus transient errors feed the Chatter.MessageBrokers retry and circuit breaker recovery policies.

## Installation

```shell
dotnet add package Chatter.MessageBrokers.AzureServiceBus
```

Targets .NET 10 (`net10.0`).

Dependencies: `Chatter.MessageBrokers`, `Azure.Messaging.ServiceBus` 7.20.2.

Companion packages:

- [Chatter.MessageBrokers.AzureServiceBus.Auth](https://www.nuget.org/packages/Chatter.MessageBrokers.AzureServiceBus.Auth) for Microsoft Entra ID token authentication.
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
        // place the order, then announce it on the "order-events" topic
        await context.Publish(new OrderPlaced { OrderId = message.OrderId }, "order-events");
    }
}

public class BillOrderHandler : IMessageHandler<OrderPlaced>
{
    public Task Handle(OrderPlaced message, IMessageHandlerContext context)
    {
        // bill the order
        return Task.CompletedTask;
    }
}
```

### 3. Register the transport and its receivers

```csharp
builder.Services.AddChatterCqrs(builder.Configuration, typeof(Program).Assembly)
    .AddMessageBrokers()
    .AddAzureServiceBus(asb => asb
        .WithConnectionString(builder.Configuration.GetConnectionString("ServiceBus"))
        .WithMaxConcurrentCalls(4)
        .AddQueueReceiver<PlaceOrder>("orders", errorQueuePath: "orders-errors")
        .AddTopicSubscription<OrderPlaced>("order-events", "billing"));
```

> **Note:** A message type registered with `AddQueueReceiver`, `AddTopicSubscription` or their session variants must not also carry the `[BrokeredMessage]` attribute. Registration throws `InvalidOperationException` when it does.

### 4. Add the connection string

```json
{
  "ConnectionStrings": {
    "ServiceBus": "Endpoint=sb://<namespace>.servicebus.windows.net/;SharedAccessKeyName=<key-name>;SharedAccessKey=<key>"
  }
}
```

The `orders` and `orders-errors` queues, the `order-events` topic and its `billing` subscription must exist in the namespace.

### 5. Send a command from your API

```csharp
using Chatter.MessageBrokers.Sending;

app.MapPost("/orders", async (PlaceOrder command, IBrokeredMessageDispatcher dispatcher) =>
{
    await dispatcher.Send(command, "orders");
    return Results.Accepted();
});
```

The command is sent to the `orders` queue, received by the queue receiver and handled by `PlaceOrderHandler`.

## Receiving

### Registration methods

Register receivers inside the `AddAzureServiceBus(asb => ...)` delegate. Each method has two overloads: one inherits the global `MaxConcurrentCalls`, and one takes a required `int maxConcurrentCalls` directly after the path parameters.

| Method | Description |
| --- | --- |
| `AddQueueReceiver<TMessage>(queueName, ...)` | Receives a Command (`TMessage : class, ICommand`) from a queue. |
| `AddTopicSubscription<TMessage>(topicName, subscriptionName, ...)` | Receives an Event (`TMessage : class, IEvent`) from a topic subscription. |
| `AddSessionQueueReceiver<TMessage>(queueName, ...)` | Receives a Command from a session-enabled queue. See [Sessions](#sessions). |
| `AddSessionTopicSubscription<TMessage>(topicName, subscriptionName, ...)` | Receives an Event from a session-enabled topic subscription. See [Sessions](#sessions). |

### Receiver parameters

| Parameter | Type | Default | Description |
| --- | --- | --- | --- |
| `maxConcurrentCalls` | `int` | global `MaxConcurrentCalls` | Messages processed at once; in session mode, sessions held at once. Only on the second overload. A value below `1` throws `ArgumentOutOfRangeException` at registration. |
| `errorQueuePath` | `string` | `null` | Queue a message is routed to once its receive attempts are exhausted. |
| `description` | `string` | `null` | Free-text description of the receiver. |
| `transactionMode` | `TransactionMode?` | global `TransactionMode` | Overrides the Chatter.MessageBrokers transaction mode for this receiver. See [Transactions](#transactions). |
| `maxReceiveAttempts` | `int` | `10` | Delivery attempts before the message is dead-lettered and routed to `errorQueuePath`. |

```csharp
using Chatter.MessageBrokers.Receiving;

builder.Services.AddChatterCqrs(builder.Configuration, typeof(Program).Assembly)
    .AddMessageBrokers()
    .AddAzureServiceBus(asb => asb
        .WithConnectionString(builder.Configuration.GetConnectionString("ServiceBus"))
        .AddQueueReceiver<PlaceOrder>(
            "orders",
            maxConcurrentCalls: 8,
            errorQueuePath: "orders-errors",
            transactionMode: TransactionMode.ReceiveOnly,
            maxReceiveAttempts: 5)
        .AddTopicSubscription<OrderPlaced>("order-events", "shipping", maxConcurrentCalls: 2));
```

> **Note:** Passing the literal `default` in the `maxConcurrentCalls` position is ambiguous between the two overloads and does not compile. Pass a typed value or omit the argument.

### Receivers declared with an attribute

Receivers found by the Chatter.MessageBrokers `[BrokeredMessage]` assembly scan also run on Azure Service Bus when it is the default infrastructure. A sending path that is empty or equal to the receiving path means a queue; a different sending path means a topic named by the sending path and a subscription named by the receiving path. Attribute receivers always use the global `MaxConcurrentCalls`.

```csharp
using Chatter.CQRS.Events;
using Chatter.MessageBrokers;

// topic "order-events", subscription "billing"
[BrokeredMessage("order-events", "billing", errorQueueName: "billing-errors")]
public class OrderPlaced : IEvent
{
    public Guid OrderId { get; set; }
}
```

### Receive mode

The transaction mode decides how messages are received. `TransactionMode.None` receives in `ReceiveAndDelete` mode, so Azure Service Bus removes the message on receipt and a failed handler loses it. `ReceiveOnly` (the Chatter.MessageBrokers default) and `FullAtomicityViaInfrastructure` receive in `PeekLock` mode, so the message is completed, abandoned or dead-lettered after the handler runs.

## Sending and publishing

### From a handler

Handlers send Commands and publish Events through the `Send` and `Publish` extensions on `IMessageHandlerContext` (namespace `Chatter.CQRS.Context`). The outbound message inherits the inbound message context; see [Inbound context inheritance](#inbound-context-inheritance).

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

Inject `IBrokeredMessageDispatcher` (scoped) and call `Send` or `Publish` with a destination path, as in the [Quick start](#quick-start).

### Choosing the infrastructure

When your application registers more than one broker, `context.AzureServiceBus()` marks the inbound message for Azure Service Bus and returns the `IMessageBrokerContext`, whose `Send`, `Publish` and `Forward` go out over Azure Service Bus. It returns `null` when the handler was not invoked by a Brokered Message Receiver. Outside a handler, select the infrastructure on the options with `options.UseMessagingInfrastructure(t => t.AzureServiceBus())`.

```csharp
await context.AzureServiceBus().Publish(new OrderPlaced { OrderId = message.OrderId }, "order-events");
```

### Azure Service Bus message properties

Set these `ASBMessageContext` keys on `SendOptions` or `PublishOptions` with `WithMessageContext` (namespace `Chatter.MessageBrokers.Routing.Options`). `WithMessageContext` returns `RoutingOptions`, so call it as its own statement.

| Key | Value type | Maps to |
| --- | --- | --- |
| `ASBMessageContext.ScheduledEnqueueTimeUtc` | `DateTime` (UTC) | `ServiceBusMessage.ScheduledEnqueueTime`. A value of any other type is ignored. |
| `ASBMessageContext.PartitionKey` | `string` | `ServiceBusMessage.PartitionKey`. With a Group Id set, it is applied only when non-empty and must equal the Group Id. |
| `ASBMessageContext.ViaPartitionKey` | `string` | `ServiceBusMessage.TransactionPartitionKey`. |
| `ASBMessageContext.To` | `string` | `ServiceBusMessage.To`. |

```csharp
using Chatter.MessageBrokers.AzureServiceBus;
using Chatter.MessageBrokers.Routing.Options;

var options = new SendOptions();
options.WithMessageContext(ASBMessageContext.ScheduledEnqueueTimeUtc, DateTime.UtcNow.AddMinutes(10));

await dispatcher.Send(new PlaceOrder { OrderId = orderId }, "orders", options: options);
```

## Sessions

Azure Service Bus sessions deliver messages that share a `SessionId` to one receiver in strict FIFO order. Chatter maps the session id to the Group Id: inbound, it appears under `MessageContext.GroupId`; outbound, `SendOptions.WithGroupId` sets `ServiceBusMessage.SessionId`. Session-enabled queues and subscriptions (`RequiresSession = true`) must already exist; this package does not create or enable them.

### Registering a session receiver

```csharp
builder.Services.AddChatterCqrs(builder.Configuration, typeof(Program).Assembly)
    .AddMessageBrokers()
    .AddAzureServiceBus(asb => asb
        .WithConnectionString(builder.Configuration.GetConnectionString("ServiceBus"))
        .AddSessionQueueReceiver<ProcessOrder>("orders-session", maxConcurrentCalls: 4)
        .AddSessionTopicSubscription<OrderPlaced>("order-events", "billing-session"));
```

### Sessions held at once

A session receiver holds up to `MaxConcurrentCalls` sessions at once, and each held session still delivers its messages one at a time in FIFO order. At the default of `1`, a receiver holds one session.

- **Handlers must be thread-safe.** Above `1`, handlers for different sessions run concurrently in one process.
- **Sizing multiplies across replicas.** With M replicas and `MaxConcurrentCalls` N, up to M×N sessions are locked at once. Size N against the number of sessions on the entity.

### Reading the session id

```csharp
using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers;

public class ProcessOrderHandler : IMessageHandler<ProcessOrder>
{
    public Task Handle(ProcessOrder message, IMessageHandlerContext context)
    {
        string? sessionId = null;
        if (context.GetInboundBrokeredMessage()?.MessageContext.TryGetValue(MessageContext.GroupId, out var groupId) == true)
        {
            sessionId = groupId as string;
        }

        // correlate work within this session
        return Task.CompletedTask;
    }
}
```

### Sending to a session

```csharp
using Chatter.MessageBrokers.Routing.Options;

await context.Send(new ProcessOrder { OrderId = id }, "orders-session", new SendOptions().WithGroupId(id.ToString()));
```

`WithGroupId` is enough on its own. A partition key is optional; if you set one, it must equal the Group Id, and a mismatch throws `ArgumentOutOfRangeException` from the Azure SDK before the message is sent:

```csharp
using Chatter.MessageBrokers.AzureServiceBus;
using Chatter.MessageBrokers.Routing.Options;

var options = new SendOptions().WithGroupId(id.ToString());
options.WithMessageContext(ASBMessageContext.PartitionKey, id.ToString());
```

### Inbound context inheritance

A handler that sends or publishes through `IMessageHandlerContext` inherits the inbound message context. The outbound message carries the inbound `CorrelationId`, `Subject`, `ReplyTo`, `ReplyToSessionId`, `To`, `TimeToLive` and Group Id, so a message received in a session is sent with the same `SessionId`. On a plain queue or topic an inherited Group Id has no effect; on a session-enabled or partitioned destination it does.

To opt out, supply your own Group Id on the outbound options, because options you supply win the merge. Alternatively, send through the `IBrokeredMessageDispatcher` overload that takes a `TransactionContext` instead of an `IMessageHandlerContext`; that overload does not merge the inbound context.

### Session state

During handling, read, write and clear durable state stored on the entity for the held session. These extensions on `IMessageHandlerContext` throw `InvalidOperationException` for a message that did not arrive through a session receiver.

```csharp
using Chatter.CQRS;
using Chatter.CQRS.Context;

public class ProcessOrderHandler : IMessageHandler<ProcessOrder>
{
    public async Task Handle(ProcessOrder message, IMessageHandlerContext context)
    {
        BinaryData? state = await context.GetSessionStateAsync(); // null when no state is set

        await context.SetSessionStateAsync(BinaryData.FromString($"last-processed:{message.OrderId}"));

        // when the session's work is complete:
        // await context.ClearSessionStateAsync();
    }
}
```

### Session timing

| Option | Builder method | Default | Description |
| --- | --- | --- | --- |
| `SessionIdleTimeout` | `WithSessionIdleTimeout(TimeSpan)` | `00:01:00` | How long a held session may yield no message before it is released and the receiver moves to the next session. |
| `MaxSessionLockRenewalDuration` | `WithMaxSessionLockRenewalDuration(TimeSpan)` | `00:05:00` | Ceiling on renewing a held session's lock during long processing. After it, renewal stops and the session lock expires. |

Both apply to session receivers only.

## Message lock renewal

For non-session receivers in `PeekLock` mode, the package renews each delivery's message lock while its handler runs, so a handler that outlasts the entity's lock duration keeps its lock. Renewal is tracked per delivery and stops at the first of: the delivery's settlement, the delivery's release after handling, the receiver closing, or the `MaxMessageLockRenewalDuration` ceiling (default 5 minutes). A handler that runs past the ceiling loses its lock and the message is redelivered.

Set the ceiling with `WithMaxMessageLockRenewalDuration(TimeSpan)` or the `MaxMessageLockRenewalDuration` key. Zero or a negative duration turns message lock renewal off. `ReceiveAndDelete` deliveries have no lock to renew.

## Transactions

The transaction mode comes from the receiver's `transactionMode` argument, or from the global Chatter.MessageBrokers `TransactionMode` (see [Chatter.MessageBrokers configuration](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers/src/README.md#configuration)).

A receiver's own `transactionMode` wins over the global mode. Set the global mode on `AddMessageBrokers`; a `Chatter:MessageBrokers:TransactionMode` key in configuration wins over this fluent call:

```csharp
using Chatter.MessageBrokers.Receiving;

builder.Services.AddChatterCqrs(builder.Configuration, typeof(Program).Assembly)
    .AddMessageBrokers(options => options.WithTransactionMode(TransactionMode.FullAtomicityViaInfrastructure))
    .AddAzureServiceBus(asb => asb
        .WithConnectionString(builder.Configuration.GetConnectionString("ServiceBus"))
        .AddQueueReceiver<PlaceOrder>("orders"));
```

| Mode | Receive mode | Behaviour |
| --- | --- | --- |
| `None` | `ReceiveAndDelete` | No transaction. The message is removed on receipt. |
| `ReceiveOnly` | `PeekLock` | The message is settled after the handler runs. Sends from the handler are not part of the receive transaction. |
| `FullAtomicityViaInfrastructure` | `PeekLock` | Settlement and the handler's sends commit or roll back together in one Azure Service Bus cross-entity transaction. |

Cross-entity transactions are enabled on the shared client when any receiver's effective mode is `FullAtomicityViaInfrastructure`, or when you call `WithCrossEntityTransactions()` (or set `EnableCrossEntityTransactions` to `true`). With them on, Azure Service Bus pins the client to one top-level entity, so the host may register receivers for only one queue or one topic. Startup throws `InvalidOperationException` listing the entities when more than one distinct top-level receiver entity is registered; subscriptions on the same topic count once.

> **Important:** The local Azure Service Bus emulator does not support transactions that span entities. Test `FullAtomicityViaInfrastructure` against a real namespace.

## Authentication

### Shared access signature

A connection string that contains `SharedAccessKeyName` and `SharedAccessKey`, or a `SharedAccessSignature`, authenticates with SAS. This is the default.

### Token credential

Pass any `Azure.Core.TokenCredential` to `AddTokenProvider` with an endpoint-only connection string. The credential is used only when the connection string contains no SAS; with SAS present, SAS is used. The `Func<TokenCredential>` overload is called immediately, at registration.

```csharp
using Azure.Identity; // from the Azure.Identity package

builder.Services.AddChatterCqrs(builder.Configuration, typeof(Program).Assembly)
    .AddMessageBrokers()
    .AddAzureServiceBus(asb => asb
        .WithConnectionString("Endpoint=sb://<namespace>.servicebus.windows.net/")
        .AddTokenProvider(new DefaultAzureCredential())
        .AddQueueReceiver<PlaceOrder>("orders"));
```

### Microsoft Entra ID with the Auth package

[Chatter.MessageBrokers.AzureServiceBus.Auth](https://www.nuget.org/packages/Chatter.MessageBrokers.AzureServiceBus.Auth) adds builder extensions for client secret, certificate, interactive and managed identity authentication, for example `asb.UseAadTokenProviderWithManagedIdentity()`. See its [README](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers.AzureServiceBus.Auth/src/README.md).

## Configuration

### Configuration section

`AddAzureServiceBus` binds `ServiceBusOptions` from `Chatter:Infrastructure:AzureServiceBus` whenever that section exists. Choose another section with `UseConfig("<section>")`. Keys you omit keep their defaults.

```json
{
  "Chatter": {
    "Infrastructure": {
      "AzureServiceBus": {
        "ConnectionString": "Endpoint=sb://<namespace>.servicebus.windows.net/;SharedAccessKeyName=<key-name>;SharedAccessKey=<key>",
        "MaxConcurrentCalls": 4,
        "PrefetchCount": 0,
        "EnableCrossEntityTransactions": false,
        "SessionIdleTimeout": "00:01:00",
        "MaxSessionLockRenewalDuration": "00:05:00",
        "MaxMessageLockRenewalDuration": "00:05:00",
        "RetryPolicy": {
          "NoRetry": false,
          "MaximumRetryCount": 5,
          "MinimumBackoffInSeconds": 1,
          "MaximumBackoffInSeconds": 30,
          "DeltaBackoffInSeconds": 0
        }
      }
    }
  }
}
```

With the connection string in configuration, registration needs no fluent calls:

```csharp
builder.Services.AddChatterCqrs(builder.Configuration, typeof(Program).Assembly)
    .AddMessageBrokers()
    .AddAzureServiceBus(asb => asb.AddQueueReceiver<PlaceOrder>("orders"));
```

### Options

| Option | Type | Default | Description |
| --- | --- | --- | --- |
| `ConnectionString` | `string` | — (required) | Namespace connection string. Registration throws when neither configuration nor `WithConnectionString` supplies one. |
| `MaxConcurrentCalls` | `int` | `1` | Messages processed at once per receiver; sessions held at once for a session receiver. Must be at least `1`, checked when the receiver starts. |
| `PrefetchCount` | `int` | `0` | Messages fetched ahead of the handler. Passed to the Azure SDK receiver options. |
| `EnableCrossEntityTransactions` | `bool` | `false` | Forces cross-entity transactions on. See [Transactions](#transactions). |
| `SessionIdleTimeout` | `TimeSpan` | `00:01:00` | See [Session timing](#session-timing). |
| `MaxSessionLockRenewalDuration` | `TimeSpan` | `00:05:00` | See [Session timing](#session-timing). |
| `MaxMessageLockRenewalDuration` | `TimeSpan` | `00:05:00` | See [Message lock renewal](#message-lock-renewal). |
| `RetryPolicy:NoRetry` | `bool` | `false` | `true` turns Azure SDK client retry off. See [Client retry policy](#client-retry-policy). |
| `RetryPolicy:MaximumRetryCount` | `int?` | SDK default | Maps to `ServiceBusRetryOptions.MaxRetries`. |
| `RetryPolicy:MinimumBackoffInSeconds` | `double?` | SDK default | Maps to `ServiceBusRetryOptions.Delay`. |
| `RetryPolicy:MaximumBackoffInSeconds` | `double?` | SDK default | Maps to `ServiceBusRetryOptions.MaxDelay`. |
| `RetryPolicy:DeltaBackoffInSeconds` | `double?` | — | Accepted and ignored. |
| `RetryOptions` | `ServiceBusRetryOptions` | `null` | Resolved client retry options. Set through the builder or `RetryPolicy`; not bindable directly. |
| `TokenCredential` | `TokenCredential` | `null` | Set with `AddTokenProvider`; not reachable from configuration. |

### Builder methods

| Method | Description |
| --- | --- |
| `WithConnectionString(string)` | Sets the connection string. |
| `WithMaxConcurrentCalls(int)` | Sets the global `MaxConcurrentCalls`. |
| `WithPrefetchCount(int)` | Sets `PrefetchCount`. |
| `WithCrossEntityTransactions(bool enabled = true)` | Sets `EnableCrossEntityTransactions`. |
| `WithSessionIdleTimeout(TimeSpan)` | Sets `SessionIdleTimeout`. |
| `WithMaxSessionLockRenewalDuration(TimeSpan)` | Sets `MaxSessionLockRenewalDuration`. |
| `WithMaxMessageLockRenewalDuration(TimeSpan)` | Sets `MaxMessageLockRenewalDuration`. |
| `WithNoRetry()` | Turns client retry off (`MaxRetries = 0`). |
| `WithExponentialDelay(int maximumRetryCount, double maximumBackoffInSeconds, double minimumBackoffInSeconds, double deltaBackoffInSeconds)` | Exponential client retry with `MaxRetries`, `MaxDelay` and `Delay` from the arguments. `deltaBackoffInSeconds` is ignored. |
| `AddTokenProvider(TokenCredential)` | Supplies a token credential. See [Authentication](#authentication). |
| `AddTokenProvider(Func<TokenCredential>)` | Same, from a factory called immediately. |
| `UseConfig(string configSectionName = "Chatter:Infrastructure:AzureServiceBus")` | Binds options from the named section. |
| `AddServiceBusOptions(string configSectionName)` | Obsolete; use `UseConfig`. |

### Precedence

An explicit fluent call wins over configuration, in either direction: `WithMaxConcurrentCalls(1)` overrides a configured `5`. A key present in configuration wins over the default, and an option set by neither keeps the default. `WithNoRetry()` and `WithExponentialDelay(...)` override the whole `RetryPolicy` section, which is then never read.

Chatter.MessageBrokers resolves the other way: its configuration wins over its fluent calls. An application that configures both packages sees the same-looking call behave differently in each.

### Per-receiver concurrency

A `maxConcurrentCalls` stated on a receiver registration wins over the global value, whatever source the global value came from. The global value is resolved first (fluent, then configuration, then default), and a stated per-receiver value then replaces it for that receiver. A per-receiver value cannot be set in configuration, and attribute receivers always use the global value. Registering the same receiver path twice with different stated values throws.

### Every injection style resolves the same options instance

`ServiceBusOptions` is registered as the concrete type and as `IOptions<ServiceBusOptions>`, `IOptionsSnapshot<ServiceBusOptions>` and `IOptionsMonitor<ServiceBusOptions>`. All four resolve the one fully built instance, and no half-built instance can be resolved. The options are built once and never reload, so `IOptionsMonitor` change callbacks never fire. A `services.Configure<ServiceBusOptions>(...)` registration is not consulted; configure the options fluently or through the configuration section.

### Prefetch tuning

`PrefetchCount` is not a throughput setting for a slow handler. A prefetched message's lock starts ageing when it is fetched, not when a handler picks it up, so with a slow handler the Nth buffered message waits roughly N handler durations. In session mode it ages against the session lock. Raise `MaxConcurrentCalls` for throughput instead.

### Other APIs

| Method | Description |
| --- | --- |
| `context.AzureServiceBus()` | Routes the handler's outbound messages over Azure Service Bus and returns `IMessageBrokerContext`; `null` outside a receiver-invoked handler. |
| `InfrastructureTypes.AzureServiceBus()` | The Azure Service Bus infrastructure type, for `UseMessagingInfrastructure(t => t.AzureServiceBus())`. |
| `pipeline.WithTransactionScopeSupressionBehavior()` | Command Pipeline behavior (namespace `Chatter.MessageBrokers.AzureServiceBus.Receiving`) that suppresses the ambient `TransactionScope` around command handling when the message was received with a `TransactionContext`. |

## Retry and circuit breaker

### Receive-side recovery

Failed receives and handlers are retried by the Chatter.MessageBrokers retry and circuit breaker policies (see [Recovery](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers/src/README.md#recovery)). This package tells those policies which errors are transient: a `ServiceBusException` whose `IsTransient` is `true`, or whose `Reason` is `ServiceCommunicationProblem`, `ServiceBusy` or `ServiceTimeout`. After `maxReceiveAttempts` deliveries, a message is dead-lettered and, by default, routed to the receiver's `errorQueuePath`.

### Client retry policy

The Azure SDK `ServiceBusClient` also retries transient failures on the wire. Its `ServiceBusRetryOptions` are resolved once. A fluent `WithNoRetry()` or `WithExponentialDelay(...)` wins outright; otherwise:

| Configuration | Resulting client retry options |
| --- | --- |
| No `Chatter:Infrastructure:AzureServiceBus` section | Unset (`null`); the Azure SDK uses its defaults. |
| Section present, no `RetryPolicy` subsection | `ServiceBusRetryOptions` with the SDK defaults. |
| `RetryPolicy:NoRetry` is `true` | `MaxRetries = 0`; client retry is off. |
| Any other `RetryPolicy` | Exponential mode. Each stated key maps to its SDK setting; each omitted key keeps the SDK default. |

A stated value is passed to the SDK unchanged, so `MaximumRetryCount: 0` also turns retry off; `NoRetry` says so explicitly. `DeltaBackoffInSeconds` is accepted so existing sections still bind, and is ignored because the Azure SDK has no equivalent.

### Where each key is validated

- A key of the wrong type fails at registration with an `InvalidOperationException` naming the full key path.
- `RetryPolicy` values are validated by the Azure SDK's `ServiceBusRetryOptions` setters at registration; an out-of-range value stops the host, and the error does not name the configuration key.
- `MaxConcurrentCalls` below `1` fails when the receiver starts, naming the receiver and the value.
- `PrefetchCount` is validated by the Azure SDK when a receiver is created.
- `DeltaBackoffInSeconds` is never validated, because it is ignored.

## Diagnostics

This package emits no telemetry of its own. Broker spans and metrics come from the `Chatter.MessageBrokers` `ActivitySource` and `Meter`; see [Chatter.MessageBrokers diagnostics](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers/src/README.md#diagnostics). With tracing on, the W3C `traceparent` header rides the message application properties in both directions (see [Trace context propagation](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers/src/README.md#trace-context-propagation)).

### Diagnostic-Id interop

The Azure Service Bus SDK stamps its legacy `Diagnostic-Id` only when a message carries no correlation identifier. Because Chatter writes `traceparent`, the SDK is expected not to stamp `Diagnostic-Id` on messages Chatter sends. The presence of both mechanisms in the shipped SDK assemblies is verified; the short-circuit itself comes from the Azure SDK's published source and is not verified in this repository.

If your correlation depends on `Diagnostic-Id`, enable the SDK's `ActivitySource` support so it reads `traceparent`. Set it at process start, before the first Azure SDK type is used:

```shell
AZURE_EXPERIMENTAL_ENABLE_ACTIVITY_SOURCE=true
```

```csharp
AppContext.SetSwitch("Azure.Experimental.EnableActivitySource", true);
```

### Semantic convention spelling

Chatter's broker spans follow OpenTelemetry semantic conventions v1.30.0 and emit `messaging.operation.type`. `Azure.Messaging.ServiceBus` emits the older `messaging.operation`. A mixed trace shows both spellings; Chatter emits one spelling per concept.

## Related packages

- [Chatter.CQRS](https://www.nuget.org/packages/Chatter.CQRS): In-process Commands, Queries, Events and the Command Pipeline.
- [Chatter.MessageBrokers](https://www.nuget.org/packages/Chatter.MessageBrokers): The broker abstractions this transport implements: receivers, routing, Inbox/Outbox and Recovery.
- [Chatter.MessageBrokers.AzureServiceBus.Auth](https://www.nuget.org/packages/Chatter.MessageBrokers.AzureServiceBus.Auth): Microsoft Entra ID token authentication for this transport.
- [Chatter.MessageBrokers.Reliability.EntityFramework](https://www.nuget.org/packages/Chatter.MessageBrokers.Reliability.EntityFramework): EF Core Inbox, Outbox and Unit of Work.
- [Chatter.MessageBrokers.Reliability.Cosmos](https://www.nuget.org/packages/Chatter.MessageBrokers.Reliability.Cosmos): Azure Cosmos DB Inbox and Outbox Relay.

## Learn more

- [Domain glossary (CONTEXT.md)](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers.AzureServiceBus/CONTEXT.md)
- [Changelog](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers.AzureServiceBus/src/Chatter.MessageBrokers.AzureServiceBus/CHANGELOG.md)
- [Context map of all Chatter modules](https://github.com/brenpike/Chatter/blob/master/CONTEXT-MAP.md)
- [Chatter suite README](https://github.com/brenpike/Chatter/blob/master/README.md)

## License

Licensed under the [MIT License](https://github.com/brenpike/Chatter/blob/master/LICENSE).
