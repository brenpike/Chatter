# Changelog

All notable changes to this project will be documented in this file.

This project follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

### Changed

### Fixed

## [0.1.0] - 2026-06-12

### Added

- Initial `Chatter.MessageBrokers.RabbitMQ` adapter module targeting `net8.0` and `net10.0`: independently-versioned NuGet package wired into `Chatter.sln` with its own test project.
- `RabbitMqReceiver` — `IMessagingInfrastructureReceiver` implementation; bridges RabbitMQ's push-consumer model to the core's blocking-pull loop via a bounded `Channel<T>` buffer (no busy-polling). Serializes ack/nack/deadletter on a single gated receive channel; guards acknowledgements with a channel-epoch so reconnect never produces a false-ack.
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

### Fixed

- Inbound delivery exchange and routing-key no longer leak into outbound routing overrides — the receiver no longer stamps the broker-supplied inbound address into the `.WithRabbitMqRouting` command keys, preventing a handler that receives-then-sends from being silently re-routed back to the originating exchange.
- Receive-channel epoch now advances on RabbitMQ automatic recovery — closes the post-recovery false-ack window where an in-flight pre-recovery delivery tag could be acknowledged on the recovered session.
