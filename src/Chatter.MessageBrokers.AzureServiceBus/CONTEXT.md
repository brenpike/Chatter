# Chatter.MessageBrokers.AzureServiceBus

Azure Service Bus implementation of the Chatter.MessageBrokers interfaces for sending and receiving brokered messages.

## Language

**Queue Receiver**: Receiver bound to an ASB queue for Commands (`AddQueueReceiver<TMessage>`).

**Session Queue Receiver**: A Queue Receiver opted into session mode (`AddSessionQueueReceiver<TMessage>`). Processes one session at a time, delivering that session's messages in strict FIFO order per Group Id (SessionId). Broader throughput is achieved by running additional receiver instances; there is no in-process multi-session parallelism knob.

**Topic Subscription**: Receiver bound to an ASB topic subscription for Events (`AddTopicSubscription<TMessage>`).

**Session Topic Subscription**: A Topic Subscription opted into session mode (`AddSessionTopicSubscription<TMessage>`). Same single-session-at-a-time, FIFO-per-Group-Id semantics as the Session Queue Receiver.

**Service Bus Sender**: Azure Service Bus realization of the message sender, publishing to queues/topics; outbound handler API via `IMessageHandlerContext.AzureServiceBus()`.

**Service Bus Options**: Configuration (connection, paths, retry, circuit breaker, session knobs) for the Azure Service Bus connection.

**Service Bus Retry**: ASB-specific Retry recovery policy applied during receiving. Distinct from the retry the Azure SDK client performs on the wire, which is configured either fluently (`WithNoRetry` / `WithExponentialDelay`) or through the `RetryPolicy` section of Service Bus Options (`NoRetry`, `MaximumRetryCount`, `MinimumBackoffInSeconds`, `MaximumBackoffInSeconds`) and is carried onto the single shared client. A configured parameter is honored only when greater than zero; otherwise the Azure SDK default for that parameter stands, so an omitted or zero value means "SDK default" and never "off". `DeltaBackoffInSeconds` is accepted for configuration compatibility and ignored — the SDK has no per-attempt delta-backoff knob. Fluent call wins over configuration.

**Service Bus Circuit Breaker**: ASB-specific Circuit Breaker recovery policy applied during receiving.

**PeekLock Settlement**: The Azure Service Bus realization of Settlement (Message Brokers context). Only a PeekLock receive owes one: in `ReceiveAndDelete` mode Azure Service Bus removes the delivery on receipt, so acknowledge, negative acknowledge and deadletter all report the **Not Required** Settlement Outcome. Under PeekLock, completing, abandoning or dead-lettering the received message reports **Settled**. When a PeekLock settlement finds no received message in the message broker context there is a lock to release and no message to release it with; that is reported as the **Failed** Settlement Outcome rather than THROWN, because the absence is deterministic — retrying the same context would find the same absence — so Recovery must not retry it.
_Avoid_: a dedicated settlement exception (the adapter no longer defines or raises one; the outcome carries the reason instead).

**Session**: An Azure Service Bus session-enabled receive mode. A held session owns the FIFO delivery of all messages sharing the same SessionId until the session is drained, released on idle timeout, or rolled due to a lost session lock. Sessions are provisioned externally; the adapter neither creates nor auto-enables session-capable entities.

**Session State**: Durable, per-session binary payload stored on the Azure Service Bus entity for the currently held session. Readable and writable during handler execution via `GetSessionStateAsync` / `SetSessionStateAsync` / `ClearSessionStateAsync`. Only available while handling a message received through a Session Queue Receiver or Session Topic Subscription; invoking it for a non-session message throws `InvalidOperationException`.

**Group Id ↔ SessionId realization**: The Azure Service Bus `SessionId` is the broker realization of the suite's existing Group Id (AMQP group-id) term. Inbound, a session message's `SessionId` is surfaced under `MessageContext.GroupId`; outbound, `SendOptions.WithGroupId` sets `ServiceBusMessage.SessionId`. No `WithSessionId` alias is introduced — Group Id is the single canonical surface. A handler sending or publishing through `IMessageHandlerContext` inherits the inbound Group Id (and the rest of the inbound message context) onto the outbound message by design, not incidentally.

**Partition Key**: Optional outbound value written via `ASBMessageContext.PartitionKey`, mapped to `ServiceBusMessage.PartitionKey` only when explicitly supplied; otherwise `SessionId` (from Group Id) stands in for it. When both a Group Id and a Partition Key are set, the Partition Key must equal the Group Id.

