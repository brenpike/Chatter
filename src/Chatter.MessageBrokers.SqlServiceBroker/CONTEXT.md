# Chatter.MessageBrokers.SqlServiceBroker

SQL Server Service Broker implementation of the Chatter.MessageBrokers interfaces for sending and receiving brokered messages.

## Language

**Service Broker Receiver**: SQL Service Broker realization of the Brokered Message Receiver, dequeuing via `RECEIVE`.

**Service Broker Sender**: SQL Service Broker realization of the message sender, enqueuing onto a conversation/queue.

**Queue**: The SQL Service Broker queue a message is received from or sent to.

**Conversation**: A SQL Service Broker dialog over which messages flow between services (`BEGIN DIALOG` / `END CONVERSATION`).

**Dialog Command**: A runtime SQL DML command this package issues to drive a conversation — `BeginDialogConversationCommand`, `SendOnConversationCommand`, `ReceiveMessageFromQueueCommand`, `EndDialogConversationCommand`. These do NOT create infrastructure.
_Avoid_: setup script (this package provisions nothing).

**Service Broker Options**: Configuration for the SQL connection, queue, recovery policies, conversation lifetime/encryption, and body compression.

## Relationships

- Implements the receiver/sender interfaces defined in the Message Brokers context.
- The Receiver/Sender drive an existing Queue and Conversation via Dialog Commands; they assume the SQL Service Broker objects already exist.
- Recovery (Retry, Circuit Breaker) wraps receiving, mirroring the Message Brokers abstraction.

## Example dialogue

> **Dev:** "Do I need to create the queues myself?"
> **Domain expert:** "Yes — this package issues only runtime Dialog Commands; you provision the Queue, service, contract, message types, and `ENABLE_BROKER` yourself. Automatic provisioning lives in the SQL Change Feed context, not here."

## Flagged ambiguities

- **Provisioning ownership**: this package does NOT create Service Broker infrastructure (contrast with SQL Change Feed, which can provision via migrations).
