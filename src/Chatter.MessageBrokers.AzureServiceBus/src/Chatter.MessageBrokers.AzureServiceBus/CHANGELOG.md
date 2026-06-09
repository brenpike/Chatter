# Changelog

All notable changes to this project will be documented in this file.

This project follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

### Changed

### Fixed

## [1.2.0] - 2026-06-09

### Changed

- The global `ServiceBusOptions.MaxConcurrentCalls` is now propagated to receivers (was previously a dead option — receivers always ran single-threaded).

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
