---
status: accepted
date: 2026-06-12
---

# RabbitMQ receive-channel epoch lifecycle via source-owned channel + consumer re-registration

The RabbitMQ adapter settles deliveries (ack/nack/deadletter-ack) on a single serialized receive channel. A delivery tag is valid **only** on the exact channel that delivered it, so after automatic recovery replaces the receive channel an old tag is meaningless on the new one — acking it would error or, worse, **falsely acknowledge an unrelated delivery** that happens to share the tag value. The adapter guards this with a **channel-epoch** stamped onto every delivery and re-checked under the gate at settle time: a settle whose carried epoch != the current channel epoch is a no-op. The open question this ADR settles is **how the epoch is kept truthful across recovery** so that the guard closes the failure as a class rather than trading one edge for another.

The guard is correct only if a single invariant holds:

> INVARIANT: a delivery's stamped epoch always equals the epoch of the session that delivered it.

When that holds, a **pre-recovery** in-flight delivery carries the old epoch and its settle correctly no-ops (the broker redelivers it — never false-acked, never lost), while a **post-recovery** delivery carries the new epoch and its settle correctly succeeds (no duplicate loop). Both edges are closed by the same invariant.

## Considered Options

- **(i) Bump-only on recovery (epoch advances, client keeps the consumer)** — rejected. Topology auto-recovery stays on, so the client silently re-binds the **old** consumer closure, which still stamps the **pre-bump** epoch. Post-recovery deliveries therefore carry a stale epoch and their settles no-op forever, so the redelivered message is redelivered again and again — a **post-recovery duplicate loop** (EDGE 2). This fixes the pre-recovery false-ack (EDGE 1) but introduces an unbounded redelivery loop in its place.
- **(ii) Live per-delivery epoch read (consumer reads the current epoch at delivery time)** — rejected. The epoch read and the recovery bump are separate events, so a delivery handled around the recovery boundary can read the epoch **before** the bump and the channel be replaced **after** — a read/stamp **race** against recovery. The invariant becomes probabilistic rather than guaranteed, and the failure is timing-dependent and unreproducible.
- **(iii) Recreate channel and re-register consumer under the gate, co-creating the epoch (ACCEPTED)** — the source owns the receive-channel and consumer lifecycle. Topology recovery is **disabled**; on every (re)creation the source recreates the channel, bumps the epoch, and re-runs the stored consume-registration delegate against the fresh channel with the freshly-bumped epoch — **as one atomic gated event**. The consume registration is the only code that stamps an epoch, and it always runs after the bump with the new epoch, so the invariant holds **by construction** with no separate sampling point to race.

## Decision

Adopt option (iii). The connection runs with **`AutomaticRecoveryEnabled = true`** (transport reconnects on its own) but **`TopologyRecoveryEnabled = false`** for the receive channel. The source subscribes to `RecoverySucceededAsync`; on every receive-channel (re)creation — cold start, lazy recreate, and each successful recovery — it, **under the receive gate and as one atomic event**, disposes any old channel, creates a fresh one, **increments the epoch**, and **re-runs the stored consume-registration delegate** against the new channel with the freshly-bumped epoch.

Because the **epoch bump and the consumer re-registration are the same gated event**:

- a **pre-recovery** in-flight delivery carries the old epoch → its settle no-ops, the broker redelivers it (EDGE 1 closed: no false-ack);
- a **post-recovery** delivery is stamped by the freshly re-registered consumer with the new epoch → its settle matches and the message is actually acked (EDGE 2 closed: no duplicate loop).

The bounded delivery buffer is created once and is **not** recreated on re-registration, so deliveries buffered before recovery survive the consumer swap.

## Consequences

- The invariant holds **by construction**, race-free: there is no separate epoch sampling point to race against the bump, so neither the pre-recovery false-ack (EDGE 1) nor the post-recovery duplicate loop (EDGE 2) is reachable — they are closed **as a class**, not patched as individual byte/path instances.
- **Loss of the client's consumer auto-recovery for the receive channel is required, not a cost.** Disabling topology recovery is precisely what lets the source — rather than the client — re-register the consumer under the freshly-bumped epoch; client-driven re-binding would resurrect the stale-epoch closure (option i). Connection/channel **transport** recovery stays enabled, and **publish channels keep ordinary connection recovery** (publisher confirms are unaffected).
- ADR 0001's **republish-before-ack** ordering still holds: for a genuinely pre-recovery delivery the settle no-ops and the broker redelivers, so the deadletter/classic republish path's "confirmed publish lands before the original is acked" guarantee is unchanged. The rare-duplicate trade-off documented there is absorbed by the Inbox exactly as before.
- The recovery callback is guarded against firing after disposal (a `_disposed` re-check under the gate plus an `ObjectDisposedException` guard on the gate wait), so a late recovery event dispatched during teardown is dropped rather than resurrecting resources past disposal.
