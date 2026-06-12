---
status: accepted
date: 2026-06-12
---

# RabbitMQ classic-queue redelivery counting via header-stamped republish

Chatter's **Max Receives Exceeded** concept requires counting how many times a brokered message has been delivered, so a poison message can be routed to the Error/Dead-letter queue once a configured limit is reached. RabbitMQ's only native per-delivery signal on classic queues is a boolean `redelivered` flag ("seen before, maybe") — it does not count, and nack-requeue increments nothing, so a poison message would loop forever and never trip Max Receives Exceeded. We adopt a config-selected delivery-count strategy: **Quorum** (default, recommended) reads RabbitMQ's native `x-delivery-count` header; **Classic** uses a header-stamped republish counter. Both stamp `MessageContext.ReceiveAttempts`, which the core's default `MessageDeliveryCountAsync` reads.

## Considered Options

- **Native `nack(requeue: true)` on classic queues** — rejected: classic queues expose no per-delivery counter, so requeue cannot count and a poison message loops indefinitely.
- **In-memory attempt dictionary keyed by message-id** — rejected: the count resets across reconnect and is not shared across replicas, so it is incorrect under horizontal scaling.
- **Mandating quorum queues only** — rejected: users who cannot adopt quorum queues still need classic support.

## Decision

A `QueueType` option selects the strategy (default `Quorum`).

- **Quorum strategy** reads the native `x-delivery-count` header, which RabbitMQ increments per redelivery.
- **Classic strategy** uses a header-stamped republish counter: on retry the adapter republishes the message to its own queue with a custom `x-chatter-delivery-count` header incremented by 1, then acks the original. The count lives in the message itself, so it survives reconnect, redelivery, and multi-replica horizontal scaling.

Both strategies stamp `MessageContext.ReceiveAttempts`. The core casts this value unguarded in its default `MessageDeliveryCountAsync`, so stamping it is mandatory, not optional.

## Consequences

- The classic republish is **not atomic** with the ack: a crash between the republish and the ack yields a rare **duplicate** — never a loss, because the confirmed publish lands before the original is acked. This is mitigated by design: (1) publisher confirms are on, so the republish is broker-confirmed before the original is acked; (2) Chatter's Inbox pattern (idempotent, once-only handling) absorbs the rare duplicate downstream.
- The classic republish sends the message to the **tail** of the queue, so it loses head-of-queue position and ordering. This is inherent to the approach and documented.
- The Quorum path carries none of these costs — hence it is the documented recommendation. Classic support exists for users who cannot use quorum queues, who accept the documented trade-offs.
