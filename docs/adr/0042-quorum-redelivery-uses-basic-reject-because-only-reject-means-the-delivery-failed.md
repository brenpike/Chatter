---
status: accepted
date: 2026-09-28
---

# Quorum redelivery uses basic.reject, because only reject means the delivery failed

When a handler fails on a quorum-queue delivery and the Receiver returns the message for another attempt,
`RabbitMqReceiver` now settles it with `basic.reject(requeue: true)` instead of `basic.nack(requeue: true)`. On
RabbitMQ 4.3 a quorum queue counts the two verbs differently: only `basic.reject` records a failed delivery attempt, so
under `basic.nack` the native `x-delivery-count` header is never emitted, Chatter's attempt count stays at 1, and a
poison message loops without end. The swap changes nothing on earlier brokers, where the two verbs run the same code.
This ADR records the cause, the options considered, the one chosen and what it leaves as it was.

Issue #533.

## Context

**ADR-0001's quorum strategy reads the broker's own count.** Under `QueueType.Quorum` the Receiver reads the native
`x-delivery-count` header and stamps `MessageContext.ReceiveAttempts` as that count + 1, flooring the first delivery at
attempt 1. The core deadletters once the attempt count reaches `maxReceiveAttempts`. ADR-0001 named the header; it never
named the verb that returns a failed delivery, and `NackMessageAsync` used `basic.nack(requeue: true)`.

**RabbitMQ 4.3 split the quorum-queue counter in two.** The 4.3 quorum-queue documentation, section *When is delivery
count incremented?* (https://www.rabbitmq.com/docs/quorum-queues; source
`versioned_docs/version-4.3/quorum-queues/index.md` in `rabbitmq/rabbitmq-website`), tabulates two counters.
`acquired-count` counts assignments to a consumer; `delivery-count` counts failed delivery attempts and is the counter
the queue's `delivery-limit` is evaluated against. For AMQP 0.9.1:

| Trigger | `acquired-count` | `delivery-count` |
| --- | --- | --- |
| `basic.nack` | incremented | not incremented |
| `basic.reject` | incremented | incremented |
| Client crash / connection loss | incremented | incremented |
| Intra-cluster network partition | incremented | not incremented |
| Consumer timeout | incremented | not incremented |

The same page states that since 4.3 the delivery count "will only be incremented for genuine failures such as
session/channel crashes or when using the modified outcome with delivery-failed=true or basic.reject in AMQP 0.9.1". It
introduces `x-acquired-count` as the header for the number of times a message has been assigned to a consumer, and
records that the quorum-queue `delivery-limit` defaults to 20 since RabbitMQ 4.0.

**The broker source shows why the verbs now differ.** In `deps/rabbit/src/rabbit_channel.erl` of `rabbitmq-server`:

- On `main` (4.3), the `basic.nack` handler settles with `Op = requeue`, and the `basic.reject` handler settles with
  `settle(true, false)`, which is `{modify, DeliveryFailed = true, ...}`. Only the second is a failed delivery.
- On `v3.13.x`, `v4.0.x` and `v4.2.x`, both handlers call the identical `reject(DeliveryTag, Requeue, Multiple, State)`.
  `basic.reject` hardcodes `Multiple = false`, and Chatter already passes `multiple: false` to `basic.nack`. Before 4.3
  the two verbs are the same operation.

In `deps/rabbit/src/rabbit_fifo_client.erl` on `main`, `add_delivery_count_header` emits `x-delivery-count` only once a
delivery count exists. Under a nack loop it never exists, so the header is absent on every redelivery, the Receiver
reads no count, and the attempt count stays at 1. That is the #533 mechanism. The same function back-fills the
counters for messages carrying pre-4.3 headers, so a message in flight across an upgrade keeps its count.

**The integration suite reproduced it.** `RabbitMqDeliveryCountingOn43Tests`, on the `RabbitMq43Collection` against a
`rabbitmq:4.3-management` container, drove a quorum queue whose handler always throws. Under `basic.nack` every
redelivery carried `x-acquired-count` values 1, 2, 3 and so on and no `x-delivery-count`; the poison message looped
about 4,200 times in 20 seconds, and the broker's default `delivery-limit` never fired. A classic queue on the same
broker was unaffected, because the classic strategy counts with its own republished `x-chatter-delivery-count` header
and never nacks.

## Considered Options

### Option A — settle a failed quorum delivery with `basic.reject(requeue: true)` (ACCEPTED)

Recorded under *Decision*. One verb is correct on every broker version. It says what happened: the Receiver returns the
message because its handler failed. It re-arms the broker's `delivery-limit` as a safety net, and it changes no public
API, configuration, ordering or throughput.

### Option B — read `x-acquired-count` as the attempt count (REJECTED)

