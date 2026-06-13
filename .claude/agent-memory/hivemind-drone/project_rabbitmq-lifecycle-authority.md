---
name: rabbitmq-lifecycle-authority
description: RabbitMqConnectionSource lifecycle-collapse (ADR 0003) — single monotonic _lifecycle authority, connection create+dispose share receive gate, publish-or-surrender; test-seam deadlock hazard
metadata:
  type: project
---

ADR 0003 collapsed `RabbitMqConnectionSource` to ONE monotonic lifecycle authority, superseding [[rabbitmq-dispose-coordination]] (which patched per-site `_disposed` rechecks). This is the closed-by-construction fix for the connection-lifecycle-disposal root cluster.

**Why:** `_connection` was CREATED under a dedicated `_connectionInitGate` but DISPOSED under `_receiveChannelGate` (different locks), and `_disposed` was a bool each domain read independently — so disposal could never exclude connection-creation. Three prior structural fixes each closed one (gate × await-suspension) coordinate; the cross-product was whack-a-mole.

**How to apply (the shape now):**
- `_lifecycle` int (Live=0 → Disposing=1 → Disposed=2), replaces `bool _disposed`. `ThrowIfNotLive()`/`IsTorn` use `Volatile.Read`; `ThrowIfNotLive` still throws `ObjectDisposedException` (observable contract unchanged so existing tests pass).
- `DisposeAsync` admits via `Interlocked.CompareExchange(Live→Disposing)` as the SINGLE gate (loser returns, idempotent); writes Disposed monotonically under the receive gate after quiesce.
- `_connectionInitGate` is GONE. `EnsureConnectionAsync` (connection CREATE) runs only under `_receiveChannelGate` — same gate as DISPOSE → mutually exclusive by construction.
- Publish-path lock ordering: permit first → `AcquireConnectionUnderReceiveGateAsync` takes the receive gate ONLY to read-or-create `_connection` then RELEASES it → publish channel `CreateChannelAsync` runs OUTSIDE the gate. NEVER hold the receive gate across publish I/O.
- Publish-or-surrender: after a resource-create step resumes, re-check `IsTorn` BEFORE assigning `_connection`/subscribing recovery/returning a rental → if torn, dispose the just-created resource and throw. Resurrection-past-dispose is unrepresentable.

**Test-seam DEADLOCK HAZARD (cost me a hung-testhost cycle):** the authorized internal seam is `Func<CancellationToken, Task<IConnection>> _createConnectionForTest` (REPLACES the real factory call — the real `factory.CreateConnectionAsync` can't connect broker-free, so a "delay after CreateConnectionAsync" hook is unusable). The connection-create step runs UNDER the receive gate. So a test that suspends mid-creation MUST NOT `await source.DisposeAsync()` to completion while the op holds the gate — DisposeAsync queues on that gate and deadlocks. Instead: start `disposeTask` (its admission CAS to Disposing runs sync before its first await, so the source is immediately torn), release the seam, THEN await both. The surrender re-check observes Disposing (CAS), not Disposed — that's sufficient.

**Verify discipline:** run new wait/race tests with `--blame-hang-timeout`; stale `testhost` processes lingering across runs = a hung test. RabbitMQ non-integration count is 130 (net8.0+net10.0) after this work.
