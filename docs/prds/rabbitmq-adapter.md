# PRD: Chatter.MessageBrokers.RabbitMQ adapter

## Problem Statement
Chatter offers a technology-agnostic message-broker abstraction with two concrete
implementations — Azure Service Bus and SQL Service Broker. Teams that run on RabbitMQ
cannot adopt Chatter's brokered-messaging model without writing their own adapter. They
need a first-class RabbitMQ broker that plugs into the existing `Chatter.MessageBrokers`
ports so the same handlers, attributes, and recovery semantics work unchanged on RabbitMQ.

## Solution
A new independently-versioned NuGet module — `Chatter.MessageBrokers.RabbitMQ` — that
implements the existing `Chatter.MessageBrokers` receiver, dispatcher, infrastructure,
body-converter, path-builder, and recovery seams over RabbitMQ. It is structurally
analogous to the SQL Service Broker and Azure Service Bus adapters: a single
`AddRabbitMq(...)` registration wires a RabbitMQ-backed receiver and sender behind the
broker-agnostic interfaces, so `[BrokeredMessage]`-decorated messages send and receive over
RabbitMQ with no handler changes. The module provisions no broker topology — it consumes
exchanges, queues, bindings, and dead-letter routing created externally — and it requires
no change to any `Chatter.MessageBrokers` core contract.

## User Stories
As a Chatter app developer, I want to register a RabbitMQ broker via `AddRabbitMq(...)`, so that I can send and receive brokered messages over RabbitMQ with the same API as the Azure Service Bus and SQL Service Broker adapters.
As a developer, I want `[BrokeredMessage]`-decorated messages to send and receive over RabbitMQ unchanged, so that I do not rewrite handlers when switching brokers.
As a developer, I want poison messages routed to my declared dead-letter/error queue once Max Receives Exceeded trips — on both quorum and classic queues — so that bad messages do not loop forever.
As an operator, I want the adapter to provision nothing and consume the topology I created via IaC or a Dockerfile, so that the broker stays the single source of truth for messaging topology.
As a developer, I want idle receivers to not burn CPU and reconnects to never falsely acknowledge a message, so that the adapter is safe to run in production.
As a developer, I want a clear startup failure if I request unsupported atomic receive-and-send, directing me to the Outbox, so that I never silently lose outgoing messages.
As a maintainer, I want unit tests plus dockerized integration tests that are excluded from PR CI and run nightly, so that the adapter is verified without gating pull requests on Docker availability.

