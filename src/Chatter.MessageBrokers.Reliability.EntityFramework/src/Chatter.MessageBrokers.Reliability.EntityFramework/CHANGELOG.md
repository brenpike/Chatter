# Changelog

All notable changes to this project will be documented in this file.

This project follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

### Changed

### Fixed

## [0.4.1] - 2026-06-07

### Changed

- Ported outbox message-context serialization to System.Text.Json via the shared serializer options; persisted wire format unchanged. Dropped the transitive Newtonsoft.Json dependency.

## [0.4.0] - 2026-05-30

### Changed

- Target frameworks migrated from `netstandard2.1;net5.0;net6.0` to `net8.0;net10.0`.
- EF Core version is now target-framework-conditional: `net8.0` uses EF Core 8.0.x and `net10.0` uses EF Core 10.0.x.

### Removed

- Dropped the `net5.0`, `net6.0`, and `netstandard2.1` target-framework monikers. This is a breaking change for consumers pinned to those in-box assets. Consumers on modern runtimes resolve the `net8.0` or `net10.0` asset.
