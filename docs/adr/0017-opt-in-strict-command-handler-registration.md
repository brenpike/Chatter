---
status: accepted
date: 2026-09-12
---

# Duplicate command handlers: an opt-in composition-time check, and a default that does not move

Command handlers are registered by an assembly scan that uses Scrutor's replace strategy —
`AddCommandHandlers` passes `RegistrationStrategy.Replace()` into the scan
(`src/Chatter.CQRS/src/Chatter.CQRS/DependencyInjection/CqrsExtensions.cs:174-175`). When two scanned
types handle the same Command, the last one scanned becomes the single registration and the earlier one
is displaced with no error and no log. Issue #449 is the report of that silence.

The displacement is not merely unreported, it is unpredictable. The scan set is whatever
`AssemblySourceFilter.Apply()` yields (`AssemblySourceFilter.cs:56-59`), which for an unbounded filter is
`AppDomain.CurrentDomain.GetAssemblies()` minus dynamic assemblies
(`CurrentAppDomainAssemblyProvider.cs:17`), and within an assembly the order is the order Scrutor
enumerates `Assembly.DefinedTypes` (Scrutor 3.3.0, `TypeSourceSelector.InternalFromAssemblies`). Neither is
specified anywhere, so which of two competing handlers wins can change between a developer's machine and a
deployment without a line of code changing.

This is the last child of epic #301 on the registration side. Its two predecessors narrowed the scan
rather than the ambiguity: #329 narrowed WHICH assemblies are scanned when marker types or explicit
assemblies are supplied, and #330 narrowed WHICH interfaces a scanned handler is registered as — the
command scan now registers only the command-handler interfaces via
`.As(handler => handler.GetMessageHandlerInterfacesFor(typeof(ICommand)))` (`CqrsExtensions.cs:189`).
Neither changes what happens when two types in the scan set genuinely handle the same Command, which is
what this ADR settles.

## Considered Options

- **Option 1 — Make the check strict by default: throw whenever the scan finds two handlers for one
  Command.** FORBIDDEN, and not on taste. It breaks a currently green test that pins the opposite
  behaviour as intended — `MustReplaceDuplicateRegistrations`
  (`src/Chatter.CQRS/tests/DependencyInjection/UsingCrqsExtensions/WhenAddingCommandHandlers.cs:59-73`)
  asserts that scanning two handlers for one Command leaves exactly one registration, the last scanned —
  and it contradicts what the module documents. Any application that today ships a deliberate override
  handler alongside a base one would stop composing on upgrade. See the standing rule below.

- **Option 2 — A conditionally-registered `IHostedService` that logs the ambiguity at host start.**
  Rejected. It is silent for every `ServiceProvider` built without a host, which is exactly the unit-test
  and library-embedding case where displacement is most confusing — the case where a developer is asking
  why their handler did not run. A diagnostic with a hole that large teaches false confidence in its
  silence. The rejection is about the blind spot, not about a dependency: `Microsoft.Extensions.Hosting` is
  already a package reference of this module on both target frameworks
  (`src/Chatter.CQRS/src/Chatter.CQRS/Chatter.CQRS.csproj:21,27`), so the option was available and was
  still the wrong shape.

- **Option 3 — An `ILoggerFactory` parameter on the registration entry point, logging the ambiguity as a
  warning.** Rejected. It adds public API surface most applications will not wire, and an unwired factory
  produces the same silence the issue reports. `Microsoft.Extensions.Logging.Abstractions` is likewise
  already referenced (`Chatter.CQRS.csproj:20,26`); the objection is the surface and the opt-in-by-accident
  failure mode, not the dependency.

- **Option 4 — An opt-in `IChatterBuilder` extension that fails composition, default off (CHOSEN).**
  `ThrowOnDuplicateCommandHandlers()` throws one `InvalidOperationException` naming every ambiguous Command
  and all its competing handlers. Nothing changes for an application that does not call it. Its weaknesses
  are stated plainly under *Non-goals*: it only sees what the scan sees, and what the scan sees can move
  between two applications of the same filter.

### Why an `IChatterBuilder` extension rather than a parameter

