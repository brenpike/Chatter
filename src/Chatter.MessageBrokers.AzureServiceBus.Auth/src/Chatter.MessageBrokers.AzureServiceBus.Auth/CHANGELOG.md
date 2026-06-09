# Changelog

All notable changes to this project will be documented in this file.

This project follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

### Changed

### Fixed

## [3.0.0] - 2026-06-08

### Changed

- `AadTokenProviderFactory` now returns `Azure.Core.TokenCredential` (via `Azure.Identity`) instead of the legacy `ITokenProvider`. Supported credential flows: client-secret (`ClientSecretCredential`), client-certificate (`ClientCertificateCredential`), interactive-browser (`InteractiveBrowserCredential`), and `DefaultAzureCredential` fallback.
- `UseAadTokenProvider*` extension methods updated to wire the returned `TokenCredential` into `ServiceBusOptions.TokenCredential`.

### Removed

- MSAL (`Microsoft.Identity.Client`) dependency removed; `Azure.Identity` is now the sole auth library.
- `AzureActiveDirectoryTokenProvider` class removed.
- `ITokenProvider` return type removed from all factory and extension-method surfaces.

## [2.0.0] - 2026-05-30

### Changed

- Target frameworks migrated from `netstandard2.1;net5.0;net6.0` to `net8.0;net10.0`.
- `Azure.Identity` bumped to 1.21.0 and `Microsoft.Identity.Client` bumped to 4.84.1.

### Removed

- Dropped the `net5.0`, `net6.0`, and `netstandard2.1` target-framework monikers. This is a breaking change for consumers pinned to those in-box assets. Consumers on modern runtimes resolve the `net8.0` or `net10.0` asset.
