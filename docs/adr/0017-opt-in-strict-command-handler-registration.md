---
status: accepted
date: 2026-09-12
---

# Duplicate command handlers: an opt-in composition-time check, and a default that does not move

Command handlers are registered by an assembly scan that uses Scrutor's replace strategy —
`AddCommandHandlers` calls `.UsingRegistrationStrategy(RegistrationStrategy.Replace())`
(`src/Chatter.CQRS/src/Chatter.CQRS/DependencyInjection/CqrsExtensions.cs:177`). When two scanned types
handle the same Command, the last one scanned becomes the single registration and the earlier one is
displaced with no error and no log. Issue #449 is the report of that silence.

The displacement is not merely unreported, it is unpredictable. The scan set is whatever
`AssemblySourceFilter.Apply()` yields (`AssemblySourceFilter.cs:56-59`), which for an unbounded filter is
`AppDomain.CurrentDomain.GetAssemblies()` minus dynamic assemblies
(`CurrentAppDomainAssemblyProvider.cs:17`), and within an assembly the order is the order its types are
defined. Neither is specified anywhere, so which of two competing handlers wins can change between a
developer's machine and a deployment without a line of code changing.

This is the last child of epic #301 on the registration side. Its two predecessors narrowed the scan
rather than the ambiguity: #329 narrowed WHICH assemblies are scanned when marker types or explicit
assemblies are supplied, and #330 narrowed WHICH interfaces a scanned handler is registered as — the
command scan now registers only the command-handler interfaces via
`.As(handler => handler.GetMessageHandlerInterfacesFor(typeof(ICommand)))` (`CqrsExtensions.cs:178`).
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
  and all its competing handlers. Nothing changes for an application that does not call it. Its weakness is
  stated plainly under *Non-goals*: it only sees what the scan sees.

### Why an `IChatterBuilder` extension rather than a parameter

The module's one composition idiom is the `IChatterBuilder` extension — 15 extension methods across the
repository's module source take `this IChatterBuilder`. It has no options object and no `IOptions<T>` type in its source
tree, and it reads no setting for this behaviour out of `IConfiguration`; the `IConfiguration` it is handed
is carried on the builder (`ChatterBuilder.cs:19`) for other modules to use. Adding a parameter to
`AddChatterCqrs` instead would be binary-breaking on a method with four public overloads
(`CqrsExtensions.cs:30,60,70,80`), and an all-optional overload would make the existing single-argument
call ambiguous. An extension method is default-off by construction: call it or do not.

## Decision

**The check ships as `public static IChatterBuilder ThrowOnDuplicateCommandHandlers(this IChatterBuilder
chatterBuilder)` in `CqrsExtensions` (`CqrsExtensions.cs:99-109`), in namespace
`Microsoft.Extensions.DependencyInjection` (`CqrsExtensions.cs:15`) so no new `using` is needed at the call
site, returning the same builder instance for chaining (`CqrsExtensions.cs:108`, pinned by
`MustReturnTheSameBuilderWhenNoCommandIsHandledMoreThanOnce`,
`tests/DependencyInjection/UsingCrqsExtensions/WhenThrowingOnDuplicateCommandHandlers.cs:87-93`).**

**It is off unless called.** `AddCommandHandlers` (`CqrsExtensions.cs:171-181`) and all four
`AddChatterCqrs` overloads (`CqrsExtensions.cs:30-50,60-61,70-71,80-81,90-91`) are untouched by this work;
none of them invokes the check. The default-off guarantee is pinned by
`MustRegisterTheLastHandlerScannedWithoutThrowingWhenTheCheckIsNotEnabled`
(`WhenThrowingOnDuplicateCommandHandlers.cs:73-85`), which composes two competing handlers through
`AddChatterCqrs` and asserts both that nothing throws and that the last scanned type is the registration.

