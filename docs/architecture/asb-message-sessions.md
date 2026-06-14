# Solution Architecture: Azure Service Bus Message Sessions

- **Status:** Proposed
- **Slug:** `asb-message-sessions`
- **PRD:** [docs/prds/asb-message-sessions.md](../prds/asb-message-sessions.md)
- **Issue:** [#209](https://github.com/brenpike/Chatter/issues/209)

This document is the HOW companion to the WHAT-only PRD. It records the design decisions,
component responsibilities, key flows, seam contracts, and risks for adding Azure Service Bus
message-session support to `Chatter.MessageBrokers.AzureServiceBus`. It honors the module's
ubiquitous language (`CONTEXT.md`): a session-enabled binding is still a **Queue Receiver**
(Commands) or a **Topic Subscription** (Events); configuration lives in **Service Bus Options**;
recovery uses the **Service Bus Retry** / **Service Bus Circuit Breaker** policies. The module
avoids the aliases the suite's glossaries flag (consumer, listener, forwarder, read request).

## Context & drivers

Chatter's broker-agnostic core (`Chatter.MessageBrokers`) drives all receiving through one
pull-based pump, `BrokeredMessageReceiver.MessageReceiverLoopAsync`, which repeatedly calls a
broker's `IMessagingInfrastructureReceiver.ReceiveMessageAsync` and fans each received message
out to a worker. The Azure Service Bus realization of that port,
`ServiceBusReceiver`, delegates the actual SDK calls to a small internal port,
`IServiceBusMessageReceiver`, whose production implementation,
`AzureSdkMessageReceiverAdapter`, wraps the low-level SDK `ServiceBusReceiver` primitive created
off a single shared `ServiceBusClient`.

The driver (issue #209) is FIFO-per-`SessionId` ordering with durable per-session state, exposed
through the same handler/recovery/reliability semantics the rest of the suite already provides —
without introducing a session concept into the broker-agnostic core. The PRD fixes the WHAT;
this document fixes the HOW within the adapter.

## Decision

**Add a single-session-at-a-time session adapter that plugs into the existing Chatter pull pump
through the existing `IServiceBusMessageReceiver` port — not the native push processor.**

A session-mode receiver accepts one session, holds it, serves that session's messages FIFO
through the same `ReceiveAsync` contract the non-session adapter already satisfies, settles on the
held session, and rolls to the next session when the current one drains, goes idle, or loses its
lock. Cross-session parallelism is achieved operationally by running more receiver instances, not
in-process.

### Rationale and precedent

The decision is the lowest-risk, highest-consistency option because the adapter **already**
receives through the low-level primitive plus Chatter's own pump rather than a native processor:

- `AzureSdkMessageReceiverAdapter` constructs the SDK `ServiceBusReceiver` primitive directly off
  the shared `ServiceBusClient` and exposes `ReceiveAsync` / `CompleteAsync` / `AbandonAsync` /
  `DeadLetterAsync` / `CloseAsync` — there is no `ServiceBusProcessor` anywhere in the receive path.
- `BrokeredMessageReceiver.MessageReceiverLoopAsync` is the sole pull loop: it owns concurrency,
  recovery (retry / circuit breaker), critical-fault notification, and teardown. A session adapter
  that satisfies the same `IServiceBusMessageReceiver` shape inherits all of that unchanged.

The session SDK provides a parallel primitive, `ServiceBusSessionReceiver`, obtained via
`ServiceBusClient.AcceptNextSessionAsync`, that exposes the same settle methods plus session-state
and session-lock-renewal operations. A session adapter is therefore a drop-in sibling of the
existing adapter behind the same internal port, and the pump cannot tell the difference.

## Alternatives considered & rejected

**Native `ServiceBusSessionProcessor` (push model).** The SDK offers a higher-level processor that
pushes session messages to a callback and manages accept/renew/settle internally. Rejected for
this Initiative because:

- It is a **push** model, whereas every receiver in Chatter is **pull**. Adopting it would force a
  new push-dispatch seam into the broker-agnostic core (`Chatter.MessageBrokers`) so a pushed
  message could reach the core's dispatch path — exactly the core change the PRD declares out of
  scope.
- It would **split the receive architecture**: the system would have one push receiver (sessions)
  and pull receivers (everything else), each with separate concurrency, recovery, and teardown
  behavior, doubling the surface that must be reasoned about and tested.
- It **overlaps deferred #147** (the broader push/processor direction), which is itself unscheduled.

This is recorded as a **possible future direction** — if Chatter ever adopts a push-dispatch core
seam, the session processor becomes attractive — but that is a larger architectural shift that
**needs its own ADR** and is not undertaken here.

## Component & responsibility view

All new components live inside `Chatter.MessageBrokers.AzureServiceBus`; none alter the core.

1. **Session message receiver adapter** — a new `IServiceBusMessageReceiver` implementation that
   wraps a held SDK `ServiceBusSessionReceiver`. It owns: accepting the next session, FIFO receive
   for the held session via the existing `ReceiveAsync` contract, settle (complete / abandon /
   dead-letter) on the held session, a bounded session-lock renewal loop, non-fatal handling of a
   lost session lock and of "no session available," and rolling to the next session. It exposes the
   held session receiver through an internal accessor so the session-state extension can reach it.

2. **Receiver registry session opt-in** — `ServiceBusReceiverRegistry` records, per registered
   receiver, whether the entity requires a session, alongside the top-level-entity and
   transaction-mode it already captures. New session-aware registration entry points
   (`AddSessionQueueReceiver` / `AddSessionTopicSubscription`) register a receiver as session-mode.
   The registry stays write-at-registration / read-at-client-build, as today.

3. **Receiver factory branch** — `ServiceBusReceiver.CreateProductionReceiver` branches on the
   registry's session flag for the receiver's entity: the session adapter when session-mode, the
   existing `AzureSdkMessageReceiverAdapter` otherwise. The registry is injected into
   `ServiceBusReceiver` while **preserving the existing `receiverFactory` test seam** (the internal
   ctor that lets tests substitute an in-memory `IServiceBusMessageReceiver`).

4. **Inbound surfacing** — `InboundBrokeredMessageFactory` surfaces the received message's
   `SessionId` onto the message context as the **Group Id** header (`MessageContext.GroupId`), and
   `ServiceBusReceiver.ReceiveMessageAsync` includes the held session receiver in the transaction
   context `Container` so the session-state extension can resolve it during handling.

5. **Session-state extension** — handler-context extension methods (Get / Set / Clear) over the
   held session receiver retrieved from the context `Container`, exposed alongside the existing
   `IMessageHandlerContext.AzureServiceBus()` surface. Session-state header keys are pinned on
   `ASBMessageContext` next to the existing ASB header constants.

## Key flows

**Accept → serve FIFO → settle → renew → roll.** The pump calls the session adapter's
`ReceiveAsync` exactly as it calls the non-session adapter; the adapter internalizes the session
lifecycle:

1. **Accept session.** With no session held, the adapter calls `AcceptNextSessionAsync` on the
   shared client for the receiver's entity, obtaining a held `ServiceBusSessionReceiver`. It starts
   the bounded lock-renewal loop for that session.
2. **Serve FIFO one-at-a-time.** Each `ReceiveAsync` returns the next message from the held session
   via the held receiver's `ReceiveMessageAsync`, preserving the existing single-message
   `IServiceBusMessageReceiver` contract. The pump's per-message worker handles it; ordering is FIFO
   because all messages come from the one held session and the pull cadence is serialized on the
   loop thread.
3. **Settle on the held session receiver.** Complete / Abandon / DeadLetter route to the **held
   session receiver**, not a plain receiver — settlement happens under the session lock, so the
   session's lock token is honored. This mirrors the existing adapter's "settle by received-message
   object" invariant.
4. **Bounded lock renewal.** While a session is held, a renewal loop calls
   `RenewSessionLockAsync` periodically up to the configured ceiling so long-running processing
   does not lose the lock.
5. **Roll to next session.** On session drain (an empty receive), idle timeout, or no session
   available, the adapter releases the current session (cancel renewal, close the session receiver)
   and **returns null** from `ReceiveAsync`, so the pump simply re-polls and the adapter accepts the
   next session on the following turn. Returning null is the established "nothing received this
   turn" signal the pump already understands (it releases the slot and continues).

## Contracts & data shapes across seams

- **`SessionId` ↔ `MessageContext.GroupId`.** `SessionId` is the Azure Service Bus realization of
  the AMQP / core **Group Id** term. Inbound, `InboundBrokeredMessageFactory` stamps the received
  `SessionId` into the context under `MessageContext.GroupId`. Outbound, the existing
  `SendOptions.WithGroupId` → `MessageContext.GroupId` → `OutboundBrokeredMessageExtensions.GetGroupId`
  → SDK `ServiceBusMessage.SessionId` mapping (already present) is reused. **No `WithSessionId`
  alias is introduced** — Group Id is the single canonical surface, matching the existing
  send-side mapping and the suite glossary.
- **Held session receiver in the context `Container`.** `ServiceBusReceiver.ReceiveMessageAsync`
  includes the held `ServiceBusSessionReceiver` (via the internal accessor) in the message's
  transaction-context `Container`. The session-state extension resolves it from there, so a handler
  reaches session state without an Azure-specific parameter on its own signature.
- **`IServiceBusMessageReceiver` unchanged.** The session adapter satisfies the existing port shape
  (receive returns one `ServiceBusReceivedMessage`; settle is by message object). The pump and
  `ServiceBusReceiver` are agnostic to which adapter is behind the port.

## Concurrency & lock-renewal design

- A held session owns **one `CancellationTokenSource` and one renewal `Task`**. The renewal task
  loops calling `RenewSessionLockAsync` on a cadence derived from the session lock duration, until
  the ceiling is reached or the session is released.
- The renewal CTS is **cancelled before the session receiver is closed** on every roll/release path
  (drain, idle, lock loss, teardown), so no renewal call races a closing/closed session receiver.
- The ceiling is the configured `MaxSessionLockRenewalDuration`; once reached, renewal stops and the
  session is allowed to expire/roll naturally rather than being held forever.
- **Option (a) fallback understanding:** if bounded programmatic renewal proves unworkable against a
  given target, the documented fallback is to rely on the entity's `LockDuration` alone (no
  programmatic renewal), accepting that a single message's processing must complete within
  `LockDuration`. This is recorded as the fallback design, not the primary path.

## Reliability & settlement

- The existing reliability paths — `TransactionMode.FullAtomicityViaInfrastructure` and
  `EnableCrossEntityTransactions` on the shared `ServiceBusClient` — are **reused unchanged**. A
  session receiver still settles through the same code in `ServiceBusReceiver` (the `_receiveMode`
  PeekLock/ReceiveAndDelete gate, the cross-entity-transaction enlistment, the dead-letter
  description cap), with the held session receiver as the settlement target.
- **Cross-entity transaction under session:** when cross-entity transactions are on, the send +
  the session settle enlist in one transaction on the shared client exactly as for a non-session
  receiver; the single-top-level-entity startup guard in
  `ResolveEffectiveCrossEntityTransactions` continues to apply to session receivers.

## Edge cases

- **SessionLockLost.** Treated as **non-fatal**: the adapter releases the session (cancel renewal,
  close session receiver) and recovers to accept the next session. It is **never** raised as
  `CriticalReceiverException` — losing a lock is an expected operational event, not a
  receiver-stopping fault. This deliberately differs from the cross-entity-transaction rejection,
  which **is** critical.
- **No session available.** `AcceptNextSessionAsync` failing to lock a session
  (`SessionCannotBeLocked`) means the entity has no available session right now. The adapter
  returns **null** from `ReceiveAsync` so the pump re-polls; no error, no fault.
- **Idle rollover.** A held session that yields no message within the configured
  `SessionIdleTimeout` is released and the adapter rolls to the next session (return null, re-poll).
- **`TransactionMode.None` (ReceiveAndDelete).** Explicit settlement does not apply in
  receive-and-delete mode; the existing `_receiveMode != PeekLock` guards in Ack/Nack/Deadletter
  already short-circuit and are honored for the session adapter too, so no settle is attempted
  against a session held in receive-and-delete mode.
- **Non-session message invoking the session-state extension.** Calling Get/Set/Clear while
  handling a message that was not received through a session (no session receiver in the
  `Container`) fails with a **predictable `InvalidOperationException`** that names the misuse, rather
  than silently no-op'ing or corrupting state.
- **Teardown while a session is held.** On `StopReceiver` / dispose while a session is held, the
  renewal CTS is cancelled and the held session receiver is closed as part of the adapter's
  `CloseAsync`, mirroring the existing adapter's close-and-null discipline so the pump's
  quiesce-before-dispose contract holds.

## Configuration

Two new knobs on **Service Bus Options** (`ServiceBusOptions` + `ServiceBusOptionsBuilder`),
following the existing nullable-backing-field "fluent call wins over config in either direction"
pattern used for `MaxConcurrentCalls` / `PrefetchCount` / `EnableCrossEntityTransactions`:

- **`SessionIdleTimeout`** — how long a held session may yield no message before it is released and
  the receiver rolls to the next session.
- **`MaxSessionLockRenewalDuration`** — the ceiling on how long a held session's lock is renewed
  for long-running processing.

A **max-concurrent-sessions** knob is intentionally **N/A**: the model is one session at a time per
receiver instance; cross-session concurrency scales by instance count, not configuration.

## Testing strategy

- **Unit (no Docker).** Prove SessionId → Group Id surfacing in `InboundBrokeredMessageFactory`;
  prove the `CreateProductionReceiver` factory branch selects the session adapter for a session-mode
  registry entry and the existing adapter otherwise; prove settle/roll behavior on the adapter where
  the `receiverFactory` / `IServiceBusMessageReceiver` seam permits an in-memory double. These run in
  PR CI.
- **Emulator integration (Docker-gated, skip when unavailable).** A new `RequiresSession` queue in
  the emulator `Config.json`; `PipelineSessionTests` proving FIFO ordering within a session and a
  full session-state read/write/clear round-trip across messages in one session; reuse of the
  existing `ServiceBusEmulatorFixture` / `RequiresDockerFact` skip-when-unavailable discipline so a
  no-Docker run stays green.
- **Sealed-type boundary.** The SDK `ServiceBusSessionReceiver` is **sealed** and cannot be mocked,
  so adapter behavior that depends directly on it (accept, renew, settle on the concrete session
  receiver) is covered at the emulator integration level rather than by a unit double. The unit
  seam covers everything reachable through the `IServiceBusMessageReceiver` port.

## Risks

- **Renewal-loop concurrency.** A renewal task racing a session close/roll is the primary
  concurrency risk; mitigated by the one-CTS-per-session design and cancelling renewal **before**
  closing the session receiver on every release path.
- **Sealed-SDK testability.** `ServiceBusSessionReceiver` being sealed limits unit isolation;
  mitigated by pushing that coverage to emulator integration tests and keeping the unit-testable
  logic behind the `IServiceBusMessageReceiver` port.
- **Emulator session support.** Whether the Azure Service Bus emulator supports session-enabled
  entities is an open verification item; if it does not, the session integration tests follow the
  existing skip-when-unavailable pattern and FIFO/state coverage relies on the documented manual /
  real-namespace path.

## Version impact

`Chatter.MessageBrokers.AzureServiceBus` is bumped **minor**, `1.3.0 → 1.4.0`. The change is
**additive and backward compatible**: new opt-in session registration entry points, two new
optional Service Bus Options knobs, and new handler-context session-state extensions. No existing
public surface changes, and non-session receivers/senders behave identically.
