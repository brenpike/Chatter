# Chatter.MessageBrokers

Technology-agnostic brokered messaging built on Chatter.CQRS: receiving, sending, routing, reliability, and recovery, with interfaces left to infrastructure-specific implementations.

## Language

**Brokered Message**: A message received from or sent to broker infrastructure, relayed (dispatched) to a Command or Event handler.

**Brokered Message Receiver**: The component that consumes brokered messages from infrastructure; runs as a background service, capped at one instance.
_Avoid_: consumer, listener.

**Brokered Message Dispatcher**: Relays a received Brokered Message to the matching Command or Event handler.

**Brokered Message Router**: Routes / forwards a brokered message to its destination path.
_Avoid_: forwarder (a Router specialization).

**Brokered Message Attribute**: Metadata declaration that maps a message type to its broker path/queue.

**Outbox**: Persistence pattern recording outgoing messages for reliable publish alongside local state changes.

**Inbox**: Persistence pattern recording received messages to enforce idempotent, once-only handling.

**Routing Slip**: A message carrying its own itinerary of steps/destinations to visit in sequence (`RoutingSlipBuilder`).

**Recovery**: Resilience policies applied to receiving — Retry and Circuit Breaker (`RetryWithCircuitBreakerStrategy`).

**Circuit Breaker**: A recovery policy that halts processing after repeated failures.

**Critical Failure**: An unrecoverable receive error; raises a Critical Failure Event and may route the message to the Error Queue.

**Error Queue**: Destination for messages that exhausted recovery and cannot be handled.

**Max Receives Exceeded**: The condition where a message has been delivered more times than allowed, triggering a configured action.

**Body Converter**: Serializes/deserializes a brokered message body to/from a domain message type.

**Two-Tier Reliability**: the reliability port supports two coexisting persistence models — a relational ambient-transaction tier and a NoSQL/document stage-then-commit tier — sharing the enqueue, inbox, and transaction-context seam.

**Relational Tier**: the ambient-transaction reliability model (Unit of Work wraps the whole handler in an open transaction; polling outbox dispatch).
_Avoid_: treating it as the only reliability model.

**Document Tier** (NoSQL): the stage-then-commit reliability model where the framework (outermost document-tier batch-lifecycle behavior) owns the atomic batch lifecycle (open + execute) and contributes the inbox marker and outbox record to the batch; the handler contributes its own aggregate ops to the framework-owned batch and owns aggregate serialization and partition selection; dispatched by a change-feed relay that is at-least-once (downstream consumers must deduplicate via the inbox marker).
_Avoid_: "the handler owns the batch" (batch-lifecycle ownership belongs to the framework, not the handler).

**Document-Tier Batch-Lifecycle Behavior**: the outermost document-tier pipeline behavior — the document-tier sibling of `UnitOfWorkBehavior` — that owns batch open, inbox-marker stamping, and batch-execute for the document tier. It opens the `TransactionalBatch`, stamps the inbox-marker create-op, calls `next()` (during which the handler and `SendToOutbox` contribute their ops to the batch via the document-tier atomic-write handle — the doc-tier sibling of `IPersistanceTransaction` on the document-tier reliability surface, NOT `TransactionContext.Container`), then executes the batch once and inspects the per-op response for conflict. A 409 on the marker op is a confirmed duplicate; the all-or-nothing batch execution ensures no aggregate or outbox write commits on conflict.
_Avoid_: consumer, listener, middleware (honor existing `_Avoid_` aliases).

**Atomic-Write Handle** (`IAtomicWriteHandle`): the abstract marker contract the enqueue contract (`SendToOutbox`) is abstracted over — satisfied by the relational `IPersistanceTransaction` (which derives from `IAtomicWriteHandle`) on the relational tier OR the document-tier atomic-write handle on the document tier. It is an empty marker, NOT "a value on the context container bag": the two tiers satisfy the same abstract contract with provider-shaped handles that add no shared members. On the document tier, the Document-Tier Batch-Lifecycle Behavior opens the batch and places the doc-tier handle before `next()`; the Co-Resident Outbox doc and the Co-Resident Inbox Marker both ride this handle — contributed to the same framework-owned batch as the aggregate write — and commit atomically at the single framework-owned batch-execute.

