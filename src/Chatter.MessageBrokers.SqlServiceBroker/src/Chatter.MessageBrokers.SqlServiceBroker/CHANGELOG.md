# Changelog

All notable changes to this project will be documented in this file.

This project follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

### Changed

### Fixed

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
