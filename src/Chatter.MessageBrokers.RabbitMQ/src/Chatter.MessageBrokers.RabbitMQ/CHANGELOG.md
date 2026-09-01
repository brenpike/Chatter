# Changelog

All notable changes to this project will be documented in this file.

This project follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.4.0] - 2026-09-01

### Added

- Published packages now ship a symbol package (`.snupkg`), an embedded `README`, a project URL, and are built deterministically. Package builds are now reproducible CI builds with SourceLink-resolvable sources, so a debugger can step into this package's original source from a consuming application.

## [0.3.0] - 2026-08-30

### Changed

- **BREAKING:** `RabbitMqReceiver`'s settlement members (`AckMessageAsync`, `NackMessageAsync`, `DeadletterMessageAsync`) now return `Task<SettlementResult>` instead of `Task<bool>`, in step with the `Chatter.MessageBrokers` 0.16.0 seam. A custom caller depending on the prior `bool` return must migrate to read `SettlementResult.IsSettled`/`.Outcome`. Both deadletter branches (dead-letter-queue-configured and error-only) are otherwise unchanged (#283).
- A settlement whose delivery arrived on a receive channel that has since been recycled (AMQP connection auto-recovery with the delivery in flight) is now reported as a FAILED settlement instead of being indistinguishable from "nothing to settle". Previously the epoch-mismatch skip returned the same `false` a genuine no-op settlement returned; it now returns `SettlementResult.Failed`, so error-rate metrics and receive spans surface these recovery events, which were previously invisible (#283).

## [0.2.0] - 2026-06-13

### Added

- `SendOptions.WithRabbitMqRouting(exchange, routingKey)` / `PublishOptions.WithRabbitMqRouting(exchange, routingKey)` — expose the non-default exchange / routing-key override on the documented `context.RabbitMq().Send/Publish(...)` handler path (previously reachable only via manual header stamping or the explicit-outbound `OutboundBrokeredMessage` overload). (#200)

## [0.1.2] - 2026-06-13

### Fixed

- `MessageContext.ExpiryTimeUtc` (a `DateTime` core key) was encoded to an ISO-8601 string outbound but never rehydrated on receive — it stayed a `byte[]`/`string` from the wire — so after a self-published round trip `OutboundBrokeredMessage.RefreshTimeToLive()`'s `(DateTime?)` cast on it threw `InvalidCastException` (the same class as the prior `CorrelationId` `byte[]` cast bug, here for a non-string CLR type). Fixed by replacing the marshaller's one-directional string-typed-key set with a per-descriptor **symmetric** encode/decode coercion table keyed by core context key: each known header key now declares both its CLR→wire encode and its wire→original-CLR decode, so any non-string core key routed through a header rehydrates to its original CLR type by construction (ExpiryTimeUtc decodes `byte[]`/`string`→`DateTime`; a malformed value drops the key so the core's null-guard short-circuits). The ten existing string-typed routing/failure keys are unchanged. A non-string header key can no longer be added with an encode but no matching decode, closing the asymmetry class on ADR 0004's translation contract.

## [0.1.1] - 2026-06-13

### Changed

- Type-aware AMQP header marshalling for the RabbitMQ adapter via a single `RabbitMqHeaderMarshaller` boundary used by publish, receive, and the nack/deadletter republish, replacing the raw context↔header-table copy at every site so an uncoerced value can no longer cross the wire boundary in either direction. Two concrete defects this closes: (1) `OutboundBrokeredMessage.WithTimeToLive` stamps `MessageContext.TimeToLive` as a `TimeSpan`, which the RabbitMQ.Client 7.2.1 field table cannot encode — it threw at publish and the native `BasicProperties.Expiration` was never set; the marshaller now lifts the TimeToLive onto the native `Expiration` (milliseconds) and drops the key from the table. (2) A real broker delivers a string application header as an AMQP longstr (`byte[]`); the core casts a fixed set of string-typed context keys (e.g. `MessageContext.CorrelationId`) straight to `string`, so a self-published round-trip left `CorrelationId` as a `byte[]` and threw `InvalidCastException` before the handler ran — the marshaller now decodes the documented known-string-typed core keys back to `string` on receive while preserving every unknown key verbatim, so genuine binary headers are never corrupted and the numeric delivery-count path is untouched.

### Fixed

- Lifecycle-authority collapse for `RabbitMqConnectionSource` (ADR 0003): the source's liveness is now a single monotonic authority (`Live` → `Disposing` → `Disposed`) advanced only by an atomic compare-and-swap, replacing the former standalone `_disposed` flag that each mutual-exclusion domain read independently. The `IConnection` is now created and disposed under the SAME gate, so connection creation and disposal are mutually exclusive by construction (the dedicated connection-init gate is removed). A publish-or-surrender handoff makes an operation suspended mid-connection/channel-creation across a completing `DisposeAsync` surrender the just-created resource (dispose it and throw `ObjectDisposedException`) instead of resurrecting it onto a torn-down source — closing the connection-lifecycle-disposal race as a class rather than per site. The ADR 0002 receive-channel epoch lifecycle, publisher-confirm settlement, multi-receiver guard, and publish-permit conservation are preserved unchanged.
- `DeadletterMessageAsync` now fails fast when a receiver is registered with neither a dead-letter (`ReceiverOptions.DeadLetterQueuePath`) nor an error (`ReceiverOptions.ErrorQueuePath`) path. Previously the resolved `destination` was null/blank, and the deadletter republish used it as the default-exchange routing key with `mandatory: true`, so the publish was unroutable and only faulted after the message exhausted its retry budget — leaving the original delivery un-acked and redelivered indefinitely (a poison-message hot loop). The receiver now throws an actionable `InvalidOperationException` naming the receiver queue instead of attempting the unroutable publish. Cites reviewer thread r3407616994.
- Outbound TTL and the advertised content type are now sourced from the core's authoritative `OutboundBrokeredMessage` accessors rather than re-interpreted from the raw `MessageContext` in the marshaller / sender, closing a class of representation-drift defects. (1) The marshaller's TimeToLive lift matched only `MessageContext.TimeToLive is TimeSpan`, so an outbox-replayed context — which materializes TimeToLive as a string — was missed and the native `Expiration` was never set; the sender now lifts `Expiration` from `OutboundBrokeredMessage.GetTimeToLive()` (tolerant of a live `TimeSpan` AND the string form), and the marshaller unconditionally drops the un-encodable `TimeToLive` key in any form. (2) `BuildProperties` advertised the sender's resolved `_bodyConverter.ContentType` instead of the content type the core actually stamped on the context (`MessageContext.ContentType`) when it serialized the body, so a replayed message could be mislabelled; the sender now advertises the stamped content type, falling back to the resolved converter only when the stamp is absent. Cites reviewer threads r3407587282 and r3407587284.
- A redelivered classic-queue message lost its per-message TTL because the nack-redelivery republish rebuilt `BasicProperties` from scratch (Persistent + MessageId + marshalled headers) and dropped every delivered native AMQP property, including the native `Expiration`. The class was broader than TTL: any delivered native property was lost on republish. Fixed by capturing the curated native AMQP property set (`Expiration`, `Priority`, `Timestamp`, `Type`, `AppId`, `ContentEncoding`, `ContentType`, `CorrelationId`) onto `ReceivedMessage` at delivery time (via the inbound `IReadOnlyBasicProperties` `Is*Present()` guards, so an absent property stays null rather than a spurious default) and re-applying them on every republish hop through ONE shared `BuildRepublishProperties` builder that all republish sites route through — so no hop can drop a native property by rebuilding from scratch. The deadletter hop intentionally drops `Expiration` (a dead-letter queue is for inspection, so a dead-lettered message must not auto-expire via the original TTL) while carrying every other native property; the nack-redelivery hop preserves `Expiration`. Cites reviewer thread r3407577438.
- Dispose-coordination hardening for `RabbitMqConnectionSource`: every receive-gated and publish-permit entrypoint now observes `_disposed` on BOTH sides of gate/permit acquisition via a single coordination primitive. A gated receive operation queued behind `DisposeAsync` no longer resurrects a connection/channel or overwrites the stored consumer registration past teardown — it throws `ObjectDisposedException`. The publish permit is now ALWAYS released on return, so a publish acquire stranded behind a saturated pool at disposal is woken and throws rather than hanging forever.

## [0.1.0] - 2026-06-12

### Added

- Initial `Chatter.MessageBrokers.RabbitMQ` adapter module targeting `net8.0` and `net10.0`: independently-versioned NuGet package wired into `Chatter.sln` with its own test project.
- `RabbitMqReceiver` — `IMessagingInfrastructureReceiver` implementation; bridges RabbitMQ's push-consumer model to the core's blocking-pull loop via a bounded `Channel<T>` buffer (no busy-polling). Serializes ack/nack/deadletter on a single gated receive channel; guards every acknowledgement with a channel-epoch stamped onto each delivery. The receive channel runs with connection auto-recovery enabled but topology recovery disabled: on every automatic recovery the connection source recreates the receive channel, bumps the epoch, and re-registers the consumer under the gate as one atomic event, so a delivery's stamped epoch always equals the session that delivered it — a pre-recovery in-flight delivery's stale-epoch settle is a no-op and the broker redelivers it (no false-ack), while a post-recovery delivery carries the current epoch and settles normally (no duplicate-redelivery loop). The receiver also does not stamp the broker-supplied inbound delivery exchange / routing key onto the message context, so a handler that receives-then-sends is never silently re-routed back to the originating exchange.
- **Quorum delivery-count strategy** (default): reads the native `x-delivery-count` header RabbitMQ increments per redelivery; stamps `MessageContext.ReceiveAttempts` for the core's Max Receives Exceeded check.
- **Classic delivery-count strategy**: header-stamped republish counter (`x-chatter-delivery-count`) for queues where quorum is not available; publisher-confirmed before the original is acknowledged. Trade-offs documented in ADR 0001 (rare duplicate on crash-between-republish-and-ack; loss of head-of-queue ordering).
- `RabbitMqSender` — `IMessagingInfrastructureDispatcher` implementation; publishes via the **default-exchange convention** (routing key = destination queue name) or an explicit exchange/routing-key override carried in message context. Publisher confirms enabled: a publish is only treated as sent once the broker confirms it.
- `.WithRabbitMqRouting(exchange, routingKey)` extension on `OutboundBrokeredMessage` — stamps an explicit exchange and routing key into the message context, overriding the default-exchange convention at dispatch time.
- Dead-letter / error routing: adapter-owned republish to the attribute-declared `DeadletterQueueName` / `ErrorQueueName` path once Max Receives Exceeded trips, then acknowledges the original (authoritative over any broker-side DLX configuration).
- `AddRabbitMq(this IChatterBuilder, Action<RabbitMqOptionsBuilder>)` DI extension — single registration entry point; wires receiver, sender, path builder, body converter, retry/circuit-breaker predicate providers, and `IMessagingInfrastructure` under the RabbitMQ type discriminator.
- `RabbitMqOptionsBuilder` — fluent builder for connection (`WithUri`, `WithHostName`, `WithCredentials`), prefetch (`WithPrefetch`), queue type (`WithQueueType`), message body type (`WithMessageBodyType` / `WithJsonBodyType`), and per-receiver registration (`AddQueueReceiver<TMessage>`).
- `TransactionMode.None` and `TransactionMode.ReceiveOnly` supported; `TransactionMode.FullAtomicityViaInfrastructure` rejected at startup with a message directing users to the Outbox.
- `context.RabbitMq()` context selector extension — stamps outbound messages for RabbitMQ infrastructure dispatch.
- `InfrastructureTypes.RabbitMq()` extension — returns the RabbitMQ infrastructure type discriminator string.
- `RabbitMqRetryExceptionPredicatesProvider` and `RabbitMqCircuitBreakerExceptionPredicatesProvider` — classify transient RabbitMQ faults through the shared `RetryWithCircuitBreakerStrategy` seam.
- `IRabbitMqConnectionSource` seam — sole originator of the singleton `IConnection`, serialized receive channel (with epoch), and pooled publish channels; substituted by an in-memory double in unit tests.
- `RabbitMqBodyConverter` — UTF-8 JSON body encoding/decoding through the shared `ChatterJson.Options`.
- `RabbitMqPathBuilder` — resolves `SendingPath`, `ReceiverName`, `ErrorQueueName`, and `DeadletterQueueName` from `BrokeredMessageAttribute` fields.

### Known Limitations

- **Single RabbitMQ queue receiver per process.** 0.1.0 supports exactly one RabbitMQ queue receiver per process. Registering more than one fails fast at startup with `NotSupportedException` — the connection source owns one receive channel and one consumer registration, so a second receiver would clobber the first and recovery would re-register only the last. Full multi-receiver support is tracked in [#195](https://github.com/brenpike/Chatter/issues/195).
