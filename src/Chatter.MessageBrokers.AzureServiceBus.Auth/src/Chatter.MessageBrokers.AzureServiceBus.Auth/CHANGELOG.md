# Changelog

All notable changes to this project will be documented in this file.

This project follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

### Changed

### Fixed

## [3.2.1] - 2026-09-02

### Changed

- Bundled dependency uplift to Chatter.MessageBrokers.AzureServiceBus 2.1.1 (an in-repo `ProjectReference`, so the pack-time package dependency moves with it).

## [3.2.0] - 2026-09-01

### Added

- Published packages now ship a symbol package (`.snupkg`), an embedded `README`, a project URL, and are built deterministically. Package builds are now reproducible CI builds with SourceLink-resolvable sources, so a debugger can step into this package's original source from a consuming application.

### Changed

- Bundled dependency uplift to Chatter.MessageBrokers.AzureServiceBus 2.1.0 (an in-repo `ProjectReference`, so the pack-time package dependency moves with it).

## [3.1.2] - 2026-08-31

### Changed

- Transitive uplift only: the sibling `Chatter.MessageBrokers.AzureServiceBus` package moved to `Azure.Messaging.ServiceBus 7.20.2`, which raises this package's transitive `Azure.Core` floor from `1.53.0` to `1.60.0`. On the `net8.0` consumer graph this also carries `Microsoft.Identity.Client` and `Microsoft.Identity.Client.Extensions.Msal` to `4.84.2`, `System.ClientModel` to `1.14.0`, `System.Text.Json` to `10.0.9`, and the `Microsoft.Extensions.*` abstractions to `10.0.9`. `Azure.Identity` itself stays pinned at `1.21.0`. No Auth API change and no behavior change in this package.

## [3.1.1] - 2026-08-25

### Fixed

- Authority URLs carrying path segments beyond the tenant — notably the `https://login.microsoftonline.com/{tenant}/v2.0` issuer form Microsoft publishes and the Azure portal hands out — were rejected at credential construction with `ArgumentException: Invalid tenant id provided (Parameter 'tenantId')`. This was an undeclared regression introduced in 3.0.0: before 3.0.0 the module passed the raw authority to MSAL, which took the first path segment itself, so these URLs worked. The tenant id is now taken from the first non-empty path segment of the authority, and any deeper segments are ignored, so behavior is strictly widening — no authority that worked before behaves differently. One residue remains: any authority whose first non-empty path segment is not the tenant id — a non-AAD routing prefix (Azure AD B2C's `/tfp/`, ADFS, DSTS) or a malformed AAD URL that omits the tenant (e.g. the `/oauth2/v2.0/token` token endpoint) — now resolves that segment as the tenant id, so the credential constructs and the misconfiguration is not detected until first token acquisition instead of at construction. The credential still carries the operator-supplied client id and secret or certificate; what a request made with a mis-resolved tenant id yields at token acquisition is decided by Microsoft Entra, and this package asserts no bound on that outcome. Accepted as an operator configuration error, and because this matches the pre-3.0.0 first-segment behavior this fix restores.

## [3.1.0] - 2026-08-24

### Added

- `AadTokenProviderFactory.WithManagedIdentity(Action<ManagedIdentityCredentialOptions> optBuilder = null)` — an explicit managed-identity credential mode requiring no client secret, certificate, or authority. It returns a `ManagedIdentityCredential` whose identity **is** the client id supplied to `AadTokenProviderFactory.Create(string)`, via `ManagedIdentityId.FromUserAssignedClientId(clientId)`; a null or whitespace client id resolves to `ManagedIdentityId.SystemAssigned`, which is how a caller asks for the system-assigned managed identity. This path constructs no credential chain, so no other credential source can answer in the managed identity's place, and neither the `AZURE_TOKEN_CREDENTIALS` environment variable nor the `Exclude*` flags of `DefaultAzureCredentialOptions` apply to it.
- `optBuilder` now configures `ManagedIdentityCredentialOptions` (previously `DefaultAzureCredentialOptions`) and is invoked before the credential is constructed. It cannot redirect the requested identity: the SDK's `ManagedIdentityCredentialOptions.ManagedIdentityId` property is internal and get-only, so the compiler forbids reassigning it.
- `ServiceBusOptionsBuilder.UseAadTokenProviderWithManagedIdentity(string clientId = null, Action<ManagedIdentityCredentialOptions> optBuilder = null)` — wires the credential above into `ServiceBusOptions.TokenCredential`. To supply only an opt builder, use the named-argument form `UseAadTokenProviderWithManagedIdentity(optBuilder: opts => ...)`; a bare lambda in the first positional slot is a compile error because that slot is `clientId`.
- A blank client id no longer implicitly adopts the `AZURE_CLIENT_ID` environment variable as the identity, as it did while this mode was backed by `DefaultAzureCredentialOptions` defaults. One bounded exception remains: on a federated-token host (for example AKS workload identity), the SDK's token-exchange managed-identity source falls back to `AZURE_CLIENT_ID` when no identity was supplied, so the blank/system-assigned case authenticates as the platform-bound workload identity rather than literally system-assigned. Supplying a client id closes that fallback entirely. The residue cannot be closed further — the SDK's exclusion switch for that source is internal, and excluding it would break AKS.
- Both members are additive. The blank-argument `DefaultAzureCredential` fallbacks in `WithSecret`, `WithCert`, and `WithInteractive` are unchanged, and they remain the package's only ambient-resolution path — including the local-development story, since they still honor `az login`. Choosing the managed-identity mode in Azure and one of those fallbacks locally is the supported way to keep a single code path across environments.

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
