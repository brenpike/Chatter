# Changelog

All notable changes to this project will be documented in this file.

This project follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

### Changed

### Fixed

## [0.1.0] - 2026-06-14

### Added

- Initial document-tier (Cosmos DB) reliability provider **skeleton** (#218): the package and DI surface, the document-tier reliability surface (the doc-tier atomic-write handle — sibling of `IPersistanceTransaction` — carrying the bound container, the open `TransactionalBatch`, the resolved partition-key value, and the container's partition-key path), the app-registered partition-key resolver, and the Document-Tier Batch-Lifecycle Behavior **shell** registered as the outermost pipeline behavior. The behavior opens a `TransactionalBatch` on the resolved partition and executes it once after the handler — guarded so an empty batch (zero staged ops) never calls the Cosmos transport. The change-feed lease container is registered for the future relay. Outbox enqueue, inbox dedup marker, the change-feed relay, and the Cosmos emulator suite are **not** implemented in this release — they arrive in #219 (outbox), #220 (inbox), #222 (relay), and #223 (emulator suite).
