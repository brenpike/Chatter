# Changelog

All notable changes to this project will be documented in this file.

This project follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

### Changed

### Fixed

## [0.4.0] - 2026-05-30

### Security

- The `netstandard2.0` target pins EF Core 3.1.32, which reached end-of-life in December 2022, because EF Core 5+ dropped `netstandard2.0` support. Consumers requiring a supported EF Core version must target `net8.0` or `net10.0`; the `netstandard2.0` asset is best-effort for legacy .NET Framework hosts only.

### Changed

- Target frameworks migrated from `netstandard2.1;net5.0;net6.0` to `netstandard2.0;net8.0;net10.0`.
- On the `netstandard2.0` target, default-interface-member defaults and in-box async APIs (`IAsyncDisposable`, `ValueTask`) are unavailable; these are provided via `Microsoft.Bcl.AsyncInterfaces` and concrete `#if NETSTANDARD2_0` implementations. `net8.0` and `net10.0` behavior is unchanged.
- EF Core version is now target-framework-conditional: `netstandard2.0` uses EF Core 3.1.32 (EOL; legacy-host support only), `net8.0` uses EF Core 8.0.x, and `net10.0` uses EF Core 10.0.x.

### Removed

- Dropped the `net5.0` and `net6.0` target-framework monikers and `netstandard2.1`. This is a breaking change for consumers pinned to those in-box assets. Consumers on modern runtimes resolve the `net8.0` or `net10.0` asset; the new `netstandard2.0` asset broadens reach to .NET Framework 4.6.2+.
