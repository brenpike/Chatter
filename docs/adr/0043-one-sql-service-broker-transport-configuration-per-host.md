---
status: accepted
date: 2026-09-29
---

# One SQL Service Broker transport configuration per host

`AddSqlServiceBroker` registers one `SqlServiceBrokerOptions` instance per host. A later call whose options match the
transport settings the first call recorded, setting by setting, is idempotent: it registers no second options
descriptor and still registers its own receivers. A later call whose options diverge is refused with a
`NotSupportedException` that names the diverging settings, and the refused call runs none of its registrations, so the
caller's `IServiceCollection` and the shared discovered-receiver registry stay as they were. `AddSqlChangeFeed` writes
only through one `AddSqlServiceBroker` call, so a refused change feed registers nothing either. Both hold for every
write that goes through the packages' own registration paths; the writes they do not cover are recorded under
*Recorded residuals*. Before this change the last registration silently won for every receiver in the process. This
ADR records the cause, the options considered, the one chosen, the ordering that makes a refusal unobservable and what
it leaves open. The approach was approved as part of the plan for the issue.

Issue #531.

**Amended 2026-09-29**, from review of this branch, before release. As first accepted, the guard compared with the
`SqlServiceBrokerOptions` instance held by the last options descriptor, printed every value except the connection
string, left `AddSqlChangeFeed` writing a registration of its own ahead of its refusals, and invoked the generic
`AddSqlChangeFeed` from the row-type overload through a name lookup that wrapped every refusal in a
`TargetInvocationException`. Each is replaced below, and *Amendment: what the review changed* records why.

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
the feeds share their transport settings, and changes no existing public signature.

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

**A host has one SQL Service Broker transport configuration.** The first successful `AddSqlServiceBroker` call on a
collection registers its `SqlServiceBrokerOptions` instance as a singleton and, beside it, a transport record: an
internal `SqlServiceBrokerTransportRegistration` registered as a singleton instance, holding a
`SqlServiceBrokerTransportSettings` snapshot of those options. Every call snapshots its own options and compares them
with the last record the collection carries:

- **No record.** The call registers its options instance and the record.
- **Equivalent.** The call registers neither. Its receivers are still registered, so every call's `AddQueueReceiver`
  receivers run against the one shared configuration.
- **Divergent.** The call throws `NotSupportedException`. The message names each diverging setting with its registered
  value and this call's value, states that receiver settings stay per receiver, and links #542. A value is shown only
  for a setting marked safe to print; every other value, the connection string's included, is shown as `(redacted)`.

**The ledger is the owned record, not the options descriptor.** The guard never reads a `SqlServiceBrokerOptions`
descriptor. Such a descriptor can be registered by instance, by type or by factory, by this package or by the host, and
the instance it holds stays mutable after registration, so none of them says what an earlier `AddSqlServiceBroker`
call registered. The record does: only `AddSqlServiceBroker` writes it, only in the branch where the collection
carries none, and its settings are read once when it is written, so a later change to the options instance does not
move what later calls compare with. `SqlServiceBrokerTransportRegistration.Find` reads the last record, which matches
the last options descriptor Microsoft DI resolves when a record arrives together with its options descriptor, for
example when one collection's descriptors are copied into another. The `INVARIANT:` comments on the record type, on
`Find` and on `RefuseDivergentOptions` name the oracles and the measured mutations.

**Receiver settings stay per receiver.** The queue, error queue, description, transaction mode, dead-letter service and
maximum receive attempts are arguments to `AddQueueReceiver`, which records them on that receiver's `ReceiverOptions`.
None of them is part of the compared configuration, so two feeds or two receivers that differ only in those settings
register side by side.

**The settings are reflected, and printing them is an allowlist.** `SqlServiceBrokerTransportSettings.SnapshotOf`
reads every public readable, non-indexed instance property of `SqlServiceBrokerOptions` once, and `FindDivergences`
compares the two snapshots with `object.Equals`. A property added to the type later takes part in the comparison with
no change at the comparison site. A diverging value is printed only when its property carries the internal
`SafeToPrintAttribute`; every other value renders as `(redacted)`. The seven non-credential settings carry the
attribute and `ConnectionString` does not, so a property added later stays hidden until someone decides its value is
not a credential. The `INVARIANT:` comments on the reflected property set, on the rendering and on
`SafeToPrintAttribute` name the oracles and the measured mutations.

