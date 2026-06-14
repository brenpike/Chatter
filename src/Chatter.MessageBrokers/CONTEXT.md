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

**Document Tier** (NoSQL): the stage-then-commit reliability model where the handler owns an atomic batch on one logical partition and the framework contributes the outbox record; dispatched by a change-feed relay.

**Atomic-Write Handle**: the active transaction or batch carried on the Transaction Context container; the seam both reliability tiers share (a relational transaction or a document-store batch). On the document tier, the Co-Resident Outbox doc and the Co-Resident Inbox Marker both ride this handle — they are contributed to the same batch as the aggregate write and commit with it atomically.

**Stage-then-Commit**: an atomic-write model where writes are accumulated then committed once (document tier), as opposed to running handler code inside an ambient transaction.

**Pollable Outbox Store**: the relational-only outbox capability for polling-based dispatch (query unprocessed records, mark processed); not implemented by document-store providers.

**Outbox Relay**: a change-feed-driven drain that publishes persisted outbox records to the broker (document tier), filtered to outbox records only — never deriving events from domain-document changes.

**Co-Resident Outbox**: a document-tier outbox record stored in the same container and logical partition as the aggregate it accompanies, so both are written in one atomic batch.

**Co-Resident Inbox Marker**: a document-tier inbox dedup marker stored in the aggregate's logical partition, contributed to the same handler-owned atomic batch as the aggregate write and the Co-Resident Outbox doc. Its id is derived from the message identity using the same Cosmos-id-safe encoding as the outbox id; it carries the Chatter-reserved `_chatterType="inbox"` discriminator so the Outbox Relay's `_chatterType="outbox"` predicate ignores it by construction. A create-conflict on the marker fails the whole batch atomically, making once-only dedup atomic with the aggregate write rather than a sequential guard. Applicable only when the incoming message deterministically maps to a single aggregate partition.

## Relationships

- A Brokered Message Receiver consumes infrastructure messages and hands them to the Brokered Message Dispatcher.
- The Dispatcher relays to a Command or Event handler (Chatter.CQRS) by message type.
- Recovery (Retry, Circuit Breaker) wraps receiving; exhausting it yields a Critical Failure routed to the Error Queue.
- Inbox and Outbox use in-memory implementations by default; durable storage is supplied by the Reliability.EntityFramework context.
- Reliability is two-tier: a relational (ambient-transaction) tier and a NoSQL/document (stage-then-commit) tier share the enqueue (SendToOutbox), inbox, and Transaction Context seam; durable storage is supplied by provider packages (EntityFramework for relational; Cosmos for document).
- A Routing Slip drives a Router across a sequence of destinations.
- Concrete brokers (Azure Service Bus, SQL Service Broker) implement the receiver/sender/path interfaces defined here.

## Example dialogue

> **Dev:** "A handler keeps throwing — where does the message end up?"
> **Domain expert:** "Recovery retries it under the Circuit Breaker. Once Max Receives is exceeded it's a Critical Failure, so it's moved to the Error Queue and a Critical Failure Event fires."

## Flagged ambiguities

- **Router vs Forwarder**: ForwardingRouter and IBrokeredMessageForwarder overlap; treat Forwarder as a Router specialization.