`x-acquired-count` counts assignments, not failures. A consumer timeout or an intra-cluster partition increments it
without any handler failing, so each would spend a healthy message's retry budget. It leaves the broker's
`delivery-limit` unarmed, because the Receiver would still settle with a verb the broker does not count as a failure. And
the header does not exist before 4.3, so the Receiver would have to branch on its presence.

### Option C — apply ADR-0001's classic republish counter to quorum queues (REJECTED)

It carries every cost ADR-0001 records for the classic strategy: the republish and the ack are not atomic, so a crash
between them leaves a duplicate; the republished message goes to the tail of the queue and loses its position; and each
attempt doubles the traffic. On a quorum queue each attempt also appends a Raft log entry, and the republished copy is a
new message whose broker delivery count starts again from zero, so the queue's `delivery-limit` could never fire.

### Option D — probe the broker version, or branch on which header is present (REJECTED)

Option A behaves the same on every version, so a branch buys nothing and adds a path that only some brokers exercise.

## Decision

**`RabbitMqReceiver.NackMessageAsync` settles a failed quorum-queue delivery with `basic.reject(requeue: true)`.** On
RabbitMQ 4.3 and later that increments the quorum queue's delivery count, so the broker emits `x-delivery-count` on the
redelivery and the Receiver's attempt count climbs. Before 4.3 the broker runs the same code for `basic.reject` as for
`basic.nack`, so nothing changes there and no version detection is needed.

**`x-delivery-count` remains the only attempt source on a quorum queue.** The ADR-0001 quorum strategy is unchanged.
`x-acquired-count` is adapter-owned receive state like `x-delivery-count`: it is stripped from outbound headers so it
never leaks onto a message the application sends on, and it is deliberately not read as an attempt count (Option B).

**The classic strategy and `TransactionMode.None` are untouched.** A classic queue still republishes with
`x-chatter-delivery-count` and acks the original. Under `TransactionMode.None` the consumer auto-acks, so there is no
delivery to settle.

This ADR refines ADR-0001 by naming the settlement verb its quorum strategy depends on. It does not supersede it.

## Consequences

- **A poison message on a RabbitMQ 4.3 quorum queue is deadlettered again.** The attempt count reaches
  `maxReceiveAttempts`, and the Receiver deadletters the message: to the Dead-letter Queue when one is configured, and
  otherwise to the Error Queue.
- **Nothing changes before 4.3, on classic queues, or under `TransactionMode.None`.**
- **The broker's `delivery-limit` advances again on 4.3.** Keep `maxReceiveAttempts` below the queue's `delivery-limit`
  (default 20). At or above it, the broker can drop the message, or dead-letter it through the queue's DLX when one is
  configured, before the Receiver reaches `maxReceiveAttempts`. The adapter provisions no topology and cannot read the
  effective limit without the management API or a non-passive declare, so this is documented rather than guarded. The
  same exposure already existed on RabbitMQ 4.0 to 4.2, where `basic.nack` still advanced the delivery count.
- **CI pins both halves.** A focused `rabbitmq:4.3-management` collection (`RabbitMq43Collection`) pins the 4.3
  semantics. `rabbitmq:3.13-management` remains the broad-surface fixture (`RabbitMqFixture`) and pins the pre-4.3 half.

## References

- Issue #533 — quorum-queue attempt counting on RabbitMQ 4.3. The defect this ADR resolves.
- ADR-0001 — *RabbitMQ classic-queue redelivery counting via header-stamped republish*. The quorum strategy this ADR
  refines, and the classic republish costs that rule out Option C.
- RabbitMQ documentation, *Quorum Queues*, section *When is delivery count incremented?* —
  https://www.rabbitmq.com/docs/quorum-queues (source `versioned_docs/version-4.3/quorum-queues/index.md` in
  `rabbitmq/rabbitmq-website`).
- `rabbitmq-server` `deps/rabbit/src/rabbit_channel.erl` — the `basic.nack` and `basic.reject` handlers on `main`, and
  on `v3.13.x`, `v4.0.x` and `v4.2.x`.
- `rabbitmq-server` `deps/rabbit/src/rabbit_fifo_client.erl` — `add_delivery_count_header` on `main`.
- `src/Chatter.MessageBrokers.RabbitMQ/src/Chatter.MessageBrokers.RabbitMQ/Receiving/RabbitMqReceiver.cs` —
  `NackMessageAsync`, the settlement this ADR changes.
- `src/Chatter.MessageBrokers.RabbitMQ/tests/Receiving/UsingRabbitMqReceiver/WhenSettlingMessage.cs` — the settlement
  unit oracles.
- `src/Chatter.MessageBrokers.RabbitMQ/tests/Integration/RabbitMqDeliveryCountingOn43Tests.cs` — the RabbitMQ 4.3
  reproduction and its facts.
- `src/Chatter.MessageBrokers.RabbitMQ/tests/Integration/RabbitMqNackRedeliveryTests.cs` — quorum and classic
  redelivery on the 3.13 fixture.
