# 9. Standalone lease-less Cosmos inbox: confirm-not-infer, fail-loud on missing id, and single-tier composition

Date: 2026-06-30

## Status

Accepted

## Context

Issue #253 adds a lease-less, relay-less standalone Cosmos inbox-dedup gate
(`CosmosBrokeredMessageInbox : IBrokeredMessageInbox, IInboxDeduplicator`, registered by
`WithCosmosInbox`) for a stateless consumer (e.g. a message ACL hop) that has no Cosmos
aggregate, no transactional outbox, and no lease container. It performs an anti-TOCTOU
write-ahead claim — a `CreateItemStream` of a `CosmosInboxMarker` on a `/idempotencyKey`
container — through the existing `InboxBehavior<T>` seam, skipping the handler on a
confirmed duplicate.

Three soundness questions arose that the issue text either answered inconsistently or in a
way that conflicts with the doctrine the document tier shipped in 0.3.0 (ADR-0007):

1. **What does a create-409 mean?** The issue's summary says "a 409 alone confirms the
   duplicate," but its own read-back section says to confirm the conflicting doc is a genuine
   Chatter inbox marker before skipping. 0.3.0 (ADR-0007, `### Fixed`) explicitly REJECTED
   bare-409 inference for the document tier: the application owns the container (it registers
   the `CosmosClient` the container is derived from) and can author a colliding
   `inbox:{encoded(MessageId)}` id through a non-staging path no guard can close; inferring
   "duplicate" from that bare 409 silently lost the colliding message's first delivery. The
   reserved id-namespace is defense-in-depth, "NO LONGER the soundness basis."

2. **What if the inbound message carries no id?** `InboundBrokeredMessage.MessageId` is an
   unconstrained `string` set straight from the transport envelope. RabbitMQ and SQL Service
   Broker auto-fill it (Guid / conversation handle), but Azure Service Bus passes
   `message.MessageId` through with no fallback — a producer that omits it delivers
   null/whitespace. The document tier and the in-memory inbox both FAIL LOUD here; the EF
   relational inbox runs the handler with no dedup.

3. **May the document tier and the standalone inbox coexist in one pipeline?** They dedup by
   different mechanisms (document tier: marker inside the aggregate `TransactionalBatch`;
   standalone inbox: `InboxBehavior<T>` write-ahead claim on a separate container). Registering
   both makes `InboxBehavior<>` run for document-tier participant commands too, so the standalone
   claim fires before the handler and pre-empts the document tier's atomic in-batch dedup.
   Investigation found the only current Cosmos-reliability consumer (skills-tracker) uses the
   standalone relay (`AddCosmosOutboxRelay`, #187) and this standalone inbox — never the document
   tier (`WithCosmosDocumentReliability`).

## Decision

1. **Confirm-not-infer (mandatory).** On a create-409, `ReceiveViaInbox` point-reads the
   conflicting marker and skips the handler ONLY when it is a genuine Chatter inbox marker
   (`_chatterType == "inbox"` AND `MessageId == messageId`), reusing the document tier's
   confirmation shape. A not-yet-visible 404 retries within a bounded budget
   (`ReadBackMaxAttempts` / `ReadBackInterval`); an exhausted or non-confirmable read
   REDELIVERS (throws) rather than silently skipping. The `ReadBackMaxAttempts`/`ReadBackInterval`
   options serve this confirm's 404 retry — not an optional add-on. The read is cold-path-only
   (runs solely on the 409 branch), gates no subsequent write, and is therefore NOT a TOCTOU:
   the atomic create already failed, so the read only disambiguates why.

2. **Fail loud on a missing id.** A null/whitespace `MessageId` throws `InvalidOperationException`
   (handler never runs, nothing written), matching the document tier and the in-memory inbox, NOT
   the EF relational inbox. For a primitive whose entire purpose is the once-only guarantee,
   silently running with no dedup is the wrong outcome, and the null path is reachable behind an
   Azure Service Bus receiver.

3. **Single-tier composition; document-only enforcement.** `WithCosmosDocumentReliability`
   (document tier) and `WithCosmosInbox` (standalone inbox) are UNSUPPORTED together in one
   pipeline; this is documented (README) rather than enforced by a registration guard, because no
   current consumer uses the document tier and building a guard for an unhit combination is
   low-value. The standalone outbox relay (`AddCosmosOutboxRelay`) and the standalone inbox are
   orthogonal and fully SUPPORTED together (skills-tracker's actual plan).

## Consequences

- The standalone inbox inherits the same soundness basis as the document tier: an app-authored
  `inbox:`-prefixed collision is detected and redelivered, never silently dropped.
- Handlers behind this inbox must be idempotent: the claim is write-ahead, so on a handler
  failure the marker is best-effort compensation-deleted and the exception rethrown for
  redelivery; non-batched handler side effects (external HTTP, non-Cosmos writes) that ran before
  the failure re-run on redelivery (AT-LEAST-ONCE for those), and a failed compensation-delete
  after a partial handler is a documented edge. This is the same side-effect-timing contract the
  document tier documents (ADR-0007).
- A consumer whose messages can arrive without an id (a raw Azure Service Bus producer) must set a
  message id upstream or accept a loud failure at the inbox.
- `WithCosmosInbox` restricts v1 to a single-segment partition-key path (default `/idempotencyKey`);
  hierarchical support is deferred to a follow-up (backlog).
- The document tier's future relative to the two lease-less primitives is under separate review
  (backlog).

## Alternatives considered

- **Bare-409-skip (the issue summary's literal reading).** Rejected: reintroduces the exact
  silent-first-delivery-loss class ADR-0007 closed for the document tier.
- **EF-style run-handler on a missing id.** Rejected: silently defeats the once-only guarantee for
  an identity-less message; inconsistent with the closest sibling (the Cosmos document tier).
- **Fail-loud registration guard for the document-tier + inbox combination.** Deferred to
  documentation-only: no current consumer hits the combination, so a code guard is unjustified
  cost.
