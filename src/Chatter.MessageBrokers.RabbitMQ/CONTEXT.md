# Chatter.MessageBrokers.RabbitMQ

RabbitMQ implementation of the Chatter.MessageBrokers interfaces for sending and receiving brokered messages.

## Language

**RabbitMq Receiver**: RabbitMQ realization of the Brokered Message Receiver, fed by a push consumer whose deliveries are buffered internally for processing.
_Avoid_: consumer, listener.

**RabbitMq Sender**: RabbitMQ realization of the Brokered Message Dispatcher; publishes via the default exchange keyed by routing key (Destination = queue name) unless an Exchange / Routing Key override is supplied.
_Avoid_: dispatcher (reserved for the Message Brokers Brokered Message Dispatcher), forwarder.

**Exchange**: The RabbitMQ router that decides which queues a published message reaches; the default exchange (`""`) routes by routing key equal to the queue name.

**Queue**: The mailbox a RabbitMq Receiver consumes from.

**Routing Key**: The key a message is published with; under the default-exchange convention it equals the destination Queue name.

**Binding**: The rule connecting an Exchange to a Queue; provisioned externally — this package declares no topology.

**Dead-Letter Exchange (DLX) / Dead-letter Queue**: The destination for messages that exhausted Recovery; the adapter republishes to the attribute-declared DeadletterQueueName / ErrorQueueName rather than relying on broker DLX configuration.

**RabbitMq Settlement**: The RabbitMQ realization of Settlement (Message Brokers context). Under `TransactionMode.None` the AMQP push consumer is registered with `autoAck`, so RabbitMQ removed the delivery at receive time and acknowledge, negative acknowledge and deadletter all report the **Not Required** Settlement Outcome — the message is simply dropped, which is what at-most-once means. Otherwise an ack, a requeue/republish nack, or a deadletter republish-then-ack reports **Settled**, and a settlement whose delivery is absent from the message broker context reports **Failed**.

**Channel Epoch**: The generation counter of the receive channel, carried on every buffered delivery. A settlement runs under the receive-channel gate and compares the delivery's carried epoch with the current one; on a mismatch the channel was recycled since delivery, so the delivery tag is meaningless on the new channel and RabbitMQ has already redelivered the message. The settlement is skipped and reports the **Failed** Settlement Outcome — it was ATTEMPTED and did not happen — never Not Required.

**Error-Queue Write Ownership** (`WritesToErrorQueue`): This receiver owns the Error Queue write exactly when NO Dead-letter Queue is configured — the ERROR-ONLY configuration, where deadlettering republishes the failed delivery to the Error Queue itself (publisher-confirmed) before acking the original. That path truthfully reports **Settled**, and the separate ownership signal is what keeps the Brokered Message Receiver from forwarding a SECOND copy of the same poison message to the SAME Error Queue: the single-copy rule holds because the duplicate is suppressed by ownership, never by misreporting the Settlement Outcome. With a Dead-letter Queue configured the receiver republishes there and never touches the Error Queue, so ownership stays with the Brokered Message Receiver and a copy is forwarded to the Error Queue as well. Configuring neither queue is rejected at startup, except under at-most-once (`TransactionMode.None`), which has no poison target to require.
_Avoid_: gating the Error Queue write on the Settlement Outcome (a truthful Settled would then write two copies of every poison message).

**Delivery Count Strategy**: How redeliveries are counted — Quorum (native `x-delivery-count`, recommended) or Classic (header-stamped republish counter). See ADR 0001.

**RabbitMq Options**: Configuration for the connection, prefetch, queue type, and body settings, supplied via the options builder.

**Topology Ownership**: This package provisions nothing — Exchanges, Queues, Bindings, and DLX are created externally (IaC in production, Dockerfile in development), mirroring the SQL Service Broker manual-provisioning stance.

## Relationships

- Implements the receiver/sender interfaces defined in the Message Brokers context.
- A RabbitMq Receiver consumes from a Queue and hands messages to the Brokered Message Dispatcher, which relays to the matching CQRS Command or Event handler.
- A RabbitMq Sender publishes through an Exchange by Routing Key; the default exchange routes by Routing Key equal to the Queue name.
- Recovery (Retry, Circuit Breaker) wraps receiving, mirroring the Message Brokers abstraction; exhausting it routes the message to the Dead-letter / Error Queue.
- Which queue that routing lands in decides Error-Queue Write Ownership: on the error-only configuration this receiver writes the Error Queue copy itself and the Brokered Message Receiver must not write a second one; with a Dead-letter Queue configured the receiver writes only there and the Brokered Message Receiver keeps the Error Queue write.
- The Receiver assumes the Exchanges, Queues, Bindings, and DLX already exist; Topology Ownership is external.

## Example dialogue

> **Dev:** "On a classic queue, how does it know a message is poison if RabbitMQ won't count deliveries?"
> **Domain expert:** "The Classic Delivery Count Strategy republishes the message to its own queue with an incremented `x-chatter-delivery-count` header, then acks the original — the count rides in the message. On a quorum queue we just read the native `x-delivery-count` instead, which is why quorum is the recommended default."

## Flagged ambiguities

- **Quorum vs Classic delivery-count semantics**: quorum queues count redeliveries natively; classic queues do not, so the count is carried in a republish header (ADR 0001) with a rare-duplicate trade-off.
- **Default-exchange-as-queue-name convention**: when no Exchange override is given, publishing uses the default exchange with Routing Key equal to the destination Queue name — Routing Key and Queue name coincide only under this convention.

## Known limitations

- **Single RabbitMQ queue receiver per process**: the connection source owns one receive channel and one consumer registration. Registering more than one RabbitMQ queue receiver fails fast at startup with `NotSupportedException`; recovery would otherwise re-register only the last receiver. Full multi-receiver support is tracked in [#195](https://github.com/brenpike/Chatter/issues/195).
