# Changelog

All notable changes to this project will be documented in this file.

This project follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

### Changed

### Fixed

## [0.15.1] - 2026-09-10

### Fixed

- Three documentation sites promised that event dispatch invokes every registered handler, with no statement anywhere of what happens if one fails: `EventDispatcher`'s XML remark, the package README's fan-out paragraph, and `CONTEXT.md`'s Event term. The actual contract, unchanged by this release, is that each resolved `IMessageHandler<TEvent>` is awaited in resolution order and the first handler that throws propagates out of `Dispatch` — it is logged once and rethrown unchanged — and no subsequent handler is invoked. This release corrects those three documentation sites and adds `MustNotInvokeSubsequentHandlersOnceAHandlerRaisesException`, a permanent characterization test pinning that a first handler runs, a third does not, and the faulting handler's own exception instance surfaces unwrapped; no production code changed and dispatch semantics have not moved. An application that needs each subscriber to run independently of its siblings must give that subscriber its own delivery — its own broker subscription or queue — rather than have one dispatch carry several handlers. This matches the shipped default of comparable .NET libraries: MediatR's default notification publisher — the closest match to Chatter's shape, an in-process fan-out with no transport underneath — NServiceBus's treatment of multiple handlers in one endpoint as a single unit of work, and Rebus. The accepted cost is that handler invocation order is registration order, which for a handler the assembly scan discovers is scan order: that order is stable within a process, while which siblings are skipped follows whichever handler threw on the delivery, so a deterministic failure skips the same set every time and a transient one shifts it; an application that has not deliberately ordered its registrations gets an order it did not choose and which can differ across builds, and re-registering an already-scanned handler does not reposition it — it registers that handler a second time. ADR-0012 records the decision (#331).

## [0.15.0] - 2026-09-10

### Changed

