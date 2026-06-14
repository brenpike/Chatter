# PRD: Cosmos DB reliability provider (two-tier reliability port)

## Problem Statement

Chatter.MessageBrokers' reliability port (outbox/inbox/unit-of-work) is shaped entirely around a relational ambient-transaction model: the unit of work wraps the whole message handler in an open transaction. Teams building on Azure Cosmos DB (NoSQL) cannot use it — Cosmos has no ambient transaction; its atomic primitive is a stage-then-commit batch confined to one logical partition. Today such teams must hand-write transactional-outbox plumbing per app, risking non-atomic "publish after a separate write" bugs and accidentally deriving integration events from domain-document change feeds. They need a first-class Cosmos reliability provider that realizes the transactional-outbox atomic-writes pattern with the same reliability guarantees the EntityFramework provider gives relational users.

## Solution

Evolve the reliability port to Two-Tier Reliability: a Relational Tier (the existing ambient-transaction, polling-dispatch model, behavior-unchanged) and a new Document Tier (NoSQL stage-then-commit). Both tiers share one seam — the enqueue contract, the inbox contract, and the transaction-context Atomic-Write Handle. A new Cosmos reliability provider lets an application atomically persist its aggregate and its outbound messages in a single partition-scoped batch the application owns, while the provider contributes the Chatter-owned outbox record to that batch (handler-owns-batch, framework-contributes-outbox). A change-feed Outbox Relay drains persisted outbox records — identified by an outbox discriminator — and publishes them through the broker, never deriving events from domain-document changes. Inbox idempotency uses conflict-on-create dedup. The existing EntityFramework users are unaffected at runtime.

## User Stories

- As an app developer on Cosmos DB, I want atomic aggregate and outbound-message persistence in one partition batch, so that I never publish an event whose state write rolled back.
- As an app developer, I want Chatter to own the outbox-record shape and the relay, so that I write no hand-written outbox plumbing and face no risk of deriving events from domain changes.
- As a message consumer, I want idempotent inbox dedup on the Document Tier, so that a redelivered message is handled once.
- As an existing EntityFramework reliability user, I want the Relational Tier to behave identically after the port change, so that upgrading does not change runtime behavior.
- As a library maintainer, I want the breaking core-port change to be scoped and versioned, so that implementers, not ordinary consumers, absorb the break knowingly.
- As a maintainer and CI, I want an emulator-backed integration suite that asserts Chatter's contracts, so that atomicity, relay, and dedup regressions are caught.

## Acceptance Criteria

- The core reliability port exposes a common enqueue contract and a common inbox contract implemented by both tiers, plus a relational-only Pollable Outbox Store capability the Document Tier does not implement; the unit-of-work (ambient-transaction) capability is relational-only.
- The Relational Tier (EntityFramework provider) exhibits identical observable behavior before and after the change (ambient transaction; polling dispatch).
- On the Document Tier, an application's aggregate write and its outbound message(s) either both persist or neither does, within a single logical partition.
- A Document-Tier outbound message persisted via the enqueue contract becomes a Chatter-owned outbox record Co-Resident with the aggregate (same container, same logical partition), carrying the agreed identity and discriminator, with an expiry applied after delivery.
- The Outbox Relay publishes only outbox records (selected by discriminator) and never publishes from domain-document changes.
- Each outbox record is published through the broker exactly once under normal operation.
- A redelivered message with the same identity is handled once (conflict-on-create dedup).
- Aggregate concurrency conflicts surface as an optimistic-concurrency failure the application can retry; inbox/outbox identity collisions surface as create-conflicts.
- The Cosmos provider package targets net8.0 and net10.0 and depends on the Cosmos SDK major line that provides the batch and change-feed-processor capabilities.
- Versioning: the core MessageBrokers package takes a breaking-change version step; the EntityFramework provider takes a corresponding version step; the new Cosmos provider ships at its initial version. CHANGELOG entries exist for all three, including a breaking-change note on the core.

## Implementation Decisions

- Two coexisting reliability tiers share three seams: the enqueue contract, the inbox contract, and the transaction-context Atomic-Write Handle (the active relational transaction or the active document-store batch, carried on the same context container). This is the unifying decision that lets one outbox enqueue contract serve both tiers.
- The outbox contract is split: a common enqueue responsibility versus a relational-only polling-dispatch responsibility. The Document Tier implements only enqueue and its own relay; it never implements polling dispatch or the unit-of-work.
- The Document Tier uses handler-owns-batch, framework-contributes-outbox: the application owns the batch, the partition, and aggregate serialization; the provider owns the outbox-record shape and the relay. This keeps the messaging library ignorant of application domain types and container ownership, and makes aggregate-plus-outbox co-location structurally enforced.
- The outbox record is Co-Resident with its aggregate in one logical partition (cross-partition and cross-container atomic writes are physically impossible on the platform and are a non-goal).
- Dispatch on the Document Tier is a change-feed Outbox Relay filtered by an outbox discriminator — not a polling query — and is forbidden from deriving integration events from domain-document changes.
- Idempotency and concurrency model on the Document Tier: optimistic concurrency for aggregate writes; conflict-on-create dedup for inbox and outbox identity.
- This is a breaking change to the core reliability port; the breaking surface is confined to code that implements the port or constructs outbox records directly, not to ordinary consumers of broker adapters.

## Testing Decisions

- An emulator-backed integration suite verifies Chatter's contracts, not the database SDK. Asserting a hand-built batch merely exists is banned as the system-under-test; every test drives Chatter's public contracts and observes results through them.
- Required contract coverage (intent, not test code): the enqueue contract produces the correct Co-Resident outbox record on the application's batch; atomicity (a forced aggregate concurrency failure leaves no outbox record; success yields both aggregate and outbox record); inbox idempotency (a duplicate identity is handled once); Outbox Relay discriminator filtering (a domain-document change is not published, an outbox record is published); relay publish-once plus post-delivery expiry; and an end-to-end path from handler through relay to a broker sink exercised through Chatter's public API.
- The suite is docker-gated and runs in the existing CI integration lane alongside the other adapters' integration suites; it auto-skips when the container runtime is unavailable.
- The Relational Tier retains its existing unit-test coverage, extended to prove the interface split is behavior-preserving.

## Success Metrics

- A Cosmos DB application can adopt the provider and achieve atomic aggregate-plus-event persistence without writing its own outbox plumbing.
- Zero observed "event published but state write rolled back" outcomes under the atomicity tests.
- Existing EntityFramework consumers upgrade across the breaking core version with no runtime-behavior change.
- The integration suite reliably passes in the CI integration lane (flake rate low enough not to gate unrelated PRs).

## Out of Scope

- Cross-partition and multi-aggregate atomic writes (saga and process-manager territory).
- Deriving integration events by tailing the domain change feed.
- The older Cosmos SDK major line and the separate non-SDK client packages.
- Migrating existing EntityFramework consumers off the Relational Tier (it remains, unchanged).
- A new strongly-typed core partition-key property (the application owns the partition by owning the batch).
- Any specific downstream application's own integration with this provider.

## Further Notes

- Primary delivery risk: change-feed Outbox Relay correctness (publish-once and lease management) is the most novel surface and warrants the heaviest contract coverage.
- The Cosmos emulator for the container runtime is heavy and historically flaky in CI; mitigation is the lighter preview emulator plus a generous startup budget and strict docker-gating so it never causes timeout-only failures on unrelated PRs.
- Exact version numbers and per-package CHANGELOG wording are resolved at implementation time under the project's versioning governance; the breaking-change classification on the core is fixed.
