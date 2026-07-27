# <a name="chatter-azureservicebus"></a> Chatter.MessageBrokers.AzureServiceBus

Azure Service Bus transport for the technology-agnostic `Chatter.MessageBrokers` abstractions.

## Overview

`Chatter.MessageBrokers.AzureServiceBus` is the Azure Service Bus (ASB) implementation of the broker-agnostic interfaces defined in `Chatter.MessageBrokers`. It plugs an ASB sender and receiver into the messaging infrastructure so that messages dispatched and handled through your `Chatter.CQRS` command/event handlers flow over Azure Service Bus queues and topic subscriptions.

The core `Chatter.MessageBrokers` package registers the broker abstraction via `IChatterBuilder.AddMessageBrokers(...)`. This package adds an `AddAzureServiceBus(...)` extension on `IChatterBuilder` that wires the concrete ASB sending/receiving components and `ServiceBusOptions`, registering an `IMessagingInfrastructure` keyed to the `ASBMessageContext.InfrastructureType`.

Key components registered:

- `ServiceBusReceiver` / `ServiceBusReceiverFactory` — pulls messages from queues and topic subscriptions.
- `ServiceBusMessageSender` / `ServiceBusMessageSenderFactory` / `BrokeredMessageSenderPool` — sends/publishes outbound messages.
- `AzureServiceBusEntityPathBuilder` — resolves queue/topic/subscription/rule paths (`IBrokeredMessagePathBuilder`).
- `ServiceBusRetryExceptionPredicatesProvider` / `ServiceBusCircuitBreakerExceptionPredicatesProvider` — feed ASB transient-exception detection into the Chatter retry and circuit-breaker recovery policies.

## Installation

```
dotnet add package Chatter.MessageBrokers.AzureServiceBus
```

## Getting Started

Register Chatter CQRS, the message broker abstraction, and then the Azure Service Bus transport. `AddAzureServiceBus` is chained off the `IChatterBuilder` returned by `AddMessageBrokers`:

```csharp
using Microsoft.Extensions.DependencyInjection;

services
    .AddChatterCqrs(configuration)
    .AddMessageBrokers()
    .AddAzureServiceBus(asb =>
    {
        // connection can come from configuration (see Configuration below) or be set explicitly
        asb.WithConnectionString(configuration.GetConnectionString("ServiceBus"));
        asb.WithMaxConcurrentCalls(5);
        asb.WithPrefetchCount(10);

        // register the queues/subscriptions to receive from
        asb.AddQueueReceiver<CreateOrder>("orders-queue");
        asb.AddTopicSubscription<OrderCreated>("order-events-topic", "order-created-subscription");
    });
```

