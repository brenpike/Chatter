# Chatter.MessageBrokers.Reliability.Cosmos

[![NuGet](https://img.shields.io/nuget/v/Chatter.MessageBrokers.Reliability.Cosmos.svg)](https://www.nuget.org/packages/Chatter.MessageBrokers.Reliability.Cosmos)
[![Downloads](https://img.shields.io/nuget/dt/Chatter.MessageBrokers.Reliability.Cosmos.svg)](https://www.nuget.org/packages/Chatter.MessageBrokers.Reliability.Cosmos)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![CI](https://github.com/brenpike/Chatter/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/brenpike/Chatter/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/brenpike/Chatter/blob/master/LICENSE)

**Azure Cosmos DB reliability for Chatter.MessageBrokers: the Document Tier, a Standalone Inbox Gate and a change-feed Outbox Relay.**

This package makes brokered messaging reliable when your state lives in Azure Cosmos DB. It ships three primitives you can register on their own: the Document Tier commits a handler's aggregate write, its outgoing messages and its inbox dedup in one Cosmos `TransactionalBatch`; the Outbox Relay publishes pending Outbox Documents from a container's change feed; and the Standalone Inbox Gate stops a service from handling the same message twice. Your application owns the `CosmosClient` and every Cosmos resource. Part of the [Chatter](https://github.com/brenpike/Chatter) suite.

## Contents

- [Features](#features)
- [Installation](#installation)
- [Choosing a primitive](#choosing-a-primitive)
- [Quick start](#quick-start)
- [Document Tier](#document-tier)
- [Outbox Relay](#outbox-relay)
- [Standalone Inbox Gate](#standalone-inbox-gate)
- [Configuration](#configuration)
- [Diagnostics](#diagnostics)
- [Related packages](#related-packages)
- [Learn more](#learn-more)
- [License](#license)

## Features

- **Document Tier**: a handler's aggregate write, its Co-Resident Outbox Document and its Batched Inbox Marker commit together in one partition-scoped `TransactionalBatch`.
- **Participation by allowlist**: only the command types you register use the Document Tier; every other command passes through untouched.
- **Change-feed Outbox Relay**: pending Outbox Documents are published from the container's change feed, then marked delivered and stamped with a TTL so Cosmos purges them.
- **Standalone Outbox Relay**: drain a container outside the Command Pipeline, and optionally build each message at drain time with an Outbox Body Resolver.
- **Standalone Inbox Gate**: lease-less redelivery dedup for stateless services, using a two-phase Write-Ahead Claim.
- **Start-time container checks**: the relay verifies each monitored container's TTL and partition-key settings when the host starts and names every problem in one exception.
- **Bounded failure cost**: a document that can never be published is marked `undeliverable`, and a lease whose delivered stamps keep failing is suspended rather than republished on every pass.
- **Opt-in metrics**: seven OpenTelemetry-compatible instruments on the relay, with no `OpenTelemetry.*` dependency.

## Installation

```shell
dotnet add package Chatter.MessageBrokers.Reliability.Cosmos
```

Targets .NET 10 (`net10.0`).

Dependencies: Chatter.MessageBrokers, Microsoft.Azure.Cosmos 3.61.0.

The relay publishes through a Chatter transport, so add one:

```shell
dotnet add package Chatter.MessageBrokers.AzureServiceBus
dotnet add package Chatter.MessageBrokers.RabbitMQ
dotnet add package Chatter.MessageBrokers.SqlServiceBroker
```

## Choosing a primitive

| Primitive | Register with | Use it when |
| --- | --- | --- |
| Document Tier | `WithCosmosDocumentReliability<TCommand>` on the Command Pipeline | A handler writes an aggregate to Cosmos and sends or publishes messages, and the write, the outgoing messages and the inbox dedup must commit together. It includes its own Outbox Relay. |
| Standalone Outbox Relay | `AddCosmosOutboxRelay` on the service collection | You write Outbox Documents or trigger documents yourself, or want to drain a container without the Command Pipeline. |
| Standalone Inbox Gate | `WithCosmosInbox` on the Command Pipeline | A stateless service persists nothing through Chatter but must not handle the same message twice. |

Combining them:

- **Document Tier plus Standalone Inbox Gate in one pipeline is unsupported.** Nothing in code prevents it, but the gate would then claim participant commands before the handler runs, pre-empting the Document Tier's own in-batch dedup.
- **Standalone Outbox Relay plus Standalone Inbox Gate is supported.** They share nothing, so a service can drain its own outbox container and dedup what it receives.

### Containers you create

This package provisions nothing. It never creates databases or containers and never changes their TTL settings, so create these before the host starts:

| Container | Needed by | Partition key | Settings |
| --- | --- | --- | --- |
| Document container, for example `orders` | Document Tier | A path of your own aggregate, for example `/customerId`. It must match the path you register. | `defaultTtl` set to `-1`, or unset. It must not be partitioned on `/status` or `/ttl`. |
| Monitored container | Standalone Outbox Relay | The path you set in `PartitionKeyPath`. | As for the document container. |
| Lease container, for example `orders-leases` | Both Outbox Relays | `/id`, as the Cosmos change feed processor requires. | — |
| Idempotency container, for example `idempotency` | Standalone Inbox Gate | One segment, `/idempotencyKey` by default. | TTL enabled if you set `MarkerTimeToLive`. |

Use `defaultTtl = -1` on a monitored container. Items without a `ttl` field then never expire, and delivered documents, which the relay stamps with a `ttl`, are purged. With `defaultTtl` unset nothing is ever purged.

## Quick start

This quick start uses the Document Tier. The samples use `WebApplication.CreateBuilder(args)` (`builder.Services`, `builder.Configuration`) with implicit usings enabled. Any `IServiceCollection` with an `IConfiguration` works the same way.

### 1. Create the containers

Run this from your provisioning code, or create the same containers with Bicep, the Azure CLI or the portal:

```csharp
using Microsoft.Azure.Cosmos;

Database shop = (await cosmosClient.CreateDatabaseIfNotExistsAsync("shop")).Database;
await shop.CreateContainerIfNotExistsAsync(new ContainerProperties("orders", "/customerId") { DefaultTimeToLive = -1 });
await shop.CreateContainerIfNotExistsAsync(new ContainerProperties("orders-leases", "/id"));
```

### 2. Define the messages and the aggregate document

```csharp
using System.Text.Json.Serialization;
using Chatter.CQRS.Commands;
using Chatter.CQRS.Events;
using Chatter.MessageBrokers;

[BrokeredMessage(sendingPath: "orders", receivingPath: "orders")]
public class PlaceOrder : ICommand
{
    public Guid OrderId { get; set; }
    public string CustomerId { get; set; }
    public string Sku { get; set; }
}

public class OrderPlaced : IEvent
{
    public Guid OrderId { get; set; }
}

public class OrderDocument
{
    [JsonPropertyName("id")] public string Id { get; set; }
    [JsonPropertyName("customerId")] public string CustomerId { get; set; }
    [JsonPropertyName("sku")] public string Sku { get; set; }
}
```

### 3. Register the Cosmos client, the Document Tier and a transport

```csharp
using Microsoft.Azure.Cosmos;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(new CosmosClient(builder.Configuration.GetConnectionString("Cosmos")));

builder.Services.AddChatterCqrs(builder.Configuration,
        pipeline => pipeline.WithCosmosDocumentReliability<PlaceOrder>(
            database: "shop",
            container: "orders",
            lease: "orders-leases",
            resolver: inbound => inbound is null
                ? null
                : new PartitionKey(inbound.GetMessageFromBody<PlaceOrder>().CustomerId),
            "/customerId"),
        typeof(Program))
    .AddMessageBrokers()
    .AddAzureServiceBus(asb => asb.WithConnectionString(builder.Configuration.GetConnectionString("ServiceBus")));
```

The resolver maps each received message to the partition of the aggregate its handler writes. It receives `null` for a command dispatched in-process; return `null` then, and the command runs without a batch.

### 4. Stage the aggregate and publish from the handler

```csharp
using System.Text.Json;
using Chatter.CQRS;
using Chatter.CQRS.Context;
using Chatter.MessageBrokers.Reliability.Cosmos;

public class PlaceOrderHandler : IMessageHandler<PlaceOrder>
{
    private readonly IDocumentTierReliabilitySurface _surface;

    public PlaceOrderHandler(IDocumentTierReliabilitySurface surface) => _surface = surface;

    public async Task Handle(PlaceOrder message, IMessageHandlerContext context)
    {
        ICosmosAtomicWriteHandle handle = _surface.CurrentHandle
            ?? throw new InvalidOperationException("PlaceOrder must be received by a Brokered Message Receiver.");

        var order = new OrderDocument { Id = message.OrderId.ToString(), CustomerId = message.CustomerId, Sku = message.Sku };
        handle.StageCreateItemStream(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(order)));

        await context.Publish(new OrderPlaced { OrderId = message.OrderId }, "order-events");
    }
}
```

The publish does not reach the broker from the handler. It becomes a Co-Resident Outbox Document in the same batch as the order, and the Outbox Relay publishes it once the batch commits.

## Document Tier

`WithCosmosDocumentReliability<TCommand>` registers one command type as a Participant. It lives in the `Microsoft.Extensions.DependencyInjection` namespace, and you call it once per command type.

**Participation is an allowlist.** A command type with a registration runs through the Document Tier. A command type without one bypasses it completely: no resolver call, no batch, and its sends and publishes go straight to the broker.

### How a batch runs

The Document-Tier Batch-Lifecycle Behavior is the outermost behavior in the Command Pipeline. For a Participant it:

1. calls your Partition-Key Resolver; a `null` result runs the handler with no batch
2. opens a `TransactionalBatch` on the registered container and partition, and stages the Batched Inbox Marker as its first operation
3. exposes the Atomic-Write Handle on `IDocumentTierReliabilitySurface.CurrentHandle` while your handler runs
4. executes the batch once, after your handler returns; the batch always holds at least the marker, so the dedup commits even when your handler staged nothing

Sends and publishes from the handler are staged into the same batch as Co-Resident Outbox Documents. A Participant received with no `MessageId` throws `InvalidOperationException` before the batch opens, because it cannot be deduped; your handler does not run and the message is not acknowledged.

### Staging writes

Inject `IDocumentTierReliabilitySurface` into your handler. `CurrentHandle` is an `ICosmosAtomicWriteHandle`, or `null` outside a batch:

| Member | Description |
| --- | --- |
| `Container` | The Cosmos container the batch writes to. |
| `PartitionKey` | The partition key your resolver returned. |
| `PartitionKeyPath` | The container's partition-key path, as registered. |
| `ETag` | An optional concurrency token for your aggregate write. |
| `StagedOperationCount` | How many operations have been staged so far. |
| `StageCreateItemStream(Stream, TransactionalBatchItemRequestOptions)` | Stages a create of a JSON document. |
| `StageReplaceItem<T>(string id, T item, TransactionalBatchItemRequestOptions)` | Stages a replace. |
| `StagePatchItem(string id, IReadOnlyList<PatchOperation>, TransactionalBatchPatchItemRequestOptions)` | Stages a patch. |

Every document you stage must carry the batch's partition-key value at the partition-key path, because a `TransactionalBatch` spans one logical partition. The `inbox:` and `outbox:` id prefixes are reserved for Chatter, and staging a document with either one throws. Use `IfMatchEtag` in the request options for optimistic concurrency; a failed precondition fails the whole batch, so nothing commits and the message is redelivered.

### Idempotency contract

The Document Tier does not read before your handler runs. It stages the Batched Inbox Marker, runs your handler, then executes the batch, and a duplicate shows up as a create conflict (409) on the marker. That splits the once-only guarantee in two:

- **Writes staged in the batch happen exactly once.** On a duplicate the whole batch fails, so the aggregate write and the outbox documents do not commit a second time.
- **Side effects outside the batch are at-least-once.** Your handler has already run by the time the duplicate is detected, so an HTTP call or a write to another store happens again on every redelivery. Make those handlers idempotent.

A 409 on the marker is only a candidate duplicate. The behavior point-reads the conflicting document and treats the message as a duplicate only when that document is a genuine Chatter inbox marker for the same message id; anything else is redelivered. Batched Inbox Markers carry no `ttl`, so they stay in the container.

### Several commands and containers

Each registration is independent, so different command types can use different databases and containers, and many command types can share one container. Registering the same command type twice throws. The Document-Tier Outbox Relay runs one change feed processor per distinct Change-Feed Source Identity, so command types that share a container share one processor.

### Supplying your own container handles

If your application already builds its `Container` handles, use the overload that takes factories. Because you control the handles, you also declare the Change-Feed Source Identity the relay keys its processor on:

```csharp
pipeline.WithCosmosDocumentReliability<PlaceOrder>(
    documentContainerFactory: sp => sp.GetRequiredService<OrderContainers>().Orders,
    leaseContainerFactory: sp => sp.GetRequiredService<OrderContainers>().OrderLeases,
    monitoredSourceIdentity: "shop/orders",
    leaseSourceIdentity: "shop/orders-leases",
    resolver: inbound => inbound is null ? null : new PartitionKey(inbound.GetMessageFromBody<PlaceOrder>().CustomerId),
    "/customerId");
```

The identities are opaque tokens compared for equality. Registrations over the same physical containers must declare the same pair so they share one processor; registrations over different containers must declare different pairs. On the plain overload the relay keys on the resolved account endpoint, database id and container id instead.

## Outbox Relay

The Outbox Relay drains pending Outbox Documents from a monitored container's change feed. It comes in two variants: the Document-Tier Outbox Relay, registered automatically by `WithCosmosDocumentReliability`, and the Standalone Outbox Relay, registered by `AddCosmosOutboxRelay`.

### What the relay drains

Cosmos change feed delivers every change in the container, so the relay admits only documents with `_chatterType` of `outbox`, `status` of `pending`, and an `id` equal to the id Chatter derives from the document's `MessageId`. For each one it:

1. publishes the brokered message through the transport
2. patches the document to `status` of `delivered` and stamps a `ttl` (86400 seconds by default) in one write, so Cosmos purges it

Everything else is skipped, including the relay's own delivered stamps, so a delivered document is never published again.

**Delivery is at-least-once.** If the publish succeeds and the stamp fails, the document stays pending and is published again on a later pass, so receivers must dedup; the Document Tier's inbox marker does that. A publish that fails issues no stamp, and the lease does not advance past it.

**Processors.** The relay runs one change feed processor per Change-Feed Source Identity: the account endpoint, database and container of both the monitored and the lease container, or the pair you declared. The processor name is stable per source, so every instance of your application cooperates on one processor, and each host uses a unique instance name. A processor with no leases yet starts at the beginning of the change feed, so documents written before the first start are drained.

### Monitored-Container Contract

When the host starts, the relay reads each monitored container's properties once and checks that:

- `defaultTtl` is `-1` or unset; any other value would delete a pending document before it is published
- the partition-key path you declared matches the container's, segment for segment and case-sensitively (a leading `/` is optional)
- the container is not partitioned on `/status` or `/ttl`, the paths the delivered stamp patches

One `InvalidOperationException` names every violation, and the host does not start. The relay's credentials need permission to read the container's properties.

### Standalone Outbox Relay

`AddCosmosOutboxRelay` registers a relay as its own hosted service, independent of `AddChatterCqrs` and of Document Tier registrations. It publishes through the transport, so `AddMessageBrokers` and a transport must also be registered.

```csharp
using Microsoft.Azure.Cosmos;

builder.Services.AddSingleton(new CosmosClient(builder.Configuration.GetConnectionString("Cosmos")));

builder.Services.AddChatterCqrs(builder.Configuration, typeof(Program).Assembly)
    .AddMessageBrokers()
    .AddAzureServiceBus(asb => asb.WithConnectionString(builder.Configuration.GetConnectionString("ServiceBus")));

builder.Services.AddCosmosOutboxRelay(options =>
{
    options.MonitoredContainerFactory = sp => sp.GetRequiredService<CosmosClient>().GetContainer("shop", "orders");
    options.LeaseContainerFactory = sp => sp.GetRequiredService<CosmosClient>().GetContainer("shop", "orders-leases");
    options.PartitionKeyPath = new[] { "/customerId" };
});
```

You can call `AddCosmosOutboxRelay` once per monitored container. Invalid stamp settings throw `ArgumentException` at registration.

Two standalone relays over the same monitored and lease containers would share one processor and one set of leases, so a document one relay's filter rejects could be checkpointed and never reach the other. Give each such relay a distinct `MonitoredSourceIdentity` and `LeaseSourceIdentity` pair. A repeated declared pair throws `InvalidOperationException` at registration; two relays with no declared identity that resolve to the same containers throw it at host start.

### Trigger documents

Without a resolver, the Standalone Outbox Relay rebuilds each message verbatim from the Outbox Document's fields, exactly as the Document-Tier Outbox Relay does. `CosmosOutboxDocument.From(message).ToJsonObject(partitionKeyPath, partitionKeyValues)` renders that full shape from an `OutboundBrokeredMessage`.

With an Outbox Body Resolver, a thin trigger document is enough. It needs the discriminator, the pending status, the message id, an `id` built with `CosmosItemId.ForOutbox`, and your partition-key value:

```csharp
using System.Text.Json;
using Chatter.MessageBrokers.Reliability.Cosmos;
using Microsoft.Azure.Cosmos;

string messageId = Guid.NewGuid().ToString();
var trigger = new Dictionary<string, object>
{
    [CosmosOutboxDocument.IdField] = CosmosItemId.ForOutbox(messageId),
    [CosmosOutboxDocument.DiscriminatorField] = CosmosItemId.OutboxKind,
    [CosmosOutboxDocument.StatusField] = CosmosOutboxDocument.StatusPending,
    [CosmosOutboxDocument.MessageIdField] = messageId,
    ["customerId"] = order.CustomerId,
    ["orderId"] = order.Id,
};

await ordersContainer.CreateItemStreamAsync(
    new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(trigger)),
    new PartitionKey(order.CustomerId));
```

A document whose `id` is not exactly `outbox:` followed by the encoded `MessageId` is skipped. On the verbatim path, a thin trigger document is marked `undeliverable`; see [Stopping safely](#stopping-safely).

### Outbox Body Resolver

An `IOutboxBodyResolver` builds the message at drain time, for example from the aggregate's current state:

```csharp
using System.Net;
using System.Text.Json;
using Chatter.MessageBrokers;
using Chatter.MessageBrokers.Reliability.Cosmos;
using Chatter.MessageBrokers.Sending;
using Microsoft.Azure.Cosmos;

public sealed class OrderPlacedBodyResolver : IOutboxBodyResolver
{
    private readonly CosmosClient _client;

    public OrderPlacedBodyResolver(CosmosClient client) => _client = client;

    public async Task<OutboundBrokeredMessage?> ResolveAsync(OutboxDrainContext context, CancellationToken cancellationToken = default)
    {
        string orderId = context.Document.GetProperty("orderId").GetString();
        Container orders = _client.GetContainer("shop", "orders");

        using ResponseMessage response = await orders.ReadItemStreamAsync(orderId, context.PartitionKey, cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        OrderDocument order = await JsonSerializer.DeserializeAsync<OrderDocument>(response.Content, cancellationToken: cancellationToken);

        return new OutboundBrokeredMessage(context.MessageId,
                                           new OrderPlaced { OrderId = Guid.Parse(order.Id) },
                                           new Dictionary<string, object>(),
                                           "order-events",
                                           new JsonBodyConverter());
    }
}
```

`OutboxDrainContext` carries `MessageId`, the recovered `PartitionKey`, the container's `PartitionKeyPath` and the raw `Document` as a `JsonElement`. The relay acts on what the resolver does:

| Resolver | Relay |
| --- | --- |
| Returns a message | Publishes it, then marks the document delivered. |
| Returns `null` | Publishes nothing and marks the document delivered, so it is purged. |
| Throws | Stamps nothing; the batch is not checkpointed and the document comes back on the next pass. |

An empty message context uses the first registered transport. Set `MessageContext.InfrastructureType` in it to choose another.

Register the resolver with the typed overload, which registers it scoped and resolves a fresh instance for each pending document, so it can depend on scoped services:

```csharp
builder.Services.AddCosmosOutboxRelay<OrderPlacedBodyResolver>(options =>
{
    options.MonitoredContainerFactory = sp => sp.GetRequiredService<CosmosClient>().GetContainer("shop", "order-triggers");
    options.LeaseContainerFactory = sp => sp.GetRequiredService<CosmosClient>().GetContainer("shop", "order-triggers-leases");
    options.PartitionKeyPath = new[] { "/customerId" };
});
```

For several relays that each need their own resolver, use the keyed overload. Each relay binds the `IOutboxBodyResolver` registered under its key:

```csharp
static Action<CosmosOutboxRelayOptions> RelayOver(string container, string partitionKeyPath) => options =>
{
    options.MonitoredContainerFactory = sp => sp.GetRequiredService<CosmosClient>().GetContainer("shop", container);
    options.LeaseContainerFactory = sp => sp.GetRequiredService<CosmosClient>().GetContainer("shop", container + "-leases");
    options.PartitionKeyPath = new[] { partitionKeyPath };
};

builder.Services.AddCosmosOutboxRelay<OrderPlacedBodyResolver>("orders", RelayOver("order-triggers", "/customerId"));
builder.Services.AddCosmosOutboxRelay<InvoiceIssuedBodyResolver>("invoices", RelayOver("invoice-triggers", "/accountId"));
```

The typed and keyed overloads own `BodyResolverFactory`; setting it in `configure` as well throws `ArgumentException`. To control resolution yourself, use the plain `AddCosmosOutboxRelay` and set `BodyResolverFactory`. The factory is called with a per-document scope's provider, so it must resolve a new resolver on every call and must not cache one. Only the Standalone Outbox Relay consults a resolver.

### Stopping safely

Two fixed safety mechanisms bound the cost of a drain that keeps failing. Both relays have them, and neither can be configured or switched off.

**Undeliverable documents.** On the verbatim path, the relay checks each admitted document against the Outbox Document Contract before publishing: it must carry a destination and a body, its message context must be readable, a content type must be resolvable, and a messaging infrastructure it names must be stored as a string. The check reads only the document's own bytes, so a failing document would fail the same way on every pass. The relay stamps it `status` of `undeliverable` with no `ttl` and moves on, so the lease advances past it. The document is never purged: it stays as evidence of the defect, and every violation is logged at `Error`.

**Drain Suspension.** A publish that succeeds followed by a delivered stamp that fails is a Confirmation Failure, and it republishes the same document on every pass. After 5 consecutive Confirmation Failures on one lease of one relay processor, that lease is suspended for 60 seconds, then one batch is let through as a probe; a probe that fails again waits another 60 seconds. The suspension lifts only when a status write lands, whether a delivered stamp or an undeliverable stamp. Nothing is halted: no processor or hosted service stops, and other leases and other sources on the same host are unaffected.

Two cases are outside both mechanisms. A document whose resolver always throws keeps its lease from advancing. A document deleted between the change feed read and the delivered stamp keeps being republished until its change record ages out.

## Standalone Inbox Gate

`WithCosmosInbox` registers a lease-less dedup gate for services that have no Cosmos aggregate, no outbox and no lease container. It replaces the default `IBrokeredMessageInbox` with a Cosmos one, registered scoped, and adds `InboxBehavior<>`. It registers nothing else.

```csharp
using Microsoft.Azure.Cosmos;

builder.Services.AddSingleton(new CosmosClient(builder.Configuration.GetConnectionString("Cosmos")));

builder.Services.AddChatterCqrs(builder.Configuration,
        pipeline => pipeline.WithCosmosInbox(inbox =>
        {
            inbox.Database = "shop";
            inbox.Container = "idempotency";
            inbox.MarkerTimeToLive = 7 * 24 * 60 * 60; // seconds
        }),
        typeof(Program))
    .AddMessageBrokers()
    .AddAzureServiceBus(asb => asb.WithConnectionString(builder.Configuration.GetConnectionString("ServiceBus")));
```

The gate applies to every command received by a Brokered Message Receiver; commands dispatched in-process pass through.

### Write-Ahead Claim

Before your handler runs, the gate creates a pending Claimed Inbox Marker keyed on the message id. After the handler returns, it patches the marker to completed. A redelivery hits a create conflict (409) and point-reads the existing marker:

| Existing marker | Gate |
| --- | --- |
| Completed marker for this message id | Skips the handler; the message is a duplicate. |
| Pending marker for this message id | Takes it over: runs the handler, then completes the marker. |
| Anything else, or a marker it cannot read back within `ReadBackMaxAttempts` | Throws, so the message is redelivered. |

### What handlers must guarantee

- **Idempotent.** A taken-over claim re-runs the handler, and a failed completion write throws so the message is redelivered and handled again.
- **Safe under concurrent execution of the same message id.** The gate dedups redeliveries, not concurrent deliveries: two deliveries of one message in flight at once both run the handler. Keeping a second delivery out while the first is in flight is the transport's job, through its message lock or session.
- **Messages carry a `MessageId`.** A message with none throws `InvalidOperationException` before anything is written, and the handler does not run.

A handler that throws leaves its pending marker in place, and the next redelivery takes it over. The marker only moves forward, from absent to pending to completed, and TTL is the only thing that removes it. Without `MarkerTimeToLive`, markers accumulate indefinitely.

## Configuration

Every setting is passed in code; nothing is bound from `IConfiguration`.

### Document Tier registration

| Parameter | Type | Default | Description |
| --- | --- | --- | --- |
| `database` | `string` | — | Required. The database holding the document container. |
| `container` | `string` | — | Required. The container your aggregate, the Co-Resident Outbox Documents and the Batched Inbox Markers share. |
| `lease` | `string` | — | Required. The lease container for the Document-Tier Outbox Relay, in the same database. |
| `resolver` | `ResolvePartitionKey` | — | Required. Maps the `InboundBrokeredMessage` (or `null`) to a `PartitionKey?`; `null` means no batch. |
| `partitionKeyPath` | `params string[]` | — | Required. The container's partition-key path; pass one value per segment for a hierarchical key. |
| `documentContainerFactory`, `leaseContainerFactory` | `Func<IServiceProvider, Container>` | — | Advanced overload only. Build the container handles yourself. |
| `monitoredSourceIdentity`, `leaseSourceIdentity` | `string` | — | Advanced overload only. The declared Change-Feed Source Identity. |

For a hierarchical partition key, register each segment, for example `"/tenantId", "/customerId"`, and build the resolver's key with the Cosmos SDK's `PartitionKeyBuilder`.

### Outbox Relay options

`CosmosOutboxRelayOptions`, set in the `AddCosmosOutboxRelay` callback:

| Option | Type | Default | Description |
| --- | --- | --- | --- |
| `MonitoredContainerFactory` | `Func<IServiceProvider, Container>` | — | Required. The container whose change feed is drained. |
| `LeaseContainerFactory` | `Func<IServiceProvider, Container>` | — | Required. The lease container. |
| `PartitionKeyPath` | `IReadOnlyList<string>` | — | Required. The monitored container's partition-key path, for example `new[] { "/customerId" }`. |
| `BodyResolverFactory` | `Func<IServiceProvider, IOutboxBodyResolver>` | `null` | Binds an Outbox Body Resolver. `null` rebuilds each message verbatim. |
| `AdditionalPendingFilter` | `Func<JsonElement, bool>` | `null` | Narrows which pending documents are admitted. It runs once per document, only after the built-in pending check, and cannot widen it. |
| `DeliveredTtlSeconds` | `int` | `86400` | The `ttl` stamped on a delivered document. Must be greater than 0. |
| `StatusPatchPath` | `string` | `"/status"` | The path of the delivered status stamp. Must be `/status`. |
| `DeliveredStatusValue` | `string` | `"delivered"` | The status written on delivery. Must not be empty, `pending` or `undeliverable`. |
| `MonitoredSourceIdentity`, `LeaseSourceIdentity` | `string` | `null` | A declared Change-Feed Source Identity. Set both or neither. |

A document admitted by the built-in check but rejected by `AdditionalPendingFilter` is neither published nor stamped.

### Inbox Gate options

`CosmosInboxOptions`, set in the `WithCosmosInbox` callback:

| Option | Type | Default | Description |
| --- | --- | --- | --- |
| `Database` | `string` | — | Required. The database holding the idempotency container. |
| `Container` | `string` | — | Required. The idempotency container. |
| `PartitionKeyPath` | `IReadOnlyList<string>` | `["/idempotencyKey"]` | The container's partition-key path. One segment only; the partition value is the message id. It must not be rooted at a field the marker writes, such as `/Completed`. |
| `MarkerTimeToLive` | `int?` | `null` | Seconds before Cosmos purges a marker. `null` or a value of 0 or less keeps markers indefinitely. Needs TTL enabled on the container. |
| `ReadBackMaxAttempts` | `int` | `5` | How many times a conflicting marker is read back before the gate gives up and throws. At least 1. |
| `ReadBackInterval` | `TimeSpan` | 50 ms | The wait between read-back attempts. Not negative. |

Invalid options throw `ArgumentException` when `WithCosmosInbox` is called.

### Fixed values

| Setting | Value |
| --- | --- |
| Drain Suspension threshold | 5 consecutive Confirmation Failures per lease |
| Suspension window | 60 seconds, then one probe batch |
| Delivered stamp TTL path | `/ttl` |
| Change feed start for a new processor | The beginning of the change feed |

### Common setups

**Several commands, one container.** Call `WithCosmosDocumentReliability` once per command type with the same `database`, `container` and `lease`. They share one relay processor.

**Filtering one container into two relays.** Give each relay its own filter and its own declared source identities, so each gets its own leases. This is the EU relay; register the second the same way with, for example, `"shop/orders/us"` and `"shop/orders-leases/us"`:

```csharp
using System.Text.Json;
using Microsoft.Azure.Cosmos;

builder.Services.AddCosmosOutboxRelay(options =>
{
    options.MonitoredContainerFactory = sp => sp.GetRequiredService<CosmosClient>().GetContainer("shop", "orders");
    options.LeaseContainerFactory = sp => sp.GetRequiredService<CosmosClient>().GetContainer("shop", "orders-leases");
    options.PartitionKeyPath = new[] { "/customerId" };
    options.AdditionalPendingFilter = document =>
        document.TryGetProperty("region", out JsonElement region)
        && region.ValueKind == JsonValueKind.String
        && region.GetString() == "eu";
    options.MonitoredSourceIdentity = "shop/orders/eu";
    options.LeaseSourceIdentity = "shop/orders-leases/eu";
});
```

**Outbox plus inbox for a stateless service.** Register `AddCosmosOutboxRelay` for the container you write Outbox Documents to, and `WithCosmosInbox` on the pipeline. They are independent.

## Diagnostics

The Outbox Relay emits OpenTelemetry-compatible metrics. Nothing is emitted until your application subscribes. This package takes no dependency on any `OpenTelemetry.*` package; it uses `System.Diagnostics.Metrics.Meter` and `System.Diagnostics.ActivitySource` from the base class library.

### Turning it on

The `Meter` and the `ActivitySource` are both named `Chatter.MessageBrokers.Reliability.Cosmos` (`CosmosReliabilityDiagnostics.MeterName` and `CosmosReliabilityDiagnostics.ActivitySourceName`). This package emits metrics only. The relay's publishes are spans of `Chatter.MessageBrokers`, so subscribe to both to see them:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource("Chatter.*"))     // or .AddSource("Chatter.MessageBrokers", "Chatter.MessageBrokers.Reliability.Cosmos")
    .WithMetrics(m => m.AddMeter("Chatter.*"));     // or .AddMeter("Chatter.MessageBrokers", "Chatter.MessageBrokers.Reliability.Cosmos")
```

Any .NET `ActivityListener` or .NET `MeterListener` works as well.

**Off means off.** When nothing subscribes to this package's meter, each emit site returns before reading a clock or building a tag, so the relay does the same work as it would uninstrumented. `CosmosReliabilityDiagnostics.IsEnabled` reports whether anything subscribes.

### What is emitted

Seven instruments, all recorded by the Outbox Relay in both variants.

**Instruments**

| Instrument | Type | Unit | Advised buckets | Records | Recorded when |
| --- | --- | --- | --- | --- | --- |
| `chatter.messaging.outbox.drain.lag` | `Histogram<double>` | `s` | `0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 300, 600`; see [Histogram bucket boundaries](#histogram-bucket-boundaries) | How long an Outbox Document had been pending when the relay admitted it. | Once per admitted document that carries a Cosmos `_ts`, at admission and before the publish, so a publish that then throws is still measured. A document with no `_ts` records nothing here but is still counted. While a .NET `MeterListener` has enabled the instrument. |
| `chatter.messaging.outbox.drain.documents` | `Counter<long>` | `{document}` | — | One change feed document the relay resolved, by Drain Outcome. | Once per document handed to the relay, including skipped ones. An admitted document whose publish throws records nothing, because the outcome is written after the publish returns. While a .NET `MeterListener` has enabled the instrument. |
| `chatter.messaging.outbox.drain.batch.size` | `Histogram<int>` | `{document}` | `1, 2, 5, 10, 25, 50, 100, 250, 500, 1000`; see [Histogram bucket boundaries](#histogram-bucket-boundaries) | How many documents one change feed batch carried. | Once per batch, after it is parsed and before its documents are processed. An empty batch is recorded as `0`. A batch the relay cannot parse records nothing. While a .NET `MeterListener` has enabled the instrument. |
| `chatter.messaging.outbox.drain.batches` | `Counter<long>` | `{batch}` | — | One change feed batch the relay handled. | Together with `chatter.messaging.outbox.drain.batch.size`, from the same emit site and with the same tags. While a .NET `MeterListener` has enabled the instrument. |
| `chatter.messaging.outbox.drain.failures` | `Counter<long>` | `{failure}` | — | One drain attempt that faulted. | Once per fault the change feed processor reports through its error notification, the only channel that carries the fault together with its lease token. While a .NET `MeterListener` has enabled the instrument. |
| `chatter.messaging.outbox.drain.undeliverable` | `Counter<long>` | `{document}` | — | One Outbox Document marked [undeliverable](#stopping-safely). | Once per document that fails the Outbox Document Contract, after its undeliverable stamp lands; a stamp that fails counts nothing. Verbatim path only. It carries no attributes, because one document can fail several checks; the always-on log carries the details. While a .NET `MeterListener` has enabled the instrument. |
| `chatter.messaging.outbox.drain.suspensions` | `Counter<long>` | `{suspension}` | — | One [Drain Suspension](#stopping-safely) opened on one lease. | Once when a suspension opens, on the fifth consecutive Confirmation Failure. A further failure while it is open is not counted again, and a suspension closing is logged, not counted. While a .NET `MeterListener` has enabled the instrument. |

**Metric attributes**

| Attribute | Instruments | Value | Emitted |
| --- | --- | --- | --- |
| `chatter.messaging.outbox.drain.outcome` | `chatter.messaging.outbox.drain.documents` | `admitted`, `skipped` or `dropped`, as below. | Always, as a key. |
| `chatter.messaging.outbox.lease_token` | `chatter.messaging.outbox.drain.batch.size`, `chatter.messaging.outbox.drain.batches`, `chatter.messaging.outbox.drain.failures`, `chatter.messaging.outbox.drain.suspensions` | The change feed lease token the batch, fault or suspension belongs to. It keeps a suspended lease distinguishable from an idle one. | Always, as a key. |
| `chatter.messaging.outbox.source_identity` | `chatter.messaging.outbox.drain.batch.size`, `chatter.messaging.outbox.drain.batches`, `chatter.messaging.outbox.drain.failures`, `chatter.messaging.outbox.drain.suspensions` | The relay processor name, which identifies the Change-Feed Source Identity. A lease token is unique only within one source, since two sources on one host both report lease `"0"`, so the two are always recorded together. | Always, as a key, on every lease-tagged instrument. `chatter.messaging.outbox.drain.undeliverable` carries no attributes. |
| `error.type` | `chatter.messaging.outbox.drain.failures` | The fault's exception type, resolved as the Chatter.MessageBrokers send path resolves it. | Always, as a key. |

The Drain Outcome vocabulary is closed:

- **`admitted`**: a pending Outbox Document whose message was published.
- **`skipped`**: not a pending Outbox Document, so never drained. This is the ordinary case for your own writes, inbox markers and delivered stamps.
- **`dropped`**: admitted, but an Outbox Body Resolver returned `null`, so the document was marked delivered with no publish.

A failure is not a fourth outcome. It is an attempt that never resolved, recorded on its own instrument.

### Always-on logs

These are written through your host's `ILogger` whether or not any meter is subscribed:

- **`Error`** for a change feed fault, with the lease token and a note that the lease does not advance until the fault clears.
- **`Error`** for an undeliverable document, with every contract violation it failed.
- **`Error`** when a Drain Suspension opens, with the lease token, the failure count and the fault.
- **`Information`** when a suspension closes.

A log call that fails is swallowed and never affects delivery. With no logging configured, nothing is logged and the relay still runs.

### Names are Chatter-native

The instrument names and the `chatter.messaging.outbox.*` attributes use a `chatter.` prefix because the OpenTelemetry messaging semantic conventions (v1.30.0) define nothing for an outbox drain. `error.type` is the standard OpenTelemetry attribute.

### Drain lag and lease progress

Drain lag is measured from the document's Cosmos `_ts` to the relay host's clock. `_ts` has 1-second resolution, so lag is quantized to whole seconds. When the host clock runs behind the Cosmos server the difference would be negative, so it is clamped to zero; many exact-zero measurements point to clock skew.

Batch size and batch count are recorded per lease even for an empty batch, so an idle lease with nothing pending looks different from a stalled one that is not advancing.

### Tracing

This package declares no span of its own. With tracing on, the relay publishes each document under the `Chatter.MessageBrokers` send span, parented to the trace context stored with the document when it was written, and writes that span's context onto the outgoing message. The trace reads write, drain, receive; a document republished N times produces N send spans under the same write-time root. A document stored without trace context starts a new root, linked to the change feed's activity. The stored document is never rewritten. See [trace context propagation](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers/src/README.md#trace-context-propagation) in Chatter.MessageBrokers.

### Histogram bucket boundaries

`chatter.messaging.outbox.drain.lag` records seconds and `chatter.messaging.outbox.drain.batch.size` records documents. The OpenTelemetry .NET SDK's default boundaries are sized for milliseconds, which would put almost every measurement in the first bucket, so this package publishes the boundaries above as instrument advice. The lag boundaries reach 10 minutes because a restarted lease or a backlog can leave a document pending that long.

Advice is a default, and a view registered by your application overrides it. `AddView` and `ExplicitBucketHistogramConfiguration` come from your application's OpenTelemetry packages:

```csharp
using OpenTelemetry.Metrics;

builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m
        .AddMeter("Chatter.MessageBrokers.Reliability.Cosmos")
        .AddView("chatter.messaging.outbox.drain.lag", new ExplicitBucketHistogramConfiguration
        {
            Boundaries = new double[] { 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 300, 600 }
        })
        .AddView("chatter.messaging.outbox.drain.batch.size", new ExplicitBucketHistogramConfiguration
        {
            Boundaries = new double[] { 1, 2, 5, 10, 25, 50, 100, 250, 500, 1000 }
        }));
```

### Attribute names are data, not API

Instrument and attribute names are emitted data, not a compile-time API, so **they may change in a minor release**. Dashboards and alerts that hard-code them should expect to be revisited; any change is announced in this package's CHANGELOG.

## Related packages

- [Chatter.CQRS](https://www.nuget.org/packages/Chatter.CQRS): The Commands, Events and Command Pipeline your handlers use.
- [Chatter.MessageBrokers](https://www.nuget.org/packages/Chatter.MessageBrokers): The brokered messaging, Inbox and Outbox abstractions this package implements.
- [Chatter.MessageBrokers.AzureServiceBus](https://www.nuget.org/packages/Chatter.MessageBrokers.AzureServiceBus): Azure Service Bus transport.
- [Chatter.MessageBrokers.RabbitMQ](https://www.nuget.org/packages/Chatter.MessageBrokers.RabbitMQ): RabbitMQ transport.
- [Chatter.MessageBrokers.SqlServiceBroker](https://www.nuget.org/packages/Chatter.MessageBrokers.SqlServiceBroker): SQL Server Service Broker transport.
- [Chatter.MessageBrokers.Reliability.EntityFramework](https://www.nuget.org/packages/Chatter.MessageBrokers.Reliability.EntityFramework): The relational alternative, an EF Core Inbox, Outbox and Unit of Work.

## Learn more

- [Domain glossary (CONTEXT.md)](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers.Reliability.Cosmos/CONTEXT.md)
- [Changelog](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers.Reliability.Cosmos/src/Chatter.MessageBrokers.Reliability.Cosmos/CHANGELOG.md)
- [Context map of all Chatter modules](https://github.com/brenpike/Chatter/blob/master/CONTEXT-MAP.md)
- [Chatter suite README](https://github.com/brenpike/Chatter/blob/master/README.md)

## License

Licensed under the [MIT License](https://github.com/brenpike/Chatter/blob/master/LICENSE).
