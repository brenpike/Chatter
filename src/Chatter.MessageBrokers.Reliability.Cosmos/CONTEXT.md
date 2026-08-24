# Chatter.MessageBrokers.Reliability.Cosmos

Azure Cosmos DB reliability provider implementing the inbox/outbox reliability ports of Chatter.MessageBrokers.

## Scope rule

The module ships three independently registrable primitives — the **Document Tier**, the **Standalone Inbox Gate**, and the **Standalone Outbox Relay**. A new primitive is added to this list before anything else in this file is written about it.

Applicability is stated here, never assumed. A statement sitting under a heading that names a primitive, or naming a variant term, holds for that variant alone; **every other statement in this file is a claim over all three primitives and must be true of all three**. A term whose content differs by primitive is therefore written as a superordinate asserting only what is common to every primitive, plus a named variant for each primitive that adds to it — so no variant occupies the superordinate's slot unnamed.

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

**Reserved Item-Id Namespace**: The `inbox:` / `outbox:` item-id prefixes every Chatter-authored document in this module draws its id from. What the namespace requires of an APPLICATION differs by primitive, so each is stated: on the Document Tier's public staging surface it is a PROHIBITION enforced at stage time — an application document staged there may not carry either prefix; on a Standalone Outbox Relay's monitored container it is an OBLIGATION — an application-authored trigger document is drained only when its id is exactly `outbox:{encoded(MessageId)}` for the verbatim message id that document carries; the Standalone Inbox Gate exposes no staging surface, so Chatter is the namespace's only author there.

**Outbox Document**: The pending outbound message persisted under the Reserved Item-Id Namespace id `outbox:{encoded(MessageId)}`, discriminated by `_chatterType=outbox`, and entering the container at `status=pending`. _Avoid_: outbox row, outbox table entry.

**Co-Resident Outbox Document**: The Document Tier variant of the Outbox Document, written into the same container and logical partition as the handler's aggregate and committed with it on one Atomic-Write Handle.

**Inbox Marker**: The `inbox:{encoded(MessageId)}` document recording that a message id has been claimed in a partition, discriminated by `_chatterType=inbox`. It has two variants whose lifecycles differ — the Batched Inbox Marker and the Claimed Inbox Marker — so name the variant whenever the lifecycle is what is meant.

**Batched Inbox Marker**: The document-tier variant, staged as the first operation on the Atomic-Write Handle and committed with the aggregate write and the Co-Resident Outbox Document in one batch. It carries no completion state, so its state is simply absent then present; a create-409 confirmed at batch execute rolls the whole batch back as a duplicate.

**Claimed Inbox Marker**: The Standalone Inbox Gate variant, the only one carrying a completion state, whose state is monotonic — absent, then pending, then completed.

**Write-Ahead Claim**: The Standalone Inbox Gate's anti-TOCTOU two-phase protocol — create a pending Claimed Inbox Marker before the handler, patch it to completed after — so a redelivery confirms a duplicate on completion rather than on mere existence. The document tier makes no such claim: its marker is staged inside the batch and lands only when the batch commits.

**Marker Take-Over**: The Standalone Inbox Gate's resolution path for a create-409 conflict in which a pending or abandoned Claimed Inbox Marker is adopted and its handler re-run, while a completed marker skips the handler and an unconfirmable conflict redelivers. The document tier has no take-over: a conflict it confirms is always a duplicate.

**Marker Time-To-Live**: The optional dedup-window TTL stamped onto each Claimed Inbox Marker, and the only removal of a marker; left unset — as it always is on the document tier — the marker persists indefinitely.

### Relay

**Outbox Relay**: The lease-backed change-feed drain that admits the pending Outbox Documents surfacing on a monitored container's change feed, publishes each admitted document's brokered message through the broker, then marks that document delivered and stamps a TTL on it in one patch; one change-feed processor per distinct Change-Feed Source Identity. _Avoid_: dispatcher, relay worker, drainer.

**Change-Feed Source Identity**: The Outbox Relay's dedup and processor-name key, declared-or-ground-truth and never inferred from a caller-controlled handle — the caller-declared monitored/lease tokens when a registration declares them, and otherwise the resolved ground truth (account endpoint + database id + container id, for both the monitored and the lease container). _Avoid_: database/container/lease triple (the account endpoint is part of the key, so identically-named containers in different accounts stay distinct).

