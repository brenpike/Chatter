# Chatter.CQRS

CQRS architecture via the mediator pattern: dispatch and handling of Commands, Queries, and Events.

## Language

**Command**: A message that changes the state of an aggregate, dispatched to exactly one handler.

**Query**: A message that retrieves data (a read model) without mutating state.
_Avoid_: read request.

**Read Model**: The data shape returned by a Query, optimized for retrieval rather than mutation.

**Event**: A message representing something that happened, dispatchable to zero or many handlers.

**Domain Event**: An Event originating from an internal aggregate, handled within the originating domain.

**Integration Event**: An Event published outward so other APIs or microservices can subscribe (requires broker infrastructure — see Message Brokers context).

**Aggregate**: A domain consistency boundary whose state changes via Commands and which emits Domain Events.

**Command Pipeline**: An ordered chain applying cross-cutting concerns (e.g. logging) across all command handlers.
_Avoid_: middleware.

**Message Context**: Per-dispatch contextual data flowing alongside a message through dispatch and handling.

**Message Dispatcher**: Routes a Command (to one handler) or an Event (to many) — `IMessageDispatcher`.
_Avoid_: mediator (used as the pattern name, not the type).

**Query Dispatcher**: Routes a Query to its `IQueryHandler<TQuery,TResult>` — `IQueryDispatcher`, separate from the Message Dispatcher.

**External Dispatcher**: The outbound-publish seam (`IExternalDispatcher`), a no-op by default (`NoOpExternalDispatcher`); a broker module replaces it to publish Integration Events.

**Diagnostics Surface**: The opt-in tracing and metrics surface Chatter dispatch emits through (`ChatterDiagnostics`), built on the .NET base class library only — `System.Diagnostics.ActivitySource` for spans and `System.Diagnostics.Metrics.Meter` for instruments — with no dependency on any OpenTelemetry package. It is defined here and is public, so every other module emits through it; each emitting assembly names its own `ActivitySource` and `Meter` after itself (`Chatter.CQRS`, `Chatter.MessageBrokers`), and that name is the scope an application subscribes to. It emits nothing until an application subscribes: every emit site guards on whether Chatter's own source has a subscriber, never on the ambient `Activity.Current`, which is non-null in any host running unrelated instrumentation. See ADR-0010.
_Avoid_: listener (a reserved alias — the .NET BCL subscription type is always named in full as a .NET `ActivityListener`); OpenTelemetry as a prerequisite (a provider merely subscribes to the surface; it is not a dependency of it).

## Relationships

- An Aggregate is changed by Commands and produces Domain Events.
- A Command is dispatched to exactly one handler; an Event fans out to many.
- Commands and Events go through the Message Dispatcher; Queries go through the separate Query Dispatcher.
- A Command Pipeline wraps all Command handlers.
- A Domain Event may be promoted to an Integration Event, published outward via the External Dispatcher (replaced by a broker module).
- Message Context accompanies every dispatch through the pipeline and handlers.
- The Diagnostics Surface observes Command and Event dispatch through the Message Dispatcher; Query dispatch is not instrumented, and nothing is emitted until an application subscribes.

## Example dialogue

> **Dev:** "Should this be a Command or an Event?"
> **Domain expert:** "If it instructs the aggregate to change state and has one owner, it's a Command. If it announces that state already changed and others may react, it's an Event."

## Flagged ambiguities

None detected during bootstrap.
