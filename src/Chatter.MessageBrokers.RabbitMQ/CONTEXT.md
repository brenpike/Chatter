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

**Delivery Count Strategy**: How redeliveries are counted — Quorum (native `x-delivery-count`, recommended) or Classic (header-stamped republish counter). See ADR 0001.

**RabbitMq Options**: Configuration for the connection, prefetch, queue type, and body settings, supplied via the options builder.

**Topology Ownership**: This package provisions nothing — Exchanges, Queues, Bindings, and DLX are created externally (IaC in production, Dockerfile in development), mirroring the SQL Service Broker manual-provisioning stance.

## Relationships

- Implements the receiver/sender interfaces defined in the Message Brokers context.
- A RabbitMq Receiver consumes from a Queue and hands messages to the Brokered Message Dispatcher, which relays to the matching CQRS Command or Event handler.
- A RabbitMq Sender publishes through an Exchange by Routing Key; the default exchange routes by Routing Key equal to the Queue name.
- Recovery (Retry, Circuit Breaker) wraps receiving, mirroring the Message Brokers abstraction; exhausting it routes the message to the Dead-letter / Error Queue.
- The Receiver assumes the Exchanges, Queues, Bindings, and DLX already exist; Topology Ownership is external.

## Example dialogue

> **Dev:** "On a classic queue, how does it know a message is poison if RabbitMQ won't count deliveries?"
> **Domain expert:** "The Classic Delivery Count Strategy republishes the message to its own queue with an incremented `x-chatter-delivery-count` header, then acks the original — the count rides in the message. On a quorum queue we just read the native `x-delivery-count` instead, which is why quorum is the recommended default."

## Flagged ambiguities

- **Quorum vs Classic delivery-count semantics**: quorum queues count redeliveries natively; classic queues do not, so the count is carried in a republish header (ADR 0001) with a rare-duplicate trade-off.
- **Default-exchange-as-queue-name convention**: when no Exchange override is given, publishing uses the default exchange with Routing Key equal to the destination Queue name — Routing Key and Queue name coincide only under this convention.

## Known limitations

- **Single RabbitMQ queue receiver per process (0.1.0)**: the connection source owns one receive channel and one consumer registration. Registering more than one RabbitMQ queue receiver fails fast at startup with `NotSupportedException`; recovery would otherwise re-register only the last receiver. Full multi-receiver support is tracked for a future minor release.
