# Changelog

All notable changes to this project will be documented in this file.

This project follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.13.0] - 2026-09-01

### Changed

- **BREAKING:** `ISqlDependencyManager` gains a read-only `Options` member (`SqlChangeFeedOptions Options { get; }`), which the Change Feed Migration reads to derive the object names it installs. Consumers of the built-in `SqlDependencyManager<TRowChangedData>` are unaffected. Anyone implementing `ISqlDependencyManager` or `ISqlDependencyManager<TRowChangedData>` themselves must now expose the `SqlChangeFeedOptions` their implementation was constructed with.
- **BREAKING:** `WithChangeFeedQueueName` and `WithChangeFeedDeadLetterServiceName` now drive the topology the Change Feed Migration installs, not just the receiver. Previously a configured queue name bound only the receiver while the migration provisioned a default-named `Chatter_Queue_<TRowChangedData>` queue, and a configured dead-letter service name was dropped entirely in `SqlChangeFeedOptionsBuilder.Build()`, so the receiver always bound `Chatter_DeadLetterService_<TRowChangedData>`. A consumer who set either name will see the effective name change. The conversation service name stays derived from the row type; a configured queue name changes the queue that derived service is created on and the queue the receiver reads. Migration: the upgrade does not rename or drop objects an earlier version provisioned under the default names, so execute the already-installed `Chatter_UninstallChangeFeed_<TRowChangedData>` Stored Procedure before re-running the Change Feed Migration if those objects should not be left behind. (#352)
- **BREAKING:** Install no longer transfers database ownership, so `ALTER DATABASE ... SET ENABLE_BROKER` now runs under the installing principal's own permissions and that principal must already hold `ALTER` on the target database. Sysadmin is no longer required. Migration: run the Change Feed Migration as a member of `db_owner`, or grant the principal `ALTER` explicitly (`GRANT ALTER ON DATABASE::[YourDatabase] TO [YourPrincipal]`). A database whose owner an earlier install reassigned to `sa` keeps that owner; this release only stops making the change. (#348)
- `ALTER DATABASE ... SET ENABLE_BROKER` now carries `WITH ROLLBACK IMMEDIATE`. A first install on a broker-disabled database previously blocked indefinitely behind any other open session, with no timeout and no diagnostic; it now terminates other sessions' in-flight transactions on the target database instead. The statement stays behind an `is_broker_enabled = 0` guard, so a database that already has Service Broker enabled is untouched. (#351)
- The Change Feed Migration now refreshes the Change Feed Trigger when the watched table's column set has drifted. The install Stored Procedure fingerprints the table's current columns, embeds that fingerprint in the trigger it creates, and on each later run drops and recreates the trigger only when the fingerprint differs or is absent. Re-running the Change Feed Migration is now the documented repair path for a watched table whose schema changed, replacing an undocumented manual uninstall and reinstall. A trigger installed by an earlier package version carries no fingerprint and is therefore refreshed once on the first run after upgrading. (#350)
- **BREAKING:** The install and uninstall Stored Procedures are now emitted with `CREATE OR ALTER` instead of an existence guard, so re-running the Change Feed Migration replaces a stale procedure body instead of silently keeping it. This raises the supported server floor to SQL Server 2016 SP1. Migration: consumers on an older server must stay on 0.12.0.
- **BREAKING:** `InstallAndConfigureSqlServiceBroker` now throws `ArgumentException` for a null or whitespace `deadLetterQueueName` or `deadLetterServiceName`, and `CreateInstallationProcedure` now throws `ArgumentException` for a null or whitespace `triggerName`. Each previously emitted malformed T-SQL. Migration: only callers constructing these script types directly are affected, and they must supply the names — the Change Feed Migration already does.

### Removed

- **BREAKING:** The four `IApplicationBuilder` overloads of `UseChangeFeedSqlMigrations` and `UseChangeFeedSqlMigrationsAsync` (the generic and `Type` form of each). Migration: call the `IServiceProvider` form through `ApplicationServices` — `app.UseChangeFeedSqlMigrations<T>()` becomes `app.ApplicationServices.UseChangeFeedSqlMigrations<T>()`, and `app.UseChangeFeedSqlMigrationsAsync<T>()` becomes `app.ApplicationServices.UseChangeFeedSqlMigrationsAsync<T>()`. Both `IServiceProvider` forms keep their existing `Type` overloads and `CancellationToken` parameter. The package no longer references the deprecated `Microsoft.AspNetCore.Http.Abstractions` 2.2.0, so a non-ASP.NET Core host no longer drags it in. (#389)
- **BREAKING:** The `ALTER AUTHORIZATION ON DATABASE::[<database>] TO [sa]` statement emitted during install. See the install-permission entry under Changed for the migration path. (#348)

### Fixed

- T-SQL identifier and literal escaping across all seven script emitters — `InstallAndConfigureSqlServiceBroker`, `UninstallSqlServiceBroker`, `CreateInstallationProcedure`, `CreateUninstallProcedure`, `SafeExecuteStoredProcedure`, `CreateChangeFeedTrigger`, and `DeleteChangeFeedTrigger`. Emitted identifiers are bracket-quoted with any embedded `]` doubled, and emitted single-quoted literals have their apostrophes doubled once per nesting level. This closes a hole where an apostrophe in a configured table, schema, queue, service, or Stored Procedure name escaped the `EXEC('...')` literal the install Stored Procedure is built from and executed arbitrary T-SQL at install time, and a `]` in any of those names broke out of bracket quoting. (#349)
- Install now fails with a named error, before any Service Broker object is created, when the watched table does not exist, has no `PRIMARY KEY`, or the server is Azure SQL Database (`EngineEdition` 5, which has no SQL Service Broker; Azure SQL Managed Instance remains supported). Previously a table with no primary key produced malformed Change Feed Trigger DDL that failed only after the queue and service had already been created, leaving partial state behind. (#353)
- `ChangeFeedReceiver` now applies `ConfigureAwait(false)` to every dispatch in its per-change-item loop, so a caller with a synchronization context is no longer marshalled back onto it once per row change. (#354)

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
