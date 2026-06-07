# Changelog

All notable changes to this project will be documented in this file.

This project follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

### Changed

### Fixed

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
