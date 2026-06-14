---
status: accepted
date: 2026-06-14
---

# Two-tier reliability port: relational ambient-tx vs NoSQL stage-then-commit

`Chatter.MessageBrokers` defines a reliability port (outbox / inbox / unit-of-work) shaped entirely around a **relational ambient-transaction model**. `UnitOfWorkBehavior` — a CQRS pipeline behavior — calls `IUnitOfWork.ExecuteAsync(_ => next(), txContext)`, wrapping the entire downstream handler pipeline inside an open EF transaction and committing after `SaveChanges`. The handler is opaque, arbitrary code running **inside** an ambient transaction; it can read its own writes and branch on intermediate persisted state.

Adding a Cosmos DB (NoSQL) reliability provider exposed a **semantic mismatch** — not merely a signature mismatch. Cosmos has no ambient transaction. `TransactionalBatch` is an accumulate-then-execute-once primitive; you cannot run arbitrary user handler code "inside" a Cosmos transaction. The existing port is therefore unportable to document stores as-is.

## Considered Options

- **(A) Keep ambient semantics; force Cosmos under the same interface** — Cosmos can only support a restricted handler style (writes buffered and flushed at batch-execute; no read-your-writes). Same interface, silently different guarantees per provider — a leaky abstraction. **Rejected.**
- **(B) Redefine the whole port to a handler-explicit stage-then-commit contract for ALL providers** — portable and honest, but a behavioral break for existing EF consumers (handlers must declare writes rather than rely on ambient tx). Destructive to existing relational consumers for no relational benefit. **Rejected.**
- **(C) Two-tier (ACCEPTED)** — keep the relational ambient-tx tier (`IUnitOfWork.ExecuteAsync` + `UnitOfWorkBehavior`) untouched and relational-only; add a NoSQL/document tier using stage-then-commit. Both tiers are honored through one existing seam.

## Decision

Adopt option (C). The unifying seam **already exists**: `TransactionContext.Container` — a `ContextContainer` property bag — abstracts "the active atomic-write handle." The relational tier stuffs `IPersistanceTransaction` into it; the NoSQL tier stuffs the `TransactionalBatch` (plus partition key) into the **same container**. `IBrokeredMessageOutbox.SendToOutbox(msgs, txContext, ct)` remains the **shared enqueue contract** — it stages outbound messages into whatever handle is in the container (relational appends rows to the transaction; Cosmos adds an outbox-doc operation to the batch). `IBrokeredMessageInbox.ReceiveViaInbox` remains shared.

**The one breaking surgery**: split `IBrokeredMessageOutbox`. Peel the relational polling-dispatch methods (`GetUnprocessedMessagesFromOutbox`, `GetUnprocessedBatch`, `UpdateProcessedDate`) into a new relational-only `IPollableOutboxStore`; the common `IBrokeredMessageOutbox` retains only `SendToOutbox`. `IUnitOfWork` is marked relational-only — Cosmos never implements it; the NoSQL tier's atomic-write initiation is a sibling primitive, not a faked `ExecuteAsync`.

The NoSQL tier originally used **model C2: handler-owns-batch, framework-contributes-outbox**. The handler opens its own `TransactionalBatch` on its aggregate's partition, registers it in `TransactionContext.Container`, performs its own aggregate writes, calls `SendToOutbox` (which adds the outbox-doc operation to that batch), then executes the batch. This mirrored EF exactly — the handler does its own `DbContext` aggregate writes and the outbox adds rows to the same context/transaction.

---

### Superseded sub-decision — document-tier atomic-write-initiation primitive (C2 → framework-owns-batch-lifecycle)

**Re-decision.** The document-tier atomic-write-initiation primitive is re-decided from C2 (handler-owns-batch) to **framework-owns-batch-lifecycle**. This amendment supersedes the C2 paragraph above for the document tier only; the C2 label and its rationale are preserved here as an audit trail of the review iterations. The relational EF tier is unchanged in every respect.

**Mechanism.** A new outermost document-tier pipeline behavior — the document-tier sibling of `UnitOfWorkBehavior` — owns the batch lifecycle. It executes in this order:

