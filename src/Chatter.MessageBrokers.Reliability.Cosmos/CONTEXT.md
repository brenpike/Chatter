# Chatter.MessageBrokers.Reliability.Cosmos

Azure Cosmos DB reliability provider for Chatter.MessageBrokers.

The module ships three independently registrable primitives — the **Document Tier**, the **Standalone Inbox Gate**, and the **Standalone Outbox Relay**.

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

**Reserved Item-Id Namespace**: The `inbox:` / `outbox:` item-id prefixes every Chatter-authored document in this module draws its id from. What the namespace requires of an APPLICATION differs by primitive, so each is stated: on the Document Tier's public staging surface it is a PROHIBITION enforced at stage time — an application document staged there may not carry either prefix; on a Standalone Outbox Relay's monitored container it is an OBLIGATION — an application-authored trigger document is drained only when its id is exactly `outbox:{encoded(MessageId)}` for the verbatim message id that document carries; the Standalone Inbox Gate exposes no staging surface to guard, and the application owns its idempotency container, so an application-authored `inbox:`-prefixed collision is possible there — it is confirmed against the marker's discriminator and message id and redelivered, never inferred to be a duplicate.

**Outbox Document**: The pending outbound message persisted under the Reserved Item-Id Namespace id `outbox:{encoded(MessageId)}`, discriminated by `_chatterType=outbox`, and entering the container at `status=pending`. _Avoid_: outbox row, outbox table entry.

**Co-Resident Outbox Document**: The Document Tier variant of the Outbox Document, written into the same container and logical partition as the handler's aggregate and committed with it on one Atomic-Write Handle.

**Inbox Marker**: The `inbox:{encoded(MessageId)}` document this module writes to dedup a message id within a partition, discriminated by `_chatterType=inbox`. It has two variants whose lifecycles differ — the Batched Inbox Marker and the Claimed Inbox Marker — so name the variant whenever the lifecycle is what is meant.

**Batched Inbox Marker**: The document-tier variant, staged as the first operation on the Atomic-Write Handle and committed with the aggregate write and the Co-Resident Outbox Document in one batch. It carries no completion state, so its state is simply absent then present; a create-409 confirmed at batch execute rolls the whole batch back as a duplicate.

**Claimed Inbox Marker**: The Standalone Inbox Gate variant, the only one carrying a completion state, whose state is monotonic — absent, then pending, then completed.

**Write-Ahead Claim**: The Standalone Inbox Gate's anti-TOCTOU two-phase protocol — create a pending Claimed Inbox Marker before the handler, patch it to completed after — so a redelivery confirms a duplicate on completion rather than on mere existence. The document tier makes no such claim: its marker is staged inside the batch and lands only when the batch commits.

**Marker Take-Over**: The Standalone Inbox Gate's resolution path for a create-409 conflict in which a pending or abandoned Claimed Inbox Marker is adopted and its handler re-run, while a completed marker skips the handler and an unconfirmable conflict redelivers. The document tier has no take-over: a conflict it confirms is always a duplicate.

**Marker Time-To-Live**: The optional dedup-window TTL stamped onto each Claimed Inbox Marker, and the only removal of a marker; left unset — as it always is on the document tier — the marker persists indefinitely.

### Relay

**Outbox Relay**: The lease-backed change-feed drain shared by the Document-Tier Outbox Relay and the Standalone Outbox Relay: it admits the pending Outbox Documents surfacing on a monitored container's change feed, publishes whatever brokered message its variant resolves for that document, then marks the document delivered and stamps a TTL on it in one patch; one change-feed processor per distinct Change-Feed Source Identity. _Avoid_: dispatcher, relay worker, drainer.

**Change-Feed Source Identity**: The Outbox Relay's dedup and processor-name key, declared-or-ground-truth and never inferred from a caller-controlled handle — the caller-declared monitored/lease tokens when a registration declares them, and otherwise the resolved ground truth (account endpoint + database id + container id, for both the monitored and the lease container). _Avoid_: database/container/lease triple (the account endpoint is part of the key, so identically-named containers in different accounts stay distinct).

**Monitored-Container Contract**: The ground-truth container facts the Outbox Relay verifies once at host start, in a single read of the monitored container's properties — the partition-key path it will recover each Outbox Document's partition key at, compared against the container's actual paths in order and case-sensitively; the container's default time-to-live mode, which must either leave items carrying no time-to-live field unexpiring or be unset, because any other setting deletes a still-pending Outbox Document before the relay ever drains it; and the container's actual partition-key path against the paths the relay itself stamps, since a container partitioned on one of them could never be stamped at all — Cosmos rejects a patch of a document's partition key — so every published document would stay pending and publish again. All three facts are reconciled against one read, every violation is named in one failure, and a violation fails host start rather than leaving a relay running degraded. _Avoid_: container check, preflight, container validation.

**Document-Tier Outbox Relay**: The Document Tier variant of the Outbox Relay, hosted alongside the command pipeline and drawing its source identities from the Document Reliability Registry; it always reconstructs the brokered message verbatim from the persisted document.

**Standalone Outbox Relay**: The Outbox Relay variant registered as its own hosted service, independent of the command pipeline and of the Document Reliability Registry, for applications that only drain a container.