**Document-Tier Outbox Relay**: The Document Tier variant of the Outbox Relay, hosted alongside the command pipeline and drawing its source identities from the Document Reliability Registry; it always reconstructs the brokered message verbatim from the persisted document.

**Standalone Outbox Relay**: The Outbox Relay variant registered as its own hosted service, independent of the command pipeline and of the Document Reliability Registry, for applications that only drain a container.

**Standalone Inbox Gate**: The lease-less inbox-dedup gate registered through the inbox behavior seam for stateless services with no aggregate, outbox, or lease container.

**Outbox Body Resolver**: The Standalone Outbox Relay's optional drain-time seam, invoked per admitted document so the brokered-message body can be sourced from current store state rather than reconstructed verbatim from the persisted document.

**Outbox Drain Context**: The per-document context the Standalone Outbox Relay hands to its Outbox Body Resolver, carrying the verbatim message id, recovered partition key, declared partition-key path, and the raw document.

## Relationships

- Implements the Outbox and Inbox reliability ports defined in the Message Brokers context, as a Cosmos-backed alternative to the EntityFramework provider.
- The Document-Tier Batch-Lifecycle Behavior wraps command dispatch as the outermost behavior in the Command Pipeline defined by the CQRS context.
- The Batch-Lifecycle Behavior consults the Document Reliability Registry, invokes the Partition-Key Resolver only for participants, and publishes the Atomic-Write Handle on the Document-Tier Reliability Surface.
- The Handle-Gated Outbound Router gates outbound routing on the presence of that handle, so non-participant dispatch never reaches the Cosmos outbox.
- The Co-Resident Outbox Document and the Batched Inbox Marker are co-resident: both live in the container selected by the participant's Document Reliability Registration, in the aggregate's logical partition, and commit together on one Atomic-Write Handle.
- The Claimed Inbox Marker instead lives in the Standalone Inbox Gate's own idempotency container, partitioned by the inbound message id (default path `/idempotencyKey`); Chatter writes no aggregate and no Outbox Document beside it there.
- The Co-Resident Outbox Document, the Batched Inbox Marker, and the Claimed Inbox Marker all draw their ids from one Reserved Item-Id Namespace and encode their id segment identically.
- The Outbox Relay consumes Outbox Documents and publishes them through the broker; delivery is at-least-once, so downstream receivers deduplicate via the Inbox Marker.
- Only the Standalone Outbox Relay consults an Outbox Body Resolver, and only when one is configured: it hands each admitted document to that resolver via an Outbox Drain Context before publishing. The Document-Tier Outbox Relay always reconstructs the message verbatim from the persisted document.

## Example dialogue

> **Dev:** "My command writes an aggregate and publishes an event. How do I stop the publish happening when the write fails?"
> **Domain expert:** "Register the command type in the Document Reliability Registry and supply a Partition-Key Resolver. The Batch-Lifecycle Behavior opens one batch on the aggregate's partition, your write and the Co-Resident Outbox Document stage on the same Atomic-Write Handle, and both land or neither does. The Document-Tier Outbox Relay publishes it afterwards."

> **Dev:** "My service has no command pipeline and no aggregate — it just writes trigger documents into a container and needs them published. What do I register?"
> **Domain expert:** "A Standalone Outbox Relay on its own. It consults no Document Reliability Registry, so its Change-Feed Source Identity comes from the monitored and lease containers you configure rather than from a registration. Give each trigger document the Reserved Item-Id Namespace id `outbox:{encoded(MessageId)}` for its own message id or the relay will not drain it, and bind an Outbox Body Resolver if the body should be read from current store state at drain rather than carried on the document."

## Flagged ambiguities

- **Inbox** covers two different registrations: the full document-tier inbox co-resident with the aggregate, and the lease-less Standalone Inbox Gate for stateless services. Name which one is meant.
- The Standalone Inbox Gate deduplicates redeliveries only, not concurrent delivery — two concurrent deliveries of one message id both run the handler, so mutual exclusion for concurrent delivery stays the transport's responsibility.