**Stage-then-Commit**: an atomic-write model where writes are accumulated then committed once (document tier), as opposed to running handler code inside an ambient transaction.

**Pollable Outbox Store** (`IPollableOutboxStore`): the relational-only outbox capability for polling-based dispatch (`GetUnprocessedMessagesFromOutbox`, `GetUnprocessedBatch`, `UpdateProcessedDate`); not implemented by document-store providers. This is relational-only mechanics: the document tier dispatches via the change-feed Outbox Relay and never implements this interface. Split out of `IBrokeredMessageOutbox`, which retains only the shared `SendToOutbox` enqueue contract.

**Inbox Deduplicator** (`IInboxDeduplicator`): the tier-neutral inbox dedup contract expressing once-only-handling intent (`HasBeenReceived`), implemented by both reliability tiers. Distinct from the relational-only `ReceiveViaInbox` wrap seam: this contract carries only the shared once-only intent, not the relational `Func<Task>` handler-wrap mechanics.
_Avoid_: consumer, listener, middleware (honor existing `_Avoid_` aliases).

**Reliability-Store Facet Resolution**: the secondary reliability facet (`IPollableOutboxStore` for the outbox pair; `IInboxDeduplicator` for the inbox pair) is not an independent DI registration. It is obtained by casting the single resolved primary (`IBrokeredMessageOutbox` / `IBrokeredMessageInbox`) at the consumption site — the same precedent `OutboxProcessor` uses to obtain `IUnitOfWork` from the outbox. A custom primary that does not implement the required secondary facet throws `InvalidCastException` at the poll site (loud, not silent). Because there is exactly one resolved instance per pair, split-store is impossible by construction; no descriptor inspection, lifetime reconciliation, or fail-fast registration is required.
_Avoid_: "Reliability-Pair Lifetime Contract" (the per-pair DI forwarding mechanism is eliminated).

**ReceiveViaInbox / InboxBehavior**: the `ReceiveViaInbox(() => next())` wrap seam is relational-only mechanics. The inbox **contract** and intent (once-only dedup, idempotency) are shared across tiers; the seam that implements them is provider-specific. The document tier enlists the inbox marker via the Document-Tier Batch-Lifecycle Behavior and the document-tier reliability surface — not through `ReceiveViaInbox`.

**Document-Tier Reliability Surface**: the provider-shaped reliability surface the document (NoSQL) tier owns. It carries the document-store primitives the relational-shaped `TransactionContext` does not and cannot carry: the resolved partition key, bound container, `TransactionalBatch` handle and batch lifecycle, inbox-marker enlistment, and ETag concurrency token. The document tier is the doc-tier sibling of `IPersistanceTransaction` for the atomic-write handle; the ETag concurrency token lives here, not on `TransactionContext`. The surface lives entirely in the Cosmos provider module — not in core.
_Avoid_: treating `TransactionContext.Container` as the semantic home of document-tier primitives (it is an untyped dict that unifies storage, not semantics; no partition concept exists in core).

**Partition-Key Resolver**: an application-registered Try/nullable delegate with signature `(InboundBrokeredMessage) -> partition-key?` invoked by the Document-Tier Batch-Lifecycle Behavior to open the `TransactionalBatch` on the correct aggregate partition. There is no core primitive carrying a partition key (zero partition references in core); the partition is the aggregate's partition, which only the application knows (the same application that owns aggregate serialization). The resolver is only ever invoked for registered participants; a `null` return means "no resolvable partition for this message" and the behavior opens no batch and passes through to `next()`. It lives per registration (see Document Reliability Registration) on the document-tier reliability surface — never as a core property.

**Document Reliability Registry**: the document-tier participation allowlist — a singleton keyed by command type to its Document Reliability Registration. Participation IS having a registration (registry-only — there is NO marker interface); the registry is a positive allowlist. The Document-Tier Batch-Lifecycle Behavior consults it on every command via a cheap lookup; a non-registered command bypasses the document tier entirely. Exactly one registration exists per command type (a duplicate registration for the same command type is a configuration error and throws). See ADR-0008.
_Avoid_: treating "all commands participate" as the model (the registry, not the empty-batch guard, is the participation gate); "marker interface" (participation is registry-only).

