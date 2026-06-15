# <a name="chatter-reliability-cosmos"></a> Chatter.MessageBrokers.Reliability.Cosmos

Document-tier (NoSQL) reliability for [Chatter.MessageBrokers](#chatter-messagebrokers), backed by Azure Cosmos DB.

> **Status — 0.2.0 (unreleased).** Outbox enqueue (#219) is implemented: the provider stages the co-resident outbox document onto the framework-owned `TransactionalBatch` atomically with the handler's own aggregate write. Inbox dedup (#220) is implemented: the Document-Tier Batch-Lifecycle Behavior stamps a co-resident `inbox:` marker into the framework-owned batch before `next()`; a 409 create-conflict on the marker (detected post-execute) is a confirmed duplicate and the message is acked with nothing committed. To keep that marker-409 → confirmed-duplicate inference collision-proof, Chatter **exclusively owns the `inbox:`/`outbox:` item-id namespace**: the public atomic-write surface rejects any staged document whose persisted item id carries a reserved `inbox:`/`outbox:` prefix. The change-feed relay (#222) is **not yet implemented**.

## Overview

`Chatter.MessageBrokers.Reliability.Cosmos` is the document-tier (NoSQL) implementation of the reliability ports defined by [Chatter.MessageBrokers](#chatter-messagebrokers). Where the relational EntityFramework tier wraps the handler in an ambient transaction, the document tier uses a **stage-then-commit** model: the framework opens a Cosmos `TransactionalBatch`, the handler contributes its own aggregate writes, and the framework executes the batch once as the single commit point.

The two tiers share **only** the abstract enqueue / inbox / message contracts — not the EF-shaped `TransactionContext` / `InboxBehavior` mechanics. The document tier carries its document-store primitives (resolved partition key, bound container, batch handle, ETag) on its own provider-shaped **document-tier reliability surface**.

See [ADR-0006](../../docs/adr/0006-two-tier-reliability-relational-ambient-tx-vs-nosql-stage-then-commit.md), [ADR-0007](../../docs/adr/0007-cosmos-outbox-co-resident-change-feed-relay.md), and [ADR-0008](../../docs/adr/0008-document-tier-participation-model-and-multi-container-via-per-command-container-registry.md) for the design.

### Handler idempotency contract

The document-tier inbox is deliberately **no-pre-read / TOCTOU-free**: rather than checking "have I seen this message?" before running the handler, the Document-Tier Batch-Lifecycle Behavior stamps the `inbox:` marker as batch op 0, calls `next()` (your handler runs), and only then executes the single `TransactionalBatch`. The confirmed-duplicate signal is a 409 create-conflict on the marker op, surfaced **after** the handler has already run. This eliminates the read-then-add race a pre-read would reintroduce, but it splits the once-only guarantee in two — and handlers must be written to that split:

- **Batched writes are EXACTLY-ONCE.** Anything staged onto the framework-owned batch — the aggregate write and the co-resident outbox document — rides the same all-or-nothing `TransactionalBatch`. On a confirmed-duplicate marker-409 the entire batch fails atomically, so on a redelivered duplicate **nothing batched commits a second time**.
- **Handler side effects performed OUTSIDE the batch are AT-LEAST-ONCE.** Because the handler runs *before* batch-execute, the marker-409 cannot pre-empt it. Any effect the handler performs outside the Cosmos batch — an external HTTP call, a non-Cosmos write, a message sent through a non-batched path — has **already happened** by the time the duplicate is detected, and will happen again on every redelivery. **Handlers with non-batched side effects MUST therefore be idempotent.**

This is the price of the TOCTOU-free design, and it is the one place the two tiers' contracts differ. The relational tier (`BrokeredMessageInbox.ReceiveViaInbox`) reads `HasBeenReceived` first and **skips the handler entirely** on a known duplicate, so its non-batched side effects do not re-run. The document tier intentionally has no such pre-read, so it cannot offer the same skip — it trades the relational tier's pre-read (and the TOCTOU window that comes with it) for closed-by-construction batched-write dedup, at the cost of pushing non-batched-side-effect idempotency onto the handler.

## Installation

```sh
dotnet add package Chatter.MessageBrokers.Reliability.Cosmos
```

The package targets `net8.0` and `net10.0` and pulls in `Microsoft.Azure.Cosmos` (SDK v3) for both frameworks.

## Getting Started

Registration happens against the **command pipeline builder**, which Chatter exposes through the `pipeline` action on `AddChatterCqrs(...)`. Call `WithCosmosDocumentReliability<TCommand>(...)` once per participating command type — each call adds one entry to the singleton `DocumentReliabilityRegistry`.

**Participation = having a registration.** The registry is a positive allowlist: a command type with a registration engages the document-tier behavior (partition-key resolution, batch open, outbox enqueue). A command type **without** a registration bypasses the document tier entirely — no resolver is called, no batch is opened, and the handler passes through untouched. This answers "does it run for every handler?" — **no**, only for commands you explicitly register.

**The app owns the `CosmosClient`.** Register a `CosmosClient` singleton in your DI container before calling `AddChatterCqrs`. The provider resolves it from DI and derives `Container` handles via `client.GetContainer(database, container)` — it never creates or provisions containers.

```csharp
public void ConfigureServices(IServiceCollection services)
{
    // App owns the CosmosClient — expensive, thread-safe, one per app.
    services.AddSingleton(new CosmosClient(Configuration.GetConnectionString("Cosmos")));

    services.AddChatterCqrs(Configuration, pipeline =>
            {
                // Register each participating command type with its own database/container/lease.
                pipeline
                    .WithCosmosDocumentReliability<CreateOrder>(
                        database: "shop",
                        container: "orders",
                        lease: "orders-leases",
                        // The resolver receives the in-flight InboundBrokeredMessage (NOT the typed command),
                        // and may be null for in-process commands — return null then ("no resolvable partition").
                        resolver: msg => msg is null ? null : new PartitionKey(msg.GetMessageFromBody<CreateOrder>().OrderId),
                        "/orderId")
                    .WithCosmosDocumentReliability<PostLedgerEntry>(
                        database: "fin",
                        container: "ledger",
                        lease: "ledger-leases",
                        resolver: msg => msg is null ? null : new PartitionKey(msg.GetMessageFromBody<PostLedgerEntry>().AccountId),
                        "/accountId");
            })
            .AddMessageBrokers(/* message broker options */);
}
```

The Document-Tier Batch-Lifecycle Behavior is registered as the **outermost** pipeline behavior. For each registered command it resolves the partition key, opens the `TransactionalBatch` on the registration's container, exposes the atomic-write handle on the document-tier reliability surface for the duration of the handler, and then executes the batch once — **only if** an op was staged (an empty batch never calls the Cosmos transport).

### Prerequisites

- **App-registered `CosmosClient`.** The application registers a `CosmosClient` singleton in DI; the provider derives container handles from it and owns no client. A missing `CosmosClient` throws at resolution time.
- **Partition-key resolver.** The resolver is application-supplied — only the application knows how a message maps to its aggregate partition. Each registration carries its own resolver; it is only invoked for that command type. A `null` return means "no resolvable partition for this message" — the behavior opens no batch and passes through to `next()`.
- **Container TTL enabled (for outbox-doc purge).** Post-delivery TTL purge of outbox documents (arriving with the #222 relay) requires the application's container to have TTL enabled (`defaultTtl` set, e.g. `-1`). This is a documented application prerequisite, not automatic.

## Multi-Container Support

Each `WithCosmosDocumentReliability<TCommand>` registration is independent: different command types can map to different Cosmos databases and containers. Many command types may map to the same container, but exactly **one** registration exists per command type — a duplicate registration for the same type throws with a clear message naming the type.

Container handles are derived and cached by the singleton `CosmosContainerFactory` (keyed by `(database, container)`) so concurrent in-flight command types resolve their containers without duplicate derivation.

## Advanced: Per-Registration Container Factories

For applications that resolve or construct containers from the service provider themselves, an overload accepts `Func<IServiceProvider, Container>` for both the document and lease containers:

```csharp
pipeline.WithCosmosDocumentReliability<CreateOrder>(
    documentContainerFactory: sp => sp.GetRequiredService<MyContainerProvider>().GetOrdersContainer(),
    leaseContainerFactory:    sp => sp.GetRequiredService<MyContainerProvider>().GetOrdersLeaseContainer(),
    resolver: msg => msg is null ? null : new PartitionKey(msg.GetMessageFromBody<CreateOrder>().OrderId),
    "/orderId");
```

These factories bypass `client.GetContainer` derivation. Use this when the `Container` handle is already managed elsewhere in your DI graph.

## Domain Language

See [CONTEXT.md](../../Chatter.MessageBrokers/CONTEXT.md) for the domain glossary (Document Tier, Document-Tier Batch-Lifecycle Behavior, Atomic-Write Handle, Partition-Key Resolver, Co-Resident Outbox / Inbox Marker, Outbox Relay, Participation).

[← All Chatter modules](../../../README.md)
