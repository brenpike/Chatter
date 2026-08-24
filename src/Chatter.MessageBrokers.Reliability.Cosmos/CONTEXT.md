# Chatter.MessageBrokers.Reliability.Cosmos

Azure Cosmos DB document-tier reliability provider implementing the inbox/outbox reliability ports of Chatter.MessageBrokers over a partition-scoped `TransactionalBatch`.

## Language

### Document Tier

**Document Tier**: The Cosmos-backed reliability tier in which a handler's aggregate write and its outbox enqueue land in one partition-scoped atomic batch. _Avoid_: transaction tier, NoSQL tier.

**Document-Tier Reliability Surface**: The DI-scoped home holding the active Atomic-Write Handle for the in-flight message; the single ownership home for that handle, not the transaction context container.

**Atomic-Write Handle**: The staging contract over the open `TransactionalBatch` — every operation is added through its staging methods so no operation can be staged uncounted. _Avoid_: transaction handle, batch object.

**Document-Tier Batch-Lifecycle Behavior**: The outermost command behavior that resolves the partition key, opens the batch, publishes the handle on the surface for the duration of the inner dispatch, then executes and clears it.

**Document Reliability Registry**: The participation allowlist keyed by command type; having a registration IS participation — there is no marker interface. _Avoid_: opt-in attribute, participation flag.

**Document Reliability Registration**: The per-command-type entry in the registry describing how that command participates in the document tier.

**Participant**: A command type carrying a registration; a non-participant bypasses the document tier entirely, with no partition-key resolution and no batch.

**Partition-Key Resolver**: The application-supplied delegate mapping an inbound brokered message to the Cosmos partition key of the aggregate the handler writes; a null return means no resolvable partition, so no batch is opened.

**Handle-Gated Outbound Router**: The outbound routing decorator that applies participation gating at routing time, delegating to the default route whenever no handle is active on the surface.

### Staging Surface

**Reserved Item-Id Namespace**: The `inbox:` / `outbox:` item-id prefixes Chatter exclusively owns on the public staging surface; an application document may not use them.

**Co-Resident Outbox Document**: The pending outbound message persisted as `outbox:{encoded(MessageId)}` in the same container and partition as the aggregate, discriminated by `_chatterType=outbox`. _Avoid_: outbox row, outbox table entry.

**Inbox Marker**: The `inbox:{encoded(MessageId)}` document recording that a message id has been claimed in a partition, discriminated by `_chatterType=inbox`. It has two variants whose lifecycles differ — the Batched Inbox Marker and the Claimed Inbox Marker — so name the variant whenever the lifecycle is what is meant.

**Batched Inbox Marker**: The document-tier variant, staged as the first operation on the Atomic-Write Handle and committed with the aggregate write and the Co-Resident Outbox Document in one batch. It carries no completion state, so its state is simply absent then present; a create-409 confirmed at batch execute rolls the whole batch back as a duplicate.

**Claimed Inbox Marker**: The Standalone Inbox Gate variant, the only one carrying a completion state, whose state is monotonic — absent, then pending, then completed.

**Write-Ahead Claim**: The Standalone Inbox Gate's anti-TOCTOU two-phase protocol — create a pending Claimed Inbox Marker before the handler, patch it to completed after — so a redelivery confirms a duplicate on completion rather than on mere existence. The document tier makes no such claim: its marker is staged inside the batch and lands only when the batch commits.

**Marker Take-Over**: The Standalone Inbox Gate's resolution path for a create-409 conflict in which a pending or abandoned Claimed Inbox Marker is adopted and its handler re-run, while a completed marker skips the handler and an unconfirmable conflict redelivers. The document tier has no take-over: a conflict it confirms is always a duplicate.

**Marker Time-To-Live**: The optional dedup-window TTL stamped onto each Claimed Inbox Marker, and the only removal of a marker; left unset — as it always is on the document tier — the marker persists indefinitely.

### Relay

**Outbox Relay**: The change-feed drain that reads pending outbox documents, publishes each through the broker, marks it delivered, and stamps a TTL; one change-feed processor per distinct Change-Feed Source Identity. _Avoid_: dispatcher, relay worker, drainer.

**Change-Feed Source Identity**: The relay's dedup and processor-name key, declared-or-ground-truth and never inferred from a caller-controlled handle — caller-declared monitored/lease tokens on the advanced overload, and on the plain overload the resolved ground truth (account endpoint + database id + container id, for both the monitored and the lease container). _Avoid_: database/container/lease triple (the account endpoint is part of the key, so identically-named containers in different accounts stay distinct).

**Standalone Outbox Relay**: The lease-backed relay registered as its own hosted service, independent of the command pipeline, for applications that only drain a container.

**Standalone Inbox Gate**: The lease-less inbox-dedup gate registered through the inbox behavior seam for stateless services with no aggregate, outbox, or lease container.

**Outbox Body Resolver**: The drain-time seam invoked per pending document so the brokered-message body can be sourced from current store state rather than reconstructed verbatim from the persisted document.

**Outbox Drain Context**: The per-document context handed to the body resolver, carrying the verbatim message id, recovered partition key, declared partition-key path, and the raw document.

## Relationships

- Implements the Outbox and Inbox reliability ports defined in the Message Brokers context, as a Cosmos-backed alternative to the EntityFramework provider.
- The Document-Tier Batch-Lifecycle Behavior wraps command dispatch as the outermost behavior in the Command Pipeline defined by the CQRS context.
- The Batch-Lifecycle Behavior consults the Document Reliability Registry, invokes the Partition-Key Resolver only for participants, and publishes the Atomic-Write Handle on the Document-Tier Reliability Surface.
- The Handle-Gated Outbound Router gates outbound routing on the presence of that handle, so non-participant dispatch never reaches the Cosmos outbox.
- The Co-Resident Outbox Document and the Inbox Marker share one container and one Reserved Item-Id Namespace, and encode their id segment identically.
- The Outbox Relay consumes Co-Resident Outbox Documents and publishes them through the broker; delivery is at-least-once, so downstream receivers deduplicate via the Inbox Marker.
- The Outbox Relay hands each admitted document to the Outbox Body Resolver via an Outbox Drain Context before publishing.

## Example dialogue

> **Dev:** "My command writes an aggregate and publishes an event. How do I stop the publish happening when the write fails?"
> **Domain expert:** "Register the command type in the Document Reliability Registry and supply a Partition-Key Resolver. The Batch-Lifecycle Behavior opens one batch on the aggregate's partition, your write and the Co-Resident Outbox Document stage on the same Atomic-Write Handle, and both land or neither does. The Outbox Relay publishes it afterwards."

## Flagged ambiguities

- **Inbox** covers two different registrations: the full document-tier inbox co-resident with the aggregate, and the lease-less Standalone Inbox Gate for stateless services. Name which one is meant.
- The Standalone Inbox Gate deduplicates redeliveries only, not concurrent delivery — two concurrent deliveries of one message id both run the handler, so mutual exclusion for concurrent delivery stays the transport's responsibility.