If `WithConnectionString` is omitted, the builder reads `ServiceBusOptions` from configuration (default section `Chatter:Infrastructure:AzureServiceBus`) — see [Configuration](#configuration). A connection string from either source is required; otherwise `Build()` throws.

### Receiving

Receivers are registered while configuring options:

```csharp
// commands -> queue (TMessage : ICommand)
asb.AddQueueReceiver<CreateOrder>(
    queueName: "orders-queue",
    errorQueuePath: "orders-error",
    transactionMode: TransactionMode.ReceiveOnly,
    maxReceiveAttempts: 10);

// events -> topic subscription (TMessage : IEvent)
asb.AddTopicSubscription<OrderCreated>(
    topicName: "order-events-topic",
    subscriptionName: "order-created-subscription",
    maxReceiveAttempts: 10);
```

Received messages are dispatched to the matching `Chatter.CQRS` handler:

```csharp
public class CreateOrderHandler : IMessageHandler<CreateOrder>
{
    public Task Handle(CreateOrder message, IMessageHandlerContext context)
    {
        // handle the command
        return Task.CompletedTask;
    }
}
```

### Sending

Within a handler you can reach ASB-specific send/publish/forward operations via the `AzureServiceBus()` extension on `IMessageHandlerContext`, which returns an `IAzureServiceBusContextDispatcher`:

```csharp
using Chatter.CQRS.Context;

public class OrderCreatedHandler : IMessageHandler<OrderCreated>
{
    public async Task Handle(OrderCreated message, IMessageHandlerContext context)
    {
        await context.AzureServiceBus()
                     .Publish(new OrderShipped { OrderId = message.OrderId });
    }
}
```

`IAzureServiceBusContextDispatcher` composes the broker abstractions `IMessageBrokerContextPublisher`, `IMessageBrokerContextSender`, and `IMessageBrokerContextForwarder`.

## Configuration

`ServiceBusOptions` is bound from configuration when a connection string is not supplied in code. The default configuration section is `Chatter:Infrastructure:AzureServiceBus` (override with `UseConfig("Your:Section")`).

```json
{
  "Chatter": {
    "Infrastructure": {
      "AzureServiceBus": {
        "ConnectionString": "Endpoint=sb://your-namespace.servicebus.windows.net/;SharedAccessKeyName=...;SharedAccessKey=...",
        "MaxConcurrentCalls": 1,
        "PrefetchCount": 0
      }
    }
  }
}
```

`ServiceBusOptions` properties:

| Property | Default | Description |
| --- | --- | --- |
| `ConnectionString` | (required) | Azure Service Bus namespace connection string. |
| `MaxConcurrentCalls` | `1` | Maximum number of messages processed concurrently. |
| `PrefetchCount` | `0` | Number of messages eagerly fetched from the broker. |
| `RetryOptions` (`ServiceBusRetryOptions`) | `null` | ASB client-level retry options, set through the `WithNoRetry()` / `WithExponentialDelay(...)` builder methods. When `null`, the Azure SDK's own default retry applies, because the shared client is created without an explicit `RetryOptions`. |
| `TokenCredential` | `null` | AAD `Azure.Core.TokenCredential` (see [Authentication](#authentication)). |
| `SessionIdleTimeout` | `00:01:00` (60 s) | How long a held session may yield no message before it is released and the receiver rolls. Applies only to session-enabled receivers. |
| `MaxSessionLockRenewalDuration` | `00:05:00` (5 min) | Ceiling on how long a held session's lock is renewed for long-running processing. Applies only to session-enabled receivers. |

### `ServiceBusOptionsBuilder` methods

The `AddAzureServiceBus(asb => ...)` delegate exposes a `ServiceBusOptionsBuilder`:

| Method | Purpose |
| --- | --- |
| `WithConnectionString(string)` | Sets the namespace connection string in code. |
| `WithMaxConcurrentCalls(int)` | Sets `MaxConcurrentCalls`. |
| `WithPrefetchCount(int)` | Sets `PrefetchCount`. |
| `WithNoRetry()` | Sets `RetryOptions` to a `ServiceBusRetryOptions` with `MaxRetries = 0`. |
| `WithExponentialDelay(maximumRetryCount, maximumBackoffInSeconds, minimumBackoffInSeconds, deltaBackoffInSeconds)` | Sets `RetryOptions` to a `ServiceBusRetryOptions` with `Mode = ServiceBusRetryMode.Exponential`, `MaxRetries = maximumRetryCount`, `Delay = minimumBackoffInSeconds`, and `MaxDelay = maximumBackoffInSeconds`. `deltaBackoffInSeconds` is accepted for source compatibility and has no effect. |
| `UseConfig(configSectionName)` | Binds `ServiceBusOptions` from the given configuration section (default `Chatter:Infrastructure:AzureServiceBus`). |
| `AddTokenProvider(TokenCredential)` / `AddTokenProvider(Func<TokenCredential>)` | Supplies an AAD `Azure.Core.TokenCredential`; the `Func<TokenCredential>` overload is invoked eagerly at registration, not deferred (see [Authentication](#authentication)). |
| `WithSessionIdleTimeout(TimeSpan)` | Overrides how long a held session may yield no message before rolling to the next. Default: 60 s. See [Sessions](#sessions). |
| `WithMaxSessionLockRenewalDuration(TimeSpan)` | Overrides the ceiling on held-session lock renewal. Default: 5 min. See [Sessions](#sessions). |
| `AddQueueReceiver<TMessage>(...)` | Registers a queue receiver for an `ICommand`. |
| `AddSessionQueueReceiver<TMessage>(...)` | Registers a session-enabled queue receiver for an `ICommand`. See [Sessions](#sessions). |
| `AddTopicSubscription<TMessage>(...)` | Registers a topic subscription receiver for an `IEvent`. |
| `AddSessionTopicSubscription<TMessage>(...)` | Registers a session-enabled topic subscription receiver for an `IEvent`. See [Sessions](#sessions). |

### Retry and Circuit Breaker (receiving)

Receive-side recovery is driven by the broker-agnostic retry and circuit-breaker policies in `Chatter.MessageBrokers.Recovery`. This package contributes ASB-aware transient-exception detection:

- `ServiceBusRetryExceptionPredicatesProvider` (`IRetryExceptionPredicatesProvider`)
- `ServiceBusCircuitBreakerExceptionPredicatesProvider` (`ICircuitBreakerExceptionPredicatesProvider`)

Both treat a `ServiceBusException` as transient when its `IsTransient` is `true`, or when its `Reason` is `ServiceBusFailureReason.ServiceCommunicationProblem`, `ServiceBusFailureReason.ServiceBusy`, or `ServiceBusFailureReason.ServiceTimeout`. The per-receiver `maxReceiveAttempts` (default `10`) bounds redelivery attempts before a message is routed to its configured `errorQueuePath`.

## Authentication

The connection string above uses SAS-based auth. Azure Active Directory (AAD) authentication — via an `Azure.Core.TokenCredential` (falling back to `DefaultAzureCredential` when no explicit credential is given) — is provided by the sibling package [`Chatter.MessageBrokers.AzureServiceBus.Auth`](#chatter-azureservicebus-auth). When a token credential is supplied (via `AddTokenProvider(...)`) and the connection string contains no SAS token/key, that credential is used to authenticate to the namespace.

## Testing

### Real-namespace cross-entity transaction tests

The `FullAtomicityViaInfrastructure` mode relies on Azure Service Bus cross-entity (multi-top-level-entity) transactions. The local Service Bus emulator **cannot** exercise these — it throws `Local transactions cannot span multiple top-level entities` — so the cross-entity atomic commit/rollback tests run only against a **real** Azure Service Bus namespace. They are tagged `Category=RealNamespaceIntegration` (deliberately *not* `Category=Integration`, so the emulator test lane never selects them).

These tests **skip cleanly** when no real namespace is configured, so a plain `dotnet test` stays green without any Azure resources.

**Run locally:** set the `CHATTER_ASB_REAL_NAMESPACE_CONNECTION_STRING` environment variable to a connection string for a real Azure Service Bus namespace. The string must carry the **Manage** claim, because the test fixture creates and deletes uniquely-named queues (per run) via the Service Bus administration client.

```bash
export CHATTER_ASB_REAL_NAMESPACE_CONNECTION_STRING="Endpoint=sb://<namespace>.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=<key>"
dotnet test src/Chatter.MessageBrokers.AzureServiceBus/tests/Chatter.MessageBrokers.AzureServiceBus.Tests.csproj --filter 'Category=RealNamespaceIntegration'
```

When the variable is unset (or blank) the tests are skipped at discovery time.

**Run in CI:** the `real-namespace-integration` job in `.github/workflows/ci.yml` runs this lane. Configure a GitHub Actions **repository secret** named `CHATTER_ASB_REAL_NAMESPACE_CONNECTION_STRING` (Manage-claim connection string) for it to execute. Without the secret (forks, or repos that have not configured it) the job is a clean no-op and never fails CI.

## Sessions

Azure Service Bus message sessions deliver messages sharing the same `SessionId` to a single receiver in strict FIFO order. Chatter surfaces this through the existing Group Id term: inbound `SessionId` appears as `MessageContext.GroupId`; outbound session addressing reuses `SendOptions.WithGroupId`. No new session-specific API is introduced on the core.

Session-enabled entities (queues or subscriptions with `RequiresSession = true`) must be provisioned externally. The adapter does not auto-create or auto-enable them, consistent with the module's no-auto-provision stance.

### Registering a session-enabled receiver

Use `AddSessionQueueReceiver` for Commands and `AddSessionTopicSubscription` for Events in place of their non-session counterparts:

```csharp
services
    .AddChatterCqrs(configuration)
    .AddMessageBrokers()
    .AddAzureServiceBus(asb =>
    {
        asb.WithConnectionString(configuration.GetConnectionString("ServiceBus"));

        // session-enabled queue (Commands)
        asb.AddSessionQueueReceiver<ProcessOrder>("orders-session-queue");

        // session-enabled topic subscription (Events)
        asb.AddSessionTopicSubscription<OrderPlaced>("order-events-topic", "order-placed-session-sub");
    });
```

Each registered receiver processes one session at a time, holding it for FIFO delivery and rolling to the next when it is drained or goes idle. To increase throughput, run additional receiver instances; there is no max-concurrent-sessions knob.

### Reading the session id in a handler

The inbound `SessionId` is surfaced as `MessageContext.GroupId`. Handlers read it through the broker-agnostic `GroupId` property — no Azure-specific import is required:

```csharp
public class ProcessOrderHandler : IMessageHandler<ProcessOrder>
{
    public Task Handle(ProcessOrder message, IMessageHandlerContext context)
    {
        var sessionId = context.BrokeredMessage?.GetBrokeredMessageDetail()?.GroupId;
        // use sessionId to correlate work within this session
        return Task.CompletedTask;
    }
}
```

### Sending a message to a session

Set `WithGroupId` on `SendOptions` to route the outbound message to the target session. Azure Service Bus requires `PartitionKey == SessionId` for session messages on partitioned entities; set `WithPartitionKey` to the same value, otherwise the broker will reject the message with `ArgumentOutOfRangeException`:

```csharp
public class DispatchOrderHandler : IMessageHandler<DispatchOrder>
{
    public async Task Handle(DispatchOrder message, IMessageHandlerContext context)
    {
        var options = new SendOptions()
            .WithGroupId(message.OrderId)        // sets ServiceBusMessage.SessionId
            .WithPartitionKey(message.OrderId);  // must equal SessionId for session messages

        await context.AzureServiceBus()
                     .Send(new ProcessOrder { OrderId = message.OrderId }, "orders-session-queue", options);
    }
}
```

`WithGroupId` and `WithPartitionKey` are both on the core `SendOptions` type; `WithPartitionKey` writes to `ASBMessageContext.PartitionKey`, which `OutboundBrokeredMessageExtensions` maps to `ServiceBusMessage.PartitionKey` when the message is sent.

### Durable per-session state

During handler execution a handler can read, write, and clear durable session state stored on the Azure Service Bus entity for the currently held session:

```csharp
using Chatter.CQRS.Context;

public class ProcessOrderHandler : IMessageHandler<ProcessOrder>
{
    public async Task Handle(ProcessOrder message, IMessageHandlerContext context)
    {
        // read existing state (null when no state has been set)
        var stateBytes = await context.GetSessionStateAsync();

        // compute and persist new state
        var newState = BinaryData.FromString($"last-processed:{message.OrderId}");
        await context.SetSessionStateAsync(newState);

        // clear state when the session is complete
        // await context.ClearSessionStateAsync();
    }
}
```

`GetSessionStateAsync`, `SetSessionStateAsync`, and `ClearSessionStateAsync` are extension methods on `IMessageHandlerContext` provided by this package. Invoking them while handling a message that was not received through a session-enabled receiver throws `InvalidOperationException`.

### Tuning session behavior

Two `ServiceBusOptions` knobs control how long a session is held. Both support fluent-or-config, with the fluent call winning in either direction:

| Knob | Fluent method | Config property | Default | Description |
| --- | --- | --- | --- | --- |
| Session idle timeout | `WithSessionIdleTimeout(TimeSpan)` | `SessionIdleTimeout` | 60 s | How long a held session may yield no message before it is released and the receiver rolls to the next session. |
| Max session lock renewal duration | `WithMaxSessionLockRenewalDuration(TimeSpan)` | `MaxSessionLockRenewalDuration` | 5 min | Ceiling on how long a held session's lock is renewed for long-running processing. Once reached, renewal stops and the session is allowed to expire or roll naturally. |

```csharp
asb.AddSessionQueueReceiver<ProcessOrder>("orders-session-queue");
asb.WithSessionIdleTimeout(TimeSpan.FromSeconds(30));
asb.WithMaxSessionLockRenewalDuration(TimeSpan.FromMinutes(10));
```

Or via configuration:

```json
{
  "Chatter": {
    "Infrastructure": {
      "AzureServiceBus": {
        "SessionIdleTimeout": "00:00:30",
        "MaxSessionLockRenewalDuration": "00:10:00"
      }
    }
  }
}
```

These knobs apply only to session-enabled receivers. Non-session receivers are unaffected.

## Domain Language

See the [domain glossary](../CONTEXT.md) for definitions of Service Bus Receiver, Session Queue Receiver, Session Topic Subscription, Service Bus Sender, Service Bus Options, Service Bus Retry, Service Bus Circuit Breaker, Session, Session State, and Group Id ↔ SessionId realization.

[← All Chatter modules](../../../README.md)
