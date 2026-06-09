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
        "PrefetchCount": 0,
        "RetryPolicy": {
          "MinimumBackoffInSeconds": 0,
          "MaximumBackoffInSeconds": 0,
          "DeltaBackoffInSeconds": 0,
          "MaximumRetryCount": 0
        }
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
| `Policy` (`RetryPolicy`) | `RetryPolicy.Default` | ASB client-level retry policy derived from the `RetryPolicy` config section. |
| `TokenProvider` | `NullTokenProvider` | AAD/token credential (see [Authentication](#authentication)). |

The `RetryPolicy` configuration section maps to an ASB `RetryExponential` policy via `MinimumBackoffInSeconds`, `MaximumBackoffInSeconds`, `DeltaBackoffInSeconds`, and `MaximumRetryCount`. When all values are `0`, `RetryPolicy.NoRetry` is used; when the section is absent, `RetryPolicy.Default` applies.

### `ServiceBusOptionsBuilder` methods

The `AddAzureServiceBus(asb => ...)` delegate exposes a `ServiceBusOptionsBuilder`:

| Method | Purpose |
| --- | --- |
| `WithConnectionString(string)` | Sets the namespace connection string in code. |
| `WithMaxConcurrentCalls(int)` | Sets `MaxConcurrentCalls`. |
| `WithPrefetchCount(int)` | Sets `PrefetchCount`. |
| `WithNoRetry()` | Uses `RetryPolicy.NoRetry` for the ASB client. |
| `WithExponentialDelay(maximumRetryCount, maximumBackoffInSeconds, minimumBackoffInSeconds, deltaBackoffInSeconds)` | Configures a `RetryExponential` client retry policy. |
| `UseConfig(configSectionName)` | Binds `ServiceBusOptions` from the given configuration section (default `Chatter:Infrastructure:AzureServiceBus`). |
| `AddTokenProvider(ITokenProvider)` / `AddTokenProvider(Func<ITokenProvider>)` | Supplies an AAD token provider (see [Authentication](#authentication)). |
| `AddQueueReceiver<TMessage>(...)` | Registers a queue receiver for an `ICommand`. |
| `AddTopicSubscription<TMessage>(...)` | Registers a topic subscription receiver for an `IEvent`. |

### Retry and Circuit Breaker (receiving)

Receive-side recovery is driven by the broker-agnostic retry and circuit-breaker policies in `Chatter.MessageBrokers.Recovery`. This package contributes ASB-aware transient-exception detection:

- `ServiceBusRetryExceptionPredicatesProvider` (`IRetryExceptionPredicatesProvider`)
- `ServiceBusCircuitBreakerExceptionPredicatesProvider` (`ICircuitBreakerExceptionPredicatesProvider`)

Both treat the following as transient (when `IsTransient` is `true`): `ServiceBusException`, `ServiceBusCommunicationException`, `ServerBusyException`, and `ServiceBusTimeoutException`. The per-receiver `maxReceiveAttempts` (default `10`) bounds redelivery attempts before a message is routed to its configured `errorQueuePath`.

## Authentication

The connection string above uses SAS-based auth. Azure Active Directory (AAD) authentication — token providers such as managed identity / `ITokenProvider` integrations — is provided by the sibling package [`Chatter.MessageBrokers.AzureServiceBus.Auth`](#chatter-azureservicebus-auth). When a token provider is supplied (via `AddTokenProvider(...)`) and the connection string contains no SAS token/key, that token provider is used to authenticate to the namespace.

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

## Domain Language

See the [domain glossary](../CONTEXT.md) for definitions of Service Bus Receiver, Service Bus Sender, Service Bus Options, Service Bus Retry, and Service Bus Circuit Breaker.

[← All Chatter modules](../../../README.md)
