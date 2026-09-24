---
status: accepted
date: 2026-09-24
---

# The duplicate command-handler check probes the assembly set the registration scanned

`ThrowOnDuplicateCommandHandlers()` (ADR-0017) now checks the assemblies `AddChatterCqrs` scanned, instead of
applying the assembly source filter a second time. This ADR records why that set is carried internally on the
builder, why the filter itself was not changed, and why `AddMessageBrokers` still applies the filter afresh.

Issue #468.

## Context

**The check applied the filter a second time.** Before this change `AddChatterCqrs` applied its
`AssemblySourceFilter` once to register handlers and handed the same filter instance to the builder, and
`ThrowOnDuplicateCommandHandlers()` called `chatterBuilder.AssemblySourceFilter.Apply()` again. ADR-0017 recorded this
as a tracked non-goal: the same filter instance does not guarantee the same assembly set.

**What a second `Apply()` reads.** `Apply()` calls `IAssemblyFilterSourceProvider.GetSourceAssemblies()` once per
call, while it builds its result (`AssemblySourceFilter.cs:56-59,64-66`); only the `Where` over the returned sequence
is deferred. In explicit-assembly-only mode it does not call the provider at all. The default provider returns
`AppDomain.CurrentDomain.GetAssemblies()` minus dynamic assemblies (`CurrentAppDomainAssemblyProvider.cs:22`), an
array taken when `Apply()` runs. So with the default provider the three registration scans inside `AddChatterCqrs`
already saw one set, because they all enumerated one `Apply()` result. The check was a second `Apply()` call, and took
a new read of the AppDomain.

**What that did to the check.** An assembly loaded between registration and the check, including one loaded by the
registration's own type scan, joined the probe's set without being in the registration's. If it held a second
handler for a Command, the check failed a composition that had registered cleanly. That growth case is the one #468
reports. ADR-0017's non-goal also records the opposite direction as reachable: a set that shrinks, for example after
a collectible `AssemblyLoadContext.Unload()`, leaves the check silent about a displacement that really happened.

**What a consumer can install.** `AddChatterCqrs` builds its filter from `AssemblySourceFilterBuilder.New()`
(`CqrsExtensions.cs:32-34`). Its delegate receives that builder, whose public methods set a namespace selector,
marker types or explicit assemblies, but no provider. The public static
`AssemblySourceFilterBuilder.WithAssemblySourceProvider` returns a different builder, which `AddChatterCqrs` never
sees, and `Build()` falls back to `CurrentAppDomainAssemblyProvider` when no provider is set
(`AssemblySourceFilterBuilder.cs:98-102`). A provider whose returned sequence re-evaluates on every enumeration can
reach `AddChatterCqrs` only through the internal `WithSourceProvider` test seam this change adds
(`AssemblySourceFilterBuilder.cs:34-42`). A consumer can still pass any `IAssemblySourceFilter` to the public
`ChatterBuilder.Create`, and any `IChatterBuilder` implementation to the check.

## Considered Options

### Option A — materialize the set once in `AddChatterCqrs` and carry it internally on the builder (ACCEPTED)

Recorded under *Decision*.

### Option B — memoize `AssemblySourceFilter.Apply()` (REJECTED)

The filter instance is shared. When no receiver source builder is supplied, `AddMessageBrokers` reads
`builder.AssemblySourceFilter` and applies it again (`ChatterMessageBrokerExtensions.cs:82,89`), then scans those
assemblies for `IMessage` classes that carry `[BrokeredMessage]` with a receiver name
(`FindBrokeredMessagesWithReceiversInAssembliesByType`, `ChatterMessageBrokerExtensions.cs:187-193`). Such message
classes commonly live in a contracts assembly, which may first load during `AddChatterCqrs`'s own type scan, when a
handler for one of its messages is inspected. With the default provider, the fresh `Apply()` in `AddMessageBrokers`
sees that assembly. A memoized first result would not, and on the default path the receivers for those messages
would be dropped without an error. No test in this repository pins that load-order case; it follows from the runtime
loading an assembly the first time one of its types is needed.

Two further objections. A `Lazy<T>` in `ExecutionAndPublication` mode caches an exception thrown by its factory, so
one failed `Apply()` would fail every later call. And `Apply()` is a public method on a public type, documented as
applying the filter against the provider, so memoizing it changes its contract for every caller, not only for the
check.

### Option C — expose the scanned set as a new public `IChatterBuilder` member (REJECTED)

It expands the public API of `Chatter.CQRS`, which ADR-0017 names as a locked constraint, and every other
implementation of the public `IChatterBuilder` interface would have to supply the member.

### Option D — wrap the filter handed to `ChatterBuilder` so that it replays the scanned set (REJECTED)

The builder would no longer expose the filter instance `AddChatterCqrs` built, which
`WhenGettingProperties.MustGetMarkerAssemblies` pins
(`src/Chatter.CQRS/tests/DependencyInjection/UsingChatterBuilder/WhenGettingProperties.cs:36-37`), and
`AddMessageBrokers` would scan the replayed set, which is the narrowing Option B rejects.

## Decision

**`AddChatterCqrs` materializes the filter's result once.** It calls `filter.Apply().ToList()`
(`CqrsExtensions.cs:47`), feeds that one list to the command, event and query scans (`CqrsExtensions.cs:50-51`), and
passes it to an internal four-argument `ChatterBuilder.Create` (`CqrsExtensions.cs:48`, `ChatterBuilder.cs:52-53`),
which stores it in the internal `ScannedAssemblies` property (`ChatterBuilder.cs:31`). The rationale lives once, in
the `INVARIANT:` at `CqrsExtensions.cs:35-46`, whose oracles are
`WhenAddingChatterCqrs.MustReadTheAssemblySourceOnlyOnceForEveryRegistrationScan` and
`MustRegisterHandlersFromOneAssemblySetEvenWhenTheSourceGrowsBetweenScans`.

