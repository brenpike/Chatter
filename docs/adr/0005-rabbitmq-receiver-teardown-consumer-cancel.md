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

   **Amended 2026-09-16 (mechanism, not decision):** the three steps above now run in a different ORDER and under a different guard. The decision itself — a gate-serialized, surgical, terminal stop that touches neither the connection nor the publish pool — is unchanged. The gated body now COMMITS THE TERMINAL STATE FIRST: it copies `_receiveChannel` and `_consumerTag` into locals, nulls `_receiveChannel`, `_registerConsumer` and `_consumerTag`, early-returns on a null channel, and only THEN performs a best-effort `BasicCancelAsync` followed by the channel dispose. Nothing between the local copies and the last clear can throw, so the commit is unconditional. The stop is also TOTAL: the exception type is NEVER consulted, so `AlreadyClosedException`/`ObjectDisposedException` are no longer the guard — they survive only as the EXEMPLARS the comment names. That is sound because the only I/O left is performed on a channel abandoned one line earlier, and disposing the channel cancels the consumer server-side whether or not the explicit cancel completed; `BasicCancelAsync` waits for cancel-ok, so `TimeoutException`, `OperationInterruptedException` and `IOException` are reachable too, and enumerating them would only move the omission rather than close it. Relinquish-first rather than a plain `try { cancel } finally { dispose; clear }`: a `finally` is STRICTLY WEAKER, because the `finally` body itself awaits `DisposeAsync()`, which can throw and would then supersede the very commit it was meant to guarantee. The intermediate state is unobservable — the whole body runs under `_receiveChannelGate` and no reader of these fields exists outside it. The outer stop-after-dispose `ObjectDisposedException` no-op is unchanged and stays narrow. Two BOUNDS of that soundness argument are recorded rather than closed. First, the server-side cancellation it leans on is the DISPOSE's doing, so it does not hold when the dispose ALSO faults: the channel can then survive on the broker with its consumer live until the connection is disposed or drops, and the deliveries it keeps pushing fault the receiver's already-completed buffer writer. Second, the source has NO LOGGER, so the swallow is UNOBSERVABLE. Both residuals are ACCEPTED: the container created the singleton, so root-provider disposal tears the connection down and reclaims the channel with it; propagating out of a teardown is strictly worse than leaking a channel a connection close will reclaim; and adding a logger is a constructor change deliberately scoped out of this change.

3. **Receiver ordering.** `RabbitMqReceiver.StopReceiver` calls `_connectionSource.StopReceivingAsync(...)` FIRST (cancel the consumer so no new delivery races the buffer completion), THEN `_buffer.Writer.TryComplete()` (so the blocking `ReceiveMessageAsync` pull drains and unblocks). `Dispose`/`DisposeAsync` ESCALATE to the source's full teardown (`Dispose()`/`DisposeAsync()`, connection + publish pool) then complete the buffer; the source's single-admission lifecycle CAS makes the DI container's own later disposal of the singleton a clean no-op.

   **Superseded in part by ADR-0019 (2026-09-16):** dispose no longer escalates to the connection source's teardown — `Dispose`/`DisposeAsync` now run the same surgical `StopReceivingAsync` this section's `StopReceiver` ordering describes, and the container that created the singleton source disposes it.

   **Amended 2026-09-16:** the three teardown paths now converge on ONE private `StopReceivingAndCompleteBufferAsync()` — the source stop in a `try`, `_buffer?.Writer.TryComplete()` in a `finally`, and deliberately NO `catch`. The stop-then-complete ORDERING described above is unchanged; the `finally` is what is new. `IRabbitMqConnectionSource` is a PUBLIC seam, so a consumer-supplied source may fault its stop for any reason: that fault still surfaces to the caller, but it can no longer skip the buffer completion, leave `ReceiveMessageAsync` parked on an empty buffer and hang host shutdown. Three copies of the two statements would have made "every teardown path remembers its finally" a convention; one helper makes it structural. A `BufferDeliveryAsync` write ALREADY PARKED on a full bounded buffer is still not quiesced by this sequence — that gap is tracked as issue #494 and remains OPEN; the ordering must not be reversed to chase it.

4. **Prefetched-unacked contract.** Prefetched-but-unacked deliveries are NOT acked on stop — they are left for broker redelivery. This is consistent with the ADR-0002 epoch guard, which already no-ops a settle after the channel is torn down: cancelling the consumer / disposing the channel requeues the broker's unacked deliveries, so the message remains on the queue rather than being false-acked or stranded.

## Deadlock-freedom

`StopReceivingAsync` routes its gate acquisition through `RunReceiveGatedAsync` like every other gated entrypoint and, inside the gated body, performs only AMQP I/O on the current receive channel plus field writes — it acquires NO nested gate and never waits on the publish pool. So a concurrent settle/recovery either runs to completion before the stop acquires the gate, or observes the stopped condition (disposed channel + cleared `_registerConsumer`) after; the stop cannot self-deadlock or deadlock against an in-flight gated op.

## Closed-by-Construction Acceptance Test

> Which class of defect is made impossible, and why?

The "deliveries pushed into a completed buffer / unacked deliveries stranded on a kept-alive channel after stop" class is made impossible: the consumer is CANCELLED and the receive channel DISPOSED under the same gate that owns channel (re)creation, so after a stop there is no live consumer to push deliveries and no channel to strand them on. The "recovery re-registers a consumer after a terminal stop" class is made impossible by clearing `_registerConsumer` under the gate — the recovery recreate's null guard then re-registers nothing. The shared-singleton "sender breaks when receiver stops" class is avoided by NOT touching the connection/publish pool/lifecycle on the surgical stop path.

The "a receive-side stop that runs but does not commit terminal state" class is made impossible (2026-09-16): the terminal state is committed BEFORE the first call that can throw, and the only work that follows is best-effort on an already-abandoned channel under one bare guard, so no broker fault, timeout or cancellation can leave the source still holding a receive channel or still able to re-register a consumer. Relatedly, "a teardown path that faults before completing the delivery buffer and strands the parked reader" is made impossible by the single helper's `finally`, which every one of the receiver's three teardown paths now runs.

## References

- ADR-0002 (RabbitMQ receive-channel epoch lifecycle) — the epoch guard that makes the prefetched-unacked redelivery contract sound.
- ADR-0003 (RabbitMqConnectionSource single monotonic lifecycle authority) — the gate the surgical teardown reuses, the GATE LIFETIME rule, and the singleton ownership the surgical stop preserves.
- Core `BrokeredMessageReceiver.StopReceiver` — the terminal, one-way stop precedent this mirrors.
