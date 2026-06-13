---
name: rabbitmq-native-prop-propagation
description: Closed-by-construction native AMQP property propagation across RabbitMQ republish hops — ReceivedMessage carries a curated native-prop set, one shared BuildRepublishProperties builder all hops route through
metadata:
  type: project
---

Closes the PR #194 TTL-propagation root-cluster (reviewer thread r3407577438, fixed 2026-06-13). ROOT CAUSE: `RabbitMqReceiver.BufferDeliveryAsync` captured only Headers + delivery tag/epoch/MessageId and DISCARDED every other delivered native AMQP property; both republish sites rebuilt outbound `BasicProperties` from scratch (Persistent + MessageId + marshalled headers), so a classic-queue message published `WithTimeToLive` lost its native `Expiration` on the first nack-redelivery. The class was broader than TTL: ANY native property was dropped on republish.

**Fix shape (closed-by-construction):**
- `ReceivedMessage` (public, `src/.../Receiving/ReceivedMessage.cs`) carries a curated native-prop set as nullable members: `Expiration string?`, `Priority byte?`, `Timestamp AmqpTimestamp?`, `Type/AppId/ContentEncoding/ContentType/CorrelationId string?`. Ctor params are OPTIONAL/defaulted (only one `new ReceivedMessage(` call site exists — BufferDeliveryAsync — but defaulting keeps it future-proof and test-call-site-safe).
- `BufferDeliveryAsync` reads `delivery.BasicProperties` (an `IReadOnlyBasicProperties`) ONCE and captures each native prop via `Is*Present()` guards (`IsExpirationPresent()` etc.) — absent => null, NEVER a spurious default. `Priority` and `Timestamp` need `(byte?)null` / `(AmqpTimestamp?)null` casts on the false branch.
- ONE shared `BuildRepublishProperties(received, headerOverrides, bool preserveExpiration)` builds outbound props for BOTH republish hops. `RepublishThenAckAsync` gained a `preserveExpiration` param threaded from each caller.

**preserveExpiration wiring (overlord-resolved):** classic nack-republish (NackMessageAsync Classic branch) passes `preserveExpiration: true` (keep per-message TTL across redelivery). Deadletter (DeadletterMessageAsync) passes `false` — a DLQ is for inspection, a dead-lettered message must NOT auto-expire via the original TTL. ALL OTHER carried native props travel on BOTH hops.

**Both republish sites route through `RepublishThenAckAsync` → `BuildRepublishProperties`** (the only two republish/forward sites). The quorum native `BasicNackAsync(requeue)` path is untouched (broker owns its redelivery). The class is closed: no hop can drop a native prop because none rebuilds props independently.

**Expiration source-of-truth (no conflict):** initial SEND derives Expiration from `MessageContext.TimeToLive` in [[rabbitmq-header-marshaller]] `ToHeaderTable` (drops the TimeToLive key). REPUBLISH re-applies the CARRIED native Expiration, NOT re-derived from TimeToLive (which is absent from received.Headers since the marshaller dropped it at send). CorrelationId/ContentType: the native frame field is authoritative on republish (re-applied from carried native value); the marshaller's decoded header copy stays in the table — consistent with fresh publish.

**Test-double extensions:** `InMemoryRabbitMqConnectionSource.PushDeliveryAsync` gained optional native-prop params, set on the constructed inbound `BasicProperties` ONLY when supplied (so Is*Present() models a real broker — unsupplied => absent). `ReceiverHarness.PushAsync` forwards them. `RecordingChannel.PublishRecord` captures Priority/Timestamp/Type/AppId/ContentEncoding/CorrelationId (beyond existing Expiration) via the published props' Is*Present() guards.

**Integration TTL proof (race-free, key trick):** `RabbitMqNackRedeliveryTests.ClassicQueueRedeliveryPreservesNativeExpiration` observes the redelivered copy's native Expiration WITHOUT a competing BasicGet against the pump-consumed work queue — the receiver includes `ReceivedMessage` in the `MessageBrokerContext.Container`, so the handler reads `context.Container.TryGet<ReceivedMessage>()` off the SECOND (redelivered) invocation. `IMessageHandlerContext : IContainContext` exposes `Container`, so no cast needed.

RabbitMQ non-integration count: 156 per TFM (was 152; +4 broker-free native-prop tests in WhenSettlingMessage.cs). See [[rabbitmq-header-marshaller]], [[rabbitmq-receiver-core]], [[rabbitmq-routing-leak]].
