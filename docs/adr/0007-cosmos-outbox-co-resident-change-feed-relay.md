---
status: accepted
date: 2026-06-14
---

# Cosmos outbox co-resident in the aggregate container, drained by a discriminator-filtered change-feed relay

The NoSQL reliability tier (ADR 0006) must realize the transactional-outbox / atomic-writes pattern on Cosmos DB. Cosmos `TransactionalBatch` imposes hard physical constraints: all items in a batch must share one partition-key value; a batch is bounded at 100 operations, 2 MB, and 5 seconds. Cross-partition and cross-container batches are physically impossible.

## Considered Options

- **Outbox in its own container/table** — mirrors the EF provider's own `DbSet`. Rejected: a cross-container batch is impossible, so the aggregate write and the outbox write cannot be atomic.
- **(C1) Framework-owns-batch (original framing)** — the handler hands domain objects and a partition key to Chatter; Chatter serializes them, builds the batch, and executes it. Rejected: forces Chatter — a messaging library — to know how to serialize the application's domain aggregates and own the container. Wrong layer.
- **(C2) Handler-owns-batch, framework-contributes-outbox + co-resident outbox + change-feed relay** — originally accepted; superseded by the framework-owns-batch-lifecycle re-decision (see ADR-0006 Superseded sub-decision). Preserved here as an audit trail.
- **(C3) Framework-owns-batch-lifecycle, handler contributes aggregate ops + co-resident outbox + change-feed relay (ACCEPTED).**

## Decision

**Co-residency.** The Cosmos outbox document lives **inside** the application's aggregate container, in the **same logical partition** as the aggregate it accompanies. Chatter's Cosmos provider does not own a container; the application injects the container (and the change-feed lease container). Co-location of aggregate and outbox in one partition is structurally enforced because the outbox document is added to the framework-owned partition-scoped batch.

**Batch lifecycle (framework-owns-batch-lifecycle).** The outermost document-tier pipeline behavior — the Document-Tier Batch-Lifecycle Behavior, the document-tier sibling of `UnitOfWorkBehavior` — owns the batch lifecycle. It opens an empty `TransactionalBatch` on the message's resolved partition, stamps the inbox-marker create-op into the batch (framework-owned shape; no domain knowledge), then calls `next()`. The partition key is sourced via the app-registered partition-key resolver — an application-supplied delegate `(InboundBrokeredMessage) -> partition-key` — invoked by the behavior to open the batch on the correct partition; there is no core primitive carrying the partition key. The handler resolves the batch from the doc-tier atomic-write handle (the doc-tier sibling of `IPersistanceTransaction`, NOT a value stuffed into `TransactionContext.Container`) and contributes its own aggregate ops; the application owns aggregate serialization and partition selection, and the framework never sees domain types. `SendToOutbox` contributes the outbox-doc op to the same batch via the shared enqueue contract (abstracted over an Atomic-Write Handle — exact shape deferred to implementation). After `next()` returns, the behavior executes the batch once — this is the single commit point. The per-op `TransactionalBatchResponse` is inspected after execution: a 409 on the marker op is a confirmed duplicate; because batch execution is all-or-nothing, no aggregate or outbox write commits on conflict. The 409 is detected at batch-execute time (after `next()`) — not via a pre-handler marker read — so there is no TOCTOU window and no sequencing gap in which a redelivery could escape dedup.

**Hard constraint.** Every atomic write in scope is single-aggregate / single-partition. A workflow needing to atomically mutate two aggregates in different partitions AND emit an event is physically impossible on Cosmos and is **out of scope** — that is a saga / process-manager concern (multiple single-partition steps), not the outbox.

**Outbox-doc shape.** Chatter owns this — it is the library value-add over hand-writing `.CreateItem(outboxMessage)`:

- A **Chatter-reserved discriminator field**, `_chatterType = "outbox"`. The discriminator lives under a namespaced, Chatter-reserved field name (not a generic top-level `type`) so it cannot collide with an application's own `type` field that may already carry an `outbox` value on a domain document. This is what lets the relay's filter (see Dispatch) select Chatter outbox documents by construction without ever matching a domain document.
- Document id `outbox:{encoded(MessageId)}`, where `encoded(...)` is a deterministic, Cosmos-id-safe encoding/hash of `OutboundBrokeredMessage.MessageId` (the raw `MessageId` may be caller-supplied and can contain characters Cosmos rejects in item ids — e.g. `/`, `?`, `#`). The original `OutboundBrokeredMessage.MessageId` is stored verbatim in a separate field as the event id; the encoded form is used only as the physical Cosmos item id.
- A `status` (or equivalent delivery-state) field, initialized to `pending` and advanced to `delivered` by the relay (see Dispatch).
- Carries the serialized `MessageContext`, message body, `Destination`, and content-type.
- A positive `ttl` is stamped when the relay marks the document `delivered`, so delivered documents self-expire. **Prerequisite**: post-delivery TTL purge requires the application's container to have TTL enabled (`defaultTtl` set on the container, e.g. `defaultTtl = -1`). This is a documented application prerequisite, not automatic — documents in a container without TTL enabled will not self-expire regardless of the `ttl` field value.