1. Opens an empty `TransactionalBatch` on the message's resolved partition and exposes it via the document-tier reliability surface — the provider-shaped doc-tier atomic-write handle (the doc-tier sibling of `IPersistanceTransaction`), NOT `TransactionContext.Container`. (See the surface-ownership amendment below and CONTEXT.md.)
2. Stamps the inbox-marker create-op into the batch (framework-owned doc shape; no domain knowledge of the application aggregate).
3. Calls `next()` — the handler resolves the batch from the document-tier handle and contributes its own aggregate ops (handler owns domain serialization and partition; the framework never sees domain types). `SendToOutbox` contributes the outbox-doc op to the same batch via the shared enqueue contract abstracted over the Atomic-Write Handle (the enqueue contract is unchanged).
4. After `next()` returns, the behavior executes the batch once (the single commit) and inspects the per-op `TransactionalBatchResponse`: a 409 on the marker op is a confirmed duplicate — the behavior acks and swallows; because batch execution is all-or-nothing, no aggregate or outbox writes commit on conflict.

**Why closed-by-construction.** The batch commit boundary moves out of the handler and is owned by the outermost behavior — exactly as EF's commit is owned by the outermost `UnitOfWorkBehavior` (single `SaveChanges`). The marker is necessarily a batch member because the framework that owns the batch adds it before `next()`. The "marker cannot join a handler-internal batch through the `ReceiveViaInbox(() => next())` wrap seam" class of failure is eliminated: `InboxBehavior` contributes the marker op via the container seam (as a batch member) before handing control to `next()`, so there is no sequencing gap in which the handler could execute and commit the batch independently, leaving no open batch for the marker to join. Both tiers are now symmetric: outermost behavior owns the commit; inner participants (handler aggregate ops, `SendToOutbox`) contribute staged ops.

**Container ownership remains with the application.** The application injects the container; the framework owns batch lifecycle (open + execute). These two responsibilities are distinct and must not be conflated — container ownership does not move into Chatter.

**Options not chosen (rejection record).**

- **(2) Pre-execute commit callback** — the handler still constructs the batch and must remember not to execute it; re-admits the failure mode that C2 produced. Not closed-by-construction. Rejected.
- **(3) Handler-enlist-marker API (drop `InboxBehavior` for the document tier)** — loses dedup by omission if a handler forgets to enlist, and forks the shared `ReceiveViaInbox` inbox seam. Rejected.

Option 1 (framework-owns-batch-lifecycle) keeps the shared seams (`SendToOutbox`, `ReceiveViaInbox`, `TransactionContext.Container`), keeps the framework ignorant of domain types, and achieves symmetry with the EF tier.

---

**Three axes — one unified, four forked:**

- **Enqueue** (`SendToOutbox`): shared.
- **Inbox dedup contract** (tier-neutral once-only-handling abstraction): shared — but this is the abstract contract, NOT the relational-shaped `ReceiveViaInbox(..., Func<Task>)` wrap seam. The relational tier realizes the contract via `ReceiveViaInbox` / `InboxBehavior` (relational-only mechanics); the document tier realizes it via inbox-marker enlistment on the document-tier surface (see "ReceiveViaInbox / InboxBehavior — relational-only mechanics" below). The contract and once-only intent are common across tiers; the seam that implements it forks.
- **Transaction Context container seam**: shared.
- **Atomic-write initiation**: forked — relational ambient `ExecuteAsync` vs document-tier outermost behavior opening the batch.
- **Inbox-marker commit point**: forked — relational inbox marker is `AddAsync`'d into the EF context and never self-committed (committed once by `UnitOfWorkBehavior`'s single `SaveChanges`); document-tier inbox marker is stamped into the framework-owned `TransactionalBatch` by the outermost behavior before `next()`, and the single batch-execute after `next()` returns is the commit point.
- **Dispatch**: forked — relational polling `OutboxProcessor` vs Cosmos change-feed relay.
- **Outbox-doc shape**: forked — relational `int Id` + datetime concurrency token vs Cosmos string id + ETag.

