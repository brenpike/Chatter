---
status: accepted
date: 2026-09-29
---

# One SQL Service Broker transport configuration per host

`AddSqlServiceBroker` registers one `SqlServiceBrokerOptions` instance per host. A later call whose options match the
registered ones setting by setting is idempotent: it registers no second options descriptor and still registers its
own receivers. A later call whose options diverge is refused with a `NotSupportedException` that names the diverging
settings, and the refused call leaves the caller's `IServiceCollection` and the shared discovered-receiver registry as
they were. Before this change the last registration silently won for every receiver in the process. This ADR records
the cause, the options considered, the one chosen, the ordering that makes a refusal unobservable and what it leaves
open. The approach was approved as part of the plan for the issue.

Issue #531.

## Context

**`SqlServiceBrokerOptions` is one process-wide singleton with three consumers.** `SqlServiceBrokerReceiver`,
`SqlClientConnectionSource` and `SqlServiceBrokerSender` each take `SqlServiceBrokerOptions` in their constructor and
resolve it from the container. There is no per-receiver or per-queue key: every receiver, connection source and sender
in the host reads the same instance.

**`AddSqlServiceBroker` blind-wrote that singleton.** Before this change it ended with
`builder.Services.AddSingleton(options)` on every call. Microsoft DI resolves a single-service request from the last
descriptor registered for the type, so the last call's options won for every receiver, including receivers registered
by earlier calls against a different connection string or with different conversation settings. Two paths reach a
second call in ordinary use: a second `AddSqlChangeFeed`, because each feed calls `AddSqlServiceBroker` with its own
`ServiceBrokerOptions`, and a change feed next to a plain `AddSqlServiceBroker`. Nothing failed at registration or at
start-up; the earlier receivers ran against settings they were never configured with.

**The other singletons a second call registers were already harmless.** The connection source, receiver and sender
are registered with `AddIfNotRegistered`, so a second call adds none of them. `IMessagingInfrastructure` and
`IBrokeredMessageBodyConverter` are folded by key: `MessagingInfrastructureProvider` keys infrastructures by `Type` and
`BodyConverterFactory` keys converters by `ContentType`, so a duplicate of the same key replaces an identical entry. A
duplicated `ICircuitBreakerExceptionPredicatesProvider` or `IRetryExceptionPredicatesProvider` contributes the same
predicates twice to an evaluator that asks whether any predicate matches. Only the options singleton changed behavior
under a second call.

## Considered Options

### Option A — one transport configuration per host, equivalent re-registration idempotent, divergent refused (ACCEPTED)

Recorded under *Decision*. It turns a silent misconfiguration into a registration-time failure that names what
differs, keeps every existing single-registration host working unchanged, keeps the multi-feed case working whenever
the feeds share their transport settings, and changes no public signature.

### Option B — per-receiver transport options keyed by queue (DEFERRED to #542)

This is the capability a host with two databases or two conversation profiles actually wants, and it is not a
registration change. The sender is resolved by the `IMessagingInfrastructure` factory delegate, which opens a fresh DI
scope and resolves `SqlServiceBrokerSender` with no receiver identity in hand, and `IMessagingInfrastructure` carries
only its `Type`, so nothing on the send path could select a per-queue configuration. The receiver and the connection
source would need the same rework. That is a design change to the receiver, sender and connection-source seams, and
it is tracked in https://github.com/brenpike/Chatter/issues/542.

### Option C — make `SqlServiceBrokerOptions` a record (REJECTED)

A record would give value equality for free, but `SqlServiceBrokerOptions` is a public, mutable, sealed class. Turning
it into a record changes `==`, `Equals` and `GetHashCode` for every consumer that compares instances, gives a mutable
type a hash code that moves when a setter runs, and replaces `ToString` with a synthesized one that prints every
property, the connection string included. The comparison is only needed at one registration site, so a change to the
public type buys nothing the site cannot do itself.

