# Chatter.MessageBrokers.Reliability.EntityFramework

EF Core persistence implementing the inbox/outbox reliability ports and unit-of-work for Chatter.MessageBrokers.

## Language

**Brokered Message Outbox**: EF-backed store of outgoing messages. `SendToOutbox` and both `UpdateProcessedDate` overloads only stage `OutboxMessage` rows into the EF context — neither saves. An enqueue is committed by the unit of work wrapping the handler, the same single `SaveChangesAsync` that commits the Brokered Message Inbox marker. A drain's processed stamp is committed by the outbox's own `IUnitOfWork.ExecuteAsync` (`UnitOfWork<TContext>.CompleteAsync`), which commits its own transaction only when this unit of work began it — the ownership captured under Unit of Work — rather than merely because it runs after the handler's transaction has already committed. An enqueue outside any surrounding unit of work is staged, never persisted — symmetric with the inbox.

**Brokered Message Inbox**: EF-backed store of received message ids enforcing once-only, idempotent handling.
_Avoid_: "closed by construction" / "commit capability denied" for `BrokeredMessageInbox<TContext>`'s `DbSet<InboxMessage>` field — it does not: the reflection guard (`MustNotDeclareADbContextField`) denies a declared `DbContext` field, not the commit capability itself; EF's `DbSet<T>` declares `IInfrastructure<IServiceProvider>`, the runtime `InternalDbSet<T>` implements `IInfrastructure<DbContext>` and holds a private `DbContext` field, and `SaveChangesAsync` is reachable through that handle with no reflection and no internal-type cast.

**Unit of Work**: Coordinates a single atomic commit spanning domain state and inbox/outbox writes. Commit, rollback and dispose act only on a transaction this unit of work itself began; a transaction it adopted from a caller is participated in — `SaveChangesAsync` still flushes into it — but never committed, rolled back or disposed here, leaving it for its owner to complete.
_Avoid_: "unrepresentable" / "capability denial" / "no commit-capable handle" / "closed by construction" for this ownership rule — none holds: `IUnitOfWork.CurrentTransaction` is still public and still hands out a commit-capable `IPersistanceTransaction`, and `BrokeredMessageOutbox<TContext>` re-exposes it, so a consumer can still commit on its own behalf through that surface. What changed is narrower: only the unit of work's own commit/rollback/dispose paths are now confined to a transaction they began, not the reachability of a commit-capable handle in general.

**Unit of Work Transaction**: The private handle `UnitOfWork<TContext>.BeginAsync` returns, pairing an `IPersistanceTransaction` with a `BegunHere` flag captured once at begin. Commit, rollback and dispose consult only that flag; they never re-derive ownership from the context's ambient transaction.

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
