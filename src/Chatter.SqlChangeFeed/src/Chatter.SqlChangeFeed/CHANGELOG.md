# Changelog

All notable changes to this project will be documented in this file.

This project follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

### Changed

### Fixed

## [0.12.0] - 2026-09-01

### Added

- Published packages now ship a symbol package (`.snupkg`), an embedded `README`, a project URL, and are built deterministically. Package builds are now reproducible CI builds with SourceLink-resolvable sources, so a debugger can step into this package's original source from a consuming application.

### Changed

- Bundled dependency uplift to Chatter.MessageBrokers.SqlServiceBroker 0.14.0 (an in-repo `ProjectReference`, so the pack-time package dependency moves with it).

## [0.11.0] - 2026-06-14

### Added

- `UseChangeFeedSqlMigrationsAsync` async/cancellable migration entry points (on `IApplicationBuilder` and `IServiceProvider`, generic and `Type` overloads) that genuinely `await` the install and observe a `CancellationToken`. (#212)
- `CancellationToken` parameters on `ISqlDependencyManager.InstallSqlDependencies` / `UninstallSqlDependencies` and on `ExecutableSqlScript.ExecuteAsync`. All are defaulted (`= default`), so existing callers compile unchanged. (#212)

### Changed

- Migration install and uninstall are now genuinely asynchronous and cancellable: `ExecutableSqlScript.ExecuteAsync` awaits `OpenAsync`/`ExecuteNonQueryAsync` with the supplied token, and `SqlDependencyManager` awaits each script instead of running it synchronously. (#212)
- The synchronous `UseChangeFeedSqlMigrations` boundary now awaits the install internally (`GetAwaiter().GetResult()`), closing a latent fire-and-forget that previously worked only because the install ran synchronously. (#212)

## [0.10.0] - 2026-06-14

### Changed

- **BREAKING:** Migrated from the deprecated `System.Data.SqlClient` to `Microsoft.Data.SqlClient` (7.0.1). The public API now exposes `Microsoft.Data.SqlClient` types via `SqlConnection`, `SqlConnectionStringBuilder`, and `SqlCommand` usage, and via the transitive dependency on the migrated `Chatter.MessageBrokers.SqlServiceBroker`. Consumers compiled against `System.Data.SqlClient` that provide a `System.Data.SqlClient.SqlConnection` or related types must migrate to `Microsoft.Data.SqlClient`. Microsoft.Data.SqlClient defaults `Encrypt=true` with server-certificate validation (the legacy provider did not). Connection strings targeting a self-signed/untrusted-certificate server must now set `Encrypt=False` or `TrustServerCertificate=True` explicitly, or `Open`/`OpenAsync` will fail. (#204)

## [0.9.1] - 2026-06-06

### Fixed

- Picked up `Chatter.MessageBrokers.SqlServiceBroker` 0.8.1, which upgrades the transitive `System.Data.SqlClient` 4.8.3 -> 4.8.6 (resolves Dependabot alerts #11 and #1).

## [0.9.0] - 2026-05-30

### Changed

- Target frameworks migrated from `netstandard2.1;net5.0;net6.0` to `net8.0;net10.0`.

### Removed

- Dropped the `net5.0`, `net6.0`, and `netstandard2.1` target-framework monikers. This is a breaking change for consumers pinned to those in-box assets. Consumers on modern runtimes resolve the `net8.0` or `net10.0` asset.