**Standalone Inbox Gate**: The lease-less inbox-dedup gate registered through the inbox behavior seam for stateless services with no aggregate, outbox, or lease container.

**Outbox Body Resolver**: The Standalone Outbox Relay's optional drain-time seam, invoked per admitted document so the brokered-message body can be sourced from current store state rather than reconstructed verbatim from the persisted document. A resolution of nothing publishes nothing yet still marks the document delivered — an intentional drop-and-acknowledge.

**Outbox Drain Context**: The per-document context the Standalone Outbox Relay hands to its Outbox Body Resolver, carrying the verbatim message id, recovered partition key, declared partition-key path, and the raw document.

**Drain Outcome**: How the Outbox Relay resolved one document the change feed handed it — *admitted* when it was a pending Outbox Document whose brokered message was published, *skipped* when it was not a pending Outbox Document and so was never drained, *dropped* when it was admitted but resolved to no brokered message and was marked delivered without a publish. It is the dimension the drained-document count carries, and the vocabulary is closed. _Avoid_: drain status, drain result.

**Drain Failure**: One Outbox Relay drain attempt that faulted, reported against the Lease Token it faulted under. It is not a Drain Outcome: an Outcome is how a document *resolved*, a Failure is an attempt that never resolved, so a Failure is never a fourth Drain Outcome value and that vocabulary stays closed. _Avoid_: drain error, failed outcome.

**Drain Lag**: The age of an Outbox Document at the moment the Outbox Relay admitted it, measured from the Cosmos server write stamp the document carries to the relay host's own clock. A negative age is not representable — a document cannot be admitted before it was written — so host clock skew is clamped to zero rather than recorded. _Avoid_: outbox latency, drain delay.

**Lease Token**: The change-feed lease a batch was delivered for, and the Outbox Relay's partition-progress dimension: batch size and batch count are recorded once per batch against it, so an idle lease with nothing pending stays distinguishable from a stalled one that is not advancing. _Avoid_: partition id, processor name (the processor name keys a whole Change-Feed Source Identity, not one lease).

## Relationships

- The Document Tier implements the Outbox reliability port and the Standalone Inbox Gate implements the Inbox reliability port defined in the Message Brokers context; the Standalone Outbox Relay implements neither port.
- The Document-Tier Batch-Lifecycle Behavior wraps command dispatch as the outermost behavior in the Command Pipeline defined by the CQRS context.
- The Batch-Lifecycle Behavior consults the Document Reliability Registry, invokes the Partition-Key Resolver only for participants, and publishes the Atomic-Write Handle on the Document-Tier Reliability Surface.
- The Handle-Gated Outbound Router gates outbound routing on the presence of that handle, so non-participant dispatch never reaches the Cosmos outbox.
- The Co-Resident Outbox Document and the Batched Inbox Marker are co-resident: both live in the container selected by the participant's Document Reliability Registration, in the aggregate's logical partition, and commit together on one Atomic-Write Handle.
- The Claimed Inbox Marker instead lives in the Standalone Inbox Gate's own idempotency container, partitioned by the inbound message id (default path `/idempotencyKey`); Chatter writes no aggregate and no Outbox Document beside it there.
- The Co-Resident Outbox Document, the Batched Inbox Marker, and the Claimed Inbox Marker all draw their ids from one Reserved Item-Id Namespace and encode their id segment identically.
- The Outbox Relay drains Outbox Documents to the broker; delivery is at-least-once, so downstream receivers deduplicate via the Inbox Marker.
- Only the Standalone Outbox Relay consults an Outbox Body Resolver, and only when one is configured: it hands each admitted document to that resolver via an Outbox Drain Context before publishing. The Document-Tier Outbox Relay always reconstructs the message verbatim from the persisted document.

## Example dialogue

> **Dev:** "My command writes an aggregate and publishes an event. How do I stop the publish happening when the write fails?"
> **Domain expert:** "Register the command type in the Document Reliability Registry and supply a Partition-Key Resolver. The Batch-Lifecycle Behavior opens one batch on the aggregate's partition, your write and the Co-Resident Outbox Document stage on the same Atomic-Write Handle, and both land or neither does. The Document-Tier Outbox Relay publishes it afterwards."

> **Dev:** "My service has no command pipeline and no aggregate — it just writes trigger documents into a container and needs them published. What do I register?"
> **Domain expert:** "A Standalone Outbox Relay on its own. It consults no Document Reliability Registry: declare `MonitoredSourceIdentity` and `LeaseSourceIdentity` to key its Change-Feed Source Identity on your own tokens, or leave both unset and it keys on the resolved ground truth of the containers it opens. Give each trigger document the Reserved Item-Id Namespace id `outbox:{encoded(MessageId)}` for its own message id or the relay will not drain it, and bind an Outbox Body Resolver if the body should be read from current store state at drain rather than carried on the document."

## Flagged ambiguities

- **Inbox** covers two different registrations: the full document-tier inbox co-resident with the aggregate, and the lease-less Standalone Inbox Gate for stateless services. Name which one is meant.
- The Standalone Inbox Gate deduplicates redeliveries only, not concurrent delivery — two concurrent deliveries of one message id both run the handler, so mutual exclusion for concurrent delivery stays the transport's responsibility.
