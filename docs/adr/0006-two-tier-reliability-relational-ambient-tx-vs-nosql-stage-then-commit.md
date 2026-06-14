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

The NoSQL tier uses **model C2: handler-owns-batch, framework-contributes-outbox**. The handler opens its own `TransactionalBatch` on its aggregate's partition, registers it in `TransactionContext.Container`, performs its own aggregate writes, calls `SendToOutbox` (which adds the outbox-doc operation to that batch), then executes the batch. This mirrors EF exactly — the handler does its own `DbContext` aggregate writes and the outbox adds rows to the same context/transaction.

**Three axes — one unified, two forked:**

- **Enqueue** (`SendToOutbox`): shared.
- **Inbox** (`ReceiveViaInbox`): shared.
- **Transaction Context container seam**: shared.
- **Atomic-write initiation**: forked — relational ambient `ExecuteAsync` vs Cosmos handler-owned batch.
- **Dispatch**: forked — relational polling `OutboxProcessor` vs Cosmos change-feed relay.
- **Outbox-doc shape**: forked — relational `int Id` + datetime concurrency token vs Cosmos string id + ETag.

## Consequences

- The relational EF tier keeps ambient-tx convenience untouched.
- The NoSQL tier is honest about its stage-then-commit model — no silently different guarantees per provider.
- The two tiers remain conceptual twins via the shared seam (`SendToOutbox`, `ReceiveViaInbox`, `TransactionContext.Container`).
- The only cross-tier code change is the interface split and the EF provider implementing `IPollableOutboxStore`.
- NoSQL handler-authoring genuinely differs from EF handler-authoring — this is an intrinsic, documented cost of two-tier, not a defect.
- **Versioning**: breaking change to the `Chatter.MessageBrokers` core port; bump 0.13.2 → 0.14.0. Pre-1.0 SemVer uses the minor as the breaking lever. Effective blast radius is low: only code that **implements** the reliability port or constructs `OutboundBrokeredMessage` directly is broken (≈ the EF provider and the author); ordinary consumers that use a broker adapter together with a reliability package never bind `IUnitOfWork` or `OutboxMessage` and are unaffected. 0.x explicitly disclaims stability.
