# Changelog

All notable changes to this project will be documented in this file.

This project follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

### Changed

### Fixed

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