### Both doors write only through one deferred phase

**`AddSqlServiceBroker`.** The method runs the options delegate, then `Build()`, then the snapshot, the record lookup
and the divergence guard, and only then writes: the shared registrations, the options instance and the record when the
collection carried no record, and last `RegisterPendingRegistrations`, which runs the builder's deferred registrations
in the order they were deferred. From inside the delegate, this package reaches the collection only through the public
`SqlServiceBrokerOptionsBuilder.DeferRegistration(Action<IServiceCollection>)`. `AddQueueReceiver` is built on it: it
runs `AddReceiver` against a throwaway `ServiceCollection`, so `AddReceiver`'s own refusals (a message type that already
carries `BrokeredMessageAttribute`) are raised at the call that caused them, and then defers the same registration. A
call refused by the divergence guard, or by `Build()`'s `ArgumentNullException` for missing options, a blank connection
string or a blank message body type, therefore runs none of its deferred registrations and leaves the caller's
`IServiceCollection` and the shared `IDiscoveredReceiverRegistry` exactly as they were. The `INVARIANT:` comment on
`AddSqlServiceBroker` names the oracles and the measured mutations.

**`AddSqlChangeFeed`.** The generic overload builds its `SqlChangeFeedOptions` and derives its object names first; the
options builder's constructor, its `Build()` and `ChangeFeedObjectNames.DeriveFrom` raise the refusals for a blank
connection string or table name, a malformed connection string, a missing database and colliding object names. The
build step receives the options builder's `Build` as a delegate, so it has no collection in scope. The method then
makes one `AddSqlServiceBroker` call whose delegate sets the feed's transport options and defers, in this order, the
queue receiver, the `ISqlDependencyManager<TRowChangedData>` registration and, when row change events are emitted, the
`Replace` of the receiver with `ChangeFeedReceiver<TRowChangedData>`. The method makes no write of its own, so the
refusals `AddSqlServiceBroker` raises for the feed (a blank message body type, a divergent transport) run none of the
feed's registrations, and the `Replace` runs after the receiver descriptor it replaces. The `INVARIANT:` comment on
`AddSqlChangeFeed<TRowChangedData>` names the oracles and the measured mutations.

**The row-type overload raises the generic overload's refusals as themselves.** `AddSqlChangeFeed(Type, ...)` invokes
a generic method definition the compiler binds from a method group over a private placeholder row type, never one looked
up by name, and invokes it with `BindingFlags.DoNotWrapExceptions`. Every refusal of the generic overload therefore
reaches the caller unwrapped, with the same exception type. A null row type is refused with an
`ArgumentNullException` naming `rowChangedDataType`; a row type that does not satisfy the generic constraints is refused
by `MakeGenericMethod` with an `ArgumentException`.

**Why the change feed defers rather than stages.** ADR-0032 stages a reliability door's mutations on a copy of the
caller's descriptors and commits them at the end. That shape does not hold for either door here. `AddReceiver` records
each receiver's `ReceiverOptions` in the `IDiscoveredReceiverRegistry` instance held by that registry's singleton
descriptor, and a copied descriptor holds the same instance, so a receiver registered against a staged copy would
mutate the live registry before the divergence guard ran, and discarding the copy would not undo it. The change feed
has a second reason: its `Replace` must land after the receiver descriptor `AddSqlServiceBroker` writes in its drain.
Staging the feed's own writes would need a commit that runs after `AddSqlServiceBroker` returns, a second commit
protocol beside the drain, and would still share the registry. Deferring into the one drain gives both packages a single
ordering point: every refusal of both doors lands before it, and the order of the `DeferRegistration` calls fixes the
order of the writes.