The module's one composition idiom is the `IChatterBuilder` extension — 15 extension methods across the
repository's module source take `this IChatterBuilder`. It has no options object and no `IOptions<T>` type in its source
tree, and it reads no setting for this behaviour out of `IConfiguration`; the `IConfiguration` it is handed
is carried on the builder (`ChatterBuilder.cs:19`) for other modules to use. Adding a parameter to
`AddChatterCqrs` instead would be binary-breaking on a method with five public overloads
(`CqrsExtensions.cs:30,60,70,80,90`), and an all-optional overload would make the existing single-argument
call ambiguous. An extension method is default-off by construction: call it or do not.

## Decision

**The check ships as `public static IChatterBuilder ThrowOnDuplicateCommandHandlers(this IChatterBuilder
chatterBuilder)` in `CqrsExtensions` (`CqrsExtensions.cs:99-109`), in namespace
`Microsoft.Extensions.DependencyInjection` (`CqrsExtensions.cs:15`) so no new `using` is needed at the call
site, returning the same builder instance for chaining (`CqrsExtensions.cs:108`, pinned by
`MustReturnTheSameBuilderWhenNoCommandIsHandledMoreThanOnce`,
`tests/DependencyInjection/UsingCrqsExtensions/WhenThrowingOnDuplicateCommandHandlers.cs:89-95`).**

**It is off unless called.** `AddCommandHandlers` (`CqrsExtensions.cs:174-175`) and all five
`AddChatterCqrs` overloads (`CqrsExtensions.cs:30-50,60-61,70-71,80-81,90-91`) behave exactly as they did
before this work; none of them invokes the check. `AddCommandHandlers` now delegates to the single scan
expression (`CqrsExtensions.cs:182-192`) with `RegistrationStrategy.Replace()`, which produces the same
descriptors, under the same strategy, in the same collection as the inline scan it replaced. The
default-off guarantee is pinned by
`MustRegisterTheLastHandlerScannedWithoutThrowingWhenTheCheckIsNotEnabled`
(`WhenThrowingOnDuplicateCommandHandlers.cs:75-87`), which composes two competing handlers through
`AddChatterCqrs` and asserts both that nothing throws and that the last scanned type is the registration.

**The check re-applies the SAME filter instance the registration used** —
`chatterBuilder.AssemblySourceFilter.Apply()` (`CqrsExtensions.cs:101`). `AddChatterCqrs` builds one filter,
applies it to register handlers, and hands it to the builder (`CqrsExtensions.cs:32-37`), which exposes it
as `IChatterBuilder.AssemblySourceFilter` (`IChatterBuilder.cs:16`, `ChatterBuilder.cs:21`). Same instance
is not the same thing as same set: `Apply()` is a deferred query (`AssemblySourceFilter.cs:56-59`) and, in
namespace or unbounded mode, re-reads `IAssemblyFilterSourceProvider.GetSourceAssemblies()` on every
enumeration, so a second application can see assemblies the first did not. In explicit-assembly mode the
set is stable, because `AssemblySourceFilterBuilder` materializes a `List<Assembly>`
(`AssemblySourceFilterBuilder.cs:12,75,86`) and `Apply()` returns it unchanged. That an assembly outside the
filter set is not reported is pinned by `MustNotReportCompetingHandlersInAnAssemblyOutsideTheFilterSet`
(`WhenThrowingOnDuplicateCommandHandlers.cs:113-123`); the deferred-set gap is recorded as a tracked
non-goal below.

**Candidates are not chosen by the check. They are read back out of the scan.**
`FindCommandsWithCompetingHandlers` (`CqrsExtensions.cs:118-127`) runs `ScanCommandHandlers`
(`CqrsExtensions.cs:182-192`) — the ONE expression that also performs registration — with
`RegistrationStrategy.Append` into a throwaway `new ServiceCollection()` (`CqrsExtensions.cs:119`), then
groups the descriptors it produced: null `ImplementationType` filtered out (`CqrsExtensions.cs:120`, Scrutor
emits factory descriptors with no implementation type from `LifetimeSelector.Populate`, though this scan
produces none), grouped by `ServiceType` (`CqrsExtensions.cs:121`), deduplicated by `ImplementationType`
(`CqrsExtensions.cs:124`), reported when two or more distinct types survive (`CqrsExtensions.cs:125`).

