# Chatter.CQRS

CQRS architecture via the mediator pattern: dispatch and handling of Commands, Queries, and Events.

## Language

**Command**: A message that changes the state of an aggregate, dispatched to exactly one handler. Exactly one handler is what the assembly scan REGISTERS, not something it verifies: command handlers are registered with a replace strategy, so when two scanned types handle the same Command the last one scanned survives and the earlier one is displaced with no error and no log, in a scan order derived from assembly load order and each assembly's type-definition order, neither of which is specified. `ThrowOnDuplicateCommandHandlers()` is the opt-in check that fails composition instead; it is off unless called, and it sees only what the scan sees. See ADR-0017.

**Query**: A message that retrieves data (a read model) without mutating state.
_Avoid_: read request.

**Read Model**: The data shape returned by a Query, optimized for retrieval rather than mutation.

**Event**: A message representing something that happened, dispatchable to zero or many handlers. Its handlers are not isolated from one another: they are invoked in turn and the first one that throws ends the dispatch, rethrowing unchanged so no later handler runs. See ADR-0012.

**Domain Event**: An Event originating from an internal aggregate, handled within the originating domain.

**Integration Event**: An Event published outward so other APIs or microservices can subscribe (requires broker infrastructure — see Message Brokers context).

**Aggregate**: A domain consistency boundary whose state changes via Commands and which emits Domain Events.

**Command Pipeline**: An ordered chain applying cross-cutting concerns (e.g. logging) across all command handlers.
_Avoid_: middleware.

**Message Context**: Per-dispatch contextual data flowing alongside a message through dispatch and handling.

**Context Container**: The type-keyed bag of contextual data a Message Context carries (`ContextContainer`), optionally chained to an inherited container so a lookup that misses falls through to the parent. It is unsynchronized: concurrent use is undefined. See ADR-0011. A lookup finds a value only when it is present under the key AND assignable to the requested type: a present value of another type reads as absent from `TryGet` and throws `InvalidCastException` from `Get`, while a stored `null` is present for a reference or nullable type and a mismatch for a non-nullable value type. A key present in the local container is answered from it whatever its type, so a local mismatch does not fall through to the parent. See ADR-0018.

**Message Dispatcher**: Routes a Command (to one handler) or an Event (to many) — `IMessageDispatcher`.
_Avoid_: mediator (used as the pattern name, not the type).

**Query Dispatcher**: Routes a Query to its `IQueryHandler<TQuery,TResult>` — `IQueryDispatcher`, separate from the Message Dispatcher. It caches one invoker per distinct Query type and Read Model type pair for the life of the process, and never evicts. See ADR-0013.

**External Dispatcher**: The outbound-publish seam (`IExternalDispatcher`), a no-op by default (`NoOpExternalDispatcher`); a broker module replaces it to publish Integration Events.

**Diagnostics Surface**: The opt-in tracing and metrics surface Chatter dispatch emits through (`ChatterDiagnostics`), built on the .NET base class library only — `System.Diagnostics.ActivitySource` for spans and `System.Diagnostics.Metrics.Meter` for instruments — with no dependency on any OpenTelemetry package. It is defined here and is public, so every other module emits through it; each emitting assembly names its own `ActivitySource` and `Meter` after itself (`Chatter.CQRS`, `Chatter.MessageBrokers`), and that name is the scope an application subscribes to. It emits nothing until an application subscribes: every emit site guards on whether Chatter's own source has a subscriber, never on the ambient `Activity.Current`, which is non-null in any host running unrelated instrumentation. See ADR-0010.
_Avoid_: listener (a reserved alias — the .NET BCL subscription type is always named in full as a .NET `ActivityListener`); OpenTelemetry as a prerequisite (a provider merely subscribes to the surface; it is not a dependency of it).

## Relationships

- An Aggregate is changed by Commands and produces Domain Events.
- A Command is dispatched to exactly one handler; an Event fans out to many, and that fan-out is a single unit of work that ends at the first handler to throw. See ADR-0012.
- Commands and Events go through the Message Dispatcher; Queries go through the separate Query Dispatcher.
- A Command Pipeline wraps all Command handlers.
- A Domain Event may be promoted to an Integration Event, published outward via the External Dispatcher (replaced by a broker module).
- Message Context accompanies every dispatch through the pipeline and handlers.
- A Context Container belongs to exactly one Message Context: a dispatch that supplies no context gets a fresh Message Context and therefore a fresh container, while a dispatch given an existing Message Context reuses that context's container. See ADR-0011.
- The Diagnostics Surface observes Command and Event dispatch through the Message Dispatcher; Query dispatch is not instrumented, and nothing is emitted until an application subscribes.

## Example dialogue

> **Dev:** "Should this be a Command or an Event?"
> **Domain expert:** "If it instructs the aggregate to change state and has one owner, it's a Command. If it announces that state already changed and others may react, it's an Event."

## Flagged ambiguities

None detected during bootstrap.
