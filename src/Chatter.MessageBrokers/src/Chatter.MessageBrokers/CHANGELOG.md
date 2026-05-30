# Changelog

All notable changes to this project will be documented in this file.

This project follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

### Changed

### Fixed

## [0.9.0] - 2026-05-30

### Changed

- Target frameworks migrated from `netstandard2.1;net5.0;net6.0` to `netstandard2.0;net8.0;net10.0`.
- On the `netstandard2.0` target, default-interface-member defaults and in-box async APIs (`IAsyncDisposable`, `ValueTask`) are unavailable; these are provided via `Microsoft.Bcl.AsyncInterfaces` and concrete `#if NETSTANDARD2_0` implementations. `net8.0` and `net10.0` behavior is unchanged.

### Removed

- Dropped the `net5.0` and `net6.0` target-framework monikers and `netstandard2.1`. This is a breaking change for consumers pinned to those in-box assets. Consumers on modern runtimes resolve the `net8.0` or `net10.0` asset; the new `netstandard2.0` asset broadens reach to .NET Framework 4.6.2+.