The probe measures the registration because it shares the registration's key. Scrutor builds the
`ServiceDescriptor` BEFORE handing it to the strategy — `LifetimeSelector.Populate` constructs
`new ServiceDescriptor(serviceType, implementationType, lifetime)` and only then calls
`strategy.Apply(services, descriptor)` — so the descriptor SET is strategy-independent and only its fate in
the collection differs. `RegistrationStrategy.Replace()` resolves `ReplacementBehavior.Default` to
`ReplacementBehavior.ServiceType`, displacing on the ServiceType key, which is exactly the key the probe
groups on; `Append` is a bare `services.Add(descriptor)` with no deduplication, so every competitor
survives into the probe instead of displacing the one before it. Same descriptors, same key, one
difference — the strategy, which is the thing being measured. (Scrutor 3.3.0, `LifetimeSelector.Populate`,
`RegistrationStrategy.Replace`, `ReplaceRegistrationStrategy.Apply` and `AppendRegistrationStrategy.Apply`,
decompiled from `~/.nuget/packages/scrutor/3.3.0/lib/netstandard2.0/Scrutor.dll`.) The dedupe by
`ImplementationType` exists because one handler type reachable from two scanned assemblies appends twice and
is not a competing pair — pinned by `MustNotReportOneHandlerReachableFromTwoScannedAssemblies`
(`WhenThrowingOnDuplicateCommandHandlers.cs:162-171`).

**This shape replaced a parallel copy of the candidate rule, and the copy had already drifted.** The
previous check re-derived its own candidates — its own type source, its own
`type.IsClass && !type.IsAbstract && type.IsValidMessageHandler(typeof(ICommand))` predicate and its own
ordering key — and reported types the scan never registers. Scrutor's own predicate,
`ReflectionExtensions.IsNonAbstractClass`, additionally rejects `IsSpecialName` types and types carrying
`[CompilerGenerated]` (with `inherit: true`), which the copy did not. That divergence is not hypothetical:
it was caught by a red-first test, `MustNotReportACompilerGeneratedHandlerBecauseItIsNeverRegistered`
(`WhenThrowingOnDuplicateCommandHandlers.cs:125-135`), which asserts both that the compiler-generated
handler is absent from the registrations and that the check stays silent about it. The structural fix is
the invariant recorded on the method itself (`CqrsExtensions.cs:111-117`): the probe must never re-derive
what a candidate is, because whichever conjunct of a re-derived copy drifts first is incidental.

**Non-public handlers ARE registered, and therefore MUST be reported.** The scan calls Scrutor's
`AddClasses(Action<IImplementationTypeFilter>)` (`CqrsExtensions.cs:186-187`), whose IL delegates to
`AddClasses(action, publicOnly: false)`; only the zero-argument `AddClasses()` passes `publicOnly: true`.
Scrutor 3.3.0's SHIPPED `Scrutor.xml` documents the Action overload as adding "all public, non-abstract
classes" and is STALE — the IL is authority, and was read from both
`lib/netstandard2.0/Scrutor.dll` and `lib/netcoreapp3.1/Scrutor.dll`. A visibility predicate on the check
would therefore be a false-NEGATIVE bug: it would stay silent about a displacement that really happened.
Because that ground truth lives in a dependency rather than in this repository, it is held by a
characterization pin, `MustRegisterAndReportNonPublicCompetingHandlers`
(`WhenThrowingOnDuplicateCommandHandlers.cs:137-160`), so a Scrutor upgrade that flips the default breaks a
test instead of silently narrowing both the registration and the check.

**Losing the check's own `ReflectionTypeLoadException` tolerance costs nothing.** The old check read types
through `AssemblySourceFilter.SafeGetLoadableTypes` (`AssemblySourceFilter.cs:74-84`); the probe does not.
That tolerance was already unreachable from this path. Registration runs first and enumerates
`Assembly.DefinedTypes` through Scrutor's `TypeSourceSelector.InternalFromAssemblies`
(`CqrsExtensions.cs:39`), so an assembly with unloadable types throws `ReflectionTypeLoadException` out of
`AddChatterCqrs` before a builder is ever returned and before the check can be called. `SafeGetLoadableTypes`
was NOT deleted — it is still the type source for
`AssemblySourceFilter.GetAssembliesThatMatchNamespaceSelector` (`AssemblySourceFilter.cs:64-66`). The
mock/dynamic-proxy case that helper names does not reach either path in the first place: dynamic assemblies
are excluded by the source provider (`CurrentAppDomainAssemblyProvider.cs:17`).