**Inbox atomicity and the commit-point fork.** The EF tier achieves inbox-plus-handler atomicity because `UnitOfWorkBehavior` is the outermost pipeline behavior: the inbox `AddAsync` joins the ambient EF context and is committed in the single `SaveChanges` that closes the unit of work. The inbox implementation never self-commits. The document tier achieves the same structural guarantee through the outermost document-tier behavior: the behavior opens the batch, adds the marker op before `next()`, and executes the batch once after `next()` returns. The 409 on the marker is detected at batch-execute time (after `next()`) — this is not a pre-handler read-then-add guard and carries no TOCTOU window; the all-or-nothing batch execution ensures no aggregate or outbox write commits on conflict. A separately-committed document-tier inbox marker is explicitly rejected: it would open a window between marker persistence and aggregate/outbox persistence in which a redelivery could escape dedup or lose the handler's write.

**Qualifying constraint.** Atomic document-tier inbox dedup requires the incoming message to deterministically map to the single aggregate partition the handler writes. The inbox marker is co-resident: its partition key equals the aggregate's partition value. Handlers whose message does not map to exactly one aggregate partition, or that write no aggregate, are outside the scope of document-tier once-only dedup.

## Consequences

- The relational EF tier keeps ambient-tx convenience untouched.
- The NoSQL tier is honest about its stage-then-commit model — no silently different guarantees per provider.
- The two tiers remain conceptual twins via the shared seam (`SendToOutbox`, `ReceiveViaInbox`, `TransactionContext.Container`).
- The only cross-tier code change is the interface split and the EF provider implementing `IPollableOutboxStore`.
- NoSQL handler-authoring genuinely differs from EF handler-authoring — this is an intrinsic, documented cost of two-tier, not a defect.
- **Versioning**: breaking change to the `Chatter.MessageBrokers` core port; bump 0.13.2 → 0.14.0. Pre-1.0 SemVer uses the minor as the breaking lever. Effective blast radius is low: only code that **implements** the reliability port or constructs `OutboundBrokeredMessage` directly is broken (≈ the EF provider and the author); ordinary consumers that use a broker adapter together with a reliability package never bind `IUnitOfWork` or `OutboxMessage` and are unaffected. 0.x explicitly disclaims stability.

---

### Superseded-in-part sub-decision — "TransactionContext.Container unifies both tiers" thesis (surface-ownership boundary)

**Context — grounded facts.** `TransactionContext` carries exactly three things: `TransactionReceiver` (string), `TransactionMode` (enum), and `ContextContainer` — an untyped `Dictionary<string,object>` keyed by type name. The statement "the container unifies both tiers" is true only in the trivial sense that an untyped dictionary can hold any object; it unifies **storage**, not **semantics**. A grep of the core for "partition" returns zero hits: there is no core concept that maps to a Cosmos partition key. `GroupId` / AMQP session is the nearest neighbor in the message model and must not be implied as derivable from it. `ReceiveViaInbox` / `InboxBehavior` — the `ReceiveViaInbox(() => next())` wrap seam — is already off the document-tier path: the Document-Tier Batch-Lifecycle Behavior stamps the inbox marker into the framework-owned batch before `next()`, not through `ReceiveViaInbox`. `ReceiveViaInbox` / `InboxBehavior` are therefore relational-only mechanics, even though the inbox contract and intent remain shared across tiers.

**The approach decision — surface-ownership boundary.** The original thesis that the document tier should slot its primitives into the shared `TransactionContext.Container` is **narrowed** by the surface-ownership boundary principle:

> The document tier owns its **own provider-shaped reliability surface** that carries the document-store primitives: resolved partition key, bound container, the `TransactionalBatch` handle, batch lifecycle, inbox-marker enlistment, and ETag concurrency token. It shares with the relational tier **only** the abstract message / enqueue / inbox **contracts** — not the EF-shaped `TransactionContext` / `InboxBehavior` mechanics.

Because the document-store primitives now live on a surface designed to hold them, the class of finding "the EF-shaped seam carries no primitive X" is no longer representable — primitive X has a home by construction. This is the **eliminated class** (resolving root-cluster r3409943227 by construction, approach-level recurrence across r3409870435 / r3409914969 / r3409943227): the finding required the doc tier to reach into the relational-shaped `TransactionContext`; the surface-ownership boundary removes that reach.

