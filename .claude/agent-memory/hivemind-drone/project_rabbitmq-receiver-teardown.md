---
name: rabbitmq-receiver-teardown
description: RabbitMQ receiver teardown lifecycle — terminal surgical consumer-cancel + receive-channel dispose under the source gate (ADR-0005)
metadata:
  type: project
---

ADR-0005 receiver teardown (closes PR #194 r3407966808). Pre-fix StopReceiver/Dispose/DisposeAsync only did `_buffer.Writer.TryComplete()` — consumer never cancelled, receive channel never closed, deliveries pushed into completed buffer, prefetched-unacked stranded.

**Why:** USER-LOCKED: teardown is TERMINAL (mirror core BrokeredMessageReceiver.StopReceiver — one-way, no restart).

**How to apply (the contract now landed):**
- Consumer-tag ownership moved INTO the source. The registration delegate signature is now `Func<IChannel, long, CancellationToken, Task<string>>` (returns the broker tag). Source stores latest tag in `_consumerTag` on every (re)creation. This is an adapter-seam change, NOT a core contract change.
- New `StopReceivingAsync(CancellationToken)` on `IRabbitMqConnectionSource`. Under `_receiveChannelGate` (routed via `RunReceiveGatedAsync` like every gated op): `BasicCancelAsync` the tag (swallow AlreadyClosedException/ObjectDisposedException), dispose receive channel, CLEAR `_registerConsumer` + `_consumerTag` so a late recovery re-registers nothing. SURGICAL: does NOT touch `_connection`, publish pool, gates (GATE LIFETIME), or `_lifecycle` (stays Live so sender keeps publishing — singleton shared with sender). Idempotent; stop-after-dispose swallows ODE.
- Receiver `StopReceiver`: call source `StopReceivingAsync` FIRST, then `_buffer.Writer.TryComplete()`. `Dispose`/`DisposeAsync` ESCALATE to source full Dispose/DisposeAsync + buffer complete. Sync `Dispose()` reaches source sync teardown via `if (_connectionSource is IDisposable d) d.Dispose()` — the seam is IAsyncDisposable only.
- Prefetched-unacked left for broker redelivery (consistent with ADR-0002 epoch guard no-op-after-teardown).
- Deadlock-free: gated body acquires no nested gate, never waits on publish pool.

**Test seams added:** `RecordingChannel.BasicCancelAsync` now records `CancelledConsumerTags` (was NotImplementedException). `InMemoryRabbitMqConnectionSource` got `StopReceivingAsync` + `ReceivingStopped` flag + null-guards in `SimulateRecoveryAsync`/`PushDeliveryAsync` for post-stop (channel null). Tag `BasicConsumeAsync` returns "in-memory-consumer-tag". Broker-free tests: tests/Receiving/UsingRabbitMqReceiver/WhenStoppingReceiver.cs. Integration: tests/Integration/RabbitMqReceiverTeardownTests.cs (source-level, drives production source directly; unacked-requeue proven via BasicGet drain on a fresh channel from same connection).

198 RabbitMQ unit tests green x net8.0+net10.0. Supersedes the teardown portions of [[rabbitmq-dispose-coordination]] and [[rabbitmq-lifecycle-authority]] (those covered DISPOSAL; this adds the surgical STOP path).
