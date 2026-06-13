---
name: rabbitmq-dispose-coordination
description: RabbitMqConnectionSource disposed-observed-on-both-sides primitive; supersedes incomplete c09ba72 dispose fix
metadata:
  type: project
---

`RabbitMqConnectionSource` routes EVERY receive-gate acquisition through one helper `RunReceiveGatedAsync` (generic + Task overload) that checks `_disposed` BEFORE the wait AND re-checks UNDER the gate AFTER the wait. No raw `_receiveChannelGate.WaitAsync` survives outside it. Publish-permit path (`AcquirePublishChannelAsync`) re-checks `_disposed` under the acquired permit and releases-then-throws if disposed.

**Why:** the earlier fix (c09ba72) only added the post-acquire disposed recheck to `OnRecoverySucceededAsync`, leaving `RunOnReceiveChannelAsync`/`StartReceivingAsync`/`AcquirePublishChannelAsync` disposed-blind on the post-wait side. Defects: (1) a receive op queued behind `DisposeAsync` could resurrect a channel/connection or overwrite `_registerConsumer` on a disposed singleton; (2) `ReturnPublishChannel` SKIPPED `_publishPoolGate.Release()` when `_disposed`, so a publish acquire stranded behind a saturated pool HUNG FOREVER on dispose. Root: `_disposed` not observed-under-sync at every gated/permit entrypoint.

**How to apply:**
- `ReturnPublishChannel` now ALWAYS releases the permit (dispose-or-closed → dispose channel, don't re-pool, but still Release). A rental from `AcquirePublishChannelAsync` == exactly one taken permit, so always-release is balanced IN PRODUCTION. The broker-free `WhenDisposing` tests construct rentals out-of-band, so they must reflectively `_publishPoolGate.Wait()` FIRST or the unconditional Release overflows the capacity-1 semaphore (SemaphoreFullException).
- `OnRecoverySucceededAsync` MUST stay no-op-on-disposed (it runs in the client's async event dispatch). It routes through `RunReceiveGatedAsync` but SWALLOWS the `ObjectDisposedException` the helper throws on the post-wait recheck; keeps its pre-wait `if (_disposed) return;` fast path.
- The blocked-publish-acquire-wakes-on-dispose test MUST be timeout-bounded (`Task.WhenAny(acquire, Task.Delay(...))`) so a regression FAILS FAST instead of hanging the suite.
- GATE-LIFETIME decision unchanged: the three SemaphoreSlims are never disposed, left for GC. See [[rabbitmq-recovery-epoch]] for the epoch lifecycle this wraps (untouched).
