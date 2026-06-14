# Changelog

All notable changes to this project will be documented in this file.

This project follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Document-tier outbox enqueue (#219): `CosmosBrokeredMessageOutbox` realizes `IBrokeredMessageOutbox.SendToOutbox` by resolving the active doc-tier atomic-write handle from the reliability surface and contributing the Chatter-owned outbox document's `CreateItemStream` op to the framework-owned `TransactionalBatch` (it never executes the batch — the Document-Tier Batch-Lifecycle Behavior owns the single commit point). The outbox document (`CosmosOutboxDocument`) carries the `_chatterType="outbox"` discriminator, an `outbox:{encoded(MessageId)}` id via the shared Cosmos-id-safe encoder (`CosmosItemId`, reused by #220 for inbox markers), the raw `MessageId` verbatim, the `MessageContext` serialized with `ChatterJson.Options` (EF parity), the message body/destination/content-type, and `status="pending"`; the resolved partition-key value is stamped at the container's actual partition-key path (supporting a hierarchical path), not a fixed field. Registered as `IBrokeredMessageOutbox` with the outbox router in DI.
- Public op-staging contribution path on `ICosmosAtomicWriteHandle` (`MarkOperationStaged()` / `StagedOperationCount`) so the outbox/inbox can contribute ops to the framework-owned batch through the public interface. Closes the first of the two deferred P1 skeleton findings.
- Batch-response inspection in the Document-Tier Batch-Lifecycle Behavior: after the single `ExecuteAsync`, a non-success `TransactionalBatchResponse` throws `CosmosBatchExecutionException` so the message is not acked when the writes did not commit (a forced aggregate ETag/412 surfaces here). The inspection is structured as a clean seam so #220 can later distinguish a confirmed-duplicate inbox-marker 409. The empty-batch guard (skip `ExecuteAsync` when no op was staged) is preserved. Closes the second deferred P1 skeleton finding.

### Changed

### Fixed

## [0.1.0] - 2026-06-14

### Added

- Initial document-tier (Cosmos DB) reliability provider **skeleton** (#218): the package and DI surface, the document-tier reliability surface (the doc-tier atomic-write handle — sibling of `IPersistanceTransaction` — carrying the bound container, the open `TransactionalBatch`, the resolved partition-key value, and the container's partition-key path), the app-registered partition-key resolver, and the Document-Tier Batch-Lifecycle Behavior **shell** registered as the outermost pipeline behavior. The behavior opens a `TransactionalBatch` on the resolved partition and executes it once after the handler — guarded so an empty batch (zero staged ops) never calls the Cosmos transport. The change-feed lease container is registered for the future relay. Outbox enqueue, inbox dedup marker, the change-feed relay, and the Cosmos emulator suite are **not** implemented in this release — they arrive in #219 (outbox), #220 (inbox), #222 (relay), and #223 (emulator suite).
