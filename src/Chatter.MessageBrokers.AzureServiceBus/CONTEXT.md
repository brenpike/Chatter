# Chatter.MessageBrokers.AzureServiceBus

Azure Service Bus implementation of the Chatter.MessageBrokers interfaces for sending and receiving brokered messages.

## Language

**Service Bus Receiver**: Azure Service Bus realization of the Brokered Message Receiver, pulling from queues/topics.

**Service Bus Sender**: Azure Service Bus realization of the message sender, publishing to queues/topics.

**Service Bus Options**: Configuration (connection, paths, retry, circuit breaker) for the Azure Service Bus connection.

**Service Bus Retry**: ASB-specific Retry recovery policy applied during receiving.

**Service Bus Circuit Breaker**: ASB-specific Circuit Breaker recovery policy applied during receiving.

## Relationships

- Implements the receiver/sender/path interfaces defined in the Message Brokers context.
- Service Bus Options configure recovery (Retry, Circuit Breaker) for receiving.
- Authentication is supplied by the Azure Service Bus Auth context.

## Example dialogue

> **Dev:** "How do I point Chatter at my Service Bus namespace?"
> **Domain expert:** "Configure Service Bus Options with the connection and paths; the Service Bus Receiver and Sender wire into the broker abstraction automatically."

## Flagged ambiguities

None detected during bootstrap.
