# Chatter.MessageBrokers

Technology-agnostic brokered messaging built on Chatter.CQRS: receiving, sending, routing, reliability, and recovery, with interfaces left to infrastructure-specific implementations.

## Language

**Brokered Message**: A message received from or sent to broker infrastructure, relayed (dispatched) to a Command or Event handler.

**Brokered Message Receiver**: The component that consumes brokered messages from infrastructure; runs as a background service, capped at one instance.
_Avoid_: consumer, listener.

**Brokered Message Dispatcher**: Relays a received Brokered Message to the matching Command or Event handler.

**Brokered Message Router**: Routes / forwards a brokered message to its destination path.
_Avoid_: forwarder (a Router specialization).

**Brokered Message Attribute**: Metadata declaration that maps a message type to its broker path/queue.

**Outbox**: Persistence pattern recording outgoing messages for reliable publish alongside local state changes.

**Inbox**: Persistence pattern recording received messages to enforce idempotent, once-only handling.

**Routing Slip**: A message carrying its own itinerary of steps/destinations to visit in sequence.

**Recovery**: Resilience policies applied to receiving — Retry and Circuit Breaker.

**Circuit Breaker**: A recovery policy that halts processing after repeated failures.

**Critical Failure**: An unrecoverable receive error; raises a Critical Failure Event and may route the message to the Error Queue.

**Error Queue**: Destination for messages that exhausted recovery and cannot be handled.

**Max Receives Exceeded**: The condition where a message has been delivered more times than allowed, triggering a configured action.

**Body Converter**: Serializes/deserializes a brokered message body to/from a domain message type.

## Relationships

- A Brokered Message Receiver consumes infrastructure messages and hands them to the Brokered Message Dispatcher.
- The Dispatcher relays to a Command or Event handler (Chatter.CQRS) by message type.
- Recovery (Retry, Circuit Breaker) wraps receiving; exhausting it yields a Critical Failure routed to the Error Queue.
- Outbox and Inbox depend on a persistence implementation (see Reliability EntityFramework context).
- A Routing Slip drives a Router across a sequence of destinations.
- Concrete brokers (Azure Service Bus, SQL Service Broker) implement the receiver/sender/path interfaces defined here.

## Example dialogue

> **Dev:** "A handler keeps throwing — where does the message end up?"
> **Domain expert:** "Recovery retries it under the Circuit Breaker. Once Max Receives is exceeded it's a Critical Failure, so it's moved to the Error Queue and a Critical Failure Event fires."

## Flagged ambiguities

- **Router vs Forwarder**: ForwardingRouter and IBrokeredMessageForwarder overlap; treat Forwarder as a Router specialization.
