# <a name="chatter-reliability-cosmos"></a> Chatter.MessageBrokers.Reliability.Cosmos

Document-tier (NoSQL) reliability for [Chatter.MessageBrokers](#chatter-messagebrokers), backed by Azure Cosmos DB.

> **Status — 0.4.1 (released 2026-08-22).** Outbox enqueue (#219) is implemented: the provider stages the co-resident outbox document onto the framework-owned `TransactionalBatch` atomically with the handler's own aggregate write. Inbox dedup (#220) is implemented: the Document-Tier Batch-Lifecycle Behavior stamps a co-resident `inbox:` marker into the framework-owned batch before `next()`; a 409 create-conflict on the marker (detected post-execute) is a *candidate* duplicate that is **confirmed by point-reading the conflicting document** before the message is acked — it is treated as a duplicate only when that doc is a genuine Chatter inbox marker (its `_chatterType` equals `inbox` AND its `MessageId` equals the inbound message id), and otherwise the message is redelivered. This closes the silent-message-loss class by construction: because the app owns the container, an app-authored `inbox:`-prefixed collision is detected and redelivered rather than silently swallowed. The public atomic-write surface still rejects reserved-prefix ids as **defense-in-depth**, but soundness comes from confirming the conflicting doc, not from the reserved namespace. The change-feed relay (#222) is **implemented**: a hosted `ChangeFeedProcessor` (one per distinct change-feed **source identity** — caller-declared on the advanced overload, ground-truth-derived incl. the account endpoint on the plain overload) drains the co-resident `_chatterType="outbox"`/`status="pending"` documents, publishes each through the broker, then marks it `delivered` and stamps a TTL so delivered documents self-purge. The relay is **at-least-once** (downstream consumers dedup via the `inbox:` marker) and requires the container to have TTL enabled (`defaultTtl` set) for the post-delivery purge — see [Change-Feed Relay](#change-feed-relay) below.

## Overview

`Chatter.MessageBrokers.Reliability.Cosmos` is the document-tier (NoSQL) implementation of the reliability ports defined by [Chatter.MessageBrokers](#chatter-messagebrokers). Where the relational EntityFramework tier wraps the handler in an ambient transaction, the document tier uses a **stage-then-commit** model: the framework opens a Cosmos `TransactionalBatch`, the handler contributes its own aggregate writes, and the framework executes the batch once as the single commit point.

The two tiers share **only** the abstract enqueue / inbox / message contracts — not the EF-shaped `TransactionContext` / `InboxBehavior` mechanics. The document tier carries its document-store primitives (resolved partition key, bound container, batch handle, ETag) on its own provider-shaped **document-tier reliability surface**.

See [ADR-0006](https://github.com/brenpike/Chatter/blob/master/docs/adr/0006-two-tier-reliability-relational-ambient-tx-vs-nosql-stage-then-commit.md), [ADR-0007](https://github.com/brenpike/Chatter/blob/master/docs/adr/0007-cosmos-outbox-co-resident-change-feed-relay.md), and [ADR-0008](https://github.com/brenpike/Chatter/blob/master/docs/adr/0008-document-tier-participation-model-and-multi-container-via-per-command-container-registry.md) for the design.

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
- **A container TTL contract the relay verifies (`defaultTtl`).** A *pending* outbox document carries no `ttl` field of its own, so a container whose `defaultTtl` is **positive** deletes it before the relay ever publishes it — silently turning at-least-once delivery into no delivery. The relay therefore reads the monitored container's properties **once, at host start**, and accepts exactly two settings: `defaultTtl = -1` (TTL on; items that carry no `ttl` field never expire) and `defaultTtl` **unset**. Every other value — any positive number, `0`, and anything below `-1` — is rejected. `-1` is the intended setting: with `defaultTtl` unset the relay still marks documents `delivered` and stamps a `ttl` on them, but Cosmos expires nothing, so delivered documents are never purged.
- **A partition-key path that matches the container's.** The same start-time read compares the partition-key path you declared — on the registration, or on `CosmosOutboxRelayOptions.PartitionKeyPath` — against the container's actual `PartitionKeyPaths`, in order, segment for segment, case-sensitively (a leading `/` is optional on your side). A mismatch is otherwise silent until drain time, where it recovers a null partition-key component and makes the delivered stamp fail **after** the publish already succeeded, so the same message re-publishes on every change-feed pass.
- **A violation fails host start.** Both checks run against that one container read, and a single exception names **every** violation, so one restart is enough to see them all. The relay never starts degraded — the same posture as the change-feed processor-name collision guard. Both variants verify the contract: the pipeline-integrated relay verifies every monitored container before it builds any processor, and the standalone relay verifies its one monitored container. The relay reads container metadata to do this, so its credentials need read access to the monitored container's properties.

## Multi-Container Support

Each `WithCosmosDocumentReliability<TCommand>` registration is independent: different command types can map to different Cosmos databases and containers. Many command types may map to the same container, but exactly **one** registration exists per command type — a duplicate registration for the same type throws with a clear message naming the type.

Container handles are derived and cached by the singleton `CosmosContainerFactory` (keyed by `(database, container)`) so concurrent in-flight command types resolve their containers without duplicate derivation.

## Change-Feed Relay

Dispatch on the document tier is a **change-feed relay**, not a polling query. When you register at least one participating command type, the provider registers a hosted `IHostedService` that runs one Cosmos `ChangeFeedProcessor` per distinct change-feed **source identity** drawn from the registry — many command types may share one source, so registrations are deduped on the source identity (one processor per source, not one per command type). The dedup key is **declared-or-ground-truth, never inferred from a caller-controlled handle**: on the plain overload it is the complete resolved identity (account **endpoint** + database id + container id, for both the monitored and lease containers); on the advanced overload the application **declares** the identity (see [Advanced](#advanced-per-registration-container-factories)) because it controls the resolved handle and the relay cannot trust it as a key. Adding the account endpoint to the plain-path key means identically-named containers in **different accounts** stay distinct rather than collapsing into one (and silently dropping the skipped source). The host resolves the registry, the container factory, the broker `IMessagingInfrastructureProvider`, and the `IBodyConverterFactory` from DI; it owns no `CosmosClient` (the app does).

Each processor monitors its container's change feed. Cosmos change feed v3 delivers **all** container changes — domain documents, `inbox:` markers, outbox documents, and the relay's own delivery-stamp updates — so the relay filters **in code** to exactly the co-resident outbox documents it must publish: `_chatterType="outbox"` **and** `status="pending"`. Everything else is skipped, including already-`delivered` outbox documents — which is what makes the relay's own delivery update a non-republish (**publish-once by construction**).

For each selected document the relay reconstructs the original `OutboundBrokeredMessage` (message id, destination, body, content-type, and message context) and publishes it through the broker, then advances the document to `status="delivered"` and stamps a positive `ttl` in a single patch so Cosmos self-purges it (the container's TTL setting and its partition-key path are verified at host start — see [Prerequisites](#prerequisites)).

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

`AddCosmosOutboxRelay` lives in the `Microsoft.Extensions.DependencyInjection` namespace and registers **its own** `IHostedService`, **independent** of `AddChatterCqrs` / `WithCosmosDocumentReliability` and the `DocumentReliabilityRegistry`. It is **repeatable** — call it once per monitored container to run multiple standalone relays side by side. The standalone relay drains the same co-resident pending outbox documents (`_chatterType="outbox"` **and** `status="pending"`), publishes each through the broker, then stamps the document `delivered` and stamps a TTL so delivered documents self-purge — exactly as the pipeline-integrated relay does, and with the same **at-least-once** delivery and the same start-time container verification (see [Prerequisites](#prerequisites)).

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
| `PoisonAfterConsecutiveFailures` | **Opt-in.** The number of **consecutive** failed drains of the *same* document after which the relay gives up on it, stamping it `PoisonStatusValue` so the change feed can advance past it. `0` is **off** — every failure re-throws, nothing is checkpointed, and the document stays `pending`. Must not be negative. | `0` (off) |
| `PoisonStatusValue` | The non-`pending` status a given-up document is stamped with. Read only while the policy is enabled, and then it must be non-empty and must differ from both `pending` and `DeliveredStatusValue`. | `"poisoned"` |
| `MonitoredSourceIdentity` / `LeaseSourceIdentity` | Optional declared change-feed source identities (the relay's processor dedup keys), as on the [advanced](#advanced-per-registration-container-factories) pipeline overload. Supply both as non-whitespace values or leave both null. | null |

The relay **always** applies the built-in pending gate `CosmosOutboxDocument.IsPendingOutbox(JsonElement)` (the **public** predicate `_chatterType="outbox"` **and** `status="pending"`) first; `AdditionalPendingFilter` can only further narrow which of those documents are admitted — it **cannot replace or weaken** the #222 id-guard. Leave it null for the default behavior, or supply a predicate to admit a strict subset of pending documents.

**The stamp knobs are validated at `AddCosmosOutboxRelay` registration** (a violation throws `ArgumentException` then, not at drain time): `DeliveredStatusValue` must differ from `pending`; `DeliveredTtlSeconds` must be `> 0` (`0`, `-1`, and negatives are rejected — a delivered document must be scheduled for self-purge); and `StatusPatchPath` must be anchored to the gate's status field (`"/"` + that field) so a delivered stamp always moves the document out of pending. The delivered stamp's TTL patch path is **not** a knob — it is hard-wired to the Cosmos reserved `/ttl` property, the only field Cosmos self-purges on, so a delivered stamp that does not schedule self-purge is unrepresentable.

**Giving up on a document that fails every time (opt-in, standalone relay only).** Throw-so-nothing-checkpoints is the right answer to a **transient** publish failure — the document simply re-surfaces on the next pass — but on its own it has no escape from a **deterministic** one: a document that fails on every pass re-throws forever, its lease never advances, and every later pending document in that lease's partition range stays undrained. `PoisonAfterConsecutiveFailures` is the escape, and it is **off by default**; the pipeline-integrated relay does not offer it and stays fail-closed. Turn it on only where head-of-line blocking is the greater risk.

With a threshold of `N > 0`, the relay counts consecutive failed drains per document id. **Below** `N` the failure propagates unchanged. **At** `N` the relay gives up on that document: one patch sets the status path to `PoisonStatusValue` — a value the always-applied pending gate no longer admits — then the give-up is counted on `chatter.messaging.outbox.drain.poisoned` and logged at `Error`, and the relay continues to the next document so the batch checkpoints and the head-of-line block on that lease clears. A **successful** drain of that id clears its count, so an intermittent failure can never accumulate across successes into a give-up, and a drain **cancelled** by host shutdown does not count toward the threshold.

**A given-up document is never deleted and carries no TTL.** Unlike a delivered document it is not scheduled for self-purge: it stays in the container at its poison status, inspectable indefinitely, because it is the evidence of whatever stalled the relay. Nothing re-publishes it — re-driving it (for example, patching its status back to `pending`) is an operator action. The poison stamp's **own** failure is never swallowed: a misconfigured partition-key path makes the poison patch fail exactly as the delivered patch would, and that surfaces rather than being laundered into "give up on everything".

Consecutive-failure counts live in memory, per relay instance — a restarted host starts counting from zero — and the set of tracked document ids is **capped at 1024**. A new id arriving past the cap is simply not tracked, so a long-lived relay under widespread transient failures degrades back to the fail-closed behavior rather than to unbounded memory. Both poison knobs are validated at `AddCosmosOutboxRelay` registration alongside the stamp knobs: a negative threshold is rejected, and while the policy is enabled `PoisonStatusValue` must be non-empty, must differ from `pending` (which would re-surface the document forever) and must differ from `DeliveredStatusValue` (which would make a give-up indistinguishable from a delivery).

**Distinct standalone relays over the same containers must declare distinct source identities.** Two standalone relays over the *same* monitored + lease containers (the same change-feed source identity) that differ only in their `AdditionalPendingFilter` or `BodyResolverFactory` would derive the **same** processor name and lease — one Cosmos consumer group whose lease ranges the SDK load-balances across both relays, so a pending outbox document checkpointed by the relay whose filter/resolver *rejects* it is never drained by the relay that would admit it (the document wedges). Because a lease cannot be keyed on a delegate, the distinction must come from the source identity. A colliding second registration is therefore rejected **fail-fast**: declaring the same `MonitoredSourceIdentity`/`LeaseSourceIdentity` pair twice throws `InvalidOperationException` at **registration**, and two ground-truth-defaulted relays (both identities null) that resolve to the same monitored+lease endpoint/database/container throw `InvalidOperationException` at **host start**. Give each standalone relay over a shared container a distinct `MonitoredSourceIdentity`/`LeaseSourceIdentity` pair.

The pipeline-integrated `WithCosmosDocumentReliability` path and its verbatim drain are **unchanged** and remain the default; `AddCosmosOutboxRelay` and the `IOutboxBodyResolver` seam are purely additive and backward-compatible.

## Standalone Inbox (`WithCosmosInbox`)

The [document tier](#change-feed-relay) dedups *inside* the aggregate's `TransactionalBatch`. For a **stateless consumer** that has no Cosmos aggregate, no transactional outbox, and no lease container — a message ACL hop that must simply not process the same message twice — `WithCosmosInbox` registers a **standalone, lease-less inbox-dedup gate** on the command pipeline. It performs an **anti-TOCTOU two-phase write-ahead claim** through the existing `InboxBehavior<T>` seam: phase 1 `CreateItemStream`s a *pending* `inbox:` marker on an `/idempotencyKey`-partitioned container **before** the handler, and phase 2 `PatchItemStream`s it to *completed* **after** the handler returns — so a redelivery **confirms a duplicate on *completion*, not mere existence**, and **skips the handler only on a confirmed completed marker** (an abandoned *pending* marker is taken over and the handler re-runs, not skipped). See [ADR-0009](https://github.com/brenpike/Chatter/blob/master/docs/adr/0009-standalone-cosmos-inbox-confirm-not-infer-and-fail-loud.md) (amended D1) for the design.

```csharp
public void ConfigureServices(IServiceCollection services)
{
    // App owns the CosmosClient — the inbox derives the idempotency container from it and owns no client.
    services.AddSingleton(new CosmosClient(Configuration.GetConnectionString("Cosmos")));

    services.AddChatterCqrs(Configuration, pipeline =>
            {
                pipeline.WithCosmosInbox(options =>
                {
                    options.Database  = "shop";
                    options.Container = "idempotency"; // partitioned by /idempotencyKey
                    // Optional dedup-window TTL so Cosmos self-purges old markers (default: persist indefinitely).
                    options.MarkerTimeToLive = 604800; // 7 days, in seconds
                });
            })
            .AddMessageBrokers(/* message broker options */);
}
```

`WithCosmosInbox` **replaces** the default `IBrokeredMessageInbox` with the standalone Cosmos inbox and adds `InboxBehavior<>` — and **registers nothing else**: **no lease container, no change-feed relay, no outbox, no router replacement, and no unit-of-work behavior**. It is registered `Scoped` (EF-inbox parity).

### Prerequisites

- **App-registered `CosmosClient`.** The application registers a `CosmosClient` singleton in DI; the inbox derives the idempotency container from it via `client.GetContainer(database, container)` and owns no client. A missing `CosmosClient` throws at resolution time.
- **A `/idempotencyKey`-partitioned container.** The application provisions the idempotency container with a **single-segment** partition-key path (default `/idempotencyKey`); the partition value of each marker is the inbound message id. The provider **provisions nothing** — it neither creates the container nor enables TTL. Hierarchical (multi-segment) partition-key paths are rejected in v1 (deferred to backlog #254); a `MarkerTimeToLive` only takes effect if the container has TTL enabled (`defaultTtl` set).

### Confirm-not-infer, two-phase claim→complete, and the write-ahead-claim idempotency contract

The claim is **two-phase and write-ahead**: **phase 1** `CreateItemStream`s a *pending* marker (`Completed=false`) **before** the handler; on a fresh `201` the handler runs, then **phase 2** `PatchItemStream`s the marker to `Completed=true` **after** the handler returns. A redelivery therefore confirms a duplicate on **completion, not mere existence** — this closes the abandoned-marker permanent-loss defect a single-phase confirm-on-existence had: a marker persisted but then **abandoned** (the process hard-killed between the `201` and handler completion) would otherwise be mistaken for a completed one and its handler would never run. There is no compensation-delete at all (it was removed — see the **monotonic-marker** guarantee below): even a hypothetical best-effort compensation could not close that window, since a compensation `catch` fires only on a handler *exception*, never on a SIGKILL between the create and completion — so the safety net is confirm-on-completion plus take-over, not compensation.

A **409 create-conflict is a *candidate* duplicate, not a confirmed one**: because the app owns the container it can author a colliding `inbox:`-prefixed id through a non-staging path, so the inbox **point-reads the conflicting document** and resolves it **three ways**, checking `_chatterType="inbox"` **and** `MessageId` equal to the inbound id (confirm-not-infer) **before** inspecting completion:

- **A genuine `Completed=true` marker for this id → SKIP** — a confirmed duplicate; the handler does not re-run.
- **A genuine but *pending* (abandoned) marker for this id → TAKE OVER** — the handler **runs** (no loss) and phase 2 then completes the claim. An abandoned pending marker is adopted, **not** confirmed as a duplicate.
- **A non-confirmable read** — a non-marker doc, a different-id marker, a non-success read, or a `404` whose bounded read-back budget (`ReadBackMaxAttempts` / `ReadBackInterval`) is exhausted — **redelivers (throws)** rather than silently skipping.

This closes the silent-first-delivery-loss class **and** the abandoned-marker permanent-loss class by construction. It costs **one extra completion write** — a single `PatchItemStream` per fresh (or taken-over) delivery.

Because the claim precedes the handler **and** an abandoned claim is taken over (re-running the handler), **handlers behind this inbox MUST be idempotent AND concurrency-safe (safe under concurrent execution of the same id)**:

- **A confirmed *completed* duplicate is closed-by-construction.** Its redelivery is suppressed without re-running the handler.
- **Take-over and completion-retry are AT-LEAST-ONCE; the marker is MONOTONIC.** A pending/abandoned marker is taken over, so the handler **re-runs**; and a phase-2 completion-write **failure THROWS** — the message redelivers rather than acking with a still-pending marker — so the handler can run again on that redelivery. On a handler **failure** (fresh claim or take-over) the original exception simply **propagates for redelivery** and the write-ahead **pending marker is LEFT IN PLACE — never deleted** — so a redelivery **takes it over** and re-runs the handler; any side effect the handler already performed before failing (external HTTP, a non-Cosmos write) has **already happened** and re-runs on that redelivery. The shared marker state is **MONOTONIC**: it only ever moves *absent → pending → completed*, and a **TTL purge (`MarkerTimeToLive`) is the only removal** — the gate never moves *completed → absent*, so a marker another delivery already completed can never be reverted (there is no compensation-delete to corrupt it under concurrent same-id delivery). A poison / permanently-failing message with no `MarkerTimeToLive` therefore leaves a persistent pending marker that each redelivery re-takes-over and re-runs (correct at-least-once — the transport eventually dead-letters it); set `MarkerTimeToLive` to bound this marker accumulation. This is the same side-effect-timing contract the [document tier documents](#handler-idempotency-contract).
- **The gate dedups redeliveries; it does NOT serialize genuinely-concurrent in-flight deliveries of the same id.** Take-over adopts a *pending* marker whether that marker is abandoned (a hard-kill) **or** still live — written by a concurrent in-flight delivery of the same id whose handler has not yet completed — because the lease-less design cannot distinguish the two without the liveness lease it rejects. Two genuinely-concurrent deliveries of the same message therefore both run the handler, **concurrently**. This gate is a dedup gate, **not a distributed lock**: mutual exclusion for concurrent delivery is the **transport's** responsibility — the message-lock / session an at-least-once broker holds while a delivery is in-flight (e.g. Azure Service Bus PeekLock or a session) is what prevents a second concurrent delivery, and the dedup gate is not a substitute for it. Hence the contract is *concurrency-safe*, not merely *sequential-retry-safe*.
- **A null/whitespace `MessageId` FAILS LOUD.** The marker is keyed on the message identity, so a message with no id cannot be deduped. The inbox throws `InvalidOperationException` before writing anything — the handler never runs — matching the document tier and the in-memory inbox (not the EF relational inbox's run-with-no-dedup). A raw Azure Service Bus producer that omits the message id must set one upstream or accept this loud failure.

### Contrast with the document tier, and composition

Unlike `WithCosmosDocumentReliability<TCommand>`, the standalone inbox is **lease-less, relay-less, and stateless**: it opens no `TransactionalBatch`, registers no outbox / unit-of-work, and skips the handler on a confirmed *completed* duplicate (rather than deduping batched writes after the handler runs). It is the once-only gate for a consumer that persists nothing through Chatter.

- **`WithCosmosDocumentReliability` + `WithCosmosInbox` is UNSUPPORTED** in one pipeline (ADR-0009 D3). They dedup by different mechanisms; registering both makes `InboxBehavior<>` fire the standalone write-ahead claim **before** the handler for document-tier participant commands too, pre-empting the document tier's atomic in-batch dedup. This is **documented, not code-guarded** — no current consumer uses the document tier.
- **`AddCosmosOutboxRelay` + `WithCosmosInbox` is fully SUPPORTED.** The standalone outbox relay and the standalone inbox are orthogonal lease-less primitives and compose cleanly (a consumer that drains its own outbox container and dedups inbound messages).

## Diagnostics and Metrics (optional, opt-in)

The Outbox Relay's drain is instrumented with OpenTelemetry-compatible metrics. They are **off until an application opts in**, and `Chatter.MessageBrokers.Reliability.Cosmos` takes **no dependency on any `OpenTelemetry.*` NuGet package** — the instrumentation is built on the .NET base class library only: `System.Diagnostics.Metrics.Meter` for the instruments, and `System.Diagnostics.ActivitySource` for the scope this module reserves for spans.

### Turning it on

The `ActivitySource` and the `Meter` are both named after the emitting assembly — **`Chatter.MessageBrokers.Reliability.Cosmos`** (ADR-0010 D3, per-assembly scope naming). That name **is** the consumer contract: an application subscribes to this module's telemetry by naming that scope, and nothing else in it is a supported subscription surface. Every sibling Chatter package emits under its own assembly-named scope, so the drain can be sampled and filtered independently of the broker boundary.

```csharp
services.AddOpenTelemetry()
        .WithTracing(t => t.AddSource("Chatter.*"))    // or .AddSource("Chatter.MessageBrokers", "Chatter.MessageBrokers.Reliability.Cosmos")
        .WithMetrics(m => m.AddMeter("Chatter.*"));    // or .AddMeter("Chatter.MessageBrokers", "Chatter.MessageBrokers.Reliability.Cosmos")
```

Any .NET `MeterListener` or `ActivityListener` works just as well — an OpenTelemetry provider merely subscribes to these base-class-library primitives, it is not a prerequisite for them.

This module emits **metrics only today**. The span the drain publishes under belongs to the `Chatter.MessageBrokers` scope, so an application that wants the drain's spans as well as its metrics subscribes to both scopes — see [The drain publishes under the shared send span](#the-drain-publishes-under-the-shared-send-span) below. The `Chatter.MessageBrokers.Reliability.Cosmos` `ActivitySource` is declared and reserved anyway, so a later module-native span joins a scope applications already subscribe to, with no rename.

### Off means off

**When nothing subscribes to this module's meter, nothing is emitted and the drain does exactly the work it did before it was instrumented.** Every emit site checks whether this module's own instrument has a listener as its first statement, and returns before a timestamp is read, a tag is built, or a lease token is touched. The guard is always the module's own `ActivitySource.HasListeners()` or `Instrument.Enabled` — never the ambient `Activity.Current`, which is non-null in any host running unrelated instrumentation and therefore says nothing about whether Chatter's diagnostics are on. The guarantee is per-operation; constructing the `ActivitySource` and `Meter` themselves is a one-time static initialization per process, which is unavoidable for any `Meter`-based design.

The `Error`-level failure logs described under [What is emitted](#what-is-emitted) are a **separate channel**: they are written through the host's own `ILogger`, never through this module's meter, so they are emitted whether or not an application opted into diagnostics.

`CosmosReliabilityDiagnostics.IsEnabled` is the public outer guard, an OR across this module's tracing and metrics subscriptions. It is the guard a call site checks before doing instrumented work at all; each individual measurement is guarded again on the specific instrument it records, so an application that opted into tracing only never enters a metric path.

### What is emitted

Six instruments, all recorded by the Outbox Relay. Four are recorded by both the Document-Tier variant and the Standalone variant, which share the same drain core; `chatter.messaging.outbox.drain.failures` is recorded from the change feed's error-notification seam, which both hosts wire; and `chatter.messaging.outbox.drain.poisoned` exists only on the Standalone variant, the only one that carries the opt-in poison policy.

**Instruments**

| Instrument | Type | Unit | Advised buckets | Records | Recorded when |
| --- | --- | --- | --- | --- | --- |
| `chatter.messaging.outbox.drain.lag` | `Histogram<double>` | `s` | `0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 300, 600` — published as instrument advice on `net10.0` only; on `net8.0` the instrument carries none. See [Histogram bucket boundaries](#histogram-bucket-boundaries) below. | How long one Outbox Document had been pending when the Outbox Relay admitted it, in seconds. | Once per admitted document that carries a Cosmos `_ts`, recorded **at admission** — before the brokered message is reconstructed, resolved or published — so a document whose publish then throws still reports how long it had been pending, which is the case the measurement exists to expose. A document carrying no `_ts` (one that never went through Cosmos) records nothing here, though its outcome is still counted. Only while a .NET `MeterListener` has enabled this instrument. |
| `chatter.messaging.outbox.drain.documents` | `Counter<long>` | `{document}` | Not applicable — a `Counter<long>` has no buckets. | One change-feed document the Outbox Relay resolved, carrying how it resolved it. | Once per change-feed document handed to the relay, including a document the relay never drains — that one is counted `skipped`. An admitted document whose publish throws records **nothing here at all**: the outcome is written only after the publish returns, so a failed drain shows up as a batch with no admitted document rather than as a fabricated outcome. Only while a .NET `MeterListener` has enabled this instrument. |
| `chatter.messaging.outbox.drain.batch.size` | `Histogram<int>` | `{document}` | `1, 2, 5, 10, 25, 50, 100, 250, 500, 1000` — published as instrument advice on `net10.0` only; on `net8.0` the instrument carries none. See [Histogram bucket boundaries](#histogram-bucket-boundaries) below. | How many documents one change-feed batch carried. | Once per change-feed batch delivered to a relay host, after the batch payload has been parsed and before any document in it is processed. An **empty** batch is recorded too, as size `0`. A batch payload whose shape the relay cannot parse fails closed and records nothing. Only while a .NET `MeterListener` has enabled this instrument. |
| `chatter.messaging.outbox.drain.batches` | `Counter<long>` | `{batch}` | Not applicable — a `Counter<long>` has no buckets. | One change-feed batch the Outbox Relay handled. | Exactly as `chatter.messaging.outbox.drain.batch.size` above — the two are recorded together, from one emit site, over one tag set, so a batch cannot be sized against one lease and counted against another. Only while a .NET `MeterListener` has enabled this instrument. |
| `chatter.messaging.outbox.drain.failures` | `Counter<long>` | `{failure}` | Not applicable — a `Counter<long>` has no buckets. | One drain attempt that faulted, carrying the lease it faulted under and the type of the fault. | Once per fault the Cosmos change-feed processor reports through its error-notification seam — the only channel that carries a lease or processor fault **together with** the lease token it happened under, which the drain core itself never sees. Recorded by both relay variants. Only while a .NET `MeterListener` has enabled this instrument. |
| `chatter.messaging.outbox.drain.poisoned` | `Counter<long>` | `{document}` | Not applicable — a `Counter<long>` has no buckets. | One Outbox Document the Outbox Relay gave up on and stamped poisoned. | Once per document the opt-in [poison policy](#configurable-knobs) elected, recorded **after** the poison stamp succeeded — a give-up is never reported before the document actually left `pending`. Standalone relay only, and never at all while the policy is off, which is its default. Only while a .NET `MeterListener` has enabled this instrument. |

**Metric attributes**

| Attribute | Instruments | Value | Emitted |
| --- | --- | --- | --- |
| `chatter.messaging.outbox.drain.outcome` | `chatter.messaging.outbox.drain.documents` | One of `admitted`, `skipped`, `dropped` — the vocabulary below. | Always, as a key. |
| `chatter.messaging.outbox.lease_token` | `chatter.messaging.outbox.drain.batch.size`, `chatter.messaging.outbox.drain.batches`, `chatter.messaging.outbox.drain.failures` and `chatter.messaging.outbox.drain.poisoned` | The change-feed lease token the batch, fault, or give-up belongs to — the partition-progress dimension. | Always, as a key. |
| `error.type` | `chatter.messaging.outbox.drain.failures` | The type of the fault that ended the attempt, resolved exactly as the shared send path resolves it for the same exception. | Always, as a key. |

The **outcome vocabulary** is closed, and each value names one way the relay resolves a document it was handed:

- **`admitted`** — the document was a pending Outbox Document and its brokered message was published.
- **`skipped`** — the document was not a pending Outbox Document, so the relay never drained it. Cosmos change feed delivers every container change, so this is the ordinary case for domain writes, `inbox:` markers, already-`delivered` outbox documents, and the relay's own delivery stamps.
- **`dropped`** — the document was admitted and resolved to no brokered message, so it was marked `delivered` without a publish. Only a bound [`IOutboxBodyResolver`](#the-ioutboxbodyresolver-seam) can produce this outcome, by returning `null` for an intentional drop-and-acknowledge.

**A failure is not a fourth outcome.** An outcome records how a document **resolved**; a failure records an attempt that never resolved. The two are separate facts on separate instruments, so the outcome vocabulary stays closed and a faulted attempt is never counted as an outcome.

**The failure channel is not only opt-in.** Metrics are opt-in, so counting a stalled lease and nothing else would leave an application that subscribed to no meter exactly as blind to it as it was before. Both failure paths therefore also write an **always-on `Error`-level log** through the host's `ILogger`, independent of any meter subscription (an application that configured no logging at all gets none, of course — a missing `ILogger` is a silent no-op and never a startup failure): a change-feed fault logs the lease token, the fault, and that the lease does not advance until the fault clears; a give-up logs the document id, the lease token, the consecutive-failure count, and the poison status the document now carries. Reporting a fault may never break delivery, so a failure **inside** the change-feed fault notification — including a faulting log sink — is swallowed rather than propagated out of the SDK's notification callback, which would wedge the very pump the notification exists to expose. The give-up path is ordered the other way round: the poison stamp runs first and its own failure propagates, so nothing reports a give-up that did not happen.

### The names are Chatter-native, not semantic conventions

All six instrument names and the two `chatter.messaging.*` attribute names sit under a `chatter.` prefix because the OpenTelemetry messaging semantic conventions pinned by this repository (**v1.30.0**) cover **no outbox-drain concept at all** — there is no standard spelling for a drain lag, a drain outcome, or a change-feed lease token (ADR-0010 D4). Inventing a `messaging.*` spelling for one would be a false claim of conformance to a convention that says nothing about it. The one attribute that is **not** Chatter-native is `error.type` on the failure counter: it is a general-purpose OpenTelemetry registry attribute defined **outside** messaging semconv, so it is emitted under its standard spelling — the same one every other Chatter emit site uses for the same concept — rather than under a second Chatter-native name for it. Because telemetry attribute names are emitted data rather than a compile-time type surface, they may change in a minor release; dashboards and alert queries that hard-code them should expect to be revisited, and any such change is announced in this package's CHANGELOG.

### Drain lag and the `_ts` clock-skew caveat

The lag is the elapsed time between the document's Cosmos `_ts` and the relay host's own clock at admission. Two properties of `_ts` follow the measurement:

- **`_ts` is the Cosmos server's write time, at SECOND granularity.** A drain that completes well inside a second therefore reports a lag quantized to whole seconds rather than a sub-second one, and the smallest advised bucket exists to keep those measurements distinguishable rather than to promise sub-second resolution.
- **Clock skew can make the age negative, and it is CLAMPED AT ZERO.** The stamp comes from the Cosmos server and the comparison clock comes from the relay host; when the host's clock runs behind the account's, subtracting one from the other yields a negative number. A document cannot be admitted before it was written, so a negative age is not representable and the skew is clamped to zero rather than recorded. A cluster of exact-zero lag measurements is therefore the signal to check host clock skew, not evidence of an instantaneous drain.

The clamp lives in one place — the module's own diagnostics surface derives the age from the raw `_ts` — so no call site can record a lag it computed for itself.

### Batch metrics and lease progress

Batch size and batch count are recorded **once per change-feed batch**, tagged by the lease token that batch was delivered for, so both are per-partition progress signals rather than per-document ones.

An **empty batch is still recorded** (size `0`, count `1`). The batch count measures lease progress, so dropping the empty ones would make an **idle** partition — one whose lease is being served but has nothing pending — indistinguishable from a **stalled** one, whose lease is not advancing at all. That distinction is the reason the instrument exists.

A batch whose payload the relay cannot parse **fails closed and records nothing**: the relay throws before it can know the batch size, so the SDK does not checkpoint and the batch re-surfaces on the next pass. Recording a fabricated size for a batch the relay could not read would report progress that did not happen.

### <a name="the-drain-publishes-under-the-shared-send-span"></a> The drain publishes under the shared send span

**This module declares no span of its own.** With tracing opted into, the relay publishes each drained document under the **`Chatter.MessageBrokers` send span** that every other Chatter send site opens, parented to the trace context persisted with that document at write time. A drained document is therefore never reported by two send spans, and this module never re-emits that span under its own scope. Its drain metrics are the module's whole contribution to the telemetry stream.

**With tracing on, the drain REPARENTS.** The relay writes **that send span's** context over the `traceparent` on the **outgoing** message, so a downstream receive parents to the drain hop rather than directly to the write. This is intended and it matches the relational outbox drain: the drain is the hop that actually put the message on the broker, minutes after the write and in another process, and the trace still reads write → drain → receive because the drain span is itself a child of the context it replaced. A document that carries no persisted context — one written while diagnostics were off, or received over a path that propagates none — starts a fresh root instead, with the change feed's ambient activity attached as a **link** rather than promoted to parent, because the feed did not cause the message.

The reparenting is written onto the outgoing message only. The **persisted** document is never rewritten, which gives the replay shape a name: because the relay is [at-least-once](#change-feed-relay), one message republished N times emits **N send spans with N distinct drain parents, all sharing the one write-time root** the document has carried since it was staged. Replays fan out under the original write rather than chaining off one another.

As on the broker boundary, this happens **only when diagnostics are opted into**. With them off, on the metrics-only path, and when the drain span is sampled out, nothing is written and the persisted `traceparent` rides out unchanged.

### Histogram bucket boundaries

`chatter.messaging.outbox.drain.lag` records **seconds** and `chatter.messaging.outbox.drain.batch.size` records **documents**. The OpenTelemetry .NET SDK's default explicit histogram boundaries are millisecond-sized (`0, 5, 10, 25, ... 10000`), so a collector that applies them puts every realistic measurement of either instrument in the first bucket and P50, P90 and P99 all report the same number forever. This package therefore publishes boundaries sized for each instrument's own unit as instrument advice; the two counters alongside them have no buckets to advise. The lag boundaries reach further than the broker boundary's own duration histogram because a drain lag is not one client call — a restarted lease or a backlog leaves a document pending for minutes.

**They are advice, not a setting.** The boundaries are published as instrument *advice* — a **default** that an application's own view **overrides**. An application that already registers a view for either instrument keeps winning exactly as it did before; nothing it configured changes.

**Advice is published on `net10.0` only.** The base class library type that carries instrument advice does not exist in the `net8.0` shared framework, and this package takes no package dependency to reach it. On `net8.0` both histograms therefore ship with no advice at all, and the collector falls back to its own millisecond-sized defaults.

**On `net8.0`, configure the equivalent views in your own application.** `AddView` and `ExplicitBucketHistogramConfiguration` are `OpenTelemetry.Metrics` types that come from *your* application's OpenTelemetry packages — this package still takes **no dependency on any `OpenTelemetry.*` NuGet package**, and the snippet below adds none to it:

```csharp
using OpenTelemetry.Metrics;

services.AddOpenTelemetry()
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

The same views are harmless on `net10.0`: they override advice that already carries these boundaries. This `net8.0` caveat retires when `net8.0` is dropped and the package single-targets `net10.0` after .NET 8 reaches end of life on 2026-11-10 — tracked in [issue #395](https://github.com/brenpike/Chatter/issues/395).

Design rationale, the per-assembly scope naming, and the off-guard rules are recorded in [ADR-0010](https://github.com/brenpike/Chatter/blob/master/docs/adr/0010-optional-bcl-only-telemetry-per-assembly-sources-and-the-off-guard.md).

## Domain Language

See this module's [CONTEXT.md](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers.Reliability.Cosmos/CONTEXT.md) for its own glossary (Outbox Document, Outbox Relay, Change-Feed Source Identity, Monitored-Container Contract, Poison Policy, Drain Outcome, Drain Failure, Drain Lag, Lease Token), and the Message Brokers [CONTEXT.md](https://github.com/brenpike/Chatter/blob/master/src/Chatter.MessageBrokers/CONTEXT.md) for the shared terms it builds on (Document Tier, Document-Tier Batch-Lifecycle Behavior, Atomic-Write Handle, Partition-Key Resolver, Co-Resident Outbox / Inbox Marker, Outbox Relay, Participation).

[← All Chatter modules](https://github.com/brenpike/Chatter/blob/master/README.md)