- The `exception` event's `exception.type` attribute now carries `Type.ToString()` on `net8.0`, where it previously carried `Type.FullName`. `net8.0` has no `Activity.AddException`, so Chatter hand-writes that event's tags there while `net10.0` lets the .NET base class library write them, and the two spellings had drifted apart. The `net10.0` value was **measured, not assumed**: a generic probe exception observed through a live `ActivityListener` renders as ``Chatter.CQRS.Tests.Diagnostics.DiagnosticsProbeException`1[System.String]``, which is `Type.ToString()`, where `Type.FullName` renders the same type as ``Chatter.CQRS.Tests.Diagnostics.DiagnosticsProbeException`1[[System.String, System.Private.CoreLib, Version=8.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e]]``. The two spellings differ **only for a generic exception type** — `FullName` assembly-qualifies generic arguments and `ToString()` does not, and for a non-generic type they render identically — so a consumer whose exception types are all non-generic sees no change at all. A consumer with a generic exception type sees its `net8.0` `exception.type` values become shorter and lose the assembly qualification on their type arguments, matching what that consumer's `net10.0` processes were already reporting. A dashboard or alert query that matches `exception.type` literally against a generic type's `FullName` on `net8.0` needs updating to the `ToString()` spelling (#339).
- Only `exception.type` was aligned. `error.type` — on the `dispatch` span and on the `chatter.cqrs.dispatch.duration` histogram alike — deliberately keeps `Type.FullName`: that value is already identical on both target frameworks and it is the attribute consumer dashboards key on, so changing it would be a larger telemetry break than the one being fixed. The consequence is recorded rather than hidden: for a generic exception type, `exception.type` and `error.type` now intentionally carry different strings, on every target framework. For a non-generic exception type they remain identical (#339).
- One `net10.0`-only behaviour is documented as diverging rather than fixed. On `net9.0` and later, `Activity.AddException` notifies an `ActivityListener.ExceptionRecorder` callback registered by a subscribed listener before adding the event, and lets it supply or override the exception tags; `net8.0` has no such API and this package does not grow one for it. A consumer that registers an `ExceptionRecorder` therefore still gets different behaviour per target framework even though the default attribute values now match (#339).
- This is a **minor** release rather than a patch under the package's own published rule: the README's "Attribute names are data, not API" section states that telemetry attributes are emitted data rather than a compile-time type surface and **may change in a minor release**. Changing an emitted `exception.type` value is exactly that kind of change, so it is announced here and versioned accordingly. No public API, type, or member signature changed (#339).
- ADR-0010 records why `exception.type` follows the platform spelling while `error.type` keeps Chatter's, amending D4 rather than opening a second telemetry record (#339).

## [0.14.2] - 2026-09-10

### Changed

- `QueryDispatcher.Query<TResult>(IQuery<TResult>, IQueryHandlerContext)` built a closed `IQueryHandler<,>` type via `MakeGenericType` and made two `dynamic` conversions — one to resolve the handler, one to invoke it — on every call, each routed through a DLR call site. It now dispatches through a closed-generic adapter cached per `(runtime query type, TResult)` pair, so `MakeGenericType` runs once per pair, on a cache miss, instead of on every dispatch. The package multi-targets `net8.0;net10.0` and carries no trimming annotations; `dynamic` was the most trim- and AOT-hostile construct on this path, and the adapter removes that dependency outright without introducing expression-tree compilation or reflection emit, either of which would have added `RequiresDynamicCode` exposure instead of removing it. The cache is static and holds only stateless invokers — never a handler instance or an `IServiceProvider` — because `QueryDispatcher` is registered per scope and an instance-level cache would be rebuilt every scope; the handler is still resolved from the `IServiceProvider` passed to each call on every dispatch, so a scoped handler lifetime behaves exactly as before. The invoker cache is a process-lifetime static with no eviction, and every entry strongly roots the caller-supplied query `Type`: its key derives from `query.GetType()`, so each distinct `(query type, result type)` pair dispatched retains one key and one stateless adapter for the life of the process — an entry count bounded by the number of distinct query types dispatched, not by traffic. A collectible `AssemblyLoadContext` that defines a dispatched query type therefore cannot unload while the process lives. Do not dispatch a query type defined in an `AssemblyLoadContext` you intend to unload through `Query<TResult>(IQuery<TResult>)`; dispatch it through `Query<TQuery, TResult>`, the cache-free path for that case — though that escape hatch only reaches callers that can name `TQuery` at compile time, not a reflective host holding an arbitrary `IQuery<TResult>`. The cache key is the pair of the runtime query type and the result type, so two distinct query types that share a result type still resolve to their own handlers. The public signature and the fault shape are unchanged: an unregistered handler still throws `InvalidOperationException` out of `GetRequiredService`, before the handler is ever invoked (#336).
- ADR-0013 records why the cache is left static and documented rather than weakened, with the README carrying the caller-facing requirement (#336).

## [0.14.1] - 2026-09-10

### Changed

- `MessageDispatcherProvider.GetDispatcher<TMessage>()` walked `typeof(TMessage).GetTypeInfo().ImplementedInterfaces` on every dispatch, with no write-back of the result. Resolved dispatcher mappings are now memoized in a separate dictionary, so a message type's interface walk result is reused for the life of the provider instead of being recomputed on every dispatch. The memo is deliberately kept apart from the dictionary the interface walk reads: writing a resolved message type back into that dictionary would let it become a key a later multi-interface message could match instead of the key it matches today, making dispatcher selection depend on resolution history and on the unspecified order of `ImplementedInterfaces`. The cache is also per provider instance, never process-wide — the provider and the dispatchers it holds are registered per scope, and a static cache would dispatch through a disposed scope. A lookup miss is never cached: an unresolvable message type still throws `KeyNotFoundException` on every call, not just the first (#334).
- `CommandBehaviorPipeline.Execute` re-enumerated its registered behaviours and rebuilt its delegate chain via `Reverse()` and `Aggregate` on every execution. It now materializes the behaviours once per call, invokes the message handler directly when none are registered, and composes the chain by iterating the materialized array backwards; the first-registered behaviour still ends up outermost, an ordering guarantee four other modules depend on and which is unchanged. Behaviours are still materialized per execution rather than in the constructor, so a behaviour registered after the pipeline was resolved still takes part in the next execution. Measured: a dispatch with no registered behaviours went from 216 bytes allocated per call and ~310 ns/op to 0 bytes per call and ~30 ns/op (#335).

## [0.14.0] - 2026-09-10

### Changed

- The `LogTrace` calls in `CommandDispatcher` and `EventDispatcher` built an interpolated string on every dispatch regardless of whether trace-level logging was enabled. They are now guarded by `ILogger.IsEnabled(LogLevel.Trace)` and use a constant message template with a per-closed-generic cached type name, so a dispatch running at a higher minimum log level no longer pays for string interpolation it will discard (#338).

### Fixed

- `CommandDispatcher.DispatchToHandler<TMessage>` was not `async`: it returned `handler.Handle(...)` / `pipeline.Execute(...)` directly from inside its `try`, so a command handler whose `Task` faulted AFTER an `await` never re-entered the dispatcher's `catch` and was logged nowhere. It is now `async` and awaits both calls inside the `try` (with `ConfigureAwait(false)`), matching `EventDispatcher`'s existing shape, so an asynchronously faulting command handler now produces an error log record where it previously produced none. The uniform consequence for callers: a synchronous resolution fault — for example, no registered `IMessageHandler<TMessage>` — now arrives on the returned `Task` unconditionally, diagnostics on or off, rather than throwing out of the `Dispatch` call itself. A caller that separates `Dispatch(...)` from its `await` will therefore see such a fault surface at the `await` rather than at the call (#415).
- All four dispatcher catch blocks — `CommandDispatcher`, `EventDispatcher`, and both `QueryDispatcher` overloads — logged `LogError($"... {e.StackTrace}")`, stringifying the stack trace into the message and passing no exception to the logger. They now use the `LogError(exception, template, args)` overload, so a structured-logging sink receives the exception object itself and the message template is a constant rather than an interpolated string (#337).

## [0.13.1] - 2026-09-10

### Changed

- A stored `null` is now treated as a present value rather than an absence. `GetOrNew<T>()` is the deliberate unchanged exception: it keeps its own guard and still returns an instance (#332).
- ADR-0011 records why the container is deliberately not synchronized; the README's Threading subsection carries the caller-facing requirement (#333).

### Fixed

- `ContextContainer.GetOrAdd<T>` conflated `default(T)` with absence, so a non-nullable value type re-invoked the factory forever and never returned a stored value; it now branches on the result `TryGet` reports. `GetOrDefault<T>()` consequently stores `default(T)` for value types as its documentation always promised (#332).

## [0.13.0] - 2026-09-10

### Changed

- `AssemblySourceFilter.Apply()` now returns only the explicitly-supplied assemblies when explicit assemblies are configured and no namespace selector is set — reached through `AddChatterCqrs(configuration, params Type[])`, `AddChatterCqrs(configuration, params Assembly[])`, and any `messageHandlerSourceBuilder` configuration that calls `WithMarkerTypes` / `WithExplicitAssemblies` without also calling `WithNamespaceSelector`. Previously it unioned the explicit assemblies with every assembly matching the namespace selector, and a null or empty selector matched every loaded assembly, so any of those configurations scanned the entire `AppDomain` anyway. Of the four filter configurations, only one changes: no explicit assemblies with no selector still scans the whole `AppDomain` (unchanged); no explicit assemblies with a selector still returns only the selector matches (unchanged); explicit assemblies with a selector still returns their union with the selector matches (unchanged); explicit assemblies with no selector now returns only the explicit assemblies — **this is a breaking change**. `services.AddChatterCqrs(configuration)`, the primary documented zero-argument ambient-scan form, supplies no explicit assemblies and is not affected. To restore the previous ambient union, add `.WithNamespaceSelector("*")` alongside your marker types or explicit assemblies. `Chatter.MessageBrokers`'s `AddMessageBrokers` reuses this same filter instance for receiver discovery and ships no change of its own, so a consumer who passed explicit assemblies to `AddChatterCqrs` and nothing to `AddMessageBrokers` now gets receiver discovery scoped to those same explicit assemblies too (#329).

### Security

- The `AppDomain`-wide scan the configuration above triggered meant any loaded assembly's `IMessageHandler<TCommand>` was auto-registered and, under the command scan's `RegistrationStrategy.Replace()`, could silently displace the application's own handler for that command with no error or warning. Any loaded assembly's duplicate `IQueryHandler<TQuery,TResult>` hit the query scan's `RegistrationStrategy.Throw` instead, so that path failed loudly with a startup `DuplicateTypeRegistrationException` rather than silently displacing a handler. For a foreign assembly to reach either path it must contain a closed handler for one of the application's own message types, which means it must be able to reference that type — so the realistic exposure is an application whose message contracts live in a shared package a compromised or careless dependency also references, or two of the application's own assemblies colliding by accident, not an arbitrary third-party package hijacking an unrelated command (#329).

## [0.12.0] - 2026-09-09

### Changed

- Handlers are no longer registered under every interface they implement. Assembly scanning previously used Scrutor's `AsImplementedInterfaces()`, so a handler class was registered against `IMessageHandler<TMessage>` / `IQueryHandler<TQuery,TResult>` *and* any other interface it happened to implement. A handler is now registered only under the closed `IMessageHandler<TMessage>` / `IQueryHandler<TQuery,TResult>` interface(s) it implements. **This is a breaking change**: if a handler class also implements an unrelated service interface and you resolve that interface from the container populated by `AddChatterCqrs` / `AddMessageHandlers` / `AddQueryHandlers`, that registration no longer exists. Register your handler's own service interface explicitly.
- The query scan now discovers only closed handler types. An open-generic query handler — `class MyHandler<TQuery, TResult> : IQueryHandler<TQuery, TResult>` — was previously discovered and registered under the open definition `IQueryHandler<,>`, as a byproduct of Scrutor's `AsImplementedInterfaces()` arity normalization; the command and event scans have always rejected open-generic handlers. **This is a breaking change**: an open-generic query handler is no longer discovered by `AddChatterCqrs` / `AddQueryHandlers`. Register it manually after `AddChatterCqrs`:
  ```csharp
  services.AddTransient(typeof(IQueryHandler<,>), typeof(MyHandler<,>));
  ```

### Fixed

- A handler class implementing both a command-handler interface (`IMessageHandler<TCommand>`) and an event-handler interface (`IMessageHandler<TEvent>`) — a dual-role handler — received every dispatched event twice. The command scan and the event scan both matched the class and, via `AsImplementedInterfaces()`, each registered it under every interface it implemented, so `IMessageHandler<TEvent>` ended up with two descriptors and `EventDispatcher` invoked the handler twice per event.
- Scanning handlers a second time — a second `AddChatterCqrs` / `AddMessageHandlers` call across multiple assemblies, or calling the event scan before the command scan — could silently delete already-registered event handlers. The command scan's `Replace()` strategy replaces prior registrations of the same closed handler interface; because `AsImplementedInterfaces()` widened a dual-role handler's registered interfaces to include an event-handler interface, a later command scan silently removed event handlers a prior scan had already registered for that event, with no error or warning (#330).

## [0.11.1] - 2026-09-02

### Fixed

- `chatter.cqrs.dispatch.duration` records **seconds**, but shipped no bucket advice — a collector applied its own millisecond-sized default boundaries (`0, 5, 10, 25, ... 10000`), and every realistic dispatch landed in the first bucket, so P50, P90, and P99 all reported the same bucket bound forever. The instrument now publishes seconds-sized bucket advice — `0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10` — but only on `net10.0`: the base class library type that carries instrument advice does not exist in the `net8.0` shared framework, and the package takes no package dependency to reach it. `net10.0` applications that already built dashboards against the previous millisecond-sized defaults will see the bucket bounds move — that is the fix, not a regression, but it is a visible change. On `net8.0`, configure the equivalent view in your own application; see the **Histogram bucket boundaries** subsection in `src/Chatter.CQRS/src/README.md` for the copy-pasteable `AddView` snippet (#399).

## [0.11.0] - 2026-09-01

### Added

- Published packages now ship a symbol package (`.snupkg`), an embedded `README`, a project URL, and are built deterministically. Package builds are now reproducible CI builds with SourceLink-resolvable sources, so a debugger can step into this package's original source from a consuming application.

### Changed

- Raised the net8.0-leg `Microsoft.Extensions.Hosting` dependency floor to `8.0.1` and the `Microsoft.Extensions.Logging.Abstractions` floor to `8.0.3`, off a dependency graph that carried an advisory-affected `System.Text.Json` 8.0.0 floor. The net10.0 leg is unchanged.

## [0.10.0] - 2026-08-30

### Added

- `ActivityOutcome.RecordFailure(Activity, string errorType, string description)` — a non-exception failure overload that marks the span as `Error` and stamps `error.type` with `errorType`, without attaching an `exception` span event (there is no exception to describe). Lets a call site report a failure the infrastructure itself DETECTED — e.g. a settlement the broker answered as failed without throwing — identically to an exception-raised failure, through the same single `ActivityOutcome` choke point. Additive; the existing exception-shaped `RecordFailure` overload is unchanged (#283).

## [0.9.0] - 2026-08-29

### Added

- An opt-in diagnostics surface (`Chatter.CQRS.Diagnostics.ChatterDiagnostics`) providing OpenTelemetry-compatible tracing and metrics built entirely on the BCL (`System.Diagnostics.ActivitySource` and `System.Diagnostics.Metrics.Meter`) with no `OpenTelemetry.*` package dependency. The `ActivitySource` and the `Meter` are both named `Chatter.CQRS`; an application opts in BY NAME with `.AddSource("Chatter.*")` / `.AddMeter("Chatter.*")` on its own provider. The scope names are the opt-in contract — the `ActivitySource` and `Meter` instances themselves are not public surface. When nothing subscribes to the source or the meter there is no cost: every entry point evaluates its off-guard — Chatter's own `ActivitySource.HasListeners`/`Instrument.Enabled`, never `Activity.Current` — as its first statement and allocates nothing (#274).
- Instrumentation of Command and Event dispatch: a span per dispatch carrying the message type and dispatch path (`chatter.message.type`, `chatter.dispatch.kind`), a `chatter.cqrs.dispatch.duration` histogram in seconds, and failure recorded once through `ActivityOutcome` as an error status plus `error.type`, with the `exception` event attached only when all data is requested. Dispatch keeps its existing shape — no async state machine and no additional allocation on the un-instrumented path. Query dispatch is not instrumented. Attribute names prefixed `chatter.` are Chatter-native; the remainder are OpenTelemetry semantic conventions pinned to v1.30.0, are emitted data rather than compile-time API, and may change in a minor release when the pin advances (#274).

## [0.8.1] - 2026-06-08

### Fixed

- Assembly-source scan no longer throws `ReflectionTypeLoadException` when a loaded assembly (e.g. a dynamic-proxy/mock assembly) contains unloadable types; it now uses the loadable types. Dynamic assemblies (e.g. `DynamicProxyGenAssembly2`) are now also excluded from the scan source, so they never reach the underlying type enumeration that throws on them.

## [0.8.0] - 2026-05-30

### Changed

- Target frameworks migrated from `netstandard2.1;net5.0;net6.0` to `net8.0;net10.0`.

### Removed

- Dropped the `net5.0`, `net6.0`, and `netstandard2.1` target-framework monikers. This is a breaking change for consumers pinned to those in-box assets. Consumers on modern runtimes resolve the `net8.0` or `net10.0` asset.