### Option D — warn on a divergent registration instead of refusing it (REJECTED)

There is no logger at registration time: `AddSqlServiceBroker` runs against an `IServiceCollection` before any
provider exists. A warning would have to be deferred to start-up, where the host has already committed to one of the
two configurations. The RabbitMQ package sets the precedent for this situation: `AddRabbitMq` refuses unsupported
configurations at registration with `NotSupportedException`.

## Decision

**A host has one SQL Service Broker transport configuration.** `AddSqlServiceBroker` reads the `SqlServiceBrokerOptions`
instance the container would resolve (the last descriptor for the type) and compares it with this call's options:

- **No registered instance.** The call registers its options as a singleton instance, as before.
- **Equivalent.** The call registers no second options descriptor. Its receivers are still registered, so every
  call's `AddQueueReceiver` receivers run against the one shared configuration.
- **Divergent.** The call throws `NotSupportedException`. The message names each diverging setting with its registered
  value and this call's value, reports connection-string values as `(redacted)` because the connection string is a
  credential, states that receiver settings stay per receiver, and links #542.

**Receiver settings stay per receiver.** The queue, error queue, description, transaction mode, dead-letter service and
maximum receive attempts are arguments to `AddQueueReceiver`, which records them on that receiver's `ReceiverOptions`.
None of them is part of the compared configuration, so two feeds or two receivers that differ only in those settings
register side by side.

**Equivalence is reflected, not listed.** `SqlServiceBrokerOptionsEquivalence.FindDivergences` compares every public
readable, non-indexed instance property of `SqlServiceBrokerOptions` with `object.Equals`. A property added to the type
later takes part in the comparison with no change at the comparison site. The `INVARIANT:` comment on its reflected
property set names the oracle and the measured mutation.

### Every refusal runs before the first write

`AddQueueReceiver` no longer writes to the collection. It runs the same `AddReceiver` registration against a throwaway
`ServiceCollection`, so `AddReceiver`'s own refusals (a message type that already carries `BrokeredMessageAttribute`)
are raised at the call that caused them, and then enqueues the registration on the `SqlServiceBrokerOptionsBuilder`.
`AddSqlServiceBroker` runs the options delegate, then `Build()`, then the divergence check, and only then writes: the
shared registrations, the options instance when none was registered, and finally the enqueued receiver registrations
in the order they were added. A call refused by the divergence check, or by `Build()`'s `ArgumentNullException` for
missing options, a blank connection string or a blank message body type, therefore leaves the caller's
`IServiceCollection` and the shared `IDiscoveredReceiverRegistry` exactly as they were. The `INVARIANT:` comment on
`AddSqlServiceBroker` names the oracles and the measured mutation.

**Why not the staged copy of ADR-0032.** ADR-0032 stages a reliability door's mutations on a copy of the caller's
descriptors and commits them at the end. That shape does not hold here. `AddReceiver` records each receiver's
`ReceiverOptions` in the `IDiscoveredReceiverRegistry` instance held by that registry's singleton descriptor, and a
copied descriptor holds the same instance. A receiver registered against a staged copy would mutate the live registry
before the divergence check ran, and discarding the copy would not undo it. Deferring the receiver registrations until
every refusal has run keeps both the collection and the registry untouched, which is the stronger property this
package needs.

**Stated limit.** `SqlServiceBrokerOptionsBuilder.Services` is public. A write a consumer makes through it from inside
the options delegate lands on the caller's collection immediately and is not undone by a later refusal. No test pins
that case.

## Consequences

- **A second divergent registration fails at start-up instead of misconfiguring earlier receivers.** This is a
  behavior change for any host that registered two differing configurations, including two `AddSqlChangeFeed` calls
  with different `ServiceBrokerOptions`. Such a host was already running every receiver against the last
  configuration.
- **Equivalent multi-feed and feed-plus-broker hosts keep working.** Each call's receivers are registered against the
  one options instance.
