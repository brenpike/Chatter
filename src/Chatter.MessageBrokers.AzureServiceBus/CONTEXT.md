# Chatter.MessageBrokers.AzureServiceBus

Azure Service Bus implementation of the Chatter.MessageBrokers interfaces for sending and receiving brokered messages.

## Language

**Queue Receiver**: Receiver bound to an ASB queue for Commands (`AddQueueReceiver<TMessage>`).

**Session Queue Receiver**: A Queue Receiver opted into session mode (`AddSessionQueueReceiver<TMessage>`). Processes one session at a time, delivering that session's messages in strict FIFO order per Group Id (SessionId). Broader throughput is achieved by running additional receiver instances; there is no in-process multi-session parallelism knob.

**Topic Subscription**: Receiver bound to an ASB topic subscription for Events (`AddTopicSubscription<TMessage>`).

**Session Topic Subscription**: A Topic Subscription opted into session mode (`AddSessionTopicSubscription<TMessage>`). Same single-session-at-a-time, FIFO-per-Group-Id semantics as the Session Queue Receiver.

**Service Bus Sender**: Azure Service Bus realization of the message sender, publishing to queues/topics; outbound handler API via `IMessageHandlerContext.AzureServiceBus()`.

**Service Bus Options**: Configuration (connection, paths, retry, circuit breaker, session knobs) for the Azure Service Bus connection.

**Service Bus Retry**: ASB-specific Retry recovery policy applied during receiving.

**Service Bus Circuit Breaker**: ASB-specific Circuit Breaker recovery policy applied during receiving.

**Session**: An Azure Service Bus session-enabled receive mode. A held session owns the FIFO delivery of all messages sharing the same SessionId until the session is drained, released on idle timeout, or rolled due to a lost session lock. Sessions are provisioned externally; the adapter neither creates nor auto-enables session-capable entities.

**Session State**: Durable, per-session binary payload stored on the Azure Service Bus entity for the currently held session. Readable and writable during handler execution via `GetSessionStateAsync` / `SetSessionStateAsync` / `ClearSessionStateAsync`. Only available while handling a message received through a Session Queue Receiver or Session Topic Subscription; invoking it for a non-session message throws `InvalidOperationException`.

**Group Id ↔ SessionId realization**: The Azure Service Bus `SessionId` is the broker realization of the suite's existing Group Id (AMQP group-id) term. Inbound, a session message's `SessionId` is surfaced under `MessageContext.GroupId`; outbound, `SendOptions.WithGroupId` sets `ServiceBusMessage.SessionId`. No `WithSessionId` alias is introduced — Group Id is the single canonical surface.

**Partition Key**: Optional outbound value written via `ASBMessageContext.PartitionKey`, mapped to `ServiceBusMessage.PartitionKey` only when explicitly supplied; otherwise `SessionId` (from Group Id) stands in for it. When both a Group Id and a Partition Key are set, the Partition Key must equal the Group Id.

**Session Idle Timeout**: Operator knob (`SessionIdleTimeout` / `WithSessionIdleTimeout`) controlling how long a held session may yield no message before it is released and the receiver rolls to the next session. Default: 60 seconds. Fluent call wins over configuration.

**Max Session Lock Renewal Duration**: Operator knob (`MaxSessionLockRenewalDuration` / `WithMaxSessionLockRenewalDuration`) setting the ceiling on how long a held session's lock is renewed for long-running processing. Once reached, renewal stops and the session is allowed to expire or roll naturally. Default: 5 minutes. Fluent call wins over configuration.

## Relationships

- Implements the receiver/sender/path interfaces defined in the Message Brokers context.
- Commands map to a Queue Receiver (or Session Queue Receiver in session mode); Events map to a Topic Subscription (or Session Topic Subscription in session mode).
- Service Bus Options configure recovery (Retry, Circuit Breaker) and session behavior (Session Idle Timeout, Max Session Lock Renewal Duration) for receiving.
- Authentication is supplied by the Azure Service Bus Auth context.
- Session State and inbound Group Id (SessionId) surfacing are entirely within this adapter; no core Message Brokers or CQRS concept is changed.

## Example dialogue

> **Dev:** "How do I point Chatter at my Service Bus namespace?"
> **Domain expert:** "Configure Service Bus Options with the connection and paths; the Service Bus Receiver and Sender wire into the broker abstraction automatically."

> **Dev:** "How do I process session messages in strict order per session?"
> **Domain expert:** "Register the queue or subscription with `AddSessionQueueReceiver` or `AddSessionTopicSubscription`. The inbound SessionId is available in the handler as `MessageContext.GroupId`. For outbound messages targeting a session, set `WithGroupId` on `SendOptions`."

## Flagged ambiguities

None detected during bootstrap.
