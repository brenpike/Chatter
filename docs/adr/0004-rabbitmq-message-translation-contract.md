---
status: accepted
date: 2026-06-13
---

# RabbitMQ single bidirectional core<->AMQP message-translation contract

The RabbitMQ adapter must map Chatter's core `MessageContext` values onto the AMQP wire
representation (native `BasicProperties` frame fields plus the application-header field table) on
**send**, map them back on **receive**, and rebuild them on **republish** (nack-redelivery and
deadletter). Each of those three boundaries had grown its own hand-rolled mapping, and every
review pass surfaced one more *semantic field translated inconsistently across boundaries* — a
field lifted onto a native frame field outbound but read from a header inbound, a content-type
written on send but ignored on receive, a TTL encoded one way and never reconstituted. This ADR
records the structural fix: a **single bidirectional translation contract** through which all
three boundaries route. `ToRepublishAmqp` does not walk the field-map table: it re-applies each
carried native by name instead (issue #465). The header arm's outbound encode and its inbound
decode are declared in separate places, with nothing pairing them (issue #465).

## Considered Options

- **(i) Per-boundary hand-rolled mapping (REJECTED — the recurring-asymmetry generator).** Send,
  receive, and republish each kept their own copy of the core<->AMQP field handling. Because the
  three copies were partial and independent, a field could be lifted onto a native frame field on
  send but read from a header on receive, or stamped on republish but dropped on receive. Each
  fixed asymmetry was one byte/field different from the next: the same shape recurred — *a
  semantic field translated inconsistently across boundaries*. Adding the next missing field to
  one of the three copies is complete-the-known-set whack-a-mole, not a class fix.
- **(ii) Marshaller-only partial step (REJECTED — headers/send-direction only).** Centralizing
  only the *header* coercion (the `RabbitMqHeaderMarshaller`) closed the field-table-legality
  class for header-home fields, but it governed only the header arm and primarily the outbound
  direction. The native-frame fields (MessageId, ContentType, CorrelationId, Expiration) and the
  inbound/republish directions stayed hand-rolled per boundary, so the cross-boundary asymmetry
  class remained open.
- **(iii) ONE declarative field-map table, both directions, all three boundaries (ACCEPTED).** A
  single `RabbitMqMessageTranslator` owns one declarative field-map of native-home fields that
  carry a core concept; every boundary (`ToAmqp` send, `ToCore` receive, `ToRepublishAmqp`
  republish) walks that one table. Header-home fields are *every remaining context entry*, routed
  through the retained marshaller (now the header arm of this contract). A new field is one
  descriptor and is necessarily symmetric across all three boundaries by construction. This aligns
  with the Azure Service Bus adapter's sibling prior-art `AsAzureServiceBusMessage()` single
  message-projection surface.

## Decision

Adopt option (iii): `RabbitMqMessageTranslator` is the single bidirectional translation contract.

**As built:** `ToAmqp` and `ToCore` walk the table; `ToRepublishAmqp` does not (issue #465).

### The field-map table

The table holds the fields that have a **native AMQP frame home AND a core concept**. Each
descriptor declares the field's AMQP home and its core binding. `MessageId`, `ContentType` and
`CorrelationId` are the three descriptors; `TimeToLive` / `Expiration` is **not** a descriptor and
is handled by a dedicated arm at each boundary:

| Field | AMQP home | Core OUT (send source) | Core IN key (receive sink) | Notes |
| --- | --- | --- | --- | --- |
| MessageId | native `BasicProperties.MessageId` | `OutboundBrokeredMessage.MessageId` | none (carried on the `MessageBrokerContext` itself) | string-shaped on the wire |
| ContentType | native `BasicProperties.ContentType` | actual-serialization stamp (`MessageContext.ContentType`), sender's resolved-converter fallback | `MessageContext.ContentType` | GAP B: receive surfaces the delivered content-type so the receiver picks the inbound body converter from it |
| CorrelationId | native `BasicProperties.CorrelationId` **+ header copy** | `OutboundBrokeredMessage.CorrelationId` | `MessageContext.CorrelationId` | DECISION-D dual-home; inbound the native frame wins, else the decoded header copy (byte[]->string) |
| TimeToLive / Expiration | native `BasicProperties.Expiration` (ms string) | `OutboundBrokeredMessage.GetTimeToLive()` (TimeSpan) | `MessageContext.TimeToLive` | GAP A: dedicated arm — TimeSpan<->ms-string; the un-encodable `TimeToLive` header key is dropped; reconstituted to a TimeSpan inbound |

Header-home fields are **not** enumerated as native descriptors: they are every remaining context
entry, coerced table-legal outbound and rehydrated inbound through `RabbitMqHeaderMarshaller`.
There is no per-key allowlist gate in the translator — the marshaller's single per-key
**`HeaderDisposition` map** (`_dispositions`, keyed by core context key) is the sole declaration of
how a header named after a core key is projected into the core context (GAP F:
table/disposition-driven, not a per-key branch), and the translator's descriptors own the
native-home keys. That map is **inbound only**: the outbound encode is not keyed by header key at
all — it is `CoerceOutboundValue`'s per-CLR-type arms, plus two per-key outbound arms: the
unconditional `TimeToLive` drop and `EncodeExpiryTimeUtc`.

**SPECIFIED BUT NOT IMPLEMENTED — issue #465.** This ADR specified the header coercion as
**bidirectional-symmetric per descriptor**: one descriptor per header key carrying an `Encode`
(core CLR -> field-table-legal wire form) **and** a `Decode` (received wire value -> the SAME
original CLR type), declared once and paired. No such paired descriptor was built.
What shipped declares the two directions in **separate** places keyed differently — inbound by core
key in `_dispositions`, outbound by CLR type in `CoerceOutboundValue` — so nothing pairs them. The
ten string-typed routing/failure keys encode string-identity outbound and inbound decode to a
`string` or drop the key — the decode is type-total: any non-null wire value becomes a `string`.
**ExpiryTimeUtc** — a non-string (`DateTime`) core key with a header home — is handled at both
ends, but by two independent declarations rather than one:

| Header key | Outbound encode | Inbound decode | Notes |
| --- | --- | --- | --- |
| ExpiryTimeUtc | `DateTime` -> ISO-8601 (`"O"`, invariant) string | wire `byte[]`/`string` -> `DateTime` (`RoundtripKind`); a malformed/unparseable value **drops the key** | OPTION (a): kept as a header field with symmetric coercion — it is the absolute-expiry-instant core concept, distinct from the relative TTL on `BasicProperties.Expiration`; the inbound decode restores the `DateTime` so `OutboundBrokeredMessage.RefreshTimeToLive`'s `(DateTime?)` cast holds after a round trip |

This was intended to **extend the closed class** the contract dissolves: the original class was "a
semantic field translated inconsistently across boundaries (native-vs-header home mismatch)"; the
symmetric header coercion was to additionally close **"a header-home core key whose inbound decode
does not restore its original CLR type"**. ExpiryTimeUtc was the recurrence of that class for a
non-string CLR type — it was encoded `DateTime` -> ISO string outbound but stayed
`byte[]`/`string` on receive, so
`RefreshTimeToLive`'s `(DateTime?)` cast threw `InvalidCastException` (the same class as the prior
CorrelationId `byte[]` cast bug, for a non-string CLR type). Only the **inbound** half of that
closure shipped: `_dispositions` is asserted complete over the core key registry at type init, so a
new core key cannot ship without an explicit inbound disposition. The pairing is what is missing:
the two directions are declared in separate places and nothing ties them together (issue #465).

OPTION (a) — **keep ExpiryTimeUtc as a header field** (rather than dropping it or mapping it onto a
native frame field) — was chosen because ExpiryTimeUtc is the absolute-expiry-**instant** concept,
which is distinct from the relative TTL the contract already lifts onto `BasicProperties.Expiration`
(GAP A); it is not redundant with that native Expiration. Dropping it would re-open a cross-boundary
asymmetry: the sibling Azure Service Bus adapter surfaces the broker's expiry instant into the core
`ExpiryTimeUtc` key inbound, so ExpiryTimeUtc is a real inbound core key in the suite, and parity
keeps the RabbitMQ adapter from silently losing it on a round trip.

### The flow

- **`ToAmqp` (send).** Build `BasicProperties`, marshal the full context into the header table,
  set ContentType from the actual-serialization stamp (fallback to the resolved converter), walk
  the table's native-home descriptors setting each frame field from its core accessor (the
  dual-home CorrelationId also writes its header copy), lift TimeToLive onto the native Expiration
  (dropping the `TimeToLive` key), hardcode `Persistent = true`.
- **`ToCore` (receive).** Decode the delivered header table through the marshaller, walk the
  table's native-home descriptors writing each to its bound core key when one exists (surfacing
  the delivered ContentType — GAP B — and the dual-home CorrelationId with header fallback),
  reconstitute the native Expiration into `MessageContext.TimeToLive` (GAP A). The C-family
  natives stay only on the captured facts (DECISION-B), never in the core context.
- **`ToRepublishAmqp` (republish).** Rebuild the outbound AMQP representation from the captured
  `NativeFacts` + carried headers. The merged header bag goes through the same marshaller call the
  send path uses, but each carried native (including the C-family) is re-applied **by name** rather
  than through the field-map table (issue #465). `Persistent = true` hardcoded; Expiration
  preserved only on the nack-redelivery hop, dropped on the deadletter hop.

### Locked decisions

- **DECISION-B — C-family carry-only.** `ContentEncoding`, `Type`, `AppId`, `Priority`, and
  `Timestamp` have a native frame home but **no Chatter domain concept consumes them**. They are
  captured inbound onto the `NativeFacts` carrier and re-applied on republish for fidelity to
  external consumers, but are **never** surfaced into the core context and **never** sourced from a
  core accessor on send. Rationale: surfacing them would invent core keys nothing reads;
  carry-only preserves wire fidelity without polluting the core contract.
- **DECISION-D — CorrelationId dual-home.** The CorrelationId is written to **both** the native
  frame field and a header copy on send, for wire compatibility with consumers that read either.
  Inbound the native frame is authoritative; when absent (delivered only as a header) the decoded
  header copy (byte[]->string) is the source, so the core's type-tested `string` read at the
  `InboundBrokeredMessage` ctor sees the real value either way — an undecoded `byte[]` reads as
  `null` there, silently losing the correlation id rather than faulting.
- **DECISION-E — persistence hardcoded.** `Persistent = true` is hardcoded on send and republish so
  a message survives a broker restart on a durable queue; the delivered delivery-mode is never
  carried.

The `RabbitMqHeaderMarshaller` is **retained** as the header-coercion helper — it is no longer a
standalone boundary but the header arm of this single contract.

## Closed-by-Construction Acceptance Test

> What class of future finding does this make impossible, and why?

**Verdict: FAILED for the native-frame class (issue #465).** The intended answer was *"a semantic
field translated inconsistently across boundaries"*, to be closed by one descriptor that all three
boundaries walk. As built, `ToRepublishAmqp` does not walk the table, the descriptor carries no
coercion, and `ContentType`'s outbound assignment plus the `TimeToLive` / `Expiration` arms live
per boundary — so per-boundary copies remain and this gate **fails** for the native-frame class.

## Consequences

- The core<->AMQP translation asymmetries this ADR set out to fix are each fixed: GAP A (TTL
  reconstitute on receive), GAP B (content-type drives inbound body-converter selection), the
  C-family carry-only natives (DECISION-B), GAP D (CorrelationId dual-home), GAP E (persistence
  hardcoded), and GAP F (table/descriptor-driven header decode, no per-key allowlist branch in the
  translator).
- Adding a native-home field with a core concept touches the field-map table and, because
  `ToRepublishAmqp` does not walk that table, the republish path as well (issue #465); adding a
  header-home core key is one entry in the marshaller's inbound `_dispositions` map plus, when its
  CLR type is not field-table-legal, a separate outbound encode. A core key cannot be added without
  an inbound disposition (type-init assertion), but nothing forces a matching outbound encode.
- **Cross-references.** ADR 0001 (classic-queue delivery-count counting) stays **out of this
  table**: `ReceiveAttempts` / the `x-chatter-delivery-count` republish counter are owned by the
  receiver's delivery-counting path, not the field-map, because they are computed per-delivery
  rather than translated. ADR 0002 (receive-channel epoch lifecycle) and ADR 0003 (connection-
  source lifecycle authority) are unaffected — this contract governs *what* is on the wire, not
  *how* the channel/connection lifecycle is managed. The Azure Service Bus
  `AsAzureServiceBusMessage()` projection is the sibling prior-art this aligns with.