**Why `DeferRegistration` is public.** The SQL Service Broker assembly grants its internals only to its own test
assembly and to Castle DynamicProxy's proxy assembly, not to `Chatter.SqlChangeFeed`. A change feed can only reach the
drain through a public member, and a host or extension that registers alongside `AddQueueReceiver` gets the same
refusal behavior from it.

## Consequences

- **A second divergent registration fails at start-up instead of misconfiguring earlier receivers.** This is a
  behavior change for any host that registered two differing configurations, including two `AddSqlChangeFeed` calls
  with different `ServiceBrokerOptions`. Such a host was already running every receiver against the last
  configuration.
- **Equivalent multi-feed and feed-plus-broker hosts keep working.** Each call's receivers are registered against the
  one options instance. The change feed builds its transport options with a conversation lifetime of `int.MaxValue`,
  while the connection-string overload of `SqlServiceBrokerOptionsBuilder.AddSqlServiceBrokerOptions` defaults it to
  `0`, so a default change feed and a default `AddSqlServiceBroker` call in one host diverge and the later one is
  refused. This decision leaves both defaults as they are.
- **A refused `AddSqlChangeFeed` registers nothing, through either overload.** Before this change the feed wrote its
  `ISqlDependencyManager<TRowChangedData>` registration before `AddSqlServiceBroker` could refuse, and the row-type
  overload surfaced every refusal as a `TargetInvocationException`.
- **`AddSqlServiceBrokerOptions()` with `AddQueueReceiver` and no `AddSqlServiceBroker` call registers no receiver.**
  `AddQueueReceiver` now defers, and only `AddSqlServiceBroker` runs the deferred registrations. That combination
  registered no transport before this change either, so it produced no working receiver then.
- **`SqlServiceBrokerOptionsBuilder.DeferRegistration` is new public surface.** It null-checks its argument, returns
  the builder, and is the one queue `AddQueueReceiver` and the change feed write through.
- **Each collection that `AddSqlServiceBroker` has configured carries one extra internal descriptor**, the
  `SqlServiceBrokerTransportRegistration` singleton instance. A host that enumerates its service collection sees it; it
  is never resolved by Chatter at run time.
- **Other shared registrations are unchanged.** An equivalent second call still re-registers the predicate providers,
  the `IMessagingInfrastructure` factory and the body converter, which were already harmless (see *Context*).
- **Versioning.** Both packages are 0.x, and a registration that used to be accepted is now refused, so each takes a
  minor bump: `Chatter.MessageBrokers.SqlServiceBroker` 0.17.0 and `Chatter.SqlChangeFeed` 0.16.0. One public method
  is added, `SqlServiceBrokerOptionsBuilder.DeferRegistration`; no existing public signature changes.
- **Scope boundary.** Per-receiver or per-queue transport configuration is out of scope and tracked in #542. This ADR
  makes one configuration per host explicit and enforced; it does not add a second.

## Recorded residuals

Each residual below is inherited: the behavior was the same before this change. Each is bounded, and its obvious
remediation was considered and rejected for the reason given. None has a tracking issue.

**R1 — a service collection that throws from a write leaves the writes before it in place.** Root cause: the write
phase of `AddSqlServiceBroker`, including the drain, runs against the caller's collection, and nothing can undo an
`Add` on an arbitrary `IServiceCollection`. A decorating collection that throws partway through keeps the writes it
accepted; a collection the host made read-only throws on the first write, before any lands. Bounded impact: only a
host-supplied collection can throw from `Add`, the exception propagates to the host's own start-up, and the host does
not start. Rejected remediation: staging and committing, as ADR-0032 does, moves the throwing write into the commit
without removing it, which is the same residual ADR-0032 records, and a compensating rollback would replay `Remove`
calls that such a collection also observes.

**R2 — a write through the public `Services` property inside the delegate lands immediately.**
`SqlServiceBrokerOptionsBuilder.Services` and `SqlChangeFeedOptionsBuilder.Services` are public, and a write an
extension makes through either one from inside the options delegate reaches the caller's collection before any refusal,
and stays after one. Bounded impact: neither package writes through them from inside the delegate; only a caller's
own extension does, and `DeferRegistration` is the provided path that runs after the last refusal. Rejected
remediation: removing the
properties, or handing the delegate a collection that records writes instead of making them, changes public surface
that existing extensions read, and a recording collection would hide the caller's own earlier registrations from any
extension that inspects `Services`.

