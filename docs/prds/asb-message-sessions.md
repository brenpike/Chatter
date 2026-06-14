# PRD: Azure Service Bus Message Sessions

## Problem Statement
Chatter's Azure Service Bus adapter receives brokered messages without any awareness of
sessions. A consumer that needs related messages processed in strict FIFO order per a
correlation key — the classic Azure Service Bus session guarantee — cannot get it through
Chatter today: messages on a session-enabled queue or subscription are interleaved or
rejected, the inbound `SessionId` is invisible to handlers, producers have no first-class
way to address a session, and there is nowhere to keep per-session state across messages.
Teams building stateful, ordered workflows on Azure Service Bus (issue #209) must drop out
of Chatter's abstraction to use the raw SDK, losing the handler, recovery, and reliability
semantics the rest of the suite provides. This matters now because session-ordered
processing is a core Azure Service Bus capability that the adapter's existing pull-based
receive model can support without disturbing the broker-agnostic core.

## Solution
Add Azure Service Bus message-session support entirely within the Azure Service Bus adapter,
so a consumer can opt a queue receiver or topic subscription into session mode and have
related messages delivered in FIFO order per `SessionId`. Inbound brokered messages surface
their `SessionId` to handlers through the established Group Id term, so handlers correlate
work by session with no new core concept. Producers set the session on an outbound message
through the existing Group Id surface, routing it to the correct session. Handler authors can
read, write, and clear durable per-session state during handling. Operators can tune session
behavior — how long an idle session is held before rolling to the next, and the ceiling on how
long a session lock is renewed for long-running work. A session-mode receiver processes one
session at a time, holding it for FIFO delivery and then rolling to the next; broader
throughput is achieved by running more receiver instances, not by in-process multi-session
parallelism. The adapter provisions nothing: session-enabled entities are created externally,
consistent with the module's existing no-auto-provision stance.

## User Stories
As a consumer, I want to configure a receiver for a session-enabled queue/subscription, so that related messages are processed FIFO per SessionId.
As a consumer, I want inbound brokered messages to expose their SessionId, so that handlers can correlate work by session.
As a producer, I want to set SessionId on outbound brokered messages, so that they route to the correct session.
As a handler author, I want to read, write, and clear session state during handling, so that I can maintain stateful per-session processing.
As an operator, I want to tune session behavior (idle timeout, lock-renewal ceiling), so that both short and long-running sessions behave correctly.

## Acceptance Criteria
- A queue receiver or topic subscription can be registered in session mode, and once registered it accepts and processes messages from a session-enabled entity.
- Messages carrying the same SessionId are delivered to the handler in the order they were enqueued (FIFO within a session); only one session is processed at a time per receiver instance.
- An inbound brokered message exposes its SessionId to the handler through the Group Id term, so a handler reads the session without referencing any Azure-specific session concept.
- An outbound brokered message whose Group Id is set is routed to the matching session on the target entity.
- During handling, a handler can read existing session state, write new session state, and clear session state, and those effects are durable for subsequent messages in the same session.
- An operator can configure the idle timeout after which a held session with no further messages is released and the receiver rolls to the next session, and the ceiling on how long a held session's lock is renewed during long-running processing.
- Settlement (complete, abandon, dead-letter) of a session message succeeds against the held session and honors the configured transaction mode, including the receive-and-delete mode where explicit settlement does not apply.
- A lost session lock is treated as a recoverable condition: the receiver releases the session and resumes with the next session rather than failing fatally and stopping the receiver.
- When no session is available to accept, the receiver yields without error and re-polls, so an empty session-enabled entity does not fault the receiver.
- Invoking the session-state capability while handling a non-session message fails predictably with a clear error rather than corrupting state or silently succeeding.
- Existing non-session receivers, senders, and reliability/atomicity behavior are unchanged; enabling sessions is additive and opt-in.

## Implementation Decisions
- Session support is realized entirely within the Azure Service Bus adapter. No change is made to the broker-agnostic core abstractions beyond carrying the already-existing Group Id term and surfacing session state through the handler context container. This is a hard boundary: pressure to add a core push-dispatch concept or a new core session contract is a signal to stop and re-evaluate, not to proceed.
- A session-mode receiver processes a single session at a time: it accepts a session, delivers that session's messages FIFO through the adapter's existing pull-based receive contract, settles on the held session, and rolls to the next session on drain, idle, or lock loss. Cross-session concurrency is an operational concern solved by running additional receiver instances, not an in-process multi-session feature.
- The Azure Service Bus `SessionId` is the broker realization of the suite's existing Group Id (AMQP group-id) term. Inbound, the received message's session is surfaced as the Group Id on the message context; outbound, the existing Group Id surface sets the session. No new "session id" alias is introduced on the producing or consuming API — Group Id is the single canonical term.
- Per-session durable state is exposed to handlers through the held session, surfaced via the handler context container, so a handler reads, writes, and clears state without taking an Azure-specific dependency in its own signature. The session-state capability is only valid while handling a session message; using it outside that context is a predictable error.
- Session lock renewal is bounded by an operator-configured ceiling so a long-running session is kept alive without renewing indefinitely. Idle session hold is bounded by an operator-configured timeout after which the receiver rolls to the next session.
- A lost session lock is a non-fatal, recoverable condition — the session is released and the receiver recovers — and is never escalated to the fatal receiver-stopping failure class. "No session available to accept" is likewise non-fatal: the receiver yields nothing and re-polls.
- Existing reliability and settlement behavior — including the full-atomicity-via-infrastructure and cross-entity-transaction paths — is reused unchanged for session receivers; settlement occurs against the held session.
- A maximum-concurrent-sessions knob is intentionally not part of this Initiative; one session at a time per receiver is the model, and concurrency scales by instance count.

## Testing Decisions
- Contract/unit level (no broker, no Docker): prove that an inbound session message surfaces its SessionId as Group Id, that the receiver-factory selects the session path when a receiver is registered in session mode, and that settle/roll behavior holds where the test seam permits substituting the session receiver. These run in PR CI.
- End-to-end level (Azure Service Bus emulator, gated on Docker availability and skipped when unavailable): prove FIFO ordering within a session, a full session-state read/write/clear round-trip across messages in one session, and correct behavior against a session-enabled (RequiresSession) entity. These are excluded from no-Docker runs so a plain test run stays green.
- The testability boundary of the sealed SDK session-receiver type is acknowledged: behavior that cannot be unit-isolated because of that boundary is covered at the emulator integration level instead.

## Success Metrics
- A consumer can stand up session-ordered brokered messaging in a Chatter app using only the documented session-mode registration and externally-provisioned session-enabled entities, with no handler changes beyond reading the Group Id and using the session-state capability.
- Messages within a session are observed in strict FIFO order, and per-session state survives across messages in the same session, in emulator integration tests.
- A lost session lock or an empty session-enabled entity never stops the receiver in integration scenarios.
- Existing non-session functionality shows no behavioral regression after the change ships.

## Out of Scope
- Sessions for other broker adapters (SQL Service Broker, RabbitMQ).
- Core CQRS/MessageBrokers abstraction changes beyond carrying SessionId / session state.
- Native ServiceBusSessionProcessor and in-process multi-session parallelism (explicitly rejected; cross-session parallelism is achieved by running more receiver instances).
- Auto-provisioning of session-enabled entities (manual provisioning required, consistent with the module's existing no-auto-provision stance).

## Further Notes
- A push-based native `ServiceBusSessionProcessor` was considered and rejected for this Initiative because it would force a new push-dispatch seam into the broker-agnostic core and split the receive architecture; it is recorded as a possible future direction that would need its own ADR. The accompanying solution-architecture document captures that rationale in HOW terms.
- Verification item carried into implementation: confirm the Azure Service Bus emulator supports session-enabled entities during the integration test run; if it does not, the session integration tests follow the same skip-when-unavailable discipline as the existing emulator suite.
- Internal mechanics (single-session accept loop, the lock-renewal task, how the held session receiver is surfaced) are HOW and belong to the directive and the solution-architecture document, not this PRD.
