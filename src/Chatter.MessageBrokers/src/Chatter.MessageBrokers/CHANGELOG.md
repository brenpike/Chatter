# Changelog

All notable changes to this project will be documented in this file.

This project follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.19.0] - 2026-09-03

### Added

- `CircuitBreakerOptionsValidationException` (namespace `Chatter.MessageBrokers.Recovery.CircuitBreaker`) — thrown by `CircuitBreakerOptionsBuilder.Build()` when the built `CircuitBreakerOptions` carry values the circuit breaker can never run with, naming every invalid knob in one aggregated message. Previously an invalid circuit-breaker configuration surfaced later as a bare `ArgumentOutOfRangeException` from `new SemaphoreSlim(0, 0)`, thrown the first time the breaker was resolved rather than at build time (#296, #311, #312).

### Changed

- **`MessageBrokerOptions`, `ReliabilityOptions`, `RecoveryOptions`, and `CircuitBreakerOptions` now bind their configuration section INTO the fluent-defaulted instance (`BindNonPublicProperties = true`) instead of REPLACING it via `Get<T>()`.** Every bindable property on these types is `internal set`, and `ConfigurationBinder.Get<T>()` defaults to `BindNonPublicProperties = false`, so a populated configuration section previously bound nothing and every configured value was silently discarded. Replacing the instance also skipped the fluent defaults applied elsewhere in the builder, so the result was worse than not configuring anything at all. Unspecified keys now keep their builder defaults; specified keys are honoured (#296, #311, #312).
- **`TransactionMode` no longer degrades from the documented `ReceiveOnly` default to `None` when a configuration section is present.** This was the most severe consequence of the binding defect above: `TransactionMode.None`'s own doc comment warns that a message is lost if an error occurs after receipt, so a consumer with any populated `Chatter:MessageBrokers` section was silently running with no at-least-once delivery guarantee, regardless of what it had configured. **Upgrader risk:** a consumer whose configuration held a stale or incorrect `TransactionMode` value has been protected from it by this bug and will see that value take effect for the first time on upgrade — review your configured `TransactionMode` before upgrading (#296, #311, #312).
- `AddMessageBrokers` now actually reads the `Chatter:MessageBrokers` configuration section. Previously the documented DI entry point never passed a section to the builder, so `appsettings` configuration was inert no matter how it was written (#296, #311, #312).
- `RecoveryOptions.CircuitBreakerOptions` is now aliased to the documented `CircuitBreaker` configuration key via `[ConfigurationKeyName]`, so the same documented key is honoured regardless of entry point (#296, #312).
- The instance `MessageBrokerOptionsBuilder.FromConfig(string)` no longer discards accumulated fluent state or registers a shadow second set of options singletons (#296, #311).

Upgrader note: any consumer that already had a populated `Chatter:MessageBrokers` section (including its `CircuitBreaker`, `Reliability`, and `Recovery` subsections) has been running on type defaults regardless of what was configured. On upgrade, that configuration starts taking effect — review `MaxRetryAttempts`, the circuit-breaker thresholds, and especially `TransactionMode` before upgrading (#296, #311, #312, #313).

## [0.18.1] - 2026-09-02

### Changed

- The propagation-scope documentation now describes the Cosmos change-feed relay alongside `OutboxProcessor`: both outbox drains reparent when diagnostics are opted into. Documentation only; no API or runtime change.

## [0.18.0] - 2026-09-02

### Added

- `TraceContextPropagator.TryExtractFromMessageContext(IDictionary<string, object>, out ActivityContext)` — reads a persisted trace context off a message-context dictionary (#407).
- `SendScope` — a public readonly struct owning the send-side diagnostics ceremony (off-guard, span, propagation, failure recording, duration and count) in one place, so a module adding its own send instrumentation cannot get the disabled-path discipline wrong. `default(SendScope)` is the well-formed disabled value and allocates nothing. `SendScope.Open(string, string, string, int, ActivityContext)` accepts an explicit parent trace context, so a deferred send (an outbox drain, minutes after the write) can be parented to the context persisted with the message rather than to whatever activity happens to be ambient at drain time; the parented span start is not exposed as a bare primitive (#407).

### Changed

- The relational outbox drain now emits a send span, a sent-messages count and a duration measurement. Previously the drain hop — the broker publish that happens minutes after the write, in a separate process, and can fail on its own — was entirely unobserved. For applications with diagnostics enabled the drained message now carries the drain span's trace context rather than the write-time context verbatim: the drain span is a child of the persisted context, so the chain is intact and gains a correct extra hop. This is wire-visible for opted-in applications only; with diagnostics off nothing is injected and the persisted `traceparent` rides out unchanged (#407).
- A reply's send span now reports the same `messaging.batch.message_count` as its sent-messages counter. Previously the span always reported `1` while the counter reported `0` or `1`, so the two disagreed about the same event (#407).
- A reply whose construction fails now produces a send span — error-statused, count `0` — alongside the failure measurement it already produced. Previously that failure was recorded on the metric with no matching span. The `messaging.system` attribute on that measurement is now the infrastructure type from the inbound message context rather than being absent (#407).

## [0.17.1] - 2026-09-02

### Fixed

- `messaging.client.operation.duration` records **seconds**, but shipped no bucket advice — a collector applied its own millisecond-sized default boundaries (`0, 5, 10, 25, ... 10000`), and every realistic broker operation landed in the first bucket, so P50, P90, and P99 all reported the same bucket bound forever. The instrument now publishes seconds-sized bucket advice — `0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10` — but only on `net10.0`: the base class library type that carries instrument advice does not exist in the `net8.0` shared framework, and the package takes no package dependency to reach it. `net10.0` applications that already built dashboards against the previous millisecond-sized defaults will see the bucket bounds move — that is the fix, not a regression, but it is a visible change. On `net8.0`, configure the equivalent view in your own application; see the **Histogram bucket boundaries** subsection in `src/Chatter.MessageBrokers/src/README.md` for the copy-pasteable `AddView` snippet (#399).

### Changed

- Bundled dependency uplift to Chatter.CQRS 0.11.1 (an in-repo `ProjectReference`, so the pack-time package dependency moves with it); no behavioral change to Chatter.MessageBrokers itself.

## [0.17.0] - 2026-09-01

### Added

- Published packages now ship a symbol package (`.snupkg`), an embedded `README`, a project URL, and are built deterministically. Package builds are now reproducible CI builds with SourceLink-resolvable sources, so a debugger can step into this package's original source from a consuming application.

### Changed

- Raised the net8.0-leg `Microsoft.Extensions.Hosting` dependency floor to `8.0.1`, off a dependency graph that carried an advisory-affected `System.Text.Json` 8.0.0 floor. The net10.0 leg is unchanged.
- Bundled dependency uplift to Chatter.CQRS 0.11.0 (an in-repo `ProjectReference`, so the pack-time package dependency moves with it); no behavioral change to Chatter.MessageBrokers itself.

## [0.16.2] - 2026-08-31

### Fixed

- The send span no longer emits `messaging.system` as an empty string when a consumer explicitly selects the default Messaging Infrastructure via `UseMessagingInfrastructure(t => t.Default)`; the attribute is now left unset across all three send paths (dispatch, forward, reply). Send metric measurements carry the `messaging.system` key with a null value in that case, as the key is kept rather than omitted. Consumers filtering or grouping on the empty-string bucket will see those spans as having no `messaging.system` (#293).

## [0.16.1] - 2026-08-31

### Fixed

- The receive span no longer emits `messaging.system` as an empty string when the Brokered Message Receiver was configured without a Messaging Infrastructure type; the attribute is now left unset, matching the send span. Receive metric measurements carry the `messaging.system` key with a null value in that case, as the send metrics already do. Consumers filtering or grouping on the empty-string bucket will see those spans as having no `messaging.system` (#289).

## [0.16.0] - 2026-08-30

### Changed

- **BREAKING:** `IMessagingInfrastructureReceiver.AckMessageAsync`, `NackMessageAsync`, and `DeadletterMessageAsync` now return `Task<SettlementResult>` instead of `Task<bool>`. `SettlementResult` (backed by the new `SettlementOutcome` enum: `Settled`, `NotRequired`, `Failed`) distinguishes "there was nothing to settle" (e.g. Azure Service Bus `ReceiveAndDelete`, RabbitMQ at-most-once) from "settlement was attempted and did not happen" — a distinction the former `bool` collapsed into a single `false`. A custom `IMessagingInfrastructureReceiver` implementation must migrate its three settlement members to return `SettlementResult.Settled()`, `SettlementResult.NotRequired(reason)`, or `SettlementResult.Failed(reason)` in place of `true`/`false`. Receive-failure retention (the diagnostics an application observes on a failed receive) now keys on the `Failed` outcome rather than a bare `false`, so `BrokerDiagnostics.ErrorTypes.SettlementFailed` (`"settlement_failed"`) now appears as `error.type` on a receive metric for a settlement the infrastructure reported as failed without throwing, in addition to the pre-existing exception-carrying case (#283).
- **BREAKING:** `IMessagingInfrastructureReceiver` gains a defaulted `bool WritesToErrorQueue => false` member: whether the infrastructure writes a failed delivery to the Error Queue itself, so the core must not run its own error-recovery action (`ErrorQueueDispatcher` → `IForwardMessages`) for that delivery and duplicate the write. A settlement is now eligible for the core's error-recovery action only when it is BOTH settled AND `WritesToErrorQueue` is `false` — previously an infrastructure that owned the Error Queue write could only suppress the core's duplicate by misreporting its own settlement as `false`. A custom implementation that owns its own Error Queue write must now override `WritesToErrorQueue` to report `true` instead of misreporting its settlement outcome (#283).
- Bundled dependency uplift to Chatter.CQRS 0.10.0 (an in-repo `ProjectReference`, so the pack-time package dependency moves with it); no behavioral change to Chatter.MessageBrokers itself beyond the additions above.

## [0.15.0] - 2026-08-29

### Added

- Optional, opt-in OpenTelemetry-compatible tracing and metrics built entirely on the BCL (`System.Diagnostics.ActivitySource` and `System.Diagnostics.Metrics.Meter`) with no `OpenTelemetry.*` package dependency. The `ActivitySource` and the `Meter` are both named `Chatter.MessageBrokers`; an application opts in BY NAME with `.AddSource("Chatter.*")` / `.AddMeter("Chatter.*")` on its own provider. The scope names are the opt-in contract — the `ActivitySource` and `Meter` instances themselves are not public surface. When nothing subscribes to the source or the meter there is no cost and no change to what goes on the wire (#274).
- Broker-boundary instrumentation: a send span per dispatch call and a receive span per delivery, a `messaging.client.operation.duration` histogram in seconds, and `messaging.client.sent.messages` / `messaging.client.consumed.messages` counters. Instrumentation never materializes a lazily-supplied publish batch — a caller's sequence is enumerated once, by the router, at the same moment and with the same side effects whether diagnostics are on or off. The batch count is therefore observed DURING that enumeration and carried on the send span at stop, and `messaging.client.sent.messages` reports the messages actually yielded, so a dispatch that fails mid-enumeration records a partial count rather than a whole batch (#274).
- Receive metrics carry `error.type` for a delivery the worker's error ladder SETTLED — a poisoned message, an exhausted delivery, a nack — not only for a failure that escaped the worker, so an application subscribed to the meter and to no `ActivitySource` does not see a failed receive reported as a successful operation (#274).
- A delivery carrying no usable `traceparent` starts a fresh root span with any ambient activity attached as a link, matching how a delivery that does carry one treats ambient context. A headerless delivery is no longer a child of whatever unrelated host activity happened to be current when the receive loop started, and the ambient activity that suppression sets aside is restored once the receive span stops, so it is intact for the rest of that delivery's flow whether the span was sampled in or out (#274).
- The send span and the send metrics carry `messaging.destination.name` for the `Send`/`Publish` overloads that omit an explicit destination and let each message's `BrokeredMessageAttribute` resolve one. The destination is observed during the single enumeration the router already performs — never resolved eagerly — and is reported only when every message of the dispatch call resolved to the SAME destination; a batch spanning several destinations reports none, because the attribute holds one value (#274).
- Receive metrics carry `error.type` when message handling succeeded but the settlement then failed — an acknowledgement, negative acknowledgement, deadletter or failed-recovery action that Recovery could not complete. Such a failure is swallowed into a `false` return by design and so never leaves the processing worker, which means it would otherwise have been reported as a successful receive while the message stayed unsettled and eligible for redelivery (#274).
- W3C trace-context propagation (`traceparent` / `tracestate`) across the brokered message boundary, carried on the message context so it survives the outbox and replay. Scope is deliberately partial: trace context flows for Azure Service Bus, RabbitMQ, the EntityFramework outbox, and the Cosmos outbox. It does not flow across the SQL Service Broker `DefaultType` receive path, nor for SQL Change Feed messages, which originate from a SQL trigger with no producer to stamp them. Both gaps are pre-existing limitations that affect all headers alike and are not introduced by tracing (#274).

### Changed

- The outbound dispatch sequence is now documented as single-pass on both seams it crosses (`IRouteBrokeredMessages.Route` and `IMessagingInfrastructureDispatcher.Dispatch`): the sequence is lazy and its iterator carries per-yield side effects — message id generation, message body conversion, W3C trace-context propagation — plus the send span's batch count, so an implementation must enumerate it exactly once and must not walk it with `Count()`/`Any()`/`ToList()` in addition to the real enumeration. This documents a contract the pipeline already relied on; no behavior change (#274).
- Bundled dependency uplift to Chatter.CQRS 0.9.0 (an in-repo `ProjectReference`, so the pack-time package dependency moves with it); no behavioral change to Chatter.MessageBrokers itself beyond the additions above.

## [0.14.1] - 2026-06-15

### Fixed

- A handler-supplied outbound `MessageId` set via `SendOptions`/`PublishOptions` now survives the handler-context Send/Publish merge and is no longer replaced by a framework-generated id (#245).

## [0.14.0] - 2026-06-14

### Added

- `IAtomicWriteHandle` — a tier-neutral marker abstraction the `SendToOutbox` enqueue contract is abstracted over; satisfied by the relational `IPersistanceTransaction` and the future document-tier atomic-write handle (#216).
- `IInboxDeduplicator` — a tier-neutral inbox dedup contract (`HasBeenReceived`) expressing once-only-handling intent, implemented by both reliability tiers, distinct from the relational-only `IBrokeredMessageInbox.ReceiveViaInbox` wrap seam (#216).

### Changed

- **BREAKING:** `IBrokeredMessageOutbox` is split into `IBrokeredMessageOutbox` (retaining only the two `SendToOutbox` overloads, with the single-message overload remaining a default-interface-method) and a new relational-only `IPollableOutboxStore` carrying the polling-dispatch trio (`GetUnprocessedMessagesFromOutbox`, `GetUnprocessedBatch`, `UpdateProcessedDate`). `IPersistanceTransaction` now derives from `IAtomicWriteHandle`, and `IUnitOfWork` is documented as relational-only (ambient-transaction tier; the document tier never implements it). Breaking for code that implements the reliability port; ordinary adapter consumers are unaffected (#216).
- Secondary reliability facets (`IPollableOutboxStore`, `IInboxDeduplicator`) are no longer independently registered or resolved as DI services. Poll consumers obtain each secondary facet by casting the single resolved primary (`IBrokeredMessageOutbox` / `IBrokeredMessageInbox`) at the consumption site — the same pattern `OutboxProcessor` uses to obtain `IUnitOfWork`. A custom store must implement both facets on one concrete or the cast throws `InvalidCastException` at the poll site. Split-store is impossible by construction: there is exactly one resolved reliability-store instance per pair; no descriptor inspection, lifetime reconciliation, or fail-fast registration. `AddReliabilityPair` and `ReliabilityStoreLifetimeException` are deleted (#216).

## [0.13.2] - 2026-06-13

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
