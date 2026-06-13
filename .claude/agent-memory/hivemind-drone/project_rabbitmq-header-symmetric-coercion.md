---
name: rabbitmq-header-symmetric-coercion
description: RabbitMqHeaderMarshaller per-descriptor symmetric encode/decode table — closed the ExpiryTimeUtc round-trip cast bug on PR #194
metadata:
  type: project
---

RabbitMqHeaderMarshaller's `_stringTypedHeaderKeys` HashSet was replaced by `IReadOnlyDictionary<string,HeaderCoercion>` keyed by core context key. `HeaderCoercion` = `{ Func<object,object> Encode; Func<object,object> Decode }` — symmetric per descriptor: Encode = CLR->wire-table-legal, Decode = wire->original CLR type.

**Why:** the header descriptors used to encode CLR->wire but decode wire->string only. `ExpiryTimeUtc` (a DateTime core key) encoded DateTime->ISO("O") outbound but on receive stayed byte[]/string, so core `OutboundBrokeredMessage.RefreshTimeToLive()`'s `(DateTime?)` cast threw `InvalidCastException` after a self-published round trip — same class as the original CorrelationId byte[] cast bug, for a non-string CLR type. Closes the marshaller recurrence (codex finding on PR #194); supersedes [[rabbitmq-header-marshaller]] and extends [[rabbitmq-translation-contract]] (ADR 0004).

**How to apply:**
- The 10 string-typed keys (Subject/GroupId/Via/RouteToSelfPath/ReplyToAddress/ReplyToGroupId/RoutingSlip/FailureDetails/FailureDescription/InfrastructureType) share one `StringTypedCoercion` (CoerceOutboundValue / DecodeStringTypedValue) — behaviour-preserving vs the old HashSet.
- ExpiryTimeUtc: Encode = DateTime->ISO("O") InvariantCulture; Decode = byte[]/string->DateTime.TryParse(Invariant, RoundtripKind); a malformed value returns null and `ToContext` DROPS the key (no throw, no bogus DateTime, core null-guard short-circuits).
- GOTCHA: `StringTypedCoercion` field MUST be declared BEFORE `_headerCoercions` — static field initializers run in textual order, so a later-declared field referenced by the dictionary initializer would be null.
- `ToContext` now drops any key whose Decode returns null. Safe for the 10 string keys because real broker header bags never carry null-valued entries (AMQP field table can't hold null; outbound null-drop pass removes them).
- `DecodeHeaderValue` (translator's dual-home CorrelationId header fallback) STAYS string-only — CorrelationId is native-frame-owned, not in the coercion table.
- ExpiryTimeUtc kept as a HEADER field (ADR-0004 OPTION a), not mapped to native Expiration: it's the absolute-expiry-instant concept, distinct from relative TTL; ASB surfaces ExpiresAtUtc->ExpiryTimeUtc inbound so parity matters.
- Shipped as RabbitMQ 0.1.2; 204->209 RabbitMQ unit tests (5 new ExpiryTimeUtc round-trip/symmetry/malformed-drop/RefreshTimeToLive tests in WhenTranslatingRoundTrip.cs).