**Grouping is by closed command-handler interface, not by handler type.** The probe groups the descriptors
by `ServiceType` (`CqrsExtensions.cs:121`), and the scan registers each handler only as the interfaces
returned by `GetMessageHandlerInterfacesFor(typeof(ICommand))` (`CqrsExtensions.cs:189,194-197`), so a class
handling both a Command and an Event contributes exactly one Command descriptor and is never ambiguous with
itself — pinned by `MustNotThrowForAHandlerOfBothACommandAndAnEvent`
(`WhenThrowingOnDuplicateCommandHandlers.cs:105-111`). The reported Command is the group key's single
generic argument (`CqrsExtensions.cs:123`). Abstract and open-generic types are excluded by the scan itself
— Scrutor's non-abstract filter and `IsValidMessageHandler`'s `IsClosedHandlerType`
(`CqrsExtensions.cs:199-204`) — and the exclusions are pinned together by
`MustNotThrowWhenTheOnlyOtherHandlersOfACommandAreAbstractOrOpenGeneric`
(`WhenThrowingOnDuplicateCommandHandlers.cs:41-49`).

**One exception names every ambiguous Command and all its competing handlers.** A single
`InvalidOperationException` is thrown (`CqrsExtensions.cs:103-106`) with one line per ambiguous Command
(`CqrsExtensions.cs:133-138`), and its opening sentence states the mechanism rather than only the symptom:
that the replace strategy keeps the last handler scanned, and that the scan order is derived from assembly
load order and each assembly's type-definition order, neither of which is specified
(`CqrsExtensions.cs:131`). Reporting all of them at once is the point — fixing one ambiguity only to
recompose and meet the next is the failure mode a first-failure throw produces.

**Ordering is ordinal by Command full name, then by handler full name**
(`CqrsExtensions.cs:126,124`). This matters precisely because the INPUT order is the thing that is
unspecified: an exception message whose order came from the scan would vary run to run for the same defect,
which makes it useless to diff, to snapshot in a test, or to search for. Within one Command the ordering is
already total AT THE OUTPUT and needs no further key: the sort key is `handler.FullName`
(`CqrsExtensions.cs:124`) and the text rendered for that handler is that same string
(`CqrsExtensions.cs:138`), so two handlers that tie on the key produce identical output and their relative
order cannot be observed. An earlier draft proposed re-keying the sort on the rendered line; against that
argument it was verified to be a no-op and was DROPPED — it is recorded here as considered and NOT made.
The one residual is narrow: two ambiguous Commands can tie on `Type.FullName` only if two distinct `Type`
instances share a full name, and only then can the order of their two lines follow the scan. Both orderings
are pinned by `MustReportEveryAmbiguousCommandInOneExceptionOrderedByFullName`
(`WhenThrowingOnDuplicateCommandHandlers.cs:51-73`).

**No Scrutor `RegistrationStrategy` subclass.** Issue #449 flagged subclassing `RegistrationStrategy` as an
unverified possibility. It is moot: nothing in this repository derives from it, and all five uses are
Scrutor's own built-ins — `Replace` for commands (`CqrsExtensions.cs:175`), `Append` for events
(`CqrsExtensions.cs:168`), `Append` for the probe (`CqrsExtensions.cs:119`), `Throw` for queries
(`CqrsExtensions.cs:212`), and `Replace(ReplacementBehavior.ImplementationType)` in
`ServiceCollectionExtensions.cs:102`. The check does
run the registration scan — that is the whole point of the probe — but it runs it into a throwaway
`ServiceCollection` (`CqrsExtensions.cs:119`) and adds nothing to the application's `IServiceCollection`,
so the registration behaviour it guards is unchanged whether or not it runs. Pinned by
`MustLeaveTheApplicationServiceCollectionUntouched` (`WhenThrowingOnDuplicateCommandHandlers.cs:173-185`),
which snapshots the descriptors before the check and asserts equality after.

### Non-goals

These are the check's boundaries. They are recorded so that a future report of "it did not catch my case"
is answered here rather than treated as a bug.

- **A manual registration displaced by the scan is not covered.** The probe reads only the descriptors its
  own scan produced, never the application's `IServiceCollection`; a handler registered by hand before
  `AddChatterCqrs` and then replaced by a scanned one produces no group of two scanned types and is not
  reported.
