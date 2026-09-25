---
status: accepted
date: 2026-09-24
---

# The duplicate command handler check reads the scan record on the service collection

`ThrowOnDuplicateCommandHandlers()` (ADR-0017) now checks the assemblies that every `AddChatterCqrs` call on the
builder's `IServiceCollection` scanned, instead of applying the assembly source filter a second time. `AddChatterCqrs`
records those assemblies in an internal bookkeeping descriptor on the collection. This ADR records why the record is
kept on the collection rather than on the builder, why the filter itself was not changed, why `AddMessageBrokers`
still applies the filter afresh, and corrects a constraint that earlier text on this surface presented as locked.

Issue #468.

## Context

**The check applied the filter a second time.** `AddChatterCqrs` applied its `AssemblySourceFilter` to register
handlers and handed the same filter instance to the builder, and `ThrowOnDuplicateCommandHandlers()` called
`chatterBuilder.AssemblySourceFilter.Apply()` again. ADR-0017 recorded this as a tracked non-goal: the same filter
instance does not guarantee the same assembly set.

**What a second `Apply()` reads.** `Apply()` calls `IAssemblyFilterSourceProvider.GetSourceAssemblies()` once per
call, while it builds its result (`AssemblySourceFilter.cs:56-59,64-66`); only the `Where` over the returned sequence
is deferred. In explicit-assembly-only mode it does not call the provider at all. The default provider returns
`AppDomain.CurrentDomain.GetAssemblies()` minus dynamic assemblies (`CurrentAppDomainAssemblyProvider.cs:22`), an
array taken when `Apply()` runs. So a second `Apply()` takes a new read of the AppDomain. An assembly loaded between
registration and the check, including one loaded by the registration's own type scan, joined the probe's set without
being in the registration's. If it held a second handler for a Command, the check could report a handler that was
never registered and fail a composition that had registered cleanly. That is the case #468 reports. ADR-0017's
non-goal also records the opposite direction: a set that shrinks, for example after a collectible
`AssemblyLoadContext.Unload()`, leaves the check silent about a displacement that really happened.

**The first fix was keyed on the builder, and local review found two more cases.** The first revision of this change
carried the scanned set on the concrete `ChatterBuilder` (Option D). Review of that revision found:

- **F1: two `AddChatterCqrs` calls on one `IServiceCollection`.** Command handlers are registered with
  `RegistrationStrategy.Replace()` (`CqrsExtensions.cs:209-210`), so the second call's command scan can displace a
  handler the first call registered. Each builder carried only its own call's set, so a check on either builder saw
  one candidate and reported nothing.
- **F2: a transparent `IChatterBuilder` wrapper.** A builder that forwards every member to the one `AddChatterCqrs`
  returned is not a `ChatterBuilder`, so the set carried on the concrete type was out of reach and the check fell
  back to re-applying the filter, with the #468 gap.

**What a consumer can install.** `AddChatterCqrs` builds its filter from `AssemblySourceFilterBuilder.New()`
(`CqrsExtensions.cs:32-34`). Its delegate receives that builder, whose public methods set a namespace selector, marker
types or explicit assemblies, but no provider. The public static
`AssemblySourceFilterBuilder.WithAssemblySourceProvider` returns a different builder, which `AddChatterCqrs` never
sees, and `Build()` falls back to `CurrentAppDomainAssemblyProvider` when no provider is set
(`AssemblySourceFilterBuilder.cs:98-102`). A provider whose returned sequence re-evaluates on every enumeration can
reach `AddChatterCqrs` only through the internal `WithSourceProvider` test seam
(`AssemblySourceFilterBuilder.cs:34-42`). A consumer can still pass any `IAssemblySourceFilter` to the public
`ChatterBuilder.Create`, and any `IChatterBuilder` implementation to the check.

## Considered Options

### Option A — a public `IChatterBuilder` default interface member, `ScannedAssemblies => null` (REJECTED)

Rejected on its merits, not because public API was ruled out (see *Correction*). A wrapper written against the
interface before the member existed, or one that does not override it, inherits `null` and falls back to re-applying
its filter, so F2 stands. A member on one builder is set by one `AddChatterCqrs` call and cannot see another call on
the same collection, so F1 stands. It would add permanent public surface to `Chatter.CQRS` and close nothing that
Option C does not close without it.

### Option B — record the duplicate evidence during registration (REJECTED)

