---
status: accepted
date: 2026-06-13
---

# RabbitMqConnectionSource single monotonic lifecycle authority + publish-or-surrender handoff

`RabbitMqConnectionSource` is the process singleton that owns the one `IConnection`, the serialized receive channel (with its epoch — see ADR 0002), and the pooled publish channels. Its disposal must quiesce all three against concurrent receive settles, publish acquires, and recovery callbacks. Three successive structural fixes hardened that teardown and each closed one race, yet a new create-vs-dispose race kept surfacing. This ADR settles **why** those fixes could not close the class and **what** structural change does.

## The three-gate problem

The source had **three non-composing mutual-exclusion domains over overlapping lifecycle state**:

- `_connection` was **created** under a dedicated `_connectionInitGate` (`EnsureConnectionAsync`) but **disposed** under `_receiveChannelGate` (`DisposeAsync`) — **different locks**. So disposal could never exclude connection-creation; adding rechecks under either lock could not make the two mutually exclusive.
- `EnsureConnectionAsync` was reachable from **both** the receive path and the publish path, so connection creation contended across two unrelated call chains.
- `_disposed` was a plain `bool` each domain read independently, at its own point relative to its own gate. There was no single authority a suspended op could consult to learn it must not publish its resource.

The cross-product of (gate × await-suspension point) was a whack-a-mole engine: each prior fix patched one coordinate (one gate, one recheck site), and a suspended op resuming across a completing disposal on a *different* coordinate could still assign `_connection` or return a live publish rental on a torn-down source — resurrecting a resource past disposal.

## Considered Options

- **(i) Add one more per-site `_disposed` recheck (REJECTED — whack-a-mole).** This is the shape of the three prior non-closing fixes: a recovery-epoch lifecycle redesign, a dispose-coordination "observed-on-both-sides" pass, and a permit-conservation/return pass each closed one (gate × suspension) coordinate. Because creation and disposal remained under different locks and `_disposed` remained a per-domain bool, the next suspension point on the next path re-opened the same class with a different byte/site. Adding an Nth recheck cannot make "create" and "dispose" mutually exclusive when they are guarded by different primitives.
- **(ii) Single monotonic lifecycle authority + connection-create-under-the-teardown-gate + publish-or-surrender handoff (ACCEPTED).** Collapse liveness to ONE monotonic integer advanced only by `Interlocked.CompareExchange`; unify connection create and connection dispose under the SAME gate; and make any op suspended mid-creation **surrender** (dispose the just-created resource, never publish it) when it resumes to find the source no longer Live. A resurrected resource becomes **unrepresentable** rather than guarded per site. This adapts the proven `BrokeredMessageReceiver` precedent (`src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Receiving/BrokeredMessageReceiver.cs`): an explicit lifecycle state machine advanced only by CAS, a single `SemaphoreSlim(1,1)` serializing teardown, a monotonic terminal state written only under the gate, swallow-and-finalize, and "object reachable by teardown only after a single atomic handoff" ownership.

## Decision

Adopt option (ii):

1. **One monotonic lifecycle authority.** `_disposed` (bool) is replaced by `_lifecycle` (int): `Live (0) -> Disposing (1) -> Disposed (2)`. `DisposeAsync` advances `Live -> Disposing` via `Interlocked.CompareExchange` as the **single admission gate** (a CAS loser is an idempotent no-op), quiesces under the receive gate, then writes `Disposed` **monotonically under the gate**. `ThrowIfNotLive` throws `ObjectDisposedException` (the same observable type the old bool produced) for any non-Live state, so the external contract is unchanged.

2. **Connection create and dispose share one gate.** `_connectionInitGate` is **eliminated**. Connection creation (`EnsureConnectionAsync`) now runs only while `_receiveChannelGate` is held — the same gate that disposes the connection in `DisposeAsync`. Create and dispose are therefore **mutually exclusive by construction**.

