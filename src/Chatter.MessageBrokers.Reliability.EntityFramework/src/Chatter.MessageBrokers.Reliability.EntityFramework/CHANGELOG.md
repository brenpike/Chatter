# Changelog

All notable changes to this project will be documented in this file.

This project follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

### Changed

### Fixed

## [0.7.0] - 2026-09-14

### Changed

- `BrokeredMessageInbox<TContext>.ReceiveViaInbox` now saves its own `InboxMessage` row. Inside a unit of work the save enlists in the ambient transaction, so atomicity is unchanged. The row is now accepted into the context rather than left pending, so a consumer inspecting the change tracker for a pending `InboxMessage` will no longer find one.
- `WithUnitOfWorkBehavior`, `WithInboxBehavior` and `WithOutboxProcessingBehavior` now guarantee a fixed resolved order — outbox processing wraps the unit of work, which wraps the inbox — regardless of call order or call count. Migration: consumers who called `WithInboxBehavior` before `WithOutboxProcessingBehavior` previously resolved inbox, outbox, unit-of-work and need no code change. Consumers who register their own behaviours between the reliability extension calls keep their behaviour's slot, but its position relative to the reliability behaviours may differ from before.

### Fixed

- Reliability behaviours could resolve in an order that depended on call order or call count, rather than the intended outbox-wraps-unit-of-work-wraps-inbox order (#379).

No schema change and no migration is required for this release.

## [0.6.1] - 2026-09-14

### Changed

- `BrokeredMessageOutbox<TContext>` now serializes the outbound message context through `Chatter.MessageBrokers`'s public `ChatterJson.Serialize` method instead of reaching into the internal `ChatterJson.Options` field directly. The persisted outbox payload is byte-identical, so existing outbox rows round-trip unchanged and there is no behaviour change for consumers of this package.
- Bundled dependency uplift to Chatter.MessageBrokers 0.31.0 (an in-repo `ProjectReference`, so the pack-time package dependency moves with it). This uplift must be taken together with this release: `Chatter.MessageBrokers` 0.31.0 removes the internal grant this package previously used to reach `ChatterJson.Options`, so an older `Chatter.MessageBrokers.Reliability.EntityFramework` binary composed against `Chatter.MessageBrokers` 0.31.0 or later would fail at runtime. Republishing this package with a `Chatter.MessageBrokers` >= 0.31.0 floor closes that gap.

## [0.6.0] - 2026-09-02

### Changed

- The relational outbox drain this package's `BrokeredMessageOutbox<TContext>` stores rows for is now instrumented: each drain cycle emits a send span, a sent-messages count, and a duration measurement. The drain logic itself lives in `Chatter.MessageBrokers`; this package's own storage and polling behavior is unchanged (#407).
- Bundled dependency uplift to Chatter.MessageBrokers 0.18.0 (an in-repo `ProjectReference`, so the pack-time package dependency moves with it).

## [0.5.0] - 2026-09-01

### Added

- Published packages now ship a symbol package (`.snupkg`), an embedded `README`, a project URL, and are built deterministically. Package builds are now reproducible CI builds with SourceLink-resolvable sources, so a debugger can step into this package's original source from a consuming application.

### Changed

- Conforms to the split core reliability port: `BrokeredMessageOutbox<TContext>` now implements `IPollableOutboxStore` and `BrokeredMessageInbox<TContext>` now implements `IInboxDeduplicator`. Behavior-preserving — the polling and dedup bodies are unchanged; the secondary reliability facets (`IPollableOutboxStore`, `IInboxDeduplicator`) are no longer independent DI registrations; poll consumers obtain them by casting the single resolved primary (`IBrokeredMessageOutbox` / `IBrokeredMessageInbox`) at the consumption site. A custom store must implement both facets on one concrete or the cast throws `InvalidCastException` at the poll site. Split-store is impossible by construction: there is exactly one resolved reliability-store instance per pair; no descriptor inspection, lifetime reconciliation, or fail-fast registration. Requires Chatter.MessageBrokers 0.17.0 (#216).

## [0.4.1] - 2026-06-07

### Changed

- Ported outbox message-context serialization to System.Text.Json via the shared serializer options; persisted wire format unchanged. Dropped the transitive Newtonsoft.Json dependency.

## [0.4.0] - 2026-05-30

### Changed

- Target frameworks migrated from `netstandard2.1;net5.0;net6.0` to `net8.0;net10.0`.
- EF Core version is now target-framework-conditional: `net8.0` uses EF Core 8.0.x and `net10.0` uses EF Core 10.0.x.

### Removed

- Dropped the `net5.0`, `net6.0`, and `netstandard2.1` target-framework monikers. This is a breaking change for consumers pinned to those in-box assets. Consumers on modern runtimes resolve the `net8.0` or `net10.0` asset.