`AddChatterCqrs` cannot know whether the opt-in check will be called. Recording the evidence during registration
means one of two things. Either every consumer pays a second full Scrutor scan in append mode on every
`AddChatterCqrs` call, for a feature that is off by default. Or Scrutor's replace semantics are re-implemented by hand
so that the registration scan itself can observe each displacement, in the most load-bearing registration code in the
module. Both still need somewhere to keep the evidence that every call and every builder can reach, which is the
question Option C answers on its own.

### Option C — a scan record keyed on the service collection (ACCEPTED)

Recorded under *Decision*.

### Option D — carry the set on the concrete `ChatterBuilder` (SUPERSEDED)

The first revision of this change. `AddChatterCqrs` passed the materialized set to an internal `ChatterBuilder.Create`
overload, which stored it in an internal property, and the check read it through an `as ChatterBuilder` cast. It was
keyed on builder identity, and failed both F1 and F2.

### Option E — memoize `AssemblySourceFilter.Apply()` (REJECTED)

The filter instance is shared. When no receiver source builder is supplied, `AddMessageBrokers` reads
`builder.AssemblySourceFilter` and applies it again (`ChatterMessageBrokerExtensions.cs:82,89`), then scans those
assemblies for `IMessage` classes that carry `[BrokeredMessage]` with a receiver name
(`FindBrokeredMessagesWithReceiversInAssembliesByType`, `ChatterMessageBrokerExtensions.cs:187-193`). Such message
classes commonly live in a contracts assembly, which may first load during `AddChatterCqrs`'s own reflection, when a
handler for one of its messages is inspected. With the default provider, the fresh `Apply()` in `AddMessageBrokers`
sees that assembly. A memoized first result would not, and the receivers for those messages would be dropped without
an error. The owner rejected this option on that ground. No test in this repository pins that load-order case; it
follows from the runtime loading an assembly the first time one of its types is needed.

Two further objections. A `Lazy<T>` in `ExecutionAndPublication` mode caches an exception thrown by its factory, so
one failed `Apply()` would fail every later call. And `Apply()` is a public method on a public type, documented as
applying the filter against the provider, so memoizing it changes its contract for every caller, not only for the
check.

### Option F — wrap the filter handed to `ChatterBuilder` so that it replays the scanned set (REJECTED)

`AddMessageBrokers` would then scan the replayed set, which is the narrowing Option E rejects. Done inside
`ChatterBuilder`, the wrap also breaks the same-instance guarantee that
`WhenGettingProperties.MustGetMarkerAssemblies` pins: a builder from the public `Create` returns the filter it was
given (`src/Chatter.CQRS/tests/DependencyInjection/UsingChatterBuilder/WhenGettingProperties.cs:36-37`). Done in
`AddChatterCqrs` before `Create`, that test stays green, but the builder then exposes a filter the consumer never
configured.

### Option G — a `ConditionalWeakTable` keyed on the collection instead of a descriptor (REJECTED)