**Document Reliability Registration**: the immutable per-command-type record carried in the Document Reliability Registry: `{database, container, lease, partition-key resolver, partition-key path}`, plus optional explicit per-registration container factories. It selects the container the command's aggregate, co-resident outbox doc, and co-resident inbox marker are written to. Many command types MAY map to one container; ADR-0007's single-aggregate/single-partition hard constraint is unaffected — the registration adds container selection, not cross-partition atomicity, so MULTIPLE single-partition containers are now supported. See ADR-0008.

**Participation** (document tier): a command type participates in the document tier if and only if it has a registration in the Document Reliability Registry (registry-only allowlist). Non-registered commands bypass the tier — no resolver call, no batch, no handle on the surface. See ADR-0008.
_Avoid_: "opt-out" framing (participation is positive opt-in via a registration, not opt-out at the write layer).

**Cosmos Container Factory**: the document-tier component that DERIVES (never provisions) a Cosmos `Container` handle from the application-registered `CosmosClient` singleton via `client.GetContainer(database, container)` (or a registration's explicit factory), caching handles thread-safely per `(database, container)`. The application owns the `CosmosClient` lifecycle and the existence of the database/container/lease (a documented prerequisite); the provider registers no client and provisions no Cosmos resources. See ADR-0008.
_Avoid_: "provisions" / "creates the container" (the factory derives handles only; container/database creation is the application's infra concern).

**Outbox Relay**: a change-feed-driven drain that publishes persisted outbox records to the broker (document tier), filtered to outbox records only — never deriving events from domain-document changes.

**Co-Resident Outbox**: a document-tier outbox record stored in the same container and logical partition as the aggregate it accompanies, so both are written in one atomic batch.

**Co-Resident Inbox Marker**: a document-tier inbox dedup marker stored in the aggregate's logical partition, contributed by the framework (Document-Tier Batch-Lifecycle Behavior) to the framework-owned batch before `next()` is called. Its id is derived from the message identity using the same Cosmos-id-safe encoding as the outbox id; it carries the Chatter-reserved `_chatterType="inbox"` discriminator so the Outbox Relay's `_chatterType="outbox"` predicate ignores it by construction. A 409 create-conflict on the marker is detected at the single framework-owned batch-execute (after `next()` returns); the all-or-nothing batch execution makes once-only dedup atomic with the aggregate write rather than a sequential guard. Applicable only when the incoming message deterministically maps to a single aggregate partition.

## Relationships

- A Brokered Message Receiver consumes infrastructure messages and hands them to the Brokered Message Dispatcher.
- The Dispatcher relays to a Command or Event handler (Chatter.CQRS) by message type.
- Recovery (Retry, Circuit Breaker) wraps receiving; exhausting it yields a Critical Failure routed to the Error Queue.
- Inbox and Outbox use in-memory implementations by default; durable storage is supplied by the Reliability.EntityFramework context.
- Reliability is two-tier: a relational (ambient-transaction) tier and a NoSQL/document (stage-then-commit) tier share **only** the abstract enqueue (SendToOutbox, abstracted over an Atomic-Write Handle), inbox, and message contracts — NOT the EF-shaped `TransactionContext`/`InboxBehavior` mechanics; the document tier carries its document-store primitives on its own provider-shaped document-tier reliability surface (partition key, bound container, batch handle/lifecycle, inbox-marker enlistment, ETag). Durable storage is supplied by provider packages (EntityFramework for relational; Cosmos for document).
- A Routing Slip drives a Router across a sequence of destinations.
- Concrete brokers (Azure Service Bus, SQL Service Broker) implement the receiver/sender/path interfaces defined here.

## Example dialogue

> **Dev:** "A handler keeps throwing — where does the message end up?"
> **Domain expert:** "Recovery retries it under the Circuit Breaker. Once Max Receives is exceeded it's a Critical Failure, so it's moved to the Error Queue and a Critical Failure Event fires."

## Flagged ambiguities

- **Router vs Forwarder**: ForwardingRouter and IBrokeredMessageForwarder overlap; treat Forwarder as a Router specialization.