**Session Idle Timeout**: Operator knob (`SessionIdleTimeout` / `WithSessionIdleTimeout`) controlling how long a held session may yield no message before it is released and the receiver rolls to the next session. Default: 60 seconds. Fluent call wins over configuration.

**Max Session Lock Renewal Duration**: Operator knob (`MaxSessionLockRenewalDuration` / `WithMaxSessionLockRenewalDuration`) setting the ceiling on how long a held session's lock is renewed for long-running processing. Once reached, renewal stops and the session is allowed to expire or roll naturally. Default: 5 minutes. Fluent call wins over configuration.

**No Retry Opt-In**: Operator knob (`RetryPolicy:NoRetry` / `WithNoRetry`) that switches the Azure SDK client's retry off outright by setting its maximum retry count to zero. It is the only way retry can be switched off from configuration: a `RetryPolicy` section whose numeric parameters are all zero or omitted reads as "not configured" and falls back to the Azure SDK defaults, so retry is never disabled by inference. Default: off, meaning retry stays enabled. Fluent call wins over configuration.

**Built Options**: the Built Options invariant defined in the Message Brokers context, applied here to Service Bus Options. `ServiceBusOptionsBuilder.Build()` seeds the defaults, binds the configuration section over them, applies the fluent sentinels, constructs the retry options through the one guarded construction site, and only then registers the finished instance for BOTH the concrete `ServiceBusOptions` type AND the `IOptions<ServiceBusOptions>`, `IOptionsSnapshot<ServiceBusOptions>` and `IOptionsMonitor<ServiceBusOptions>` facets, so every resolution path returns that same instance and the container's own options factory never builds a second one. Registration runs last, after the connection-string guard and the fluent overrides, so no facet can observe a half-built instance. This builder never registered a `Configure<ServiceBusOptions>`, so nothing divergent existed to close: registering the facets is an ADDED guarantee here, not the repair of a defect this builder introduced. A consumer's own `Configure<ServiceBusOptions>` is consequently no longer consulted.
_Avoid_: "second options instance", "options factory instance".

## Relationships

- Implements the receiver/sender/path interfaces defined in the Message Brokers context.
- Commands map to a Queue Receiver (or Session Queue Receiver in session mode); Events map to a Topic Subscription (or Session Topic Subscription in session mode).
- Service Bus Options configure recovery (Retry, Circuit Breaker) and session behavior (Session Idle Timeout, Max Session Lock Renewal Duration) for receiving.
- Service Bus Options are configurable from appsettings: the `Chatter:Infrastructure:AzureServiceBus` section is bound over the builder's defaults whenever it exists, including its nested `RetryPolicy` section and the No Retry Opt-In within it. An explicit fluent call wins over a configured key here, whereas the Message Brokers context lets configuration win over the fluent call; the divergence is deliberate, and an application configuring both contexts needs to know which way each one resolves.
- Built Options is a term SHARED with the Message Brokers context: it is defined there, and this context applies it unchanged to Service Bus Options, reaching the internal registration helper through an `InternalsVisibleTo` grant. The invariant decides which INSTANCE every resolution path returns, not which SOURCE wins, so the deliberate Options Binding Precedence divergence between the two contexts is unaffected.
- Authentication is supplied by the Azure Service Bus Auth context.
- PeekLock Settlement realizes the Settlement Outcome contract defined in the Message Brokers context; a `ReceiveAndDelete` receiver owes no settlement at all, and a PeekLock settlement with no received message to settle reports Failed instead of raising.
- Session State and inbound Group Id (SessionId) surfacing are entirely within this adapter; no core Message Brokers or CQRS concept is changed.

## Example dialogue

> **Dev:** "How do I point Chatter at my Service Bus namespace?"
> **Domain expert:** "Configure Service Bus Options with the connection and paths; the Service Bus Receiver and Sender wire into the broker abstraction automatically."

> **Dev:** "How do I process session messages in strict order per session?"
> **Domain expert:** "Register the queue or subscription with `AddSessionQueueReceiver` or `AddSessionTopicSubscription`. The inbound SessionId is available in the handler as `MessageContext.GroupId`. For outbound messages targeting a session, set `WithGroupId` on `SendOptions`."

## Flagged ambiguities

None detected during bootstrap.