**R3 — an options singleton the host registers after `AddSqlServiceBroker` still wins at resolution.** Root cause:
Microsoft DI resolves the last `SqlServiceBrokerOptions` descriptor, and this package inspects only the registrations
it makes. Later `AddSqlServiceBroker` calls keep comparing with the record, so they neither detect nor refuse such a
descriptor. Bounded impact: the host wrote that descriptor itself, after the call, and gets the configuration it
registered. Rejected remediation: resolving the options through the record, or validating at start-up, would override
or refuse a registration the host made on purpose and adds a start-up component to a registration-time decision.

**R4 — a foreign options descriptor present before the first call is overridden, not refused.** Root cause: a collection
with no record has no transport this package registered, so the first call registers its own options instance after
the foreign descriptor, and Microsoft DI resolves the later one. Bounded impact: the effective configuration is the
one the `AddSqlServiceBroker` call configured, as before this change, and every later call compares with it.
Rejected remediation: refusing the first call when any options descriptor exists would break a host that registers
options ahead of the call, and a descriptor registered by type or by factory has no instance to compare with, which is
the provenance problem the record exists to avoid.

**R5 — concurrent `AddSqlServiceBroker` calls on one collection are unsupported.** Root cause: the record lookup and
the record write are separate steps over a collection that is not thread-safe, so two concurrent first calls can both
find no record. Bounded impact: service registration runs on one thread during host building, and
`IServiceCollection` itself makes no concurrency promise. Rejected remediation: a lock needs an object every caller
shares, and the collection offers none that a host-supplied implementation is bound to honor.

**Unpinned.** Two further edges are pinned by no test. Overload-selection drift
in the row-type `AddSqlChangeFeed` is prevented only by the compiler binding: reverting to a name lookup of the first
generic `AddSqlChangeFeed` keeps every test green. A record descriptor copied into another collection without the
options descriptor that came with it makes an equivalent call there register no options.

## Closed-by-Construction Acceptance Test

> What class of future finding does this make impossible, and why?

**Eliminated: an options descriptor the guard cannot interpret.** The guard's key moved from the
`SqlServiceBrokerOptions` descriptor to a record only `AddSqlServiceBroker` writes. Findings of the shape "a descriptor
registered by type, by factory or by the host bypasses the comparison" and "a caller mutating its options instance after
registration moves the baseline" cannot arise, because the guard reads no options descriptor and the record holds a
frozen snapshot.

**Eliminated: a diverging value printed that should not be.** Rendering is an allowlist. A value appears only for a
property carrying `SafeToPrintAttribute`, so a setting added to `SqlServiceBrokerOptions` later, credential or not, is
redacted until someone marks it, rather than printed until someone excludes it.

**Eliminated: a refused registration leaving a deferred write behind, in either door.** Both doors write only through
the one drain that `AddSqlServiceBroker` runs after its last refusal, and `AddSqlChangeFeed` has no write of its own. A
registration added later through `DeferRegistration`, by either package or by a host, inherits the refusal behavior
with no change at the refusal sites.

**Eliminated: the row-type overload diverging from the generic overload in what it raises or which method it
calls.** The target is bound by the compiler, not found by name, and the call does not wrap exceptions, so a refusal
added to the generic overload later reaches the row-type caller as itself.

**Not closed.** A direct write added to either door ahead of its last refusal would reintroduce the observable refusal.
Nothing structural stops that edit; the refusal tests in both packages catch it for the refusals they enumerate. The
residuals R1 to R5 and the two unpinned edges above stay open as recorded.

## Amendment: what the review changed

**Amended 2026-09-29**, from review of this branch, before release.

