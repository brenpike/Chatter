# <a name="chatter-reliability-cosmos"></a> Chatter.MessageBrokers.Reliability.Cosmos

Document-tier (NoSQL) reliability for [Chatter.MessageBrokers](#chatter-messagebrokers), backed by Azure Cosmos DB.

> **Status — 0.2.0 (unreleased).** Outbox enqueue (#219) is implemented: the provider stages the co-resident outbox document onto the framework-owned `TransactionalBatch` atomically with the handler's own aggregate write. Inbox dedup (#220) is implemented: the Document-Tier Batch-Lifecycle Behavior stamps a co-resident `inbox:` marker into the framework-owned batch before `next()`; a 409 create-conflict on the marker (detected post-execute) is a *candidate* duplicate that is **confirmed by point-reading the conflicting document** before the message is acked — it is treated as a duplicate only when that doc is a genuine Chatter inbox marker (its `_chatterType` equals `inbox` AND its `MessageId` equals the inbound message id), and otherwise the message is redelivered. This closes the silent-message-loss class by construction: because the app owns the container, an app-authored `inbox:`-prefixed collision is detected and redelivered rather than silently swallowed. The public atomic-write surface still rejects reserved-prefix ids as **defense-in-depth**, but soundness comes from confirming the conflicting doc, not from the reserved namespace. The change-feed relay (#222) is **implemented**: a hosted `ChangeFeedProcessor` (one per distinct change-feed **source identity** — caller-declared on the advanced overload, ground-truth-derived incl. the account endpoint on the plain overload) drains the co-resident `_chatterType="outbox"`/`status="pending"` documents, publishes each through the broker, then marks it `delivered` and stamps a TTL so delivered documents self-purge. The relay is **at-least-once** (downstream consumers dedup via the `inbox:` marker) and requires the container to have TTL enabled (`defaultTtl` set) for the post-delivery purge — see [Change-Feed Relay](#change-feed-relay) below.

## Overview

`Chatter.MessageBrokers.Reliability.Cosmos` is the document-tier (NoSQL) implementation of the reliability ports defined by [Chatter.MessageBrokers](#chatter-messagebrokers). Where the relational EntityFramework tier wraps the handler in an ambient transaction, the document tier uses a **stage-then-commit** model: the framework opens a Cosmos `TransactionalBatch`, the handler contributes its own aggregate writes, and the framework executes the batch once as the single commit point.

The two tiers share **only** the abstract enqueue / inbox / message contracts — not the EF-shaped `TransactionContext` / `InboxBehavior` mechanics. The document tier carries its document-store primitives (resolved partition key, bound container, batch handle, ETag) on its own provider-shaped **document-tier reliability surface**.

See [ADR-0006](../../docs/adr/0006-two-tier-reliability-relational-ambient-tx-vs-nosql-stage-then-commit.md), [ADR-0007](../../docs/adr/0007-cosmos-outbox-co-resident-change-feed-relay.md), and [ADR-0008](../../docs/adr/0008-document-tier-participation-model-and-multi-container-via-per-command-container-registry.md) for the design.

### Handler idempotency contract

The document-tier inbox is deliberately **no-pre-read / TOCTOU-free**: rather than checking "have I seen this message?" before running the handler, the Document-Tier Batch-Lifecycle Behavior stamps the `inbox:` marker as batch op 0, calls `next()` (your handler runs), and only then executes the single `TransactionalBatch`. The confirmed-duplicate signal is a 409 create-conflict on the marker op, surfaced **after** the handler has already run. This eliminates the read-then-add race a pre-read would reintroduce, but it splits the once-only guarantee in two — and handlers must be written to that split:

- **Batched writes are EXACTLY-ONCE.** Anything staged onto the framework-owned batch — the aggregate write and the co-resident outbox document — rides the same all-or-nothing `TransactionalBatch`. On a confirmed-duplicate marker-409 the entire batch fails atomically, so on a redelivered duplicate **nothing batched commits a second time**.
- **Handler side effects performed OUTSIDE the batch are AT-LEAST-ONCE.** Because the handler runs *before* batch-execute, the marker-409 cannot pre-empt it. Any effect the handler performs outside the Cosmos batch — an external HTTP call, a non-Cosmos write, a message sent through a non-batched path — has **already happened** by the time the duplicate is detected, and will happen again on every redelivery. **Handlers with non-batched side effects MUST therefore be idempotent.**

This is the price of the TOCTOU-free design, and it is the one place the two tiers' contracts differ. The relational tier (`BrokeredMessageInbox.ReceiveViaInbox`) reads `HasBeenReceived` first and **skips the handler entirely** on a known duplicate, so its non-batched side effects do not re-run. The document tier intentionally has no such pre-read, so it cannot offer the same skip — it trades the relational tier's pre-read (and the TOCTOU window that comes with it) for closed-by-construction batched-write dedup, at the cost of pushing non-batched-side-effect idempotency onto the handler.

A participant command MUST carry a non-empty `MessageId`. The `inbox:` marker is keyed on the message identity, so a participant that resolves a partition but arrives with a null/whitespace `MessageId` cannot be deduped — the once-only guarantee cannot be honored. Rather than silently proceed (which would leave a redelivery of the same identity-less message undetectable), the Document-Tier Batch-Lifecycle Behavior **fails loud**: it throws `InvalidOperationException` before opening the batch, so nothing is staged, your handler never runs, and the message is not acked. A participant without a `MessageId` is a protocol/config error, not a runtime condition to tolerate.

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
- **Container TTL enabled (for outbox-doc purge).** Post-delivery TTL purge of outbox documents by the change-feed relay requires the application's container to have TTL enabled (`defaultTtl` set, e.g. `-1`). This is a documented application prerequisite, not automatic — without it the relay still marks documents `delivered` and stamps a `ttl` field, but Cosmos will not expire them.

## Multi-Container Support

Each `WithCosmosDocumentReliability<TCommand>` registration is independent: different command types can map to different Cosmos databases and containers. Many command types may map to the same container, but exactly **one** registration exists per command type — a duplicate registration for the same type throws with a clear message naming the type.

Container handles are derived and cached by the singleton `CosmosContainerFactory` (keyed by `(database, container)`) so concurrent in-flight command types resolve their containers without duplicate derivation.

## Change-Feed Relay

Dispatch on the document tier is a **change-feed relay**, not a polling query. When you register at least one participating command type, the provider registers a hosted `IHostedService` that runs one Cosmos `ChangeFeedProcessor` per distinct change-feed **source identity** drawn from the registry — many command types may share one source, so registrations are deduped on the source identity (one processor per source, not one per command type). The dedup key is **declared-or-ground-truth, never inferred from a caller-controlled handle**: on the plain overload it is the complete resolved identity (account **endpoint** + database id + container id, for both the monitored and lease containers); on the advanced overload the application **declares** the identity (see [Advanced](#advanced-per-registration-container-factories)) because it controls the resolved handle and the relay cannot trust it as a key. Adding the account endpoint to the plain-path key means identically-named containers in **different accounts** stay distinct rather than collapsing into one (and silently dropping the skipped source). The host resolves the registry, the container factory, the broker `IMessagingInfrastructureProvider`, and the `IBodyConverterFactory` from DI; it owns no `CosmosClient` (the app does).

Each processor monitors its container's change feed. Cosmos change feed v3 delivers **all** container changes — domain documents, `inbox:` markers, outbox documents, and the relay's own delivery-stamp updates — so the relay filters **in code** to exactly the co-resident outbox documents it must publish: `_chatterType="outbox"` **and** `status="pending"`. Everything else is skipped, including already-`delivered` outbox documents — which is what makes the relay's own delivery update a non-republish (**publish-once by construction**).

For each selected document the relay reconstructs the original `OutboundBrokeredMessage` (message id, destination, body, content-type, and message context) and publishes it through the broker, then advances the document to `status="delivered"` and stamps a positive `ttl` in a single patch so Cosmos self-purges it (container TTL must be enabled — see [Prerequisites](#prerequisites)).

**The relay is at-least-once.** Publish and the delivered/TTL stamp are two separate writes: if publish succeeds but the stamp fails, the document stays `pending` and is re-published on the next change-feed pass. **Downstream consumers must deduplicate** — the document-tier `inbox:` marker is the in-framework mechanism. A publish failure issues no stamp and lets the document re-surface next pass rather than advancing the lease past an unpublished document.

`processorName` is stable per **source identity** so every application instance sharing a source cooperates on one logical processor — and two distinct sources never share a `processorName`; `instanceName` is unique per host so co-located instances do not collide on the lease.

## Advanced: Per-Registration Container Factories

For applications that resolve or construct containers from the service provider themselves, an overload accepts `Func<IServiceProvider, Container>` for both the document and lease containers. Because **you** control the resolved handle on this path, the relay cannot trust it to identify the change-feed source for dedup — so this overload **requires you to declare a stable source identity** (`monitoredSourceIdentity` / `leaseSourceIdentity`) that becomes the relay's dedup/processor key:

```csharp
pipeline.WithCosmosDocumentReliability<CreateOrder>(
    documentContainerFactory: sp => sp.GetRequiredService<MyContainerProvider>().GetOrdersContainer(),
    leaseContainerFactory:    sp => sp.GetRequiredService<MyContainerProvider>().GetOrdersLeaseContainer(),
    // Declared change-feed source identity — the relay dedup/processor key on this path.
    // Two registrations resolving the SAME physical source MUST declare the SAME pair (they then
    // collapse to one processor); two DISTINCT sources MUST declare DISTINCT pairs.
    monitoredSourceIdentity: "shop/orders",
    leaseSourceIdentity:     "shop/orders-leases",
    resolver: msg => msg is null ? null : new PartitionKey(msg.GetMessageFromBody<CreateOrder>().OrderId),
    "/orderId");
```

These factories bypass `client.GetContainer` derivation. Use this when the `Container` handle is already managed elsewhere in your DI graph. The declared identity is opaque to the relay — any stable token that uniquely names the source works; the relay only compares the declared pairs for equality. (On the plain overload no identity is declared: the handle is derived from the app `CosmosClient`, so the relay keys on the ground-truth resolved identity instead.)

## Standalone Outbox Relay (`AddCosmosOutboxRelay`)

The [Change-Feed Relay](#change-feed-relay) above is wired implicitly by `WithCosmosDocumentReliability<TCommand>` and bound to the command-pipeline `DocumentReliabilityRegistry`. For applications that want a change-feed Outbox Relay **without** participating in the command pipeline — or that want to drain a container the pipeline never registered — `AddCosmosOutboxRelay` registers a **standalone** relay directly on the service collection:

```csharp
services.AddCosmosOutboxRelay(options =>
{
    options.MonitoredContainerFactory = sp => sp.GetRequiredService<CosmosClient>().GetContainer("shop", "orders");
    options.LeaseContainerFactory     = sp => sp.GetRequiredService<CosmosClient>().GetContainer("shop", "orders-leases");
    options.PartitionKeyPath          = "/orderId";

    // Optional: resolve the brokered-message body from CURRENT store state at drain time.
    // Omit to keep the verbatim Reconstruct default (see below).
    options.BodyResolverFactory = sp => new OrderOutboxBodyResolver(sp.GetRequiredService<CosmosClient>());
});
```

`AddCosmosOutboxRelay` lives in the `Microsoft.Extensions.DependencyInjection` namespace and registers **its own** `IHostedService`, **independent** of `AddChatterCqrs` / `WithCosmosDocumentReliability` and the `DocumentReliabilityRegistry`. It is **repeatable** — call it once per monitored container to run multiple standalone relays side by side. The standalone relay drains the same co-resident pending outbox documents (`_chatterType="outbox"` **and** `status="pending"`), publishes each through the broker, then stamps the document `delivered` and stamps a TTL so delivered documents self-purge — exactly as the pipeline-integrated relay does, and with the same **at-least-once** delivery and TTL-enabled-container prerequisite (see [Prerequisites](#prerequisites)).

### Safe-by-default resolver registration (recommended)

The raw `BodyResolverFactory` shown above is the **advanced escape hatch**: you own resolving the `IOutboxBodyResolver` and must honor its per-document-scope contract (see [Resolver DI lifetime](#the-ioutboxbodyresolver-seam) below). For the common case, prefer the generic **typed overload**, which registers your resolver `Scoped` and auto-wires the per-document factory for you — no knowledge of the raw factory required:

```csharp
services.AddCosmosOutboxRelay<OrderOutboxBodyResolver>(options =>
{
    options.MonitoredContainerFactory = sp => sp.GetRequiredService<CosmosClient>().GetContainer("shop", "orders");
    options.LeaseContainerFactory     = sp => sp.GetRequiredService<CosmosClient>().GetContainer("shop", "orders-leases");
    options.PartitionKeyPath          = "/orderId";
    // Do NOT set options.BodyResolverFactory here — the typed overload owns that wiring (see below).
});
```

`AddCosmosOutboxRelay<TResolver>(...)` registers `TResolver` as `Scoped` (via `TryAdd`, so an existing registration is preserved) and resolves it from the host-owned per-document scope — a fresh instance per **pending outbox document** — so your resolver may depend on / capture scoped services (e.g. a scoped `DbContext`) in its constructor. The host's pre-scope gate is the pure `IsPendingOutbox` identity guard, so a change-feed document that is not a pending outbox document never constructs your resolver.

For applications running **multiple monitored containers**, each with its **own** resolver, use the **keyed overload** `AddCosmosOutboxRelay<TResolver>(serviceKey, configure)`: it registers `TResolver` as a keyed-scoped `IOutboxBodyResolver` under `serviceKey` and binds *this* relay to that keyed resolver, so relays with distinct keys never collide.

```csharp
services.AddCosmosOutboxRelay<OrderOutboxBodyResolver>("orders", options => { /* orders container + lease + pk path */ });
services.AddCosmosOutboxRelay<LedgerOutboxBodyResolver>("ledger", options => { /* ledger container + lease + pk path */ });
```

Both typed and keyed overloads **own** the `BodyResolverFactory` wiring: if `configure` **also** sets `BodyResolverFactory`, the overload throws `ArgumentException` rather than silently overriding your delegate. Either let the typed/keyed overload wire the resolver, or use the raw `AddCosmosOutboxRelay(configure)` escape hatch and set `BodyResolverFactory` yourself — never both.

### The `IOutboxBodyResolver` seam

The pipeline-integrated relay always **reconstructs** the `OutboundBrokeredMessage` verbatim from the fields persisted on the outbox document. The standalone relay keeps that as its default but adds an optional **Body Resolver** seam so the message can instead be **resolved from current store state at drain time** — useful when the trigger document is a thin marker and the body should reflect the aggregate's latest state rather than its state at enqueue:

```csharp
public sealed class OrderOutboxBodyResolver : IOutboxBodyResolver
{
    private readonly CosmosClient _client;
    public OrderOutboxBodyResolver(CosmosClient client) => _client = client;

    public async Task<OutboundBrokeredMessage?> ResolveAsync(
        OutboxDrainContext context, CancellationToken cancellationToken)
    {
        // context carries: MessageId, PartitionKey, PartitionKeyPath, Document (JsonElement).
        // Re-read the CURRENT aggregate state and build the message to publish now,
        // or return null to self-purge the trigger document without publishing anything.
    }
}
```

`OutboxDrainContext` is a readonly struct exposing the drained document's `MessageId`, its resolved `PartitionKey`, the container's `PartitionKeyPath` (`IReadOnlyList<string>`), and the raw `Document` as a `JsonElement`. For each pending document the relay invokes the bound resolver once and acts on its outcome:

- **Returns a non-null `OutboundBrokeredMessage`** → the relay **publishes** it, then stamps the document `delivered`.
- **Returns `null`** → **nothing is published**, but the document is **still stamped `delivered`** so it self-purges. A document that resolves to nothing is purged rather than left `pending` to re-trigger every change-feed pass and pin the lease.
- **Throws** → the document is **NOT stamped** and the exception propagates out of the change-feed handler, so the SDK does not checkpoint and the document **re-surfaces** on the next pass (at-least-once).

When **no** resolver is bound (`BodyResolverFactory` left null), the relay uses the unchanged **verbatim Reconstruct path** — identical to the pipeline-integrated relay. A **thin** trigger document that carries only the marker fields (no `MessageBody` / `MessageContentType` / `MessageContext`) therefore **requires** a resolver: the verbatim path throws `"no content type"` on it.

> **Resolver DI lifetime.** The host's **pre-scope gate is the pure `IsPendingOutbox` identity guard only** (it runs no caller code and never throws); the optional `AdditionalPendingFilter` is composed at a **single admission site — evaluated exactly once inside the relay**, never re-evaluated by the host gate, so a non-idempotent or throwing caller filter cannot be double-evaluated and wedge the change feed. The host opens a fresh `IServiceScope` and resolves the resolver from that scope **for any document that passes `IsPendingOutbox`**, disposing the scope after the document is processed. A resolver **may** therefore depend on / capture scoped services (e.g. a scoped `DbContext`) in its constructor — each pending document gets its own scoped instance, so no manual per-document scope is required inside `ResolveAsync`. A change-feed document that is **not** a pending outbox document (a domain write, an `inbox:` marker, a malformed item, or the relay's own delivered/TTL event) is cheaply skipped with **no scope opened, no factory call, and no user DI touched**, so it cannot run user code or wedge the change feed. A document that passes `IsPendingOutbox` but is narrowed out by the `AdditionalPendingFilter` **opens a scope and invokes the factory before the relay rejects it on the filter** (one wasted scope; the resolver is never asked to resolve; nothing is published or stamped; the scope is disposed).
>
> The raw `BodyResolverFactory` is the **advanced escape hatch**: because resolution happens inside the per-document scope, the factory **must resolve a fresh resolver from the supplied provider on every call and must not cache or capture** the resolver (or its scoped dependencies) across documents — a captured resolver would outlive the per-document scope it was bound to. Most applications should instead use the safe-by-default typed `AddCosmosOutboxRelay<TResolver>(...)` (or keyed) overload above, which registers the resolver scoped and wires this factory correctly for you. The typed/keyed overloads own the `BodyResolverFactory` wiring and throw `ArgumentException` if `configure` also sets it.

### Configurable knobs

`CosmosOutboxRelayOptions` exposes the document selection, stamping paths, and TTL as options (defaults in parentheses):

| Option | Purpose | Default |
| --- | --- | --- |
| `MonitoredContainerFactory` | `Func<IServiceProvider, Container>` for the container whose change feed is drained. | required |
| `LeaseContainerFactory` | `Func<IServiceProvider, Container>` for the lease container. | required |
| `PartitionKeyPath` | The monitored container's partition-key path, used to recover each document's partition key for the delivered/TTL patch. | required |
| `BodyResolverFactory` | Optional `Func<IServiceProvider, IOutboxBodyResolver>`; when null the relay uses the verbatim Reconstruct path. | null |
| `AdditionalPendingFilter` | Optional `Func<JsonElement, bool>` that can only further **narrow** admission. The relay **always** applies `CosmosOutboxDocument.IsPendingOutbox`; this predicate runs (logical `AND`) only on documents that already passed it, and is **evaluated exactly once, inside the relay** — the host's pre-scope gate uses `IsPendingOutbox` only. A pending document this filter narrows out opens a scope before the relay rejects it (one wasted scope; nothing published or stamped). | null |
| `StatusPatchPath` | Patch path for the delivered-status stamp. Anchored to the gate's status field — must equal `"/"` + the `status` field the pending gate reads. | `"/status"` |
| `DeliveredStatusValue` | Status value written on delivery. Must differ from `pending`. | `"delivered"` |
| `DeliveredTtlSeconds` | Per-document retention stamped on delivery so Cosmos self-purges the document. The delivered stamp's TTL is **hard-wired** to the Cosmos reserved `/ttl` property (the only field Cosmos self-purges on); this knob is the configurable retention in seconds, and must be `> 0`. | `86400` (24h) |
| `MonitoredSourceIdentity` / `LeaseSourceIdentity` | Optional declared change-feed source identities (the relay's processor dedup keys), as on the [advanced](#advanced-per-registration-container-factories) pipeline overload. Supply both as non-whitespace values or leave both null. | null |

The relay **always** applies the built-in pending gate `CosmosOutboxDocument.IsPendingOutbox(JsonElement)` (the **public** predicate `_chatterType="outbox"` **and** `status="pending"`) first; `AdditionalPendingFilter` can only further narrow which of those documents are admitted — it **cannot replace or weaken** the #222 id-guard. Leave it null for the default behavior, or supply a predicate to admit a strict subset of pending documents.

**The stamp knobs are validated at `AddCosmosOutboxRelay` registration** (a violation throws `ArgumentException` then, not at drain time): `DeliveredStatusValue` must differ from `pending`; `DeliveredTtlSeconds` must be `> 0` (`0`, `-1`, and negatives are rejected — a delivered document must be scheduled for self-purge); and `StatusPatchPath` must be anchored to the gate's status field (`"/"` + that field) so a delivered stamp always moves the document out of pending. The delivered stamp's TTL patch path is **not** a knob — it is hard-wired to the Cosmos reserved `/ttl` property, the only field Cosmos self-purges on, so a delivered stamp that does not schedule self-purge is unrepresentable.

**Distinct standalone relays over the same containers must declare distinct source identities.** Two standalone relays over the *same* monitored + lease containers (the same change-feed source identity) that differ only in their `AdditionalPendingFilter` or `BodyResolverFactory` would derive the **same** processor name and lease — one Cosmos consumer group whose lease ranges the SDK load-balances across both relays, so a pending outbox document checkpointed by the relay whose filter/resolver *rejects* it is never drained by the relay that would admit it (the document wedges). Because a lease cannot be keyed on a delegate, the distinction must come from the source identity. A colliding second registration is therefore rejected **fail-fast**: declaring the same `MonitoredSourceIdentity`/`LeaseSourceIdentity` pair twice throws `InvalidOperationException` at **registration**, and two ground-truth-defaulted relays (both identities null) that resolve to the same monitored+lease endpoint/database/container throw `InvalidOperationException` at **host start**. Give each standalone relay over a shared container a distinct `MonitoredSourceIdentity`/`LeaseSourceIdentity` pair.

The pipeline-integrated `WithCosmosDocumentReliability` path and its verbatim drain are **unchanged** and remain the default; `AddCosmosOutboxRelay` and the `IOutboxBodyResolver` seam are purely additive and backward-compatible.

## Domain Language

See [CONTEXT.md](../../Chatter.MessageBrokers/CONTEXT.md) for the domain glossary (Document Tier, Document-Tier Batch-Lifecycle Behavior, Atomic-Write Handle, Partition-Key Resolver, Co-Resident Outbox / Inbox Marker, Outbox Relay, Participation).

[← All Chatter modules](../../../README.md)
