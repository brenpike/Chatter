# Changelog

All notable changes to this project will be documented in this file.

This project follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

### Changed

### Fixed

## [2.2.0] - 2026-09-03

### Added

- `RetryPolicy:NoRetry` — an explicit opt-in that replaces the previous all-zero-`RetryPolicy`-section-infers-disable-retry behaviour. Every numeric knob of the section is now nullable, so an OMITTED key and a STATED one are no longer the same thing: an omitted key falls back to the SDK's own retry default for that parameter, while a stated value is carried through to the SDK unchanged. A stated `MaximumRetryCount` of `0` is consequently bound faithfully and yields `MaxRetries = 0` — no client retry — instead of being silently replaced by that same SDK default. `NoRetry` remains the intention-revealing way to say the same thing, and issue #423 owns the question of whether a stated zero should keep binding this way (#296, #313).
- `IOptions<ServiceBusOptions>`, `IOptionsSnapshot<ServiceBusOptions>` and `IOptionsMonitor<ServiceBusOptions>` now resolve the instance `ServiceBusOptionsBuilder.Build()` finished. This is an ADDED capability, not a defect fix: this builder never registered a `Configure<ServiceBusOptions>`, so there was no second instance to remove — what the facets resolved instead was a framework-created, all-default `ServiceBusOptions` whose `ConnectionString` was `null`, reachable only by an application that resolved a facet itself. Registration runs last, after the connection-string guard and the fluent overrides, so no facet can observe a half-built instance. The monitor facet is for resolution only: the section is bound once at build time, so the options never reload, the change registration is inert, and — there being no named options in this package — every name, including none, resolves the same built instance (#296).

### Changed

- **The `RetryPolicy` configuration section now binds and takes effect.** `ServiceBusOptions` binds its section into the fluent-defaulted instance rather than replacing it, which is what it shares with how `Chatter.MessageBrokers` 0.19.0 now binds its own — so a populated `RetryPolicy` block, which previously did nothing, now configures the shared client's retry behaviour. **Upgrader risk:** a consumer who set `RetryPolicy` values expecting retry to already be disabled, relying on the old all-zero-section inference, still gets no retry — a stated `MaximumRetryCount` of `0` binds faithfully to `MaxRetries = 0` — but the section's other zero-valued keys now reach the SDK instead of being inferred away, so state `RetryPolicy:NoRetry` explicitly rather than relying on zeros. See the breaking-change note below (#296, #313).
- `DeltaBackoffInSeconds` is retained on `ServiceBusOptions` for configuration compatibility and is ignored — the `Azure.Messaging.ServiceBus` retry model has no equivalent knob (#313).
- **The `ServiceBusOptions` configuration bind surface is narrowed to the type's public properties.** The section was previously bound with non-public binding enabled, which also handed the binder the internal-set `RetryOptions` and `TokenCredential` properties; `RetryPolicy` — the one non-public configuration property that has to bind — is now bound explicitly from its own `RetryPolicy` subsection instead. Neither a stray `RetryOptions` key nor a nested `TokenCredential` object is reachable from configuration any more; both previously failed the host at startup. No documented key changes meaning (#296).
- **The retry options are now resolved AFTER the fluent override rather than during the bind.** `ServiceBusOptionsBuilder.Build()` binds the section, carries the configured `RetryPolicy` forward as data, and only at the end — once every fluent override is in hand — resolves the effective `ServiceBusRetryOptions` from the first source that stated any: the fluent call, then the bound section, then the SDK default. A configured section that a fluent `WithNoRetry()` or `WithExponentialDelay(...)` overrides is therefore never constructed at all, so none of its values is ever handed to the Azure SDK. The ordering is load-bearing: construct any earlier and a configured value the SDK rejects — say a `MaximumRetryCount` of `101` under an explicit `WithNoRetry()` — would reach the SDK before the fluent call could discard it, blocking the host from starting on a retry policy it was never going to use and contradicting this package's documented fluent-wins precedence (#296).
- **A consumer's own `services.Configure<ServiceBusOptions>(...)` is no longer consulted.** The options facets are bound directly to the built instance and never go through the container's options factory. No known consumer does this, but it is a public behaviour change: configure the options fluently or through the `Chatter:Infrastructure:AzureServiceBus` section instead (#296).
- Bundled dependency uplift to Chatter.MessageBrokers 0.19.0 (an in-repo `ProjectReference`, so the pack-time package dependency moves with it).

BREAKING — an invalid `RetryPolicy` section now stops the host from starting: **if your `Chatter:Infrastructure:AzureServiceBus:RetryPolicy` section states a value the Azure Service Bus SDK cannot run with — a typo'd negative, or a retry count outside the `0` through `100` the SDK accepts — the host will now refuse to start**. A stated value is carried to the Azure SDK, which may reject it and so prevent the host from starting; that failure comes from the SDK and does not name the configuration key you wrote. It previously started, silently, on SDK-default retry.

The remedy depends on what you meant:

- **You wanted retry off.** Zeros now mean off: a stated `MaximumRetryCount` of `0` binds faithfully to `MaxRetries = 0`. Prefer `RetryPolicy:NoRetry` set to `true`, which says so outright.
- **You wanted the SDK default.** Remove the key. An omitted key falls back to the SDK default for that parameter and is never handed to the SDK; only a stated one is.
- **You meant a real value.** Correct it. A section carrying several bad values may take more than one pass to clear.

A key that is not merely out of range but of the wrong TYPE — `MaximumRetryCount: "oops"`, a non-boolean `NoRetry` — fails earlier and differently: the configuration binder cannot convert it, so `Build()` throws an `InvalidOperationException` naming the full key path before the value reaches the SDK at all. That failure does name the key you wrote, so it is the easier of the two to act on.

Named build-time validation over these keys — one aggregated failure naming every offending value in the operator's own vocabulary — is deferred to issue #423.

Blast radius is bounded, and this is stated as a fact rather than as a reason to skim the above: the `RetryPolicy` section did not bind at all in any released version (see the binding fix in this same release), so no released consumer's retry behaviour changes underneath them. What changes is that a section which has been inert all along starts being read — and, if it is invalid, starts being refused.

## [2.1.1] - 2026-09-02

### Changed

- Bundled dependency uplift to Chatter.MessageBrokers 0.18.0 (an in-repo `ProjectReference`, so the pack-time package dependency moves with it).

## [2.1.0] - 2026-09-01

### Added

- Published packages now ship a symbol package (`.snupkg`), an embedded `README`, a project URL, and are built deterministically. Package builds are now reproducible CI builds with SourceLink-resolvable sources, so a debugger can step into this package's original source from a consuming application.

### Changed

- Bundled dependency uplift to Chatter.MessageBrokers 0.17.0 (an in-repo `ProjectReference`, so the pack-time package dependency moves with it).

## [2.0.1] - 2026-08-31

### Changed

- `Azure.Messaging.ServiceBus` uplifted `7.20.1` -> `7.20.2`.
- The `net10.0` target now binds the SDK's native `lib/net10.0` assembly. `7.20.1` shipped only `net8.0` and `netstandard2.0` assets, so the `net10.0` build previously resolved the `net8.0` one.
- The transitive `Azure.Core` floor rises `1.46.2` -> `1.60.0`. On the `net8.0` consumer graph this carries forward: `Microsoft.Identity.Client` (and `Microsoft.Identity.Client.Extensions.Msal`) to `4.84.2`, `System.ClientModel` to `1.14.0`, `System.Text.Json` to `10.0.9`, and the `Microsoft.Extensions.*` abstractions to `10.0.9`.
- The SDK's internal allocation reductions on the settlement logging path carry through. The SDK's own tracing remains off by default, and Chatter's emitted telemetry is unaffected by this uplift.

UPGRADER NOTE: a consumer that explicitly down-pins `System.Text.Json`, `Microsoft.Identity.Client`, or `Microsoft.Extensions.*` at `8.x` on `net8.0` will hit `NU1605` after upgrading and must raise those pins.

## [2.0.0] - 2026-08-30

### Changed

- **BREAKING, SUPERSEDES 1.4.3:** In PeekLock mode, a settlement (acknowledge, reject/abandon, or dead-letter) that cannot find the received message in the message broker context no longer throws `ServiceBusMessageSettlementException` — that exception type is REMOVED. It now reports a `Failed` settlement outcome (`SettlementResult.Failed`, part of the `Chatter.MessageBrokers` 0.16.0 seam), the same terminal, deterministic-fault shape the 1.4.3 release described as a thrown exception. A consumer that caught `ServiceBusMessageSettlementException` around a settlement call must instead observe the returned `SettlementResult`/receive failure — the throw is gone. Operators lose `error.type = ServiceBusMessageSettlementException` on the affected receive metric/span in favour of `error.type = "settlement_failed"` plus a reason string carried on the settlement result; a dashboard or alert keyed on the old `error.type` value must be updated to the new one (#283).
- Bundled dependency uplift to Chatter.MessageBrokers 0.16.0 (an in-repo `ProjectReference`, so the pack-time package dependency moves with it) — required for the `SettlementResult` seam above.

## [1.4.3] - 2026-08-30

### Fixed

- In PeekLock mode, the three settlement operations — acknowledge, reject/abandon, and dead-letter — now throw `ServiceBusMessageSettlementException` when the received message cannot be found in the message broker context, instead of logging a warning and silently returning `false`. Previously the delivery was left unsettled, the broker redelivered it after the lock expired, and the application processed the same message twice with no signal (#284). Upgraders should note this makes previously-invisible failures visible: a delivery that used to report a successful acknowledgement now surfaces as a failed receive carrying an `error.type`, so dashboards may show new failures that were always occurring.

## [1.4.2] - 2026-08-30

### Fixed

- Batch dispatch now enumerates the outbound message sequence exactly once. Previously, a capacity hint enumerated the sequence before the real send pass, so message bodies were serialized twice per message with the first pass's results discarded, and a caller-supplied one-shot lazy sequence failed outright because nothing remained for the send pass to yield. Single-message sends were unaffected. Batch telemetry counts (introduced elsewhere in this release) were also inflated by the double enumeration; that is corrected as a side effect of this fix (#274).

## [1.4.1] - 2026-08-21

### Fixed

- `AsAzureServiceBusMessage` now assigns `ServiceBusMessage.PartitionKey` only when a partition key was explicitly supplied, instead of unconditionally. Previously, sending a message that carried only a Group Id threw `ArgumentOutOfRangeException` from the Azure SDK's `set_PartitionKey`, because a null partition key differs from an already-set `SessionId`. This reached consumers who never asked for sessions or partitioning at all: the receive path promotes an inbound `SessionId` onto `MessageContext.GroupId`, and republishing via the inbound `IMessageHandlerContext` inherits it. Sending with a Group Id alongside a genuinely different, non-empty partition key still throws, as intended (#262). Upgraders should note this makes the by-design inheritance observable for the first time: a handler that republishes via the inbound `IMessageHandlerContext` now emits a message carrying the inbound Group Id instead of throwing.

## [1.4.0] - 2026-06-14

### Added

- Single-session-at-a-time receiver support via `AddSessionQueueReceiver` and `AddSessionTopicSubscription` — dispatches one Azure Service Bus session receiver per accepted session, guaranteeing FIFO ordering within a `SessionId`.
- Inbound `SessionId` is surfaced on `MessageContext.GroupId` so handlers can read the session affinity without touching SDK types.
- Durable per-session state via `GetSessionStateAsync`, `SetSessionStateAsync`, and `ClearSessionStateAsync` on the session context — backed by the Azure Service Bus session-state store.
- `SessionIdleTimeout` and `MaxSessionLockRenewalDuration` knobs on the session receiver options to control how long an idle session is held open and how aggressively the session lock is renewed.

### Fixed

- (F6) A parked Azure Service Bus receive is now unblocked on teardown — the receive-loop `CancellationToken` is threaded through the internal receive port into the SDK `ReceiveMessageAsync(maxWaitTime, cancellationToken)` overload, and a shutdown-cancelled receive is swallowed quietly (returns `null`, no error log, no settle). Prevents teardown hangs when the broker or network is stalled.
- (F7) Session-enabled topic subscriptions now accept sessions via the correct `AcceptNextSessionAsync(topicName, subscriptionName)` overload. The session entity is carried as a structured `(topic, subscription)` identity through the session path so a topic-subscription receiver is never mis-addressed as a flat queue name.

## [1.3.0] - 2026-06-09

### Changed

- Real concurrent message processing bounded by `MaxConcurrentCalls` is now delivered end-to-end: the `MaxConcurrentCalls` value propagated from `ServiceBusOptions` in 1.2.0 now drives actual parallel processing workers in `BrokeredMessageReceiver` (default `MaxConcurrentCalls = 1` preserves sequential behavior). Satisfies the "not yet delivered (#147)" caveat carried in the 1.2.0 release notes (#147).

## [1.2.0] - 2026-06-09

### Changed

- The global `ServiceBusOptions.MaxConcurrentCalls` is now propagated to receivers as a source-of-truth value (previously a dead option). NOTE: this does not yet enable parallel message processing — that is tracked as a follow-up (#147).

### Fixed

- (F3) Attribute-registered (`[BrokeredMessage]`) receivers are now included in the cross-entity-transactions single-top-level-entity startup guard via the new discovered-receiver seam — previously they bypassed the registry and were unprotected. The fold-in now respects the core's default-infrastructure resolution: a blank-`InfrastructureType` receiver is attributed to Azure Service Bus only when Azure Service Bus is the resolved default (no other broker registered its infrastructure first), so in a multi-broker host a non-ASB blank-typed receiver is no longer mis-claimed by the ASB guard or stamped with ASB's `MaxConcurrentCalls`.
- (F4) A cross-entity startup guard violation now fails host startup with a plain `InvalidOperationException` instead of being silently swallowed by the core receiver.
- (F5) Explicit `WithMaxConcurrentCalls(...)` / `WithPrefetchCount(...)` calls set to the default value are no longer silently dropped in favor of configuration (nullable builder backing fields distinguish "unset" from "set-to-default").

## [1.1.0] - 2026-06-09

### Added

- `ServiceBusOptions.EnableCrossEntityTransactions` (default `false`) and `ServiceBusOptionsBuilder.WithCrossEntityTransactions()` opt-in for explicitly enabling cross-entity transaction support.
- Startup guard that fails fast with a clear error when cross-entity transactions are enabled alongside more than one distinct top-level receiver entity.

### Changed

- `EnableCrossEntityTransactions` now defaults to `false` and is auto-enabled only when a `FullAtomicityViaInfrastructure` receiver is registered (previously always on).

### Fixed

- Cross-entity transactions are no longer forced on, so a host can run multiple queue receivers on distinct entities again (regression introduced in 1.0.0 by the Azure.Messaging.ServiceBus migration).
- The fatal cross-entity rejection now surfaces as a `CriticalReceiverException` instead of hanging silently.
- A global `WithTransactionMode(FullAtomicityViaInfrastructure)` now correctly auto-enables cross-entity transactions on the shared client; previously only per-receiver transaction modes were honored, so receivers inheriting the global atomicity mode silently lost the cross-entity guarantee.
- `ServiceBusOptions.EnableCrossEntityTransactions` now has a public setter so the cross-entity opt-in binds from configuration (`Chatter:Infrastructure:AzureServiceBus:EnableCrossEntityTransactions`); previously the `internal` setter meant `ConfigurationBinder` silently skipped it and config-only opt-in was ignored.
- An explicit `WithCrossEntityTransactions(false)` now overrides a config-bound `EnableCrossEntityTransactions = true`; previously the fluent value was applied only when it differed from the default, so an explicit disable was silently ignored when configuration enabled it.

## [1.0.0] - 2026-06-08

### Changed

- Migrated from `Microsoft.Azure.ServiceBus` to `Azure.Messaging.ServiceBus` 7.20.1. Receive and send paths now operate on `ServiceBusReceivedMessage` / `ServiceBusMessage` objects.
- `FullAtomicityViaInfrastructure` is now backed by a shared `ServiceBusClient` created with `EnableCrossEntityTransactions = true`.
- `ServiceBusOptions` exposes a `TokenCredential` property (for AAD auth via the Auth package) and a `ServiceBusRetryOptions` property (replaces legacy retry-policy surface).
- Entity-path formatting and exception predicates ported to the new SDK types.

### Removed

- `BrokeredMessageSenderPool` removed; sender lifecycle is now managed internally by the `Azure.Messaging.ServiceBus` SDK.
- `NullTokenProvider` removed; no replacement required — omit `TokenCredential` to use connection-string auth.
- Legacy `ITokenProvider` and `RetryPolicy` option surface removed (was `Microsoft.Azure.ServiceBus` types).
- Send-via connection routing removed; the new SDK does not expose an equivalent send-via API.

**Migration path:** Connection-string authentication is unchanged. AAD consumers should supply a `TokenCredential` via `ServiceBusOptions.TokenCredential`; use `Chatter.MessageBrokers.AzureServiceBus.Auth` to obtain a credential from client-secret, client-certificate, interactive-browser, or `DefaultAzureCredential`.

## [0.10.3] - 2026-06-07

### Changed

- Swapped ServiceBusOptions [JsonIgnore] attributes from Newtonsoft.Json to System.Text.Json. Dropped the transitive Newtonsoft.Json dependency.

### Fixed

- Cap the Azure Service Bus deadletter error description to the SDK's 4096-character limit, preventing `System.ArgumentOutOfRangeException` on `OnDeadLetterAsync` (#92).

## [0.10.1] - 2026-06-07

### Changed

- Rerouted DI registration to the shared core `MessagingInfrastructureFactory`; removed the internal `ServiceBusInfrastructureFactory` (behavior-preserving — identical scope-open/resolve/dispose semantics). Now depends on `Chatter.MessageBrokers` >= 0.10.0.

## [0.10.0] - 2026-06-07

### Changed

- `IMessageHandlerContext.AzureServiceBus()` now returns the core `IMessageBrokerContext` (was `IAzureServiceBusContextDispatcher`). The same `Send`/`Publish`/`Forward` members remain, so callers using those recompile unchanged.

### Removed

- `AzureServiceBusContextDispatcher` and `IAzureServiceBusContextDispatcher` — pass-through wrappers collapsed into `IMessageBrokerContext`. Code referencing those types directly is broken.

## [0.9.0] - 2026-06-07

### Changed

- Deepened the Azure Service Bus adapters (#122): introduced an internal `IServiceBusMessageReceiver` port with production and in-memory adapters, extracted a pure `InboundBrokeredMessageFactory`, made `BrokeredMessageSenderPool` client construction injectable, and folded the receiver/sender infrastructure factories into a single internal `Func<>`-backed factory. Behavior-preserving for consumers; internals are now unit-testable without a live namespace.

### Removed

- `ServiceBusReceiver` is now `internal` (was `public`). It was an infrastructure adapter not intended for direct consumption; resolve messaging infrastructure through the public `AddAzureServiceBus(...)` registration instead. (Breaking for any code referencing the type directly.)

## [0.8.0] - 2026-05-30

### Changed

- Target frameworks migrated from `netstandard2.1;net5.0;net6.0` to `net8.0;net10.0`.

### Removed

- Dropped the `net5.0`, `net6.0`, and `netstandard2.1` target-framework monikers. This is a breaking change for consumers pinned to those in-box assets. Consumers on modern runtimes resolve the `net8.0` or `net10.0` asset.