**The check probes the carried set** (`GetAssembliesToProbe`, `CqrsExtensions.cs:127-138`). Pinned by
`WhenThrowingOnDuplicateCommandHandlers.MustProbeTheAssemblySetCapturedAtRegistrationWhenTheSourceGrowsAfterwards`.

**A builder that carries no set is probed through its filter, as before.** That is a foreign `IChatterBuilder`, or a
`ChatterBuilder` from the public three-argument `Create`, whose `ScannedAssemblies` is `null` and never empty: an
empty set would make the check pass without looking. Pinned by `MustProbeTheFilterWhenTheBuilderCarriesNoScanSet`,
which builds its builder with the public `Create`.

**No public API changes.** The four-argument `Create`, `ScannedAssemblies` and `WithSourceProvider` are internal.
`AssemblySourceFilter.Apply()` is unchanged, and `Chatter.MessageBrokers` is not touched.

### Why `AddMessageBrokers` keeps applying the filter

`AddMessageBrokers` answers a different question: which message classes have receivers. It runs after
`AddChatterCqrs`, and for the reason given under Option B it should see the assemblies loaded by then. Nothing
compares its result with the handler scan, so it has no reason to share the handler scan's set. It is left as it was
on purpose.

## Closed-by-Construction Acceptance Test

> What class of future finding does this make impossible, and why?

**ELIMINATED CLASS, for `AddChatterCqrs` followed by `ThrowOnDuplicateCommandHandlers()`: the check and the
registration disagreeing about which assemblies were scanned.** Both read one materialized list, and nothing on that
path enumerates the filter a second time, so no provider behaviour and no assembly load or unload between the two can
make them differ. The growth direction and the shrink direction ADR-0017 records are both closed on this path.

**What it does NOT close.** A builder that carries no scanned set, a foreign `IChatterBuilder` or one from the public
`ChatterBuilder.Create`, is still probed by applying its filter again and keeps the old gap in both directions.
`AddMessageBrokers` also still applies the filter again, by choice.

## Consequences

- **With the default provider the three registration scans see no change.** They already enumerated one `Apply()`
  result, and that result is fixed when `Apply()` runs. What a consumer can observe is the check's change: it no
  longer takes a second read of the AppDomain.
- **The registration scans change only for a provider whose returned sequence re-evaluates on every enumeration**,
  and `AddChatterCqrs` gives a consumer no public way to install one (see *Context*). What changes for such a provider
  is stated once, in the `INVARIANT:` at `CqrsExtensions.cs:35-46`.
- **The fallback path keeps the old behaviour.** A builder that carries no set is probed through a fresh `Apply()`,
  with the gap ADR-0017 records.
- **Enabling the check reads the source less.** With the default provider in namespace or unbounded mode, the check
  used to cost a second `AppDomain.GetAssemblies()` read and a second pass of the namespace selector over every source
  assembly's types. On the carried path it costs neither. The check's own Scrutor scan of the scanned assemblies
  remains; that scan is what makes the probe agree with the registration (ADR-0017).
- **`WithSourceProvider` is an internal test seam.** It refuses a null provider rather than letting `Build()` widen
  the scan to the whole AppDomain; the `INVARIANT:` at `AssemblySourceFilterBuilder.cs:36-39` names the oracle,
  `WhenSettingAssemblySourceProvider.MustThrowWhenTheSourceProviderIsNull`.
- **ADR-0017 is amended in place.** Its tracked non-goal on the re-derived scan set is resolved for the carried path,
  and the passages that described the check as re-applying the filter carry dated amendments pointing here.

## References

- Issue #468 — *Assembly scan set is re-enumerated per consumer, not snapshotted at registration*. The defect this
  ADR resolves.
- Issue #449 — *Command-handler displacement during assembly scanning is silent*. The report the check answers.
- Epic #301 — *Chatter.CQRS — handler registration correctness and per-dispatch hot path*.
- ADR-0017 — *Duplicate command handlers: an opt-in composition-time check, and a default that does not move*. Records
  the gap as a tracked non-goal, and the two constraints this decision keeps: no new public `IChatterBuilder` member,
  and no narrowing of what `AddMessageBrokers` scans.
- ADR-0027 — *An `INVARIANT:` comment names the oracle that falsifies it, and states its rationale once*. Why this ADR
  cites the `INVARIANT:` at `CqrsExtensions.cs:35-46` rather than restating it.
- `src/Chatter.CQRS/src/Chatter.CQRS/DependencyInjection/CqrsExtensions.cs` — the materialization (`:35-51`) and
  `GetAssembliesToProbe` (`:127-138`).
- `src/Chatter.CQRS/src/Chatter.CQRS/DependencyInjection/ChatterBuilder.cs` — `ScannedAssemblies` (`:31`) and the
  internal `Create` (`:52-53`).
- `src/Chatter.CQRS/src/Chatter.CQRS/DependencyInjection/AssemblySourceFilter.cs` — `Apply()` (`:56-59`) and the
  provider call (`:64-66`).
- `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/DependencyInjection/ChatterMessageBrokerExtensions.cs` — the
  receiver scan's filter read (`:82,89`) and `FindBrokeredMessagesWithReceiversInAssembliesByType` (`:187-193`).
