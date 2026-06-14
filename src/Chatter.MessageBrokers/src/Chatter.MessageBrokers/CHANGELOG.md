# Changelog

All notable changes to this project will be documented in this file.

This project follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- `IAtomicWriteHandle` — a tier-neutral marker abstraction the `SendToOutbox` enqueue contract is abstracted over; satisfied by the relational `IPersistanceTransaction` and the future document-tier atomic-write handle (#216).
- `IInboxDeduplicator` — a tier-neutral inbox dedup contract (`HasBeenReceived`) expressing once-only-handling intent, implemented by both reliability tiers, distinct from the relational-only `IBrokeredMessageInbox.ReceiveViaInbox` wrap seam (#216).

### Changed

- **BREAKING:** `IBrokeredMessageOutbox` is split into `IBrokeredMessageOutbox` (retaining only the two `SendToOutbox` overloads, with the single-message overload remaining a default-interface-method) and a new relational-only `IPollableOutboxStore` carrying the polling-dispatch trio (`GetUnprocessedMessagesFromOutbox`, `GetUnprocessedBatch`, `UpdateProcessedDate`). `IPersistanceTransaction` now derives from `IAtomicWriteHandle`, and `IUnitOfWork` is documented as relational-only (ambient-transaction tier; the document tier never implements it). Breaking for code that implements the reliability port; ordinary adapter consumers are unaffected (#216).
- Secondary reliability facets (`IPollableOutboxStore`, `IInboxDeduplicator`) are no longer independently registered or resolved as DI services. Poll consumers obtain each secondary facet by casting the single resolved primary (`IBrokeredMessageOutbox` / `IBrokeredMessageInbox`) at the consumption site — the same pattern `OutboxProcessor` uses to obtain `IUnitOfWork`. A custom store must implement both facets on one concrete or the cast throws `InvalidCastException` at the poll site. Split-store is impossible by construction: there is exactly one resolved reliability-store instance per pair; no descriptor inspection, lifetime reconciliation, or fail-fast registration. `AddReliabilityPair` and `ReliabilityStoreLifetimeException` are deleted (#216).

### Fixed

- Per-send `SendOptions`/`PublishOptions` no longer leak into the inbound handler message context: `SendOptions.Create`/`PublishOptions.Create` now copy the inbound context so a routed/configured send does not persist its options (exchange/routing key, subject, TTL, correlation-id, etc.) into subsequent sends on the same handler context (#201).

## [0.13.1] - 2026-06-09

### Fixed

- `BrokeredMessageReceiver` startup now owns the infrastructure receiver privately through `InitializeAsync` (outside the teardown gate) and publishes it only via an atomic, non-blocking go-live/surrender handoff taken under the teardown gate, so teardown can no longer reach a still-initializing receiver. This structural change dissolves the entire "await / field-assignment outside the gate during the `Starting` window" defect class — subsuming the earlier per-window guards (the indivisible disposal-claim-with-receiver and the half-built-state guards below) — while keeping init off the gate, so a hung `InitializeAsync` cannot block teardown.
- `BrokeredMessageReceiver` teardown disposition (stop vs. dispose) is now decided exactly once, monotonically, entirely under the teardown gate, and is never reset. A premature pre-start `Dispose()` on a DI-singleton receiver still takes the `NotStarted` restartable no-op without recording a disposition, so it can no longer leak `Dispose` strength into a later genuine `StopReceiver()` (which would have disposed infrastructure that should only be stopped). Because the disposition is recorded once under the gate and never reset, strongest-wins holds by construction across the receiver's life — a `Dispose` racing or following a `Stop` still disposes infrastructure — and there is no longer a lock-free outside-the-gate strength mutation or a per-epoch reset to race against.
- `BrokeredMessageReceiver` teardown admission is now a single `SemaphoreSlim`-serialized critical section (replacing the earlier lock-free single-flight flag) layered atop the retained lifecycle+strength state machine (`NotStarted` → `Starting` → `Live` → `TornDown`). By construction this makes impossible: (a) stranded-admission hangs — losers serialize on the gate and re-observe terminal state; (b) startup/teardown orphaned primitives and double-Stop — loop-primitive construction and go-live run under the same gate teardown takes, so teardown sees either the pre-construction infra-only state or the full set, never a half-built one; (c) disposal-disposition races — the teardown disposition is recorded once under the same teardown gate and never reset (subsuming the former separate escalation step and per-epoch reset), so a teardown loser can neither erase a recorded `Dispose` request via a concurrent reset nor observe a stale disposition that would leave infrastructure merely stopped instead of disposed; deadlock-safe with no `SynchronizationContext` capture; (d) disposal-claim-over-null-receiver leak — the one-shot infrastructure disposal claim is now produced indivisibly with the non-null receiver via a single primitive, so a `Dispose`-strength teardown racing the `Starting` window before the infrastructure receiver is assigned can no longer latch the disposal claim over a null receiver and leak the infrastructure receiver that the abandoned go-live path subsequently disposes. Preserved: idempotency, strength monotonicity (a `Dispose` racing a `Stop` still disposes infrastructure), startup-window partial teardown, and the original non-OCE receive-loop-fault TOCTOU fix that caused a net8.0-specific flaky shutdown (`WhenReceiveLoopFaults`).
- `BrokeredMessageReceiver` teardown is now non-throwing and terminal, aligned to the canonical .NET `DisposeAsync`/`DisposeAsyncCore` pattern. Infrastructure-teardown faults raised during `DisposeAsync`, `Dispose`, or `StopReceiver` are swallowed-and-logged (per the guideline that disposal must not throw) and teardown still reaches a clean terminal state — replacing the prior fault-resettable retry machinery: a faulted quiesce or transient teardown fault no longer leaves teardown retryable, and synchronous `Dispose` no longer defers latching `disposed` on a fault. Net behavior: teardown is serialized and race-free across startup, strength, and epoch, and now also terminal and non-throwing rather than retryable.
- `BrokeredMessageReceiver.DisposeAsync` now follows the canonical `DisposeAsyncCore` template, delegating teardown through the shared core so async disposal, synchronous `Dispose`, and `StopReceiver` share one terminal, non-throwing teardown path.
- A throwing `InitializeAsync` during startup now disposes the partially-initialized infrastructure receiver instead of leaking it; startup remains startup-fatal and the original exception propagates.

## [0.13.0] - 2026-06-09

### Added

- Real concurrent message processing bounded by `MaxConcurrentCalls`: `BrokeredMessageReceiver` now fans out up to `MaxConcurrentCalls` concurrent processing workers, gated by a semaphore (default `MaxConcurrentCalls = 1` preserves the existing sequential behavior unchanged). Satisfies the "not yet delivered (#147)" caveat carried in the 0.12.0 release notes — real parallelism is now delivered and test-verified (#147).

## [0.12.0] - 2026-06-09

### Added

- `ReceiverOptions.MaxConcurrentCalls` (default 1) — is now carried through as a source-of-truth value (previously hard-coded to 1) and sizes the receive-loop semaphore. NOTE: actual parallel message processing is not yet delivered; the value is honored at receiver init but the receive loop still processes one message at a time. Real concurrency is tracked as a follow-up (#147).
- `IDiscoveredReceiverRegistry` / `DiscoveredReceiverRegistry` — a generic, infrastructure-agnostic seam that retains discovered receiver options so any messaging infrastructure (in-repo, out-of-repo, or consumer-built) can participate in its own startup bookkeeping without re-scanning assemblies.

### Fixed

- Startup-fatal receiver errors (e.g. an infrastructure's cross-entity startup guard) now propagate out of `IHostedService.StartAsync` to abort host startup loudly instead of being silently swallowed (the receiver no longer stops silently while the host keeps running).

## [0.11.1] - 2026-06-09

### Changed

- Re-released to pull in Chatter.CQRS 0.8.1 (assembly-source scan no longer throws `ReflectionTypeLoadException` on dynamic/unloadable assemblies); no functional change to Chatter.MessageBrokers itself.

## [0.11.0] - 2026-06-07

### Changed

- **BREAKING:** Newtonsoft.Json is removed. Message DTOs annotated with Newtonsoft contract attributes — `[JsonProperty("name")]`, `[JsonIgnore]`, `[JsonConverter]`, etc. — are no longer honored. Migrate such DTOs to the System.Text.Json equivalents (`[JsonPropertyName("name")]`, `[System.Text.Json.Serialization.JsonIgnore]`, STJ `[JsonConverter]`). Property-name aliasing and member-ignore contracts must be re-expressed with STJ attributes or wire compatibility for those specific contracts will break. (The migration preserves default Newtonsoft read/write behavior — casing, number/leniency, private-setter binding, type fidelity — for un-annotated DTOs; only explicit Newtonsoft attribute contracts require migration.)
- Replaced Newtonsoft.Json with System.Text.Json for all serialization (body converter, routing slips, outbox message-context). Wire format preserved byte-for-byte via a custom relaxed JSON encoder (ChatterJson/ChatterJsonEncoder) that mirrors Newtonsoft escaping, including literal supplementary-plane/emoji output, so persisted and in-flight payloads remain cross-version compatible.

### Fixed

- Outbox replay now restores heterogeneous CLR types from the persisted message context (JSON integers → Int64, ISO-8601 strings → DateTime) so the Azure Service Bus typed readers (scheduled-enqueue time, time-to-live, receive attempts) no longer throw and outbox rows are no longer stranded. Wire format unchanged.
- RoutingSlip visited-step history (non-empty) now survives JSON round-trip. Wire format unchanged.
- Outbox replay and SQL Service Broker receive now restore heterogeneous CLR types from the persisted/transmitted message context via a centralized materializer applied at every System.Text.Json deserialization seam, so typed header readers no longer throw. Newtonsoft wire/round-trip parity preserved (JSON strings are not coerced to Guid).
- Object-typed and `IDictionary<string,object>`-typed values now restore CLR-type fidelity by construction at every System.Text.Json deserialize seam — message body DTOs (`object`/dictionary members), routing-slip attachments, and outbox/SQL Service Broker message context — via a global object converter on the shared serializer options. Integer JSON values materialize to `Int64`, ISO-8601 strings to `DateTime`, structured values to navigable `Dictionary<string,object>`/`List<object>`; Guid-shaped strings stay `string` (Newtonsoft parity). Replaces the prior per-seam materialization with a single source of truth. Wire format unchanged (serialize output byte-identical).

### Removed

- Removed the Newtonsoft.Json package dependency.

## [0.10.0] - 2026-06-07

### Added

- `MessagingInfrastructureFactory` — a public shared `Func<>`-backed factory implementing both `IMessagingInfrastructureReceiverFactory` and `IMessagingInfrastructureDispatcherFactory`. Brokers register it with their receiver/dispatcher delegates instead of each shipping an identical internal factory.

## [0.9.0] - 2026-05-30

### Changed

- Target frameworks migrated from `netstandard2.1;net5.0;net6.0` to `net8.0;net10.0`.

### Removed

- Dropped the `net5.0`, `net6.0`, and `netstandard2.1` target-framework monikers. This is a breaking change for consumers pinned to those in-box assets. Consumers on modern runtimes resolve the `net8.0` or `net10.0` asset.