- **`AddSqlServiceBrokerOptions()` with `AddQueueReceiver` and no `AddSqlServiceBroker` call registers no receiver.**
  `AddQueueReceiver` now enqueues, and only `AddSqlServiceBroker` drains the queue. That combination registered no
  transport before this change either, so it produced no working receiver then.
- **An options descriptor registered by type or by factory has no instance to compare.** The check reads it as absent,
  and the call appends its own instance. No test pins that case.
- **Other shared registrations are unchanged.** An equivalent second call still re-registers the predicate providers,
  the `IMessagingInfrastructure` factory and the body converter, which were already harmless (see *Context*).
- **Versioning.** Both packages are 0.x, and a registration that used to be accepted is now refused, so each takes a
  minor bump: `Chatter.MessageBrokers.SqlServiceBroker` 0.17.0 and `Chatter.SqlChangeFeed` 0.16.0. No public signature
  changes.
- **Scope boundary.** Per-receiver or per-queue transport configuration is out of scope and tracked in #542. This ADR
  makes one configuration per host explicit and enforced; it does not add a second.

## Closed-by-Construction Acceptance Test

> What class of future finding does this make impossible, and why?

**Eliminated: a later `AddSqlServiceBroker` call silently replacing the transport configuration of receivers
registered earlier.** A later call either matches the registered instance, and adds no descriptor, or is refused before
it writes anything. Because the comparison reflects the property set, a setting added to `SqlServiceBrokerOptions`
later is covered with no change at the registration site.

**Eliminated: a refused `AddSqlServiceBroker` call leaving receivers behind.** Receivers reach the collection and the
registry only through the drain at the end of the method, after every refusal the method raises.

**Not closed.** A descriptor registered by type or factory bypasses the comparison, and a write through
`SqlServiceBrokerOptionsBuilder.Services` bypasses the ordering; neither is pinned by a test. A hand-written
`AddSingleton<SqlServiceBrokerOptions>` after `AddSqlServiceBroker` still replaces the configuration, because Microsoft
DI resolves the last descriptor and nothing inspects registrations made outside this method.

## References

- Issue #531 — the SQL Change Feed options defects, including the last-registration-wins options singleton this ADR
  resolves.
- Issue #542 — per-receiver transport options, the capability this ADR defers.
- ADR-0032 — *The reliability registration door stages its mutations, so a refusal cannot be observed*. The staged-copy
  shape this ADR does not use, and why.
- ADR-0027 — *An `INVARIANT:` comment names the oracle that falsifies it*. The rule the `INVARIANT:` comments cited
  here follow.
- `src/Chatter.MessageBrokers.SqlServiceBroker/src/Chatter.MessageBrokers.SqlServiceBroker/DependencyInjection/Extensions.cs`
  — `AddSqlServiceBroker`, `RefuseDivergentOptions` and `AddQueueReceiver`, with their `INVARIANT:` comments.
- `src/Chatter.MessageBrokers.SqlServiceBroker/src/Chatter.MessageBrokers.SqlServiceBroker/Configuration/SqlServiceBrokerOptionsBuilder.cs`
  — `Build()` and the receiver-registration queue.
- `src/Chatter.MessageBrokers.SqlServiceBroker/src/Chatter.MessageBrokers.SqlServiceBroker/Configuration/SqlServiceBrokerOptionsEquivalence.cs`
  — the reflected comparison and the connection-string redaction.
- `src/Chatter.MessageBrokers.SqlServiceBroker/tests/DependencyInjection/UsingExtensions/WhenAddingSqlServiceBroker.cs`
  — the idempotence, refusal and no-mutation oracles.
- `src/Chatter.MessageBrokers.RabbitMQ/src/Chatter.MessageBrokers.RabbitMQ/DependencyInjection/Extensions.cs` — the
  registration-time `NotSupportedException` precedent.
