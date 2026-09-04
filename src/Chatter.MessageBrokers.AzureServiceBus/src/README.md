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

`ServiceBusOptions` is bound from configuration whenever the section exists — not only when a connection string is missing from code. The default configuration section is `Chatter:Infrastructure:AzureServiceBus` (override with `UseConfig("Your:Section")`). Keys the section omits keep their default, and an explicit fluent call still wins over a configured key (see [Precedence](#precedence-an-explicit-fluent-call-wins)).

```json
{
  "Chatter": {
    "Infrastructure": {
      "AzureServiceBus": {
        "ConnectionString": "Endpoint=sb://your-namespace.servicebus.windows.net/;SharedAccessKeyName=...;SharedAccessKey=...",
        "MaxConcurrentCalls": 1,
        "PrefetchCount": 0,
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

`ServiceBusOptions` properties:

| Property | Default | Description |
| --- | --- | --- |
| `ConnectionString` | (required) | Azure Service Bus namespace connection string. |
| `MaxConcurrentCalls` | `1` | Maximum number of messages processed concurrently. |
| `PrefetchCount` | `0` | Number of messages eagerly fetched from the broker. |
| `TokenCredential` | `null` | AAD `Azure.Core.TokenCredential` (see [Authentication](#authentication)). |
| `SessionIdleTimeout` | `00:01:00` (60 s) | How long a held session may yield no message before it is released and the receiver rolls. Applies only to session-enabled receivers. |
| `MaxSessionLockRenewalDuration` | `00:05:00` (5 min) | Ceiling on how long a held session's lock is renewed for long-running processing. Applies only to session-enabled receivers. |
| `RetryPolicy:NoRetry` | `false` | Set to `true` to disable Azure SDK client retry outright (`MaxRetries = 0`). This is the intention-revealing way to switch retry off — see [Client retry](#client-retry-retrypolicy). |
| `RetryPolicy:MaximumRetryCount` | SDK default (`3`) | Maximum retry attempts. Omit the key to keep the SDK default; a stated value is carried to `ServiceBusRetryOptions.MaxRetries`, whose own setter rejects anything outside `0` through `100`. A stated `0` is carried faithfully and disables client retry; `NoRetry` says the same thing in an intention-revealing way. |
| `RetryPolicy:MinimumBackoffInSeconds` | SDK default (`0.8` s) | Base backoff the exponential delay is calculated from (`ServiceBusRetryOptions.Delay`). Omit the key to keep the SDK default; a stated value is carried to `Delay`'s own setter, which enforces its own accepted range. |
| `RetryPolicy:MaximumBackoffInSeconds` | SDK default (`60` s) | Ceiling on the delay between attempts (`ServiceBusRetryOptions.MaxDelay`). Omit the key to keep the SDK default; a stated value is carried to `MaxDelay`'s own setter, which enforces its own accepted range. |
| `RetryPolicy:DeltaBackoffInSeconds` | `0` | Accepted for configuration compatibility and **ignored** — `Azure.Messaging.ServiceBus` has no per-attempt delta-backoff knob. |

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

### Precedence: an explicit fluent call wins

The builder seeds `ServiceBusOptions` with its defaults, binds the configuration section over that instance — its public surface only, and never replacing the instance — and then applies the fluent values last. Each fluent value is held in a nullable sentinel (`int?`, `bool?`, `TimeSpan?`), so the builder can tell "never called" from "called with the default value" and applies only the calls that were actually made. A key present in configuration therefore wins over the builder default, while an explicit fluent call wins over configuration — in either direction, so `WithMaxConcurrentCalls(1)` overrides a configured `5` exactly as `WithMaxConcurrentCalls(5)` overrides a configured `1`. A key absent from configuration and never set fluently keeps the default.

The bind surface is deliberately narrow. `RetryPolicy` is the one non-public configuration property that has to bind, so it is bound explicitly from its own `RetryPolicy` subsection rather than by opening the whole type to the binder. Two things together keep `RetryOptions` and `TokenCredential` unreachable from configuration: that narrow surface, and the fact that both properties are null when the bind runs. The binder passes over a property whose setter it cannot reach only while the value behind the public getter is null; had either been given a non-null initializer, the binder would bind into that object and drive its own public setters instead, so their null defaults are load-bearing. With both conditions in place, a stray `RetryOptions` key cannot run the SDK's own validating setters during the bind, and a nested `TokenCredential` object cannot drive the binder into trying to activate an abstract type — both of which failed with a raw binder exception at host start.

The same rule covers retry: `WithNoRetry()` and `WithExponentialDelay(...)` beat a configured `RetryPolicy` section. The effective `ServiceBusRetryOptions` are resolved once, at the end of `Build()`, from the first source that stated any — the fluent call, then the bound `RetryPolicy` section, then the SDK default — so a section-derived one is never constructed at all when a fluent call was made. A configured `RetryPolicy` block the fluent call overrides is therefore never turned into `ServiceBusRetryOptions` at all, which is deliberate: its values never reach the SDK's own setters, so a retry policy the host was never going to use can no longer stop it from starting.

`Chatter.MessageBrokers` applies the opposite rule: its builders carry no nullable sentinel, so configuration is bound last and wins — over the builder default and over an explicit fluent call alike. The divergence is deliberate, and an application that configures both modules needs to know that the same-looking fluent call is authoritative in one module and overridable in the other.

### Every injection style resolves the same options instance

`ServiceBusOptions` is registered twice over the one instance the builder finished: once as the concrete type, and once behind `IOptions<ServiceBusOptions>`, `IOptionsSnapshot<ServiceBusOptions>` and `IOptionsMonitor<ServiceBusOptions>`. All four resolve the same object — connection string guarded, section bound, fluent values applied, retry options resolved — so nothing that reads the options can see a differently built one. Previously the three facets went to the container's own options factory, which produced a fresh, all-default `ServiceBusOptions` whose `ConnectionString` was `null`; nothing inside this package injects those facets, so that instance was reachable only by an application resolving a facet itself.

`IOptionsMonitor<ServiceBusOptions>` is supported for resolution only. The section is bound once, while the options are being built, so the options never reload and the change callback is inert. Named options are not a concept in this package either — every name, including none, resolves the same built instance.

**Behaviour change:** a `services.Configure<ServiceBusOptions>(...)` registration of your own is **no longer consulted**, because the facets are bound directly to the built instance and never go through the options factory. No known application relies on it, but it is a public behaviour change: configure the options fluently or through the `Chatter:Infrastructure:AzureServiceBus` section instead.

`Chatter.MessageBrokers` applies the same rule to `MessageBrokerOptions`, `ReliabilityOptions`, `RecoveryOptions` and `CircuitBreakerOptions`. It is not applied across the whole suite: `Chatter.MessageBrokers.RabbitMQ` and `Chatter.MessageBrokers.SqlServiceBroker` still register only the concrete options singleton, deliberately — neither registers a `Configure<T>`, so neither has anything divergent to close.

### Retry and Circuit Breaker (receiving)

Receive-side recovery is driven by the broker-agnostic retry and circuit-breaker policies in `Chatter.MessageBrokers.Recovery`. This package contributes ASB-aware transient-exception detection:

- `ServiceBusRetryExceptionPredicatesProvider` (`IRetryExceptionPredicatesProvider`)
- `ServiceBusCircuitBreakerExceptionPredicatesProvider` (`ICircuitBreakerExceptionPredicatesProvider`)

Both treat a `ServiceBusException` as transient when its `IsTransient` is `true`, or when its `Reason` is `ServiceBusFailureReason.ServiceCommunicationProblem`, `ServiceBusFailureReason.ServiceBusy`, or `ServiceBusFailureReason.ServiceTimeout`. The per-receiver `maxReceiveAttempts` (default `10`) bounds redelivery attempts before a message is routed to its configured `errorQueuePath`.

#### Client retry (`RetryPolicy`)

Separately from the Chatter recovery policies above, the Azure SDK's own `ServiceBusClient` retries transient failures on the wire. The `Chatter:Infrastructure:AzureServiceBus:RetryPolicy` section configures *that* retry, and **it now takes effect**. Earlier versions read the surrounding section in a way that discarded it, so an entire `RetryPolicy` block did nothing at all; a populated section is now carried onto the single shared `ServiceBusClient` as `ServiceBusClientOptions.RetryOptions`. If you have had a `RetryPolicy` section in `appsettings` all along, expect it to start being honored on this version — and to start reaching the SDK's own setters, so a value the SDK could never run with now stops the host at startup instead of being quietly discarded.

A fluent `WithNoRetry()` or `WithExponentialDelay(...)` settles the retry options on its own and the configuration below is not consulted at all — see [Precedence: an explicit fluent call wins](#precedence-an-explicit-fluent-call-wins). With no fluent retry call, three rules decide the resulting `ServiceBusRetryOptions`, and nothing is inferred beyond them:

| Configuration | Resulting `ServiceBusRetryOptions` |
| --- | --- |
| No `RetryPolicy` section (or an empty one) | The SDK defaults: `Mode = Exponential`, `MaxRetries = 3`, `Delay = 0.8 s`, `MaxDelay = 60 s`. |
| `RetryPolicy:NoRetry` is `true` | `MaxRetries = 0` — client retry disabled. |
| Any other populated `RetryPolicy` | `Mode = Exponential`, with `MaxRetries`, `Delay` and `MaxDelay` taken from `MaximumRetryCount`, `MinimumBackoffInSeconds` and `MaximumBackoffInSeconds` respectively — each one whenever its key was stated, and the SDK default for that parameter when the key was omitted. |

An absent key and a stated one are distinguishable because every numeric parameter of the `RetryPolicy` section is nullable. Absent means the key was never written: the SDK default for that parameter stands and nothing is passed to the SDK. Stated means an operator wrote it: the value is passed to the SDK's own setter, so one the SDK could not run with raises that setter's own failure instead of being silently replaced by that same default. A stated `MaximumRetryCount` of `0` is a value the SDK accepts, so it binds faithfully to `MaxRetries = 0` and client retry is off; `RetryPolicy:NoRetry` says the same thing outright and is the intention-revealing form. Issue #423 owns the question of whether a stated zero should keep binding this way.

`DeltaBackoffInSeconds` is accepted so that an existing `RetryPolicy` section still binds without error, but it is **ignored**: `Azure.Messaging.ServiceBus` has no per-attempt delta-backoff knob to map it onto, and the value is not folded into any of the other parameters either. The fluent `WithExponentialDelay(...)` treats its own `deltaBackoffInSeconds` argument the same way, for the same reason.

**Where each `ServiceBusOptions` key is checked.** Every key is checked at its sink rather than while the options are built. A stated `RetryPolicy` value reaches the Azure SDK's own `MaxRetries`, `Delay` or `MaxDelay` setter as the retry options are constructed, so a value the SDK cannot run with raises that setter's own `ArgumentOutOfRangeException` and the host does not start — naming the SDK member rather than the configuration key you wrote. `MaxConcurrentCalls` is checked when a receiver initializes, which raises an `InvalidOperationException` naming the receiver and the offending value when it is below `1`. `PrefetchCount` is handed to the Azure SDK's `ServiceBusReceiverOptions.PrefetchCount` when a receiver is created, that setter being the authority on what it accepts. `DeltaBackoffInSeconds` is checked nowhere, because it is ignored everywhere — see above. Nothing in this package aggregates these into one named failure; issue #423 tracks build-time validation.

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

Set `WithGroupId` on `SendOptions` to route the outbound message to the target session:

```csharp
public class DispatchOrderHandler : IMessageHandler<DispatchOrder>
{
    public async Task Handle(DispatchOrder message, IMessageHandlerContext context)
    {
        var options = new SendOptions()
            .WithGroupId(message.OrderId);  // sets ServiceBusMessage.SessionId

        await context.AzureServiceBus()
                     .Send(new ProcessOrder { OrderId = message.OrderId }, "orders-session-queue", options);
    }
}
```

`WithGroupId` alone is enough: the mapping only assigns `ServiceBusMessage.PartitionKey` when a non-empty partition key was explicitly supplied, letting `SessionId` stand in for it otherwise. An explicit partition key is optional; if set, it must equal the Group Id, and it has to be a separate statement — `WithMessageContext` returns `RoutingOptions`, not `SendOptions`, so it cannot terminate the fluent chain above:

```csharp
options.WithMessageContext(ASBMessageContext.PartitionKey, message.OrderId);
```

A mismatched partition key throws `ArgumentOutOfRangeException` from the Azure SDK's `ServiceBusMessage.PartitionKey` setter — client-side, before the message ever reaches the broker.

### Inbound context inheritance

A handler that sends or publishes through `IMessageHandlerContext` (for example `context.AzureServiceBus().Send(...)`) inherits the entire inbound message context. A message received from a session-stamped entity therefore emits an outbound `ServiceBusMessage.SessionId` equal to the inbound one, even if the handler's own `SendOptions` never called `WithGroupId`.

This is by design and is not special to Group Id: `CorrelationId`, `Subject`, `ReplyTo`, `ReplyToSessionId`, `To`, and `TimeToLive` are inherited the same way. It's load-bearing only when the destination is session-enabled or partitioned — on a plain queue or topic an inherited Group Id is an inert wire property, but on a partitioned destination it still affects partition affinity even when that destination is not session-enabled.

To opt out, either resolve `IBrokeredMessageDispatcher` and call the overload that takes a `TransactionContext` instead of an `IMessageHandlerContext` — that overload does not merge inbound context — or supply your own Group Id on the outbound options, since caller-supplied options win the merge.

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

## Trace Context and the Azure SDK's `Diagnostic-Id`

Chatter's [opt-in tracing](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers/src/README.md#diagnostics-and-trace-context-optional-opt-in) writes the W3C `traceparent` header onto outbound messages, where it rides the application properties in both directions. This has one interop consequence worth knowing about before you turn tracing on.

The Azure Service Bus SDK stamps its own legacy `Diagnostic-Id` correlation identifier **only when the message does not already carry a correlation identifier**. Because Chatter writes `traceparent`, the SDK's own `Diagnostic-Id` stamping is expected to be suppressed on Chatter-sent messages.

> **Confidence note.** The presence of both mechanisms in the shipped `Azure.Messaging.ServiceBus` / `Azure.Core` assemblies is verified; the short-circuit **control flow** itself is taken from the Azure SDK's published source and is **not verified in this repository**. Treat the mitigation below as the safe course if your correlation depends on `Diagnostic-Id`, and confirm against your own traces.

**Mitigation for applications that rely on `Diagnostic-Id`-based correlation:** enable the SDK's `ActivitySource` support so it reads `traceparent` instead of stamping and reading `Diagnostic-Id`:

```bash
AZURE_EXPERIMENTAL_ENABLE_ACTIVITY_SOURCE=true
```

```csharp
// equivalent AppContext switch
AppContext.SetSwitch("Azure.Experimental.EnableActivitySource", true);
```

Set it **at process start**, before the first Azure SDK type is touched: the SDK is documented to read the switch once, though that caching is likewise not verified here. Applications that do not correlate on `Diagnostic-Id` need no change — the SDK's own tracing stays off by default either way, and Chatter neither suppresses nor namespaces it.

One further observation on a mixed trace: Chatter's broker-boundary spans use the OpenTelemetry semantic conventions pinned at **v1.30.0** (`messaging.operation.type`), while `Azure.Messaging.ServiceBus` still emits the older `messaging.operation` spelling. Both are valid under their respective pins; Chatter deliberately emits one spelling per concept rather than both. See [ADR-0010](https://github.com/brenpike/Chatter/blob/master/docs/adr/0010-optional-bcl-only-telemetry-per-assembly-sources-and-the-off-guard.md).

## Domain Language

See the [domain glossary](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers.AzureServiceBus/CONTEXT.md) for definitions of Service Bus Receiver, Session Queue Receiver, Session Topic Subscription, Service Bus Sender, Service Bus Options, Service Bus Retry, No Retry Opt-In, Service Bus Circuit Breaker, Session, Session State, and Group Id ↔ SessionId realization.

[← All Chatter modules](https://github.com/brenpike/Chatter/blob/master/README.md)
