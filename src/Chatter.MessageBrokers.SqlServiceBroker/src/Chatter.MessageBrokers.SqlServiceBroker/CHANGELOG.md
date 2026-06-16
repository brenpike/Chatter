# Changelog

All notable changes to this project will be documented in this file.

This project follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

### Changed

### Fixed

- Receive-side SQL transaction now honors the per-receiver `ReceiverOptions.TransactionMode` (set via `AddQueueReceiver<T>(transactionMode:)`) instead of always falling back to the global `MessageBrokerOptions.TransactionMode` — two receivers configured with different modes both previously used the global mode. (#235)

## [0.12.1] - 2026-06-14

### Fixed

- Enable `MultipleActiveResultSets` on the Service Broker connection — `Microsoft.Data.SqlClient` enforces MARS, which the `RECEIVE`-then-settle dialog pattern requires (the receiver otherwise threw `InvalidOperationException: The connection does not support MultipleActiveResultSets`).
- Fix `EndDialogConversationCommand` to await its command execution — it previously returned an un-awaited Task while disposing the `SqlCommand`, which `Microsoft.Data.SqlClient` rejects with `EndExecuteNonQuery cannot be called more than once`.
- Receive-path transaction lifecycle: route every receive outcome (empty-RECEIVE discard, end-dialog, ack, nack, deadletter) through a single guarded settle so each connection/transaction is committed-or-rolled-back-or-disposed exactly once — fixes `InvalidOperationException: This SqlTransaction has completed; it is no longer usable` (and an `await null` NRE under TransactionMode.None) surfaced by the Microsoft.Data.SqlClient migration.
- Decode the inbound typed payload with the converter for the inner body's own content type (carried in the envelope's `ContentType` header, `application/json`/UTF-8 by default) instead of reusing the UTF-16 envelope converter — the inner DTO is encoded UTF-8 by the core dispatcher, so reusing the UTF-16 `JsonUnicodeBodyConverter` mis-decoded it and threw `PoisonedMessageException` (`'0xE2' is an invalid start of a value`) on every round-trip. The UTF-16 envelope wire format is unchanged (non-breaking).

## [0.12.0] - 2026-06-14

### Changed

- **BREAKING:** Migrated from the deprecated `System.Data.SqlClient` to `Microsoft.Data.SqlClient` (7.0.1). The public API now exposes `Microsoft.Data.SqlClient` types — the `Scripts` command types (e.g. `BeginDialogConversationCommand`) and any `SqlConnection`/`SqlTransaction` passed via the message `TransactionContext`. Consumers compiled against `System.Data.SqlClient` that construct these types or stow a `System.Data.SqlClient.SqlTransaction` in the transaction context must migrate to `Microsoft.Data.SqlClient`. Microsoft.Data.SqlClient defaults `Encrypt=true` with server-certificate validation (the legacy provider did not). Connection strings targeting a self-signed/untrusted-certificate server must now set `Encrypt=False` or `TrustServerCertificate=True` explicitly, or `Open`/`OpenAsync` will fail. Fixes the nightly SSB integration `ReflectionTypeLoadException` (SqlGuidCaster) on net8/net10. (#204)

## [0.11.1] - 2026-06-12

### Fixed

- Renamed the SQL parameter `@timeoutInSeconds` to `@timeoutInMilliseconds` in `ReceiveMessageFromQueueCommand` to reflect that the value passed to `WAITFOR ... TIMEOUT` is in milliseconds, not seconds. Pure rename; no change to wait duration or behavior. (#181)

## [0.11.0] - 2026-06-07

### Changed

- **BREAKING:** `JsonUnicodeBodyConverter` (UTF-16) now deserializes message DTOs with System.Text.Json. Message DTOs annotated with Newtonsoft contract attributes — `[JsonProperty("name")]`, `[JsonIgnore]`, `[JsonConverter]`, etc. — are no longer honored. Migrate such DTOs to the System.Text.Json equivalents (`[JsonPropertyName("name")]`, `[System.Text.Json.Serialization.JsonIgnore]`, STJ `[JsonConverter]`). Property-name aliasing and member-ignore contracts must be re-expressed with STJ attributes or wire compatibility for those specific contracts will break. (Default Newtonsoft read/write behavior is preserved for un-annotated DTOs; only explicit Newtonsoft attribute contracts require migration.)
- Ported JsonUnicodeBodyConverter (UTF-16) to System.Text.Json via the shared Chatter.MessageBrokers serializer options; wire format unchanged. Dropped the transitive Newtonsoft.Json dependency.

### Fixed

- SQL Service Broker inbound headers now materialize System.Text.Json-deserialized context values to their CLR types so downstream typed reads no longer throw InvalidCastException; removed two vestigial Guid casts in the sender that could throw on round-tripped values.

## [0.10.1] - 2026-06-07

### Changed

- Rerouted DI registration to the shared core `MessagingInfrastructureFactory`; removed the internal `SqlServiceBrokerInfrastructureFactory` (behavior-preserving — identical scope-open/resolve/dispose semantics). Now depends on `Chatter.MessageBrokers` >= 0.10.0.

## [0.10.0] - 2026-06-07

### Changed

- `IMessageHandlerContext.SqlServiceBroker()` now returns the core `IMessageBrokerContext` (was `ISqlServiceBrokerContextDispatcher`). The same `Send`/`Publish`/`Forward` members remain, so callers using those recompile unchanged.

### Removed

- `SqlServiceBrokerContextDispatcher` and `ISqlServiceBrokerContextDispatcher` — pass-through wrappers collapsed into `IMessageBrokerContext`. Code referencing those types directly is broken.

## [0.9.0] - 2026-06-07

### Changed

- `SqlServiceBrokerReceiver` and `SqlServiceBrokerSender` are now `internal` (removed from the public API surface), thinned behind injectable SDK seams. Behaviour is unchanged.
- Connection creation now flows through an internal `ISqlConnectionSource` seam (production `SqlClientConnectionSource`), shared by the receiver and sender.
- Message classification extracted into a pure internal `ServiceBrokerMessageClassifier`; the sender's transaction-enlistment decision extracted into a pure internal `OutboundTransactionPolicy`.
- The separate `SqlServiceBrokerReceiverFactory` / `SqlServiceBrokerSenderFactory` are folded into a single `Func<>`-backed internal `SqlServiceBrokerInfrastructureFactory`.

### Removed

- `SqlServiceBrokerReceiverFactory` and `SqlServiceBrokerSenderFactory` (folded into `SqlServiceBrokerInfrastructureFactory`).

## [0.8.1] - 2026-06-06

### Fixed

- Upgraded `System.Data.SqlClient` 4.8.3 -> 4.8.6, resolving Dependabot alert #11 (SQL Data Provider Security Feature Bypass, high) and #1 (.NET Information Disclosure, medium).

## [0.8.0] - 2026-05-30

### Changed

- Target frameworks migrated from `netstandard2.1;net5.0;net6.0` to `net8.0;net10.0`.

### Removed

- Dropped the `net5.0`, `net6.0`, and `netstandard2.1` target-framework monikers. This is a breaking change for consumers pinned to those in-box assets. Consumers on modern runtimes resolve the `net8.0` or `net10.0` asset.
