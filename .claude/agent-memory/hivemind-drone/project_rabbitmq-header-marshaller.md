---
name: rabbitmq-header-marshaller
description: The single type-aware AMQP header marshalling boundary (RabbitMqHeaderMarshaller) — outbound TimeSpan/ulong coercion + inbound byte[]->string decode policy
metadata:
  type: project
---

`RabbitMqHeaderMarshaller` (internal, `src/Chatter.MessageBrokers.RabbitMQ/src/.../RabbitMqHeaderMarshaller.cs`) is the SOLE boundary between core MessageContext CLR values and AMQP field-table wire types. Used at every site that crosses the wire: `RabbitMqSender.BuildProperties`, `RabbitMqReceiver.ReceiveMessageAsync` (inbound seed), and `RabbitMqReceiver.RepublishThenAckAsync` (nack/deadletter republish). Replaces the former raw `new Dictionary<>(context)` copy that leaked uncoerced values.

**Why (PR #194 2-member root-cluster, fixed 2026-06-13):** RabbitMQ.Client 7.2.1 field table can encode only string/bool/sbyte/int/long/decimal/byte[]/nested-IDictionary — NOT TimeSpan/DateTime/Guid/ulong/byte/uint/ushort. (P2) `WithTimeToLive` stamps `MessageContext.TimeToLive` as a TimeSpan -> threw at publish, native `BasicProperties.Expiration` never set. (P1) a real broker delivers string app headers as AMQP longstr (byte[]); core casts string-typed keys (e.g. CorrelationId via InboundBrokeredMessage ctor) straight to (string) -> InvalidCastException on self-published round-trip BEFORE the handler. Unit doubles missed P1 because they didn't model byte[] inbound.

**Outbound policy (ToHeaderTable):** TimeToLive(TimeSpan)->native `properties.Expiration` ms-string (floored, <=0 -> "0"), key DROPPED from table. ExpiryTimeUtc(DateTime)->ISO-8601 "O" string in table. ulong->long (or invariant string if >long.MaxValue); uint/ushort/byte->long; short->int; Guid->ToString(); null->key dropped; passthrough for already-legal types; any other CLR type -> Convert.ToString(InvariantCulture) catch-all (never throw, never silently drop).

**Inbound policy (ToContext):** KNOWN string-typed core keys decoded byte[]->UTF8 string. UNKNOWN keys preserved VERBATIM (do NOT force-decode byte[] -> avoids corrupting genuine binary headers). [overlord-resolved]. Numeric path untouched — `RabbitMqReceiver.ReadHeaderAsLong` still owns delivery-count tolerance.

**KNOWN-string-typed-key set** (the `_knownStringTypedKeys` HashSet — the core keys the receive path casts to (string)): CorrelationId, ContentType, Subject, GroupId, Via, RouteToSelfPath, ReplyToAddress, ReplyToGroupId, RoutingSlip, FailureDetails, FailureDescription, InfrastructureType. Adding a string-typed core key = single edit here.

**Marshaller signatures take `IEnumerable<KeyValuePair<string,object>>`** because `OutboundBrokeredMessage.MessageContext` is `IDictionary` (NOT IReadOnlyDictionary) while `ReceivedMessage.Headers` is IReadOnlyDictionary — the IEnumerable supertype accepts both without a wrapper.

**Test-double byte[] modeling:** `InMemoryRabbitMqConnectionSource.PushDeliveryAsync` now has `coerceStringHeadersToBytes` (default true) modeling broker longstr coercion STRING-ONLY (numeric/byte[] verbatim, so delivery-count/epoch tests unaffected). `ReceiverHarness.PushVerbatimAsync` pushes pre-typed (no coercion) for unknown-key verbatim-preservation tests. `RecordingChannel.PublishRecord` gained `Expiration` (from basicProperties.Expiration) so TimeToLive->Expiration is assertable. See [[rabbitmq-routing-leak]] (the inbound TargetExchange/RoutingKey strip still applies ON TOP of ToContext), [[rabbitmq-receiver-core]].