**The scan set is `chatterBuilder.AssemblySourceFilter.Apply()` (`CqrsExtensions.cs:101`) — the SAME filter
instance the registration used.** `AddChatterCqrs` builds one filter, applies it to register handlers, and
hands it to the builder (`CqrsExtensions.cs:32-37`), which exposes it as `IChatterBuilder.AssemblySourceFilter`
(`IChatterBuilder.cs:16`, `ChatterBuilder.cs:21`). The check therefore covers exactly what was scanned and
nothing else — pinned by `MustNotReportCompetingHandlersInAnAssemblyOutsideTheFilterSet`
(`WhenThrowingOnDuplicateCommandHandlers.cs:111-121`).

**Candidates come from the existing `AssemblySourceFilter.SafeGetLoadableTypes`
(`AssemblySourceFilter.cs:74-84`, `internal static`), filtered to
`type.IsClass && !type.IsAbstract && type.IsValidMessageHandler(typeof(ICommand))`
(`CqrsExtensions.cs:115`).** That is deliberate parity with what Scrutor actually registers: the scan's own
predicate is `IsValidMessageHandler(handler, typeof(ICommand))` (`CqrsExtensions.cs:176`), and
`IsValidMessageHandler` already excludes open generics through `IsClosedHandlerType`
(`CqrsExtensions.cs:188-193`). Reusing `SafeGetLoadableTypes` rather than calling `Assembly.GetTypes`
directly is what keeps a mock or dynamic-proxy assembly with unloadable types from aborting the check with
a `ReflectionTypeLoadException`, which is the reason that helper exists. The abstract and open-generic
exclusions are pinned together by
`MustNotThrowWhenTheOnlyOtherHandlersOfACommandAreAbstractOrOpenGeneric`
(`WhenThrowingOnDuplicateCommandHandlers.cs:39-47`). Parity is the property that matters: a check that
reported a type Scrutor never registers would be a false alarm about a displacement that never happened.

**Grouping is by closed command-handler interface, not by handler type.** Each candidate contributes one
entry per interface returned by `GetMessageHandlerInterfacesFor(typeof(ICommand))`
(`CqrsExtensions.cs:116-118`), so a class handling both a Command and an Event contributes exactly one
Command entry and is never ambiguous with itself — pinned by
`MustNotThrowForAHandlerOfBothACommandAndAnEvent` (`WhenThrowingOnDuplicateCommandHandlers.cs:103-109`).
A group of two or more distinct types (`CqrsExtensions.cs:114,122`) is an ambiguous Command.

**One exception names every ambiguous Command and all its competing handlers.** A single
`InvalidOperationException` is thrown (`CqrsExtensions.cs:103-106`) with one line per ambiguous Command
(`CqrsExtensions.cs:130-136`), and its opening sentence states the mechanism rather than only the symptom:
that the replace strategy keeps the last handler scanned, and that the scan order derives from assembly
load order and each assembly's type-definition order, neither of which is specified
(`CqrsExtensions.cs:128`). Reporting all of them at once is the point — fixing one ambiguity only to
recompose and meet the next is the failure mode a first-failure throw produces.

**Ordering is ordinal by Command full name, then by handler full name**
(`CqrsExtensions.cs:121,123`). This matters precisely because the INPUT order is the thing that is
unspecified: an exception message whose order came from the scan would vary run to run for the same defect,
which makes it useless to diff, to snapshot in a test, or to search for. Both orderings are pinned by
`MustReportEveryAmbiguousCommandInOneExceptionOrderedByFullName`
(`WhenThrowingOnDuplicateCommandHandlers.cs:49-71`).

**No Scrutor `RegistrationStrategy` subclass.** Issue #449 flagged subclassing `RegistrationStrategy` as an
unverified possibility. It is moot: nothing in this repository derives from it, and the four uses are
Scrutor's own built-ins — `Replace` for commands (`CqrsExtensions.cs:177`), `Append` for events
(`CqrsExtensions.cs:165`), `Throw` for queries (`CqrsExtensions.cs:201`), and
`Replace(ReplacementBehavior.ImplementationType)` in `ServiceCollectionExtensions.cs:102`. The check is a
pre-scan over reflection, entirely outside the registration pipeline, so the registration behaviour it
guards is unchanged whether or not it runs.

