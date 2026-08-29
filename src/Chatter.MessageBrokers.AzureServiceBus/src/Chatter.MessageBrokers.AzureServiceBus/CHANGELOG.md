# Changelog

All notable changes to this project will be documented in this file.

This project follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

### Changed

### Fixed

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