3. **Publish-path lock ordering.** `AcquirePublishChannelAsync` takes a `_publishPoolGate` permit first, then reads-or-creates the connection through a helper that acquires `_receiveChannelGate` ONLY long enough to read-or-create the `_connection` object (lifecycle-checked under the gate) and **releases it before** the publish channel is created. Blocking publish channel I/O never runs while the receive gate is held — publishing never contends with the receive/ack gate, and there is no nested-gate deadlock (the receive gate is always acquired-then-released before the permit-held channel I/O).

4. **Publish-or-surrender handoff.** After the one blocking await inside connection creation resumes, the lifecycle is re-checked under the gate **before** `_connection` is assigned or `RecoverySucceededAsync` is subscribed: if not Live, the just-created connection is disposed and `ObjectDisposedException` thrown — it is never assigned, never subscribed. Symmetrically, after a publish channel is created, the lifecycle is re-checked: if not Live, the channel is disposed, the permit released, and `ObjectDisposedException` thrown. An op suspended mid-creation across a completing disposal **surrenders** rather than resurrecting.

## Closed-by-Construction Acceptance Test

> Which class of defect is made impossible, and why?

**"A resource (connection or publish channel) created or resurrected after disposal becomes reachable on a torn-down source."** It is closed by construction because (a) connection create and dispose are now under one gate, so they cannot interleave; (b) liveness is one monotonic authority every creating path consults, with no per-domain bool to read at an un-composed point; and (c) the publish-or-surrender handoff means the resume side of every creation re-checks that single authority before publishing the resource and disposes-then-throws if not Live. There is no remaining (gate × suspension) coordinate on which a created resource can escape onto a disposed source — so there is no next byte/site for the same-shaped defect to reappear.

## Consequences

- **ADR 0002's epoch lifecycle is PRESERVED and re-justified.** `TopologyRecoveryEnabled = false`, the gated atomic receive-channel recreate + epoch bump + consumer re-registration, and the publisher-confirm / epoch-guard settlement are **unchanged**. This collapse only narrows *when* the connection materializes (now strictly under the receive gate) — never *how* the channel epoch is bumped or how the consumer is re-registered. The recovery callback (`OnRecoverySucceededAsync`) keeps its pre-wait not-Live fast path and swallows `ObjectDisposedException`, so recovery firing after disposal stays a clean no-op out of the client's event dispatch.
- **Permit conservation is preserved.** `ReturnPublishChannel` still ALWAYS releases the permit (orphan-disposing the channel on a torn source), and `AcquirePublishChannelAsync` releases on every non-rental exit (not-Live recheck, create failure, surrender). No path double- or under-releases.
- **Gate lifetime unchanged.** The surviving two `SemaphoreSlim`s (`_receiveChannelGate`, `_publishPoolGate`) are left for GC, mirroring `BrokeredMessageReceiver._teardownGate`; `_connectionInitGate` is gone entirely.
- **Test seam.** A narrow `InternalsVisibleTo`-only connection-create override (`_createConnectionForTest`, default null) lets the mid-creation-vs-dispose race be exercised broker-free and deterministically. No public or DI surface changes; the `IRabbitMqConnectionSource` contract is unchanged.

Cross-references ADR 0002 (the epoch lifecycle this collapse preserves) and the `BrokeredMessageReceiver` lifecycle-state-machine precedent it adapts.

**RabbitMQ.Client 7.2.1 confirm-tracking dependency.** `AcquirePublishChannelAsync` creates publish channels with `publisherConfirmationsEnabled + publisherConfirmationTrackingEnabled`. In 7.2.1 this means an unroutable `mandatory:true` publish triggers a `basic.return`; the client correlates the return to the originating publish by publish-sequence-number (`HandleReturn → HandleNack(isReturn:true) → tcs.SetException(PublishException)`) and faults the awaited `BasicPublishAsync` — no `BasicReturnAsync` handler is needed. `RabbitMqSender.Dispatch` bare-awaits `BasicPublishAsync`, so an unroutable publish surfaces as a `Dispatch` failure with no silent loss. **Upgrade flag**: if `RabbitMQ.Client` is updated past 7.2.1, or if `CreatePublishChannelAsync`'s `CreateChannelOptions` are changed, the confirm-tracking fault-on-return guarantee must be re-verified; losing it silently converts an unroutable mandatory publish from a propagated fault into a dropped message.
