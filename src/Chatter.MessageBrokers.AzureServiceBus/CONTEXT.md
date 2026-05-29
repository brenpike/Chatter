# Chatter.MessageBrokers.AzureServiceBus

Azure Service Bus implementation of the Chatter.MessageBrokers interfaces for sending and receiving brokered messages.

## Language

**Queue Receiver**: Receiver bound to an ASB queue for Commands (`AddQueueReceiver<TMessage>`).

**Topic Subscription**: Receiver bound to an ASB topic subscription for Events (`AddTopicSubscription<TMessage>`).

**Service Bus Sender**: Azure Service Bus realization of the message sender, publishing to queues/topics; outbound handler API via `IMessageHandlerContext.AzureServiceBus()`.

**Service Bus Options**: Configuration (connection, paths, retry, circuit breaker) for the Azure Service Bus connection.

**Service Bus Retry**: ASB-specific Retry recovery policy applied during receiving.

**Service Bus Circuit Breaker**: ASB-specific Circuit Breaker recovery policy applied during receiving.

## Relationships

- Implements the receiver/sender/path interfaces defined in the Message Brokers context.
- Commands map to a Queue Receiver; Events map to a Topic Subscription.
- Service Bus Options configure recovery (Retry, Circuit Breaker) for receiving.
- Authentication is supplied by the Azure Service Bus Auth context.

## Example dialogue

> **Dev:** "How do I point Chatter at my Service Bus namespace?"
> **Domain expert:** "Configure Service Bus Options with the connection and paths; the Service Bus Receiver and Sender wire into the broker abstraction automatically."

## Flagged ambiguities

None detected during bootstrap.