**What the review found.** The guard compared with the instance held by the last `SqlServiceBrokerOptions` descriptor,
so a descriptor registered by type or by factory read as absent, a foreign or copied descriptor was compared as if this
package had registered it, and a caller that changed its options instance after registration moved the baseline.
Rendering printed every value except the connection string, so a credential-bearing setting added later would have
been printed. `AddSqlChangeFeed` registered its `ISqlDependencyManager<TRowChangedData>` before `AddSqlServiceBroker`
could refuse, so a refused feed was not refusal-free. The row-type overload found the generic method by name and
invoked it without `DoNotWrapExceptions`, so each refusal reached the caller as a `TargetInvocationException`.

**What changed.** The comparison helper `SqlServiceBrokerOptionsEquivalence` was replaced by the
`SqlServiceBrokerTransportSettings` snapshot and its allowlist rendering. The guard's descriptor read was replaced by
the owned `SqlServiceBrokerTransportRegistration` record. The builder's internal receiver queue became the public
`DeferRegistration`, and the change feed moved every write into it. The row-type overload is compiler-bound and
invoked with `DoNotWrapExceptions`.

**Rejected narrower variant: teach the descriptor read about type and factory registrations.** It would have answered
the first finding as filed and left the next one of the same shape (a foreign instance, a mutated instance, a copied
descriptor) to be found. Under the Same-Framing Test each would have been the same finding with a different
descriptor shape, so the key was changed instead.

## References

- Issue #531 — the SQL Change Feed options defects, including the last-registration-wins options singleton this ADR
  resolves.
- Issue #542 — per-receiver transport options, the capability this ADR defers.
- ADR-0032 — *The reliability registration door stages its mutations, so a refusal cannot be observed*. The staged-copy
  shape this ADR does not use, why, and the read-only-collection residual R1 shares.
- ADR-0027 — *An `INVARIANT:` comment names the oracle that falsifies it*. The rule the `INVARIANT:` comments cited
  here follow.
- `src/Chatter.MessageBrokers.SqlServiceBroker/src/Chatter.MessageBrokers.SqlServiceBroker/DependencyInjection/Extensions.cs`
  — `AddSqlServiceBroker`, `RefuseDivergentOptions` and `AddQueueReceiver`, with their `INVARIANT:` comments.
- `src/Chatter.MessageBrokers.SqlServiceBroker/src/Chatter.MessageBrokers.SqlServiceBroker/DependencyInjection/SqlServiceBrokerTransportRegistration.cs`
  — the transport record, `Record` and `Find`.
- `src/Chatter.MessageBrokers.SqlServiceBroker/src/Chatter.MessageBrokers.SqlServiceBroker/Configuration/SqlServiceBrokerOptionsBuilder.cs`
  — `Build()`, `DeferRegistration` and `RegisterPendingRegistrations`.
- `src/Chatter.MessageBrokers.SqlServiceBroker/src/Chatter.MessageBrokers.SqlServiceBroker/Configuration/SqlServiceBrokerTransportSettings.cs`
  and `Configuration/SafeToPrintAttribute.cs` — the reflected snapshot, the comparison and the allowlist rendering.
- `src/Chatter.SqlChangeFeed/src/Chatter.SqlChangeFeed/DependencyInjection/SqlChangeFeedExtensions.cs` — both
  `AddSqlChangeFeed` overloads, with their `INVARIANT:` comments.
- `src/Chatter.MessageBrokers.SqlServiceBroker/tests/DependencyInjection/UsingExtensions/WhenAddingSqlServiceBroker.cs`
  and `tests/Configuration/UsingSqlServiceBrokerTransportSettings/WhenSnapshotting.cs` — the idempotence, refusal,
  no-mutation, record and rendering oracles.
- `src/Chatter.SqlChangeFeed/tests/UsingSqlChangeFeedExtensions/WhenAddingSqlChangeFeed.cs` — the change feed's
  refusal, no-mutation and overload oracles.
- `src/Chatter.MessageBrokers.RabbitMQ/src/Chatter.MessageBrokers.RabbitMQ/DependencyInjection/Extensions.cs` — the
  registration-time `NotSupportedException` precedent.
