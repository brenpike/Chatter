---
name: rabbitmq-receiver-core
description: Non-obvious constraints baked into the RabbitMQ receiver core (STEP-004) — settle delegate type, header parsing, epoch no-op
metadata:
  type: project
---

RabbitMQ adapter receiver core (`RabbitMqReceiver`, STEP-004 of the rabbitmq-adapter initiative).

**Why:** RabbitMQ.Client 7.2.1 async API has sharp edges the core port and SSB analog don't telegraph.
**How to apply:** when extending the RabbitMQ receiver (STEP-005 tests, STEP-006 DI) or debugging settlement.

- `IChannel.BasicAckAsync` / `BasicNackAsync` / `BasicQosAsync` return **`ValueTask`**, not `Task`. A settle-delegate seam must be `Func<IChannel, ValueTask>`. `BasicConsumeAsync` returns `Task<string>` (consumer tag), `BasicPublishAsync<TProperties>` returns `ValueTask`.
- `BasicDeliverEventArgs` exposes `DeliveryTag/Exchange/RoutingKey/Redelivered/Body/ConsumerTag/BasicProperties` as **public fields** (not properties); `CancellationToken` is the only property (from `AsyncEventArgs` base). Body is `ReadOnlyMemory<byte>` — call `.ToArray()` to detach before buffering.
- AMQP header values are weakly typed: string headers arrive as `byte[]`, numeric headers (`x-delivery-count`, `x-chatter-delivery-count`) as boxed int/long/short. Parse tolerantly via a switch over numeric boxes + `byte[]`/`string` UTF8 fallback. See `ReadHeaderAsLong`.
- Quorum delivery count = native `x-delivery-count + 1`; Classic = `x-chatter-delivery-count` value (0 absent), advanced on the republish path. Strategy keyed off `RabbitMqOptions.QueueType`.
- `MessageContext.ReceiveAttempts` MUST be stamped as `int` on every message — core's default `MessageDeliveryCountAsync` casts `(int)...[ReceiveAttempts]` unguarded. SSB does NOT override `MessageDeliveryCountAsync`; the RabbitMQ receiver matches (uses the default). See [[receiver-startup-signal-placement]].
- Epoch guard: settlement runs under `IRabbitMqConnectionSource.RunOnReceiveChannelAsync`; if `received.ChannelEpoch != currentEpoch` the settle is a **no-op returning false** (broker redelivers). Classic nack + all deadletter go through `RepublishThenAckAsync` — confirmed publish on a pooled channel BEFORE the epoch-guarded ack of the original.
- `ReceivedMessage` is carried on `MessageBrokerContext.Container` (`Include`/`TryGet`), same pattern as SSB's `ReceivedMessage`.
- Predicate providers (`RabbitMqRetry/CircuitBreakerExceptionPredicatesProvider`) are `internal sealed`, implement `IRetry/ICircuitBreakerExceptionPredicatesProvider`, `yield return` a list of `Predicate<Exception>` matching transient AMQP types: `BrokerUnreachableException, AlreadyClosedException, OperationInterruptedException, ConnectFailureException, SocketException, IOException`. Mirrors SSB shape (which used `#if NET5_0_OR_GREATER` for `SqlException.IsTransient` — no analog needed here).
