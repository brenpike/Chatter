# Changelog

All notable changes to this project will be documented in this file.

This project follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.14.0] - 2026-09-02

### Deprecated

- Both synchronous `UseChangeFeedSqlMigrations` overloads (the generic `UseChangeFeedSqlMigrations<TRowChangedData>(this IServiceProvider, CancellationToken)` and the `UseChangeFeedSqlMigrations(this IServiceProvider, Type, CancellationToken)` form) are now marked `[Obsolete]` at warning level. They still compile and still work — this is not a breaking change. Callers should move to `UseChangeFeedSqlMigrationsAsync`. The 0.13.0 migration guidance for #389, which pointed callers at `app.ApplicationServices.UseChangeFeedSqlMigrations<T>()`, remains valid: the synchronous form is deprecated, not removed. (#354)

### Fixed

- The synchronous migration overload no longer risks a startup deadlock on a host that carries a `SynchronizationContext`: the installation now runs on the thread pool, so the blocking wait cannot deadlock against a single-threaded context whose only pump thread is the caller's. Exceptions still propagate unwrapped (never a bare `AggregateException`), and the supplied `CancellationToken` is still observed by the installation itself. A consumer-supplied `ISqlDependencyManager<T>` that genuinely depended on the caller's `SynchronizationContext` during install now runs without it — vanishingly unlikely for database DDL, but a real observable difference. (#354)

## [0.13.0] - 2026-09-01

### Changed

- **BREAKING:** `ISqlDependencyManager` gains a read-only `Options` member (`SqlChangeFeedOptions Options { get; }`), which the Change Feed Migration reads to derive the object names it installs. Consumers of the built-in `SqlDependencyManager<TRowChangedData>` are unaffected. Anyone implementing `ISqlDependencyManager` or `ISqlDependencyManager<TRowChangedData>` themselves must now expose the `SqlChangeFeedOptions` their implementation was constructed with.
- **BREAKING:** `WithChangeFeedQueueName` and `WithChangeFeedDeadLetterServiceName` now drive the topology the Change Feed Migration installs, not just the receiver. Previously a configured queue name bound only the receiver while the migration provisioned a default-named `Chatter_Queue_<TRowChangedData>` queue, and a configured dead-letter service name was dropped entirely in `SqlChangeFeedOptionsBuilder.Build()`, so the receiver always bound `Chatter_DeadLetterService_<TRowChangedData>`. A consumer who set either name will see the effective name change. The conversation service name stays derived from the row type; a configured queue name changes the queue that derived service is created on and the queue the receiver reads. Migration: the upgrade does not rename or drop objects an earlier version provisioned under the default names, so execute the already-installed `Chatter_UninstallChangeFeed_<TRowChangedData>` Stored Procedure before re-running the Change Feed Migration if those objects should not be left behind. (#352)
- **BREAKING:** Install no longer transfers database ownership, so `ALTER DATABASE ... SET ENABLE_BROKER` now runs under the installing principal's own permissions and that principal must already hold `ALTER` on the target database. Sysadmin is no longer required. Migration: run the Change Feed Migration as a member of `db_owner`, or grant the principal `ALTER` explicitly (`GRANT ALTER ON DATABASE::[YourDatabase] TO [YourPrincipal]`). A database whose owner an earlier install reassigned to `sa` keeps that owner; this release only stops making the change. (#348)
- `ALTER DATABASE ... SET ENABLE_BROKER` now carries `WITH ROLLBACK IMMEDIATE`. A first install on a broker-disabled database previously blocked indefinitely behind any other open session, with no timeout and no diagnostic; it now terminates other sessions' in-flight transactions on the target database instead. The statement stays behind an `is_broker_enabled = 0` guard, so a database that already has Service Broker enabled is untouched. (#351)
- The Change Feed Migration now refreshes the Change Feed Trigger when the watched table's column set has drifted. The install Stored Procedure fingerprints the table's current columns, embeds that fingerprint in the trigger it creates, and on each later run drops and recreates the trigger only when the fingerprint differs or is absent. Re-running the Change Feed Migration is now the documented repair path for a watched table whose schema changed, replacing an undocumented manual uninstall and reinstall. A trigger installed by an earlier package version carries no fingerprint and is therefore refreshed once on the first run after upgrading. (#350)
- **BREAKING:** The install and uninstall Stored Procedures are now emitted with `CREATE OR ALTER` instead of an existence guard, so re-running the Change Feed Migration replaces a stale procedure body instead of silently keeping it. This raises the supported server floor to SQL Server 2016 SP1. Migration: consumers on an older server must stay on 0.12.0.
- **BREAKING:** `InstallAndConfigureSqlServiceBroker` now throws `ArgumentException` for a null or whitespace `deadLetterQueueName` or `deadLetterServiceName`, and `CreateInstallationProcedure` now throws `ArgumentException` for a null or whitespace `triggerName`. Each previously emitted malformed T-SQL. Migration: only callers constructing these script types directly are affected, and they must supply the names — the Change Feed Migration already does.
- **BREAKING:** The Change Feed Migration now refuses to run when the installed SQL Service Broker topology diverges from the configured names, instead of installing over it. Before it creates or alters any Service Broker object, the install Stored Procedure reads the service-to-queue bindings recorded in `sys.services` / `sys.service_queues` and fails with a named error when the derived conversation service is bound to a queue other than the configured conversation queue, or when a service other than the configured dead-letter service is bound to this change feed's dead-letter queue. Previously the guards saw only that a service of that name existed, so a re-run under a changed queue or dead-letter service name silently installed a broken topology and left the Change Feed Trigger delivering to a queue nothing reads. The refusal is non-destructive: nothing is rebound, dropped, or repaired, and because the gate runs ahead of every Service Broker statement a refused run leaves no partial state behind. Migration: run the already-installed `Chatter_UninstallChangeFeed_<TRowChangedData>` Stored Procedure for that change feed, then re-run the Change Feed Migration.
- **BREAKING:** A configuration whose object names collide within a catalog namespace is now rejected at `AddSqlChangeFeed` time with `ChangeFeedObjectNameCollisionException`, comparing case-insensitively to match SQL Server's default collation. A configured `ChangeFeedQueueName` is rejected when it equals the dead-letter queue name, the Change Feed Trigger name, or either Change Feed Stored Procedure name — queues, triggers, and stored procedures all share the schema-scoped `sys.objects` namespace — and a configured `ChangeFeedDeadLetterServiceName` is rejected when it equals the derived conversation service name, with which it shares the database-scoped service catalog. A queue name and a service name may still be identical; those are different catalogs, so that is not a collision. Migration: a configuration that set either name to a colliding value now throws at registration instead of reaching the database, where the collision previously either let one object silently stand in for another or failed mid-install on a duplicate object name; choose a distinct name.

### Removed

- **BREAKING:** The four `IApplicationBuilder` overloads of `UseChangeFeedSqlMigrations` and `UseChangeFeedSqlMigrationsAsync` (the generic and `Type` form of each). Migration: call the `IServiceProvider` form through `ApplicationServices` — `app.UseChangeFeedSqlMigrations<T>()` becomes `app.ApplicationServices.UseChangeFeedSqlMigrations<T>()`, and `app.UseChangeFeedSqlMigrationsAsync<T>()` becomes `app.ApplicationServices.UseChangeFeedSqlMigrationsAsync<T>()`. Both `IServiceProvider` forms keep their existing `Type` overloads and `CancellationToken` parameter. The package no longer references the deprecated `Microsoft.AspNetCore.Http.Abstractions` 2.2.0, so a non-ASP.NET Core host no longer drags it in. (#389)
- **BREAKING:** The `ALTER AUTHORIZATION ON DATABASE::[<database>] TO [sa]` statement emitted during install. See the install-permission entry under Changed for the migration path. (#348)

### Fixed

- T-SQL identifier and literal escaping across all seven script emitters — `InstallAndConfigureSqlServiceBroker`, `UninstallSqlServiceBroker`, `CreateInstallationProcedure`, `CreateUninstallProcedure`, `SafeExecuteStoredProcedure`, `CreateChangeFeedTrigger`, and `DeleteChangeFeedTrigger`. Emitted identifiers are bracket-quoted with any embedded `]` doubled, and emitted single-quoted literals have their apostrophes doubled once per nesting level. This closes a hole where an apostrophe in a configured table, schema, queue, service, or Stored Procedure name escaped the `EXEC('...')` literal the install Stored Procedure is built from and executed arbitrary T-SQL at install time, and a `]` in any of those names broke out of bracket quoting. (#349)
- Live column names read back from the watched table's own `INFORMATION_SCHEMA` rows are now delimited with `QUOTENAME` when `CreateInstallationProcedure` builds the Change Feed Trigger's `@ColumnList` and `@JoinColumns`, instead of hand-concatenated brackets. `COLUMN_NAME` is `sysname` and can legally contain a closing bracket, which previously closed the identifier early inside the trigger body that `sp_executesql` then runs. This is the install-time counterpart of the emit-time escaping above (#349); that entry covers names configured at emit time, this one covers names the server reads back at install time.
- The Change Feed Migration's queue-existence guards now key on the schema-qualified object identity (`OBJECT_ID('[schema].[queue]', 'SQ') IS NULL`) instead of an unqualified `sys.service_queues` name probe. Queues are schema-scoped, so a same-named queue in another schema previously suppressed creation of the target-schema queue that `CREATE SERVICE ... ON QUEUE` then binds to.
- Install now fails with a named error, before any Service Broker object is created, when the watched table does not exist, has no `PRIMARY KEY`, or the server is Azure SQL Database (`EngineEdition` 5, which has no SQL Service Broker; Azure SQL Managed Instance remains supported). Previously a table with no primary key produced malformed Change Feed Trigger DDL that failed only after the queue and service had already been created, leaving partial state behind. (#353)
- `ChangeFeedReceiver` now applies `ConfigureAwait(false)` to every dispatch in its per-change-item loop, so a caller with a synchronization context is no longer marshalled back onto it once per row change. (#354)
- The column fingerprint the install Stored Procedure hashes now length-prefixes each column name, so the serialization is injective. A delimited identifier may legally contain the `:` and `|` separators the previous concatenation used, so two different column sets could produce the same fingerprint; across such a rename the Change Feed Migration returned without refreshing the Change Feed Trigger and subsequent DML failed against the stale column list. The fingerprint format changed, so a trigger installed by 0.13.0-preview or by an earlier package version is refreshed once on the first run after upgrading. (#350)
- The Change Feed Migration regenerates the uninstall Stored Procedure only after the install Stored Procedure has executed successfully. Both procedures are emitted with `CREATE OR ALTER`, and the uninstall procedure was previously regenerated under the *new* names before the install ran, so a re-run under a changed queue or dead-letter service name overwrote the consumer's only handle on the objects the previous run installed — the very `Chatter_UninstallChangeFeed_<TRowChangedData>` Stored Procedure the migration paths above prescribe. A failed or refused run now leaves that previously installed procedure body intact.

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