**Concurrency and idempotency.**

- Aggregate upsert carries `IfMatchEtag` — the ETag concurrency token lives on the document-tier reliability surface, not on the relational `TransactionContext`; a 412 on conflict is an application-level retry signal.
- The outbox `CreateItem` is a fresh document with no ETag.
- Inbox deduplication uses a **co-resident inbox marker** contributed by the Document-Tier Batch-Lifecycle Behavior to the framework-owned `TransactionalBatch` before `next()` is called. The marker enlistment happens via the document-tier reliability surface — not through `ReceiveViaInbox` / `InboxBehavior`, which are relational-only mechanics. The inbox contract (once-only dedup intent) is shared; the seam that implements it is provider-specific. The marker is co-resident: `partitionKey` equals the aggregate's partition value; `id` is `inbox:{encoded(MessageId)}` using the same Cosmos-id-safe encoding as the outbox id; the raw `MessageId` is stored verbatim in a separate field. A 409 create-conflict on the marker is detected by inspecting the per-op `TransactionalBatchResponse` after the framework-owned batch-execute (which runs after `next()` returns); because batch execution is all-or-nothing, no aggregate write occurs, no outbox doc is written, and the message is acked as a confirmed duplicate. Dedup is closed-by-construction: the marker is a member of the framework-owned batch, and the Document-Tier Batch-Lifecycle Behavior adds the marker before `next()`, so the "marker cannot join a handler-internal batch" class of failure is eliminated. There is no window between marker persistence and aggregate/outbox persistence in which a redelivery could escape.
- The inbox marker carries the same Chatter-reserved discriminator field with the value `_chatterType = "inbox"`. The change-feed relay's `_chatterType = "outbox"` filter predicate excludes inbox markers by construction — no relay-side inbox suppression logic is required.
- Inbox markers are not given a post-delivery TTL. They persist for the dedup window. An app-configurable dedup-window TTL is a deferred design point.
- No datetime concurrency token (relational-only).

**Single-partition constraint for inbox dedup.** Atomic document-tier inbox dedup requires the incoming message to deterministically map to the single aggregate partition the handler writes — the same single-partition scope this ADR already accepts for aggregate-plus-outbox atomicity. Handlers whose message does not map to exactly one aggregate partition, or that write no aggregate, are outside the scope of document-tier once-only inbox dedup.

**Dispatch via change-feed relay.** A change-feed processor attached to the application's container drains outbox documents **filtered by the Chatter-reserved `_chatterType="outbox"` discriminator** and publishes them through the broker. The relay **must not** derive integration events from domain-document changes — only explicitly authored outbox documents are published. The relay (not a polling query) is the Cosmos dispatch mechanism; the Cosmos provider does **not** implement `IPollableOutboxStore`.

The Cosmos change feed emits **both inserts and updates** for an item, so the relay's own post-delivery write (advancing `status` to `delivered` and stamping `ttl`) re-surfaces the document on the feed. To preserve publish-once, the `_chatterType="outbox"` discriminator alone is **not** a sufficient relay predicate: the relay **must additionally skip any document already marked `delivered`** (equivalently, any document whose `ttl`/delivery marker is set). Only `pending` outbox documents are published; the delivered-state check guards against republishing a document on the change-feed event generated by its own delivery stamp.

**At-least-once relay semantics.** The change-feed relay is **at-least-once**: a publish can succeed and then the post-delivery `status`/`ttl` update can fail, causing the outbox document to be redelivered on the next change-feed pass. Downstream consumers **must** deduplicate; the document-tier inbox marker is the in-framework mechanism for this. Callers must not assume exactly-once delivery from the relay.

**Platform.** New module `Chatter.MessageBrokers.Reliability.Cosmos`, targeting `net8.0;net10.0`, depending on the Microsoft.Azure.Cosmos SDK v3 (`TransactionalBatch` + change-feed-processor API). Initial version `0.1.0`.

## Consequences

- Atomic aggregate-plus-event write is achieved with a single `TransactionalBatch`.
- Chatter remains ignorant of application domain types and container ownership.
- Co-location of aggregate and outbox in one partition is enforced by construction, not by convention.
- The relay's discriminator filter encodes the "never derive events from the domain change feed" rule inside Chatter — it cannot be accidentally bypassed by a caller.
- Cross-partition atomic writes are an explicit non-goal (saga territory).
- The Cosmos Linux emulator is heavy and flaky in CI; this is a known integration-test risk, mitigated by the lighter vnext-preview emulator and docker-gating.
