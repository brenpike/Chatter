# Changelog

All notable changes to this project will be documented in this file.

This project follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

### Changed

### Fixed

## [0.9.0] - 2026-06-07

### Changed

- Deepened the Azure Service Bus adapters (#122): introduced an internal `IServiceBusMessageReceiver` port with production and in-memory adapters, extracted a pure `InboundBrokeredMessageFactory`, made `BrokeredMessageSenderPool` client construction injectable, and folded the receiver/sender infrastructure factories into a single internal `Func<>`-backed factory. Behavior-preserving for consumers; internals are now unit-testable without a live namespace.

### Removed

- `ServiceBusReceiver` is now `internal` (was `public`). It was an infrastructure adapter not intended for direct consumption; resolve messaging infrastructure through the public `AddAzureServiceBus(...)` registration instead. (Breaking for any code referencing the type directly.)

## [0.8.0] - 2026-05-30

### Changed

- Target frameworks migrated from `netstandard2.1;net5.0;net6.0` to `net8.0;net10.0`.

### Removed

- Dropped the `net5.0`, `net6.0`, and `netstandard2.1` target-framework monikers. This is a breaking change for consumers pinned to those in-box assets. Consumers on modern runtimes resolve the `net8.0` or `net10.0` asset.
