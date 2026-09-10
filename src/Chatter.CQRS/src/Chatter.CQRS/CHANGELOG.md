# Changelog

All notable changes to this project will be documented in this file.

This project follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

### Changed

### Fixed

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