- **A handler registered by another module after `AddChatterCqrs` is not covered**, for the same reason.
  The check is a snapshot of the scan at the moment it is called.
- **Deferred (tracked) — the scan set is re-derived, not frozen** (issue
  [#468](https://github.com/brenpike/Chatter/issues/468)). `AssemblySourceFilter.Apply()` is a deferred
  query (`AssemblySourceFilter.cs:56-59`); in namespace or unbounded mode it re-reads
  `IAssemblyFilterSourceProvider.GetSourceAssemblies()` on every enumeration
  (`AssemblySourceFilter.cs:64-66`, `CurrentAppDomainAssemblyProvider.cs:17`), so the same filter INSTANCE
  does not guarantee the same assembly SET to registration and to the check. Explicit-assembly mode is
  stable, because the builder materializes a `List<Assembly>` (`AssemblySourceFilterBuilder.cs:12,75,86`).
  The structural close is to materialize the scan set once in `AddChatterCqrs` (`CqrsExtensions.cs:37`) and
  hand that frozen set to both the registration and the check. It is not done here because both routes break a locked
  constraint: exposing the frozen set adds a new public `IChatterBuilder` member, which expands the API
  surface; wrapping the filter instead narrows what `AddMessageBrokers` scans for receivers and breaks the
  same-instance pin at `tests/DependencyInjection/UsingChatterBuilder/WhenGettingProperties.cs:37`. The
  impact is bounded to namespace or unbounded mode; the DIRECTION is not bounded. A set that GROWS between
  the two applications makes the check fail a composition that registered cleanly. A set that SHRINKS leaves
  the check silent about a displacement that really happened, and nothing forbids one:
  `IAssemblyFilterSourceProvider` and `IAssemblySourceFilter` are public interfaces
  (`IAssemblyFilterSourceProvider.cs:7`, `AssemblySourceFilter.cs:12`) and `ChatterBuilder.Create` accepts
  any `IAssemblySourceFilter` (`ChatterBuilder.cs:35`), while the default provider reads
  `AppDomain.CurrentDomain.GetAssemblies()` (`CurrentAppDomainAssemblyProvider.cs:17`), from which a
  collectible `AssemblyLoadContext.Unload()` removes assemblies. An earlier revision of this bullet bounded
  the impact to "the false-POSITIVE direction only" and claimed it "can never produce a false negative for
  what was registered". Neither is earned, and both are DELETED here rather than softened.
- **Events are out of scope by design.** `AddEventHandlers` uses `RegistrationStrategy.Append`
  (`CqrsExtensions.cs:168`), so several handlers for one Event all register and all run; that is the
  documented fan-out contract (see ADR-0012), not a displacement. Pinned by
  `MustNotThrowWhenSeveralHandlersHandleTheSameEvent` (`WhenThrowingOnDuplicateCommandHandlers.cs:33-39`).
- **Queries need no such flag — they already fail loudly by default.** `AddQueryHandlers` registers with
  `RegistrationStrategy.Throw` (`CqrsExtensions.cs:212`), so a second handler for the same Query and Read
  Model pair throws during the scan itself.

### The standing rule: the default does not flip without a MAJOR

**Turning this check on by default is a breaking change and must be treated as one.** Silently replacing a
Command handler is the module's documented, test-pinned behaviour today
(`WhenAddingCommandHandlers.cs:59-73`), so making it a composition-time throw would fail startup for an
application that composes successfully now. A future contributor who reaches for "this should just be the
default" should know the question was asked and answered: it may only ship in a MAJOR release of
`Chatter.CQRS`, with the deliberate-override case called out in the release notes. It is not a patch and
not a minor.

## Consequences

- **`Chatter.CQRS` gains a backward-compatible public capability**, so this half of the change is MINOR. No
  existing call site changes behaviour, because nothing calls the new method unless an application does.
- **Enabling the check costs a second full scan at composition time.** It re-applies the filter
  (`AssemblySourceFilter.cs:56-59`), re-enumerates every source assembly's types through Scrutor, and
  rebuilds every command-handler descriptor into a throwaway collection (`CqrsExtensions.cs:119`). That is
  startup-only and opt-in, and it is what buys the probe its agreement with the registration: the price of
  measuring the registration is performing it.
- **Documentation must not describe the replace strategy as enforcement.** The strategy selects ONE
  registration; it does not verify that only one candidate existed. Any claim that scanning "enforces that
  a Command resolves to exactly one handler" is false about registration and must be deleted rather than
  reworded (ADR-0015 doctrine). What is true, and unchanged by this ADR, is the DISPATCH-time statement:
  a Command resolves through `GetRequiredService<IMessageHandler<TMessage>>`
  (`src/Chatter.CQRS/src/Chatter.CQRS/Commands/CommandDispatcher.cs:60`) to the single registration that
  survived.
- **Testing this check requires a mocked assembly, always.** The CQRS test assembly itself contains a
  genuine duplicate command-handler pair — `FakeCommandHandler` and `FakeCommandHandler2`, both
  `IMessageHandler<FakeCommand>` (`WhenAddingCommandHandlers.cs:169,174`) — so any test that lets the
  filter reach the current AppDomain throws for reasons unrelated to its own fixture. Every test in
  `WhenThrowingOnDuplicateCommandHandlers.cs` pins its scan set with
  `New.Common().Assembly.WithTypes(...)` plus `b => b.WithExplicitAssemblies(assembly)`
  (`WhenThrowingOnDuplicateCommandHandlers.cs:190-195`).
- **A Command handler is still resolved the same way, and the ambiguity is still real when the check is
  off.** This ADR adds a way to be told; it does not add a way to have two handlers for one Command.
- **Epic #301's registration thread closes here.** With #329 (which assemblies), #330 (which interfaces)
  and #449 (what happens when two survive) answered, the scan's registration semantics are fully stated.

## References

- Issue #449 — *Command-handler displacement during assembly scanning is silent*. The report this ADR
  answers, including the `RegistrationStrategy`-subclassing possibility it flagged as unverified and this
  decision records as moot.
- Issue #468 — *Assembly scan set is re-enumerated per consumer, not snapshotted at registration*. The
  deferred finding carried as a tracked non-goal above.
- Issue #329 — *`AddChatterCqrs` scans the entire AppDomain even when marker types are supplied, and
  silently replaces command handlers*; issue #330 — *`AsImplementedInterfaces` registers dual-role handlers
  twice*. The two closed predecessors that narrowed the scan set and the registered interfaces; neither
  addressed two genuine handlers for one Command.
- Epic #301 — *Chatter.CQRS — handler registration correctness and per-dispatch hot path*. The parent this
  work finishes on the registration side.
- `src/Chatter.CQRS/src/Chatter.CQRS/DependencyInjection/CqrsExtensions.cs` — the extension, the probe and
  the message builder (`:99-142`), the single command-handler scan both of them go through (`:174-192`),
  and the unchanged event and query registrations (`:162-172`, `:206-216`).
- `src/Chatter.CQRS/src/Chatter.CQRS/DependencyInjection/AssemblySourceFilter.cs` — `Apply()` and its
  deferred re-read, and `SafeGetLoadableTypes`, still the type source for the namespace selector.
- Scrutor 3.3.0 — `ImplementationTypeSelector.AddClasses`, `ReflectionExtensions.IsNonAbstractClass`,
  `TypeSourceSelector.InternalFromAssemblies`, `LifetimeSelector.Populate` and the `RegistrationStrategy`
  built-ins, read from the IL in `lib/netstandard2.0/Scrutor.dll` and `lib/netcoreapp3.1/Scrutor.dll`
  because the shipped `Scrutor.xml` is stale about `publicOnly`.
- `src/Chatter.CQRS/tests/DependencyInjection/UsingCrqsExtensions/WhenThrowingOnDuplicateCommandHandlers.cs`
  — the pinned behaviour: the throw and its contents, deterministic ordering, default-off, the untouched
  application collection, the Scrutor visibility characterization, and the seven cases that must NOT be
  reported. `.../WhenAddingCommandHandlers.cs:59-73` — the replace behaviour that stays true.
- ADR-0012 — *Event fan-out: abort on the first failing handler, documented rather than aggregated*. Why
  several handlers for one Event are intended and are not a displacement.
- ADR-0015 — *Inbound header trust: ground truth stamped over wire values, and no trust boundary*. Source
  of the delete-the-unearned-claim doctrine applied to the documentation consequence above.
