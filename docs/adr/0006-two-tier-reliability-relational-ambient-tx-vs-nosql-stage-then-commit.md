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

1. Opens an empty `TransactionalBatch` on the message's resolved partition and places it on `TransactionContext.Container` (the Atomic-Write Handle).
2. Stamps the inbox-marker create-op into the batch (framework-owned doc shape; no domain knowledge of the application aggregate).
3. Calls `next()` — the handler resolves the batch from the container and contributes its own aggregate ops (handler owns domain serialization and partition; the framework never sees domain types). `SendToOutbox` contributes the outbox-doc op to the same batch via the container seam (the shared enqueue contract is unchanged).
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
- **Inbox contract / seam** (`ReceiveViaInbox`): shared — the inbox interface and the once-only dedup contract are common across tiers.
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