It is hidden process-wide state, and it is lost when a host copies the descriptors into a new collection. A
descriptor is the standard .NET marker-service idiom (ASP.NET Core's `MvcMarkerService` is one example), and it
travels with the collection's other descriptors, including the handler registrations it describes.

## Decision

**`AddChatterCqrs` materializes the filter's result once.** It calls `filter.Apply().ToList()`
(`CqrsExtensions.cs:47`) and feeds that one list to the command, event and query scans (`CqrsExtensions.cs:50-51`).
The rationale lives once, in the `INVARIANT:` at `CqrsExtensions.cs:35-46`.

**It records that list on the collection.** `HandlerScanRecord.GetOrAdd(chatterBuilder.Services).Record(...)`
(`CqrsExtensions.cs:52`) finds the collection's `HandlerScanRecord`, or adds one as a singleton-instance descriptor
(`HandlerScanRecord.cs:51-63`), and adds each scanned assembly not already recorded (`HandlerScanRecord.cs:34-43`). A
collection carries one record however many `AddChatterCqrs` calls it receives, and the record accumulates the
assemblies of every call. Why the record is keyed on the collection is stated in the `INVARIANT:` at
`HandlerScanRecord.cs:12-20`; the one-record rule in the `INVARIANT:` at `HandlerScanRecord.cs:47-49`.

**The check reads the record through `IChatterBuilder.Services`** (`GetAssembliesToProbe`,
`CqrsExtensions.cs:129-143`), with no cast to a concrete builder. A collection that carries no record, including a
`null` collection (`HandlerScanRecord.cs:69-73`), falls back to re-applying the builder's filter, never to an empty
set: an empty set would make the check pass without looking. Both rules are stated in the `INVARIANT:` blocks at
`CqrsExtensions.cs:131-141`.

**No public API changes.** `HandlerScanRecord` and `WithSourceProvider` are internal. `IChatterBuilder`,
`ChatterBuilder` and `AssemblySourceFilter.Apply()` are unchanged, and `Chatter.MessageBrokers` is not touched.

### Why `AddMessageBrokers` keeps applying the filter

`AddMessageBrokers` answers a different question: which message classes have receivers. It runs after
`AddChatterCqrs`, and for the reason given under Option E it should see the assemblies loaded by then, including a
contracts assembly that the handler scan's reflection loaded. Nothing compares its result with the handler scan, so
it has no reason to share the handler scan's set. It is left as it was on purpose.

## Correction

The previous revision of this ADR, and ADR-0017's non-goal on the re-derived scan set, stated "no new public
`IChatterBuilder` member" as a constraint locked with the user. It was never a user decision. It first appears in the
#468 issue body, filed as a finding during the local review of the work closing #449 and #451. That text called it a
constraint "locked with the user for the originating initiative", but the reason it gave for deferring the fix was
that neither route "belongs in a review-remediation pass", which is a statement about the scope of that pass, not about
the public surface. The label was then carried forward as if the user had set it.

The only user decisions for this surface are that the check is opt-in and off by default, and that it ships as a
public extension method on `IChatterBuilder` (#449, #451). Option A above is rejected on its merits for that reason.

## Closed-by-Construction Acceptance Test

> What class of future finding does this make impossible, and why?

**ELIMINATED CLASS: "builder X does not carry the set".** The key changed from builder identity to the
`IServiceCollection`. The collection is the one object that every `AddChatterCqrs` call on a container shares, and
the one member that the public `IChatterBuilder` contract obliges every implementation, a wrapper included, to expose.
So a second `AddChatterCqrs` call, a builder from an earlier call, and a forwarding wrapper all reach the same record.
And on the recorded path nothing enumerates the filter a second time, so no provider behaviour and no assembly load or
unload between registration and the check can make them disagree. Both the growth and the shrink direction ADR-0017
records are closed on that path.

**What it does NOT close** is listed under *Consequences* as recorded residuals R1 to R3. `AddMessageBrokers` also
still applies the filter again, by choice.

## Consequences

- **An opted-in consumer that calls `AddChatterCqrs` more than once can now see composition fail where it passed.**
  This happens only when a Command genuinely has two scanned handlers across the calls, one of which displaced the
  other. It is a behaviour change, so `Chatter.CQRS` takes a MINOR bump, to 0.20.0. Pinned by
  `WhenThrowingOnDuplicateCommandHandlers.MustReportCompetingHandlersAcrossTwoAddChatterCqrsCallsOnOneServiceCollection`
  and `MustReportCompetingHandlersWhenCheckedOnTheBuilderFromTheEarlierCall`.
- **The #468 false report is gone on the recorded path.** Pinned by
  `MustProbeTheAssemblySetCapturedAtRegistrationWhenTheSourceGrowsAfterwards`, and through a wrapper by
  `MustProbeTheScanRecordThroughABuilderThatOnlyForwardsItsServices`.
- **Every collection that `AddChatterCqrs` sees gains one internal bookkeeping descriptor.** Pinned as one record per
  collection by `WhenAddingChatterCqrs.MustRecordTheHandlerScanOnceHoweverManyTimesChatterCqrsIsAdded`.
- **Enabling the check reads the AppDomain less.** With the default provider in namespace or unbounded mode, the check
  used to cost a second `AppDomain.GetAssemblies()` read and a second pass of the namespace selector over every source
  assembly's types. On the recorded path it costs neither. The check's own Scrutor scan into a throwaway collection
  remains; that scan is what makes the probe agree with the registration (ADR-0017).
- **With the default provider the three registration scans see no change.** They already enumerated one `Apply()`
  result. They change only for a provider whose returned sequence re-evaluates on every enumeration, which a consumer
  cannot install through `AddChatterCqrs` (see *Context*); the `INVARIANT:` at `CqrsExtensions.cs:35-46` states what
  changes for such a provider. Pinned by `WhenAddingChatterCqrs.MustReadTheAssemblySourceOnlyOnceForEveryRegistrationScan`
  and `MustRegisterHandlersFromOneAssemblySetEvenWhenTheSourceGrowsBetweenScans`.
- **`WithSourceProvider` is an internal test seam.** It refuses a null provider rather than letting `Build()` widen the
  scan to the whole AppDomain; the `INVARIANT:` at `AssemblySourceFilterBuilder.cs:36-39` names the oracle,
  `WhenSettingAssemblySourceProvider.MustThrowWhenTheSourceProviderIsNull`.
- **`MustNotReportOneHandlerWhenTheSameAssemblyIsScannedByTwoCalls` is a characterization pin.** No mutation of
  Chatter code reddens it today, because Scrutor 7 collects the scanned types into a set (`ToHashSet`), so one
  assembly scanned twice yields each handler once even without the record's dedupe or the probe's `Distinct()`. It
  guards a Scrutor upgrade that drops that behaviour.

### Recorded residuals

These are decisions, not open work, and no issues are filed for them.

- **R1: a hand-registered command handler that the scan displaces is still not reported.** Root cause: the probe's
  contract is "the scan found two candidates"; it reads only the descriptors its own scan produced. Impact: bounded to
  a consumer who registers a Command handler by hand before `AddChatterCqrs` and also has a scanned handler for the
  same Command. Why not closed: comparing against the application's collection would also flag the deliberate
  overrides other modules make, which ADR-0017 places out of scope.
- **R2: the check reports the collection as it is when the check is called.** Root cause: the record is read when
  `ThrowOnDuplicateCommandHandlers()` runs, so an `AddChatterCqrs` call made after it is not seen. Impact: bounded to
  call order, which the consumer controls. Why not closed: the check is a composition-time call, not a hook on the
  finished container. Guidance: call it after the last `AddChatterCqrs` call.
- **R3: a builder whose `Services` carries no record re-applies its filter, with the old gap.** Root cause: there is
  no record to read. That is a builder from the public `ChatterBuilder.Create` over a collection no `AddChatterCqrs`
  call has seen, or a wrapper that substitutes its own collection. Impact: bounded to builders not produced by
  `AddChatterCqrs` over the collection being checked. Why not closed: such a builder has no registration scan to
  agree with, so its filter is the only statement of what it covers. Pinned by
  `MustProbeTheFilterWhenTheServiceCollectionCarriesNoScanRecord`.

## References

- Issue #468 — *Assembly scan set is re-enumerated per consumer, not snapshotted at registration*. The defect this
  ADR resolves, and the origin of the constraint the *Correction* withdraws.
- Issue #449 — *Command-handler displacement during assembly scanning is silent*. The report the check answers.
- Epic #301 — *Chatter.CQRS — handler registration correctness and per-dispatch hot path*.
- ADR-0017 — *Duplicate command handlers: an opt-in composition-time check, and a default that does not move*. Records
  the re-derived-set gap as a tracked non-goal, and the non-goals R1 and R2 restate.
- ADR-0027 — *An `INVARIANT:` comment names the oracle that falsifies it, and states its rationale once*. Rule 2 is
  why this ADR cites the `INVARIANT:` blocks rather than restating them.
- `src/Chatter.CQRS/src/Chatter.CQRS/DependencyInjection/HandlerScanRecord.cs` — the collection key `INVARIANT:`
  (`:12-20`), `Record` (`:34-43`), `GetOrAdd` and its `INVARIANT:` (`:45-63`), and `Find` (`:69-73`).
- `src/Chatter.CQRS/src/Chatter.CQRS/DependencyInjection/CqrsExtensions.cs` — the materialization and record
  (`:35-52`), `GetAssembliesToProbe` (`:129-143`), and the command scan's replace strategy (`:209-210`).
- `src/Chatter.CQRS/src/Chatter.CQRS/DependencyInjection/AssemblySourceFilter.cs` — `Apply()` (`:56-59`) and the
  provider call (`:64-66`).
- `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/DependencyInjection/ChatterMessageBrokerExtensions.cs` — the
  receiver scan's filter read (`:82,89`) and `FindBrokeredMessagesWithReceiversInAssembliesByType` (`:187-193`).
- `src/Chatter.CQRS/tests/DependencyInjection/UsingCrqsExtensions/WhenThrowingOnDuplicateCommandHandlers.cs` and
  `WhenAddingChatterCqrs.cs` — the oracles named above.