## Acceptance Criteria
- Registering the broker through the single `AddRabbitMq(...)` entry point exposes a working RabbitMQ-backed receiver and sender behind the broker-agnostic interfaces, selectable as a RabbitMQ infrastructure type.
- A message type decorated with the brokered-message attribute (sending path, receiving name, error-queue name, dead-letter-queue name) sends and is received end-to-end over RabbitMQ without handler changes.
- Sending to a destination with no routing override delivers to the queue whose name equals the destination; an explicit exchange/routing-key override carried in message context routes accordingly. Publishes are confirmed by the broker before being treated as sent.
- An idle receiver consumes no measurable CPU while waiting (no busy polling) and delivers messages to the core promptly once they arrive.
- Concurrent message processing up to the configured maximum never corrupts acknowledgement; an acknowledgement attempted after a broker reconnect never acknowledges the wrong message and never falsely acknowledges — the message is redelivered instead.
- On a successful handle the message is acknowledged; on failure it is negatively acknowledged for redelivery; once the configured receive limit is exceeded the message is routed to the declared dead-letter (or error) path.
- The receive limit is honored on quorum queues (using the broker's native delivery count) and on classic queues (using a persisted per-message attempt count), per ADR 0001. Quorum is the documented recommendation.
- Configuring atomic receive-and-send (full-atomicity-via-infrastructure) fails at startup with a message directing the user to the Outbox; the none and receive-only transaction modes are supported.
- Connection drops recover automatically and transient RabbitMQ faults are classified through the same retry/circuit-breaker recovery seam the other adapters use.
- The package targets net8.0 and net10.0, ships at initial version 0.1.0, is part of the solution, and has its own per-module CI plus nightly integration wiring; documentation states the required externally-provisioned topology and the quorum-queue recommendation.

## Implementation Decisions
- The adapter realizes the existing `Chatter.MessageBrokers` ports (receiver, dispatcher, infrastructure, body converter, path builder, retry/circuit-breaker predicate providers) and the DI registration surface; no `Chatter.MessageBrokers` core contract changes. This is a hard boundary — any pressure to alter a core contract is a signal to stop and re-evaluate.
- Destination addressing uses a default-exchange convention: a bare destination names a queue (routing key equals queue name). Richer exchange/routing-key addressing is expressed as message-context data carried alongside the message, not as a new core contract or a parsed compound string.
- Dead-letter and error routing target the attribute-declared dead-letter/error path names directly (an adapter-owned republish), not whatever a queue's broker-side dead-letter configuration happens to point at. The declared names are authoritative.
- Delivery counting is a selectable strategy with two behaviors — broker-native count for quorum queues and a persisted per-message count for classic queues — both surfaced to the core as the same single attempt-count value the core already reads. The classic strategy and its rare-duplicate / ordering trade-offs are governed by ADR 0001 and absorbed downstream by the Inbox.
- The module owns no broker topology. Required exchanges, queues, bindings, and dead-letter routing are created and owned outside the module; the adapter assumes they exist, mirroring the SQL Service Broker manual-provisioning stance.
- Atomic receive-and-send is not offered; the supported reliability path for transactional send is the existing Outbox. Requesting full atomicity is rejected at startup rather than silently degraded.
- The body crossing the wire is UTF-8 JSON produced through the shared Chatter JSON options, so payloads stay consistent with the rest of the suite.

## Testing Decisions
- Contract/unit level: exercise options building, DI registration, infrastructure-type resolution, receive, acknowledge/negative-acknowledge, body round-trip, and transient-fault classification against an in-memory connection-source double, with no live broker required. These run in PR CI.
- End-to-end level: exercise the full pipeline against a real RabbitMQ instance — round-trip publish/receive/handle, negative-acknowledge redelivery, and dead-letter routing — gated behind a docker-available check, excluded from PR CI and run on the nightly integration schedule.
- The Max Receives Exceeded behavior is proven on both queue types so the quorum and classic counting strategies are each verified.

## Success Metrics
- A developer can stand up RabbitMQ-backed brokered messaging in a Chatter app using only the documented registration call and externally-provisioned topology, with no handler changes versus another broker.
- No busy-spin: an idle receiver shows no measurable CPU attributable to polling.
- No poison-message loops and no false acknowledgements observed across reconnect and redelivery scenarios in integration tests.
- The module ships as the eighth package with green per-module CI and passing nightly integration runs.

## Out of Scope
- Changing any `Chatter.MessageBrokers` core contract.
- Auto-declaring or provisioning RabbitMQ topology (exchanges, queues, bindings, dead-letter exchanges).
- Atomic receive-and-send / honoring full-atomicity-via-infrastructure.
- An optional declare-topology-on-startup convenience flag (deferred to a later minor version).
- CI deploy-environment and secret provisioning for the RabbitMQ release pipeline (deferred).
- A non-JSON or UTF-16 body converter.

## Further Notes
- Internal mechanics settled during interrogation — a push-consumer feeding an internal buffer, a single serialized receive channel for consume-and-acknowledge, pooled publish channels, and reconnect-epoch-guarded acknowledgements — are HOW and belong to the build directive, not this PRD. They are recorded here only as pointers.
- ADR 0001 governs the classic-queue counting trade-off (rare duplicate on a crash between republish and acknowledge, mitigated by publisher confirms and the Inbox; loss of head-of-queue ordering on classic redelivery).
- CONTEXT.md for the new module and its CONTEXT-MAP registration are already written and committed on this branch.
- The build remains a single-PR, ten-step sequence; this PRD is path-agnostic and does not itself decide that shape.
