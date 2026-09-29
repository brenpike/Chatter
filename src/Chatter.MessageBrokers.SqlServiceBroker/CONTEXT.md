# Chatter.MessageBrokers.SqlServiceBroker

SQL Server Service Broker implementation of the Chatter.MessageBrokers interfaces for sending and receiving brokered messages.

## Language

**Service Broker Receiver**: SQL Service Broker realization of the Brokered Message Receiver, dequeuing via `RECEIVE`.

**Service Broker Sender**: SQL Service Broker realization of the message sender, enqueuing onto a conversation/queue.

**Queue**: The SQL Service Broker queue a message is received from or sent to.

**Conversation**: A SQL Service Broker dialog over which messages flow between services (`BEGIN DIALOG` / `END CONVERSATION`).

**Dialog Command**: A runtime SQL DML command this package issues to drive a conversation — `BeginDialogConversationCommand`, `SendOnConversationCommand`, `ReceiveMessageFromQueueCommand`, `EndDialogConversationCommand`. These do NOT create infrastructure.
_Avoid_: setup script (this package provisions nothing).

**Service Broker Options**: Configuration for the SQL connection, conversation lifetime/encryption, and body compression. Scoped per host: one set of transport settings is shared by every Service Broker Receiver and the Service Broker Sender, and `AddSqlServiceBroker` refuses a second, different set. Receiver settings (queue, error queue, transaction mode, dead-letter service, maximum receive attempts) are per receiver, passed to `AddQueueReceiver`.

**Deferred Registration**: A service registration handed to `SqlServiceBrokerOptionsBuilder.DeferRegistration` inside the `AddSqlServiceBroker` delegate. It runs against the host's service collection only after `AddSqlServiceBroker` has accepted the Service Broker Options, in the order it was deferred; a refused call runs none. Receivers added with `AddQueueReceiver` are Deferred Registrations, and the SQL Change Feed context registers every change feed through them.

## Relationships

- Implements the receiver/sender interfaces defined in the Message Brokers context.
- The Receiver/Sender drive an existing Queue and Conversation via Dialog Commands; they assume the SQL Service Broker objects already exist.
- Recovery (Retry, Circuit Breaker) wraps receiving, mirroring the Message Brokers abstraction.

## Example dialogue

> **Dev:** "Do I need to create the queues myself?"
> **Domain expert:** "Yes — this package issues only runtime Dialog Commands; you provision the Queue, service, contract, message types, and `ENABLE_BROKER` yourself. Automatic provisioning lives in the SQL Change Feed context, not here."

## Flagged ambiguities

- **Provisioning ownership**: this package does NOT create Service Broker infrastructure (contrast with SQL Change Feed, which can provision via migrations).
