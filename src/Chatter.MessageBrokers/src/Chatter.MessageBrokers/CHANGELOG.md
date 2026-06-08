# Changelog

All notable changes to this project will be documented in this file.

This project follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

### Changed

### Fixed

## [0.10.1] - 2026-06-07

### Changed

- Replaced Newtonsoft.Json with System.Text.Json for all serialization (body converter, routing slips, outbox message-context). Wire format preserved byte-for-byte via a custom relaxed JSON encoder (ChatterJson/ChatterJsonEncoder) that mirrors Newtonsoft escaping, including literal supplementary-plane/emoji output, so persisted and in-flight payloads remain cross-version compatible.

### Fixed

- Outbox replay now restores heterogeneous CLR types from the persisted message context (JSON integers → Int64, ISO-8601 strings → DateTime) so the Azure Service Bus typed readers (scheduled-enqueue time, time-to-live, receive attempts) no longer throw and outbox rows are no longer stranded. Wire format unchanged.
- RoutingSlip visited-step history (non-empty) now survives JSON round-trip. Wire format unchanged.
- Outbox replay and SQL Service Broker receive now restore heterogeneous CLR types from the persisted/transmitted message context via a centralized materializer applied at every System.Text.Json deserialization seam, so typed header readers no longer throw. Newtonsoft wire/round-trip parity preserved (JSON strings are not coerced to Guid).

### Removed

- Removed the Newtonsoft.Json package dependency.

## [0.10.0] - 2026-06-07

### Added

- `MessagingInfrastructureFactory` — a public shared `Func<>`-backed factory implementing both `IMessagingInfrastructureReceiverFactory` and `IMessagingInfrastructureDispatcherFactory`. Brokers register it with their receiver/dispatcher delegates instead of each shipping an identical internal factory.

## [0.9.0] - 2026-05-30

### Changed

- Target frameworks migrated from `netstandard2.1;net5.0;net6.0` to `net8.0;net10.0`.

### Removed

- Dropped the `net5.0`, `net6.0`, and `netstandard2.1` target-framework monikers. This is a breaking change for consumers pinned to those in-box assets. Consumers on modern runtimes resolve the `net8.0` or `net10.0` asset.
