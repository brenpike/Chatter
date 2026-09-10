# Changelog

All notable changes to this project will be documented in this file.

This project follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

### Changed

### Fixed

## [0.12.0] - 2026-09-09

### Changed

- Handlers are no longer registered under every interface they implement. Assembly scanning previously used Scrutor's `AsImplementedInterfaces()`, so a handler class was registered against `IMessageHandler<TMessage>` / `IQueryHandler<TQuery,TResult>` *and* any other interface it happened to implement. A handler is now registered only under the closed `IMessageHandler<TMessage>` / `IQueryHandler<TQuery,TResult>` interface(s) it implements. **This is a breaking change**: if a handler class also implements an unrelated service interface and you resolve that interface from the container populated by `AddChatterCqrs` / `AddMessageHandlers` / `AddQueryHandlers`, that registration no longer exists. Register your handler's own service interface explicitly.

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
