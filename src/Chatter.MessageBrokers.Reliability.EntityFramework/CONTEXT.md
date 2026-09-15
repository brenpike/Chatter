# Chatter.MessageBrokers.Reliability.EntityFramework

EF Core persistence implementing the inbox/outbox reliability ports and unit-of-work for Chatter.MessageBrokers.

## Language

**Brokered Message Outbox**: EF-backed store of outgoing messages, persisted in the same transaction as local state for reliable publish.

**Brokered Message Inbox**: EF-backed store of received message ids enforcing once-only, idempotent handling.

**Unit of Work**: Coordinates a single atomic commit spanning domain state and inbox/outbox writes.

**Persistance Transaction**: The transaction abstraction wrapping the Unit of Work commit (note: spelled `Persistance` in code).

## Relationships

- Implements the Outbox and Inbox persistence ports defined in the Message Brokers context, replacing their in-memory defaults.
- All types are generic over the consumer's own `DbContext` (`TContext : DbContext`) — no separate Chatter context; entity configs are applied in the consumer's `OnModelCreating`.
- Wired through the Command Pipeline as behaviors (`WithInboxBehavior<TContext>()`, `WithOutboxProcessingBehavior<TContext>()`, `WithUnitOfWorkBehavior<TContext>()`), not a standalone DI registration.
- The Unit of Work commits domain changes together with Outbox/Inbox writes via a Persistance Transaction (`IPersistanceTransaction`).
- Outbox processing wraps the Unit of Work, which wraps the Inbox — this order is package-guaranteed, not derived from the order the pipeline extension methods are called in. The guarantee covers only descriptors registered through `WithUnitOfWorkBehavior<TContext>()`, `WithInboxBehavior<TContext>()`, and `WithOutboxProcessingBehavior<TContext>()`; later direct `WithBehavior` calls, and closed-generic, factory, keyed, or decorated registrations of these behavior types, are outside normalization.

## Example dialogue

> **Dev:** "How do I guarantee the message publishes only if my DB write succeeds?"
> **Domain expert:** "Write to the Brokered Message Outbox inside the same Unit of Work as your aggregate; the Persistance Transaction commits both or neither."

## Flagged ambiguities

- **Persistance** is misspelled in the codebase; keep the spelling when referencing the type, use _persistence_ in prose.
