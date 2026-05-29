# Chatter.MessageBrokers.SqlServiceBroker

SQL Server Service Broker implementation of the Chatter.MessageBrokers interfaces for sending and receiving brokered messages.

## Language

**Service Broker Receiver**: SQL Service Broker realization of the Brokered Message Receiver, dequeuing via `RECEIVE`.

**Service Broker Sender**: SQL Service Broker realization of the message sender, enqueuing onto a conversation/queue.

**Queue**: The SQL Service Broker queue a message is received from or sent to.

**Conversation**: A SQL Service Broker dialog over which messages flow between services.

**Setup Scripts**: SQL scripts that provision the Service Broker objects (queues, services, message types).

**Service Broker Options**: Configuration for the SQL connection, queue, and recovery policies.

## Relationships

- Implements the receiver/sender interfaces defined in the Message Brokers context.
- Setup Scripts provision the Queue and Conversation objects the Receiver/Sender depend on.
- Recovery (Retry, Circuit Breaker) wraps receiving, mirroring the Message Brokers abstraction.

## Example dialogue

> **Dev:** "Do I need to create the queues myself?"
> **Domain expert:** "No — run the Setup Scripts; they provision the SQL Service Broker Queue and Conversation that the Service Broker Receiver reads from."

## Flagged ambiguities

None detected during bootstrap.