**What stays shared.** The abstract message / enqueue / inbox contracts are shared:

- `SendToOutbox` is the **shared enqueue contract**, abstracted over an **Atomic-Write Handle** (the relational `IPersistanceTransaction` OR the document-tier handle both satisfy it) — not over the concrete `TransactionContext`. Exact C# shape / type name for this abstraction is deferred to implementation children.
- The **inbox contract and intent** (once-only dedup, idempotency) are shared; the mechanics that implement it fork per tier (see below).

**What forks onto the document-tier reliability surface.** The document tier owns a provider-shaped surface distinct from the relational tier. That surface carries:

- The **doc-tier atomic-write handle / context** — carries the resolved partition key, bound container, and the `TransactionalBatch`; this is the doc-tier sibling of `IPersistanceTransaction`, NOT a value stuffed into `TransactionContext.Container`.
- The **Document-Tier Batch-Lifecycle Behavior** — opens the batch, stamps the inbox-marker create-op, calls `next()`, and executes the batch once.
- **Inbox-marker enlistment** — contributed by the Document-Tier Batch-Lifecycle Behavior to the framework-owned batch before `next()`.
- **Partition resolution** — see Partition-Key Source below.
- **Container binding** — the application injects the container; the framework binds it at batch open.
- **ETag concurrency token** — document-tier aggregate-upsert optimistic concurrency; no equivalent on `TransactionContext`.

**ReceiveViaInbox / InboxBehavior — relational-only mechanics.** `ReceiveViaInbox` and `InboxBehavior` are relational-only mechanics. The inbox-marker enlistment on the document tier happens via the document-tier surface (Document-Tier Batch-Lifecycle Behavior contributes the marker to the framework-owned batch), not through the `ReceiveViaInbox(() => next())` wrap seam. The inbox **contract** (once-only dedup intent) is shared; the **seam that implements it** forks.

**Pollable Outbox Store — relational-only mechanics.** The polling-dispatch methods (`GetUnprocessedMessagesFromOutbox`, `GetUnprocessedBatch`, `UpdateProcessedDate`) live on the relational-only `IPollableOutboxStore`. The document tier does not implement this interface; it dispatches through the change-feed Outbox Relay.

**Partition-Key Source.** There is no core primitive carrying the partition key (grep-proven: zero hits for "partition" in core). The partition is the aggregate's partition, which only the application knows (the same application that already owns aggregate serialization). The partition key is therefore sourced via an **app-registered partition-key resolver** — an application-supplied delegate `(InboundBrokeredMessage) -> partition-key` — invoked by the Document-Tier Batch-Lifecycle Behavior to open the batch on the correct partition. This is handler-context-derived via an app resolver, not core-derived (impossible) and not handler-imperative (iter2 handler-owns-batch was rejected). It is consistent with the existing Out-of-Scope "no new strongly-typed core partition-key property": the key lives on the document-tier surface via the resolver, never as a core property.

**Desirable simplification — #216 blast radius narrows.** The surface-ownership boundary shrinks the core breaking change. The document tier no longer needs `TransactionContext` to carry a partition key, does not need `InboxBehavior` generalized to cover Cosmos, and does not need `ContextContainer` to become a typed multi-tier handle. The only genuinely shared core mutation is abstracting `SendToOutbox` over the Atomic-Write Handle. The breaking surface for epic #216 therefore narrows to: the outbox-interface split (`IPollableOutboxStore` peel) plus the Atomic-Write Handle abstraction on the enqueue contract. The document-tier reliability surface lives entirely in the new Cosmos module.

**Prior narrative preserved.** The original Decision prose ("the unifying seam already exists: `TransactionContext.Container`…"), the C2 → framework-owns-batch-lifecycle amendment, and the Three-axes table above are preserved as an audit trail. The "shared seam" claim is narrowed — not removed — by this sub-decision: `SendToOutbox` remains the shared enqueue contract; what is superseded is the claim that `TransactionContext.Container` is the semantic unifier for both tiers' full primitive set.
