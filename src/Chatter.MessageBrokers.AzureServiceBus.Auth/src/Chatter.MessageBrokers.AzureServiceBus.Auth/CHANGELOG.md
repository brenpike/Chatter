# Changelog

All notable changes to this project will be documented in this file.

This project follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

### Changed

### Fixed

## [3.1.0] - 2026-08-24

### Added

- `AadTokenProviderFactory.WithManagedIdentity(Action<DefaultAzureCredentialOptions> optBuilder = null)` — an explicit managed-identity credential mode requiring no client secret, certificate, or authority. It returns a `DefaultAzureCredential` with the client id supplied to `AadTokenProviderFactory.Create(string)` pinned onto **both** `ManagedIdentityClientId` and `WorkloadIdentityClientId`; it does not return a `ManagedIdentityCredential`. Both arms are pinned because `WorkloadIdentityClientId` also defaults to the `AZURE_CLIENT_ID` environment variable and `WorkloadIdentityCredential` precedes `ManagedIdentityCredential` in the `DefaultAzureCredential` chain. The pin is applied before `optBuilder` runs, so an explicit assignment in `optBuilder` still wins. A null or whitespace client id pins nothing, which is how a caller asks for the system-assigned managed identity.
- `ServiceBusOptionsBuilder.UseAadTokenProviderWithManagedIdentity(string clientId = null, Action<DefaultAzureCredentialOptions> optBuilder = null)` — wires the credential above into `ServiceBusOptions.TokenCredential`. To supply only an opt builder, use the named-argument form `UseAadTokenProviderWithManagedIdentity(optBuilder: opts => ...)`; a bare lambda in the first positional slot is a compile error because that slot is `clientId`.
- Both members are additive. The blank-argument `DefaultAzureCredential` fallbacks in `WithSecret`, `WithCert`, and `WithInteractive` are unchanged: they still resolve a managed identity ambiently, with no client id pinned onto either arm. The pin applies only to the new managed-identity path.

### Fixed

- Corrected the NuGet package `<Description>`, which named a nonexistent `Chatter.MessageBrokers.AzureServiceProvider` package and still described the package as shipping `TokenProvider` implementations — wording that predates 3.0.0, which moved to `Azure.Core.TokenCredential` via `Azure.Identity` and removed the legacy `ITokenProvider` and the MSAL dependency. Package metadata only — no behavior change, nothing to do on upgrade.

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