### Non-goals

These are the check's boundaries. They are recorded so that a future report of "it did not catch my case"
is answered here rather than treated as a bug.

- **A manual registration displaced by the scan is not covered.** The check reads assemblies, not the
  `IServiceCollection`; a handler registered by hand before `AddChatterCqrs` and then replaced by a scanned
  one produces no group of two scanned types and is not reported.
- **A handler registered by another module after `AddChatterCqrs` is not covered**, for the same reason.
  The check is a snapshot of the scan set at the moment it is called.
- **Events are out of scope by design.** `AddEventHandlers` uses `RegistrationStrategy.Append`
  (`CqrsExtensions.cs:165`), so several handlers for one Event all register and all run; that is the
  documented fan-out contract (see ADR-0012), not a displacement. Pinned by
  `MustNotThrowWhenSeveralHandlersHandleTheSameEvent` (`WhenThrowingOnDuplicateCommandHandlers.cs:31-37`).
- **Queries need no such flag — they already fail loudly by default.** `AddQueryHandlers` registers with
  `RegistrationStrategy.Throw` (`CqrsExtensions.cs:201`), so a second handler for the same Query and Read
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
- **Enabling the check costs a second reflection pass at composition time.** `Apply()` is lazy
  (`AssemblySourceFilter.cs:56-59`), so calling it again re-enumerates the source assemblies and, for an
  unbounded filter, reflects over every loaded assembly a second time. That is startup-only and opt-in, and
  it is the price of the check covering exactly what was scanned rather than a separately configured set.
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
  (`WhenThrowingOnDuplicateCommandHandlers.cs:126-131`).
- **A Command handler is still resolved the same way, and the ambiguity is still real when the check is
  off.** This ADR adds a way to be told; it does not add a way to have two handlers for one Command.
- **Epic #301's registration thread closes here.** With #329 (which assemblies), #330 (which interfaces)
  and #449 (what happens when two survive) answered, the scan's registration semantics are fully stated.

## References

- Issue #449 — *Command-handler displacement during assembly scanning is silent*. The report this ADR
  answers, including the `RegistrationStrategy`-subclassing possibility it flagged as unverified and this
  decision records as moot.
- Issue #329 — *`AddChatterCqrs` scans the entire AppDomain even when marker types are supplied, and
  silently replaces command handlers*; issue #330 — *`AsImplementedInterfaces` registers dual-role handlers
  twice*. The two closed predecessors that narrowed the scan set and the registered interfaces; neither
  addressed two genuine handlers for one Command.
- Epic #301 — *Chatter.CQRS — handler registration correctness and per-dispatch hot path*. The parent this
  work finishes on the registration side.
- `src/Chatter.CQRS/src/Chatter.CQRS/DependencyInjection/CqrsExtensions.cs` — the extension, the pre-scan
  and the message builder (`:99-139`), alongside the unchanged command, event and query registrations
  (`:159-205`).
- `src/Chatter.CQRS/src/Chatter.CQRS/DependencyInjection/AssemblySourceFilter.cs` — `Apply()` and
  `SafeGetLoadableTypes`, both reused rather than reimplemented.
- `src/Chatter.CQRS/tests/DependencyInjection/UsingCrqsExtensions/WhenThrowingOnDuplicateCommandHandlers.cs`
  — the pinned behaviour: the throw and its contents, deterministic ordering, default-off, and the five
  cases that must NOT be reported. `.../WhenAddingCommandHandlers.cs:59-73` — the replace behaviour that
  stays true.
- ADR-0012 — *Event fan-out: abort on the first failing handler, documented rather than aggregated*. Why
  several handlers for one Event are intended and are not a displacement.
- ADR-0015 — *Inbound header trust: ground truth stamped over wire values, and no trust boundary*. Source
  of the delete-the-unearned-claim doctrine applied to the documentation consequence above.
