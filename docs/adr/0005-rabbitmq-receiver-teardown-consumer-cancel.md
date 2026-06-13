---
status: accepted
date: 2026-06-13
---

# RabbitMQ receiver teardown: terminal, surgical consumer-cancel + receive-channel dispose

`RabbitMqReceiver.StopReceiver`/`Dispose`/`DisposeAsync` previously ONLY called `_buffer.Writer.TryComplete()`. The AMQP consumer was never cancelled (`BasicCancelAsync` on the consumer tag was never called) and the receive channel was never closed, so the `RabbitMqConnectionSource` kept the channel and its registered consumer alive: deliveries could keep being pushed into a completed buffer (`WriteAsync` throws on a completed channel) and prefetched-but-unacked deliveries were stranded on a channel nobody would settle. This ADR records the teardown lifecycle that closes that defect (PR #194 review finding r3407966808).

## Context

The source OWNS the receive-channel + consumer lifecycle (ADR-0002 epoch lifecycle, ADR-0003 single monotonic lifecycle authority). The receiver only buffers deliveries and settles them under the source's receive gate. So tearing down RECEIVING must be done where the channel and consumer live — in the source, under the same `_receiveChannelGate` that owns channel (re)creation and the recovery recreate.

Two constraints shape the fix:

- **Shared singleton.** `IRabbitMqConnectionSource` is a process singleton shared with the sender (ADR-0003; `Extensions.AddRabbitMq` registers it `Singleton` while the receiver/sender are `Scoped`). The sender keeps publishing after the receiver stops, so stopping receiving must NOT dispose the `IConnection` or the publish pool.
- **Terminal stop (USER-LOCKED).** Stopping is one-way and not restartable, mirroring the core `BrokeredMessageReceiver.StopReceiver` precedent (`src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Receiving/BrokeredMessageReceiver.cs`): there is no resume-after-stop.

## Decision

1. **Consumer-tag ownership moves into the source.** The consume-registration delegate (`StartReceivingAsync`) now RETURNS the broker-assigned consumer tag (`Func<IChannel, long, CancellationToken, Task<string>>`). The source stores the latest returned tag in `_consumerTag` on every (re)creation (cold start, lazy recreate, recovery), so the source — not the receiver — can cancel the consumer that is actually live on the current channel. This is an adapter-seam change only; no core `Chatter.MessageBrokers` contract changes.

2. **New surgical `StopReceivingAsync(CancellationToken)` on the source seam.** Under `_receiveChannelGate` (the SAME gate that owns channel (re)creation and the recovery recreate, so cancel-and-teardown is mutually exclusive with them BY CONSTRUCTION), as one atomic event:
   - `BasicCancelAsync` the stored consumer tag on the current receive channel, guarded against `AlreadyClosedException`/`ObjectDisposedException` (a dropped channel has already implicitly cancelled the consumer — mirrors the recovery-callback swallow);
   - dispose the receive channel and null it;
   - CLEAR `_registerConsumer` and `_consumerTag`, so a late `OnRecoverySucceededAsync` that wins the gate after the stop recreates a channel but re-registers NOTHING (the terminal, one-way semantics).

   `StopReceivingAsync` DELIBERATELY does NOT touch `_connection`, the publish pool, the gates (GATE LIFETIME — left for GC per ADR-0003), or `_lifecycle` (the source stays `Live` so the sender's publish path keeps working). Idempotent: a double-stop finds a null channel + cleared delegate and no-ops; a stop after `DisposeAsync` observes not-`Live` and swallows the `ObjectDisposedException` as a clean no-op.

3. **Receiver ordering.** `RabbitMqReceiver.StopReceiver` calls `_connectionSource.StopReceivingAsync(...)` FIRST (cancel the consumer so no new delivery races the buffer completion), THEN `_buffer.Writer.TryComplete()` (so the blocking `ReceiveMessageAsync` pull drains and unblocks). `Dispose`/`DisposeAsync` ESCALATE to the source's full teardown (`Dispose()`/`DisposeAsync()`, connection + publish pool) then complete the buffer; the source's single-admission lifecycle CAS makes the DI container's own later disposal of the singleton a clean no-op.

4. **Prefetched-unacked contract.** Prefetched-but-unacked deliveries are NOT acked on stop — they are left for broker redelivery. This is consistent with the ADR-0002 epoch guard, which already no-ops a settle after the channel is torn down: cancelling the consumer / disposing the channel requeues the broker's unacked deliveries, so the message remains on the queue rather than being false-acked or stranded.

## Deadlock-freedom

`StopReceivingAsync` routes its gate acquisition through `RunReceiveGatedAsync` like every other gated entrypoint and, inside the gated body, performs only AMQP I/O on the current receive channel plus field writes — it acquires NO nested gate and never waits on the publish pool. So a concurrent settle/recovery either runs to completion before the stop acquires the gate, or observes the stopped condition (disposed channel + cleared `_registerConsumer`) after; the stop cannot self-deadlock or deadlock against an in-flight gated op.

## Closed-by-Construction Acceptance Test

> Which class of defect is made impossible, and why?

The "deliveries pushed into a completed buffer / unacked deliveries stranded on a kept-alive channel after stop" class is made impossible: the consumer is CANCELLED and the receive channel DISPOSED under the same gate that owns channel (re)creation, so after a stop there is no live consumer to push deliveries and no channel to strand them on. The "recovery re-registers a consumer after a terminal stop" class is made impossible by clearing `_registerConsumer` under the gate — the recovery recreate's null guard then re-registers nothing. The shared-singleton "sender breaks when receiver stops" class is avoided by NOT touching the connection/publish pool/lifecycle on the surgical stop path.

## References

- ADR-0002 (RabbitMQ receive-channel epoch lifecycle) — the epoch guard that makes the prefetched-unacked redelivery contract sound.
- ADR-0003 (RabbitMqConnectionSource single monotonic lifecycle authority) — the gate the surgical teardown reuses, the GATE LIFETIME rule, and the singleton ownership the surgical stop preserves.
- Core `BrokeredMessageReceiver.StopReceiver` — the terminal, one-way stop precedent this mirrors.
