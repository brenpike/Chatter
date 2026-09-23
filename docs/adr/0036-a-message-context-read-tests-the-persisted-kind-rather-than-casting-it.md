---
status: accepted
date: 2026-09-23
---

# A Message Context read tests the persisted kind rather than casting it

A Message Context brought back by Outbox replay is deserialized JSON, so a value can come back as a different kind
from the one it was stamped as. `OutboundBrokeredMessage` read those values through a hard `(TValue)` cast, and the
Azure Service Bus extensions through `(DateTime?)` and `(string)` casts over the non-generic accessor, so a value of
the wrong kind threw an unclassified `InvalidCastException` — on every attempt, because no retry changes a stored
kind. This ADR records the decision that a Message Context read TESTS the kind and treats a mismatch as absent, and
the residuals that decision leaves.

Issues #418 and #464.

## Context

**Where a different kind comes from.** `MessageContext.MaterializePersistedContext` restores each persisted value as
the kind Newtonsoft's untyped read produced: a number as a `long`, or a `double` when it does not fit one; `true` /
`false` as a `bool`; an object as a `Dictionary<string, object>`; an array as a `List<object>`; and a string that is a
strict ISO-8601 value as a `DateTime`. A value stamped as an `int` therefore reads back as a `long`, and a string that
happens to look like a date reads back as a `DateTime`. Each restored kind is pinned by its own fact in
`UsingMessageContext/WhenMaterializingPersistedContext`.

**What the cast did with it.** Three surfaces wedged:

- **The Outbox drain.** `OutboxProcessor.Process` read the row's content type through an indexer and a `(string)`
  cast, and its infrastructure type through a `(string)` cast. Either throw landed in the drain's generic catch,
  which records a dispatch attempt, so the row spent an attempt on every poll without ever being dispatched — and
  `OutboxMaxDispatchAttempts` is absent by default (ADR-0031), so nothing ever gave up on it.
- **Construction itself.** `CorrelationId` and `InfrastructureType` read through `GetMessageContextByKey<string>`, and
  the constructor reads `CorrelationId` to decide whether to stamp one, so a wrong-kind correlation id threw out of
  the `OutboundBrokeredMessage` constructor.
- **Every Azure Service Bus send from a handler (#464).** A handler's outbound message inherits the inbound Message
  Context by design (ADR-0015). `GetScheduledEnqueueTimeUtc` and `GetToAddress` cast, and `AsAzureServiceBusMessage`
  calls the first, so one wrong-kind inherited header made every such send throw.

## Considered Options

### Option A — kind-test at one construction point; a mismatch reads as absent (ACCEPTED)

Recorded under *Decision*.

### Option B — throw a classified exception on a mismatch (REJECTED)

A named exception is better diagnostics and exactly the same wedge. The read happens at an arbitrary call site —
inside the drain, inside a constructor, inside `AsAzureServiceBusMessage` — and a classified throw there still fails
the row on every poll and the send on every attempt. It would also answer a question no caller asks: every
production reader wants the value or nothing, and none of them has anything to do with a kind report except fail.

### Option C — PATCH-only: kind-test the Azure Service Bus reads inline, add no public member (REJECTED)

It would fix the reads that are known today and leave the next one to be written as a cast. That is
complete-the-known-set: its answer to the Closed-by-Construction question is a list of call sites, and the next
finding is the next call site. It also leaves a caller that has to tell *absent* from *present but `default`*
with no way to do so.

### Option D — obsolete the non-generic `GetMessageContextByKey(string)` overload (REJECTED)

The overload does not cast; it returns the stored `object`, and the cast was always the caller's. It is also the
right door for a reader that CONVERTS rather than kind-tests: `ReceiveAttempts` reads through it and converts with
`Convert.ToInt32`, because a live-receive context holds an `int` and a replayed one a `long`. Obsoleting it would warn
every such caller, and break one that builds with warnings as errors, to discourage a cast the overload never made.

## Decision

**A Message Context read tests the kind of the stored value; it never casts it.**

- **`OutboundBrokeredMessage.TryGetMessageContextByKey<TValue>` is the single construction point.** It answers `true`
  only when the key is present AND its value is a `TValue`. `GetMessageContextByKey<TValue>` answers the Try's value,
  or `default(TValue)`, and the non-generic overload is `GetMessageContextByKey<object>`, so every read on the type
  goes through that one test. The rationale and its oracles are recorded once, in the `INVARIANT:` at the mechanism:
  `MustReadAMismatchedKindAsAbsentFromTheTypedAccessor` and `MustReportAMismatchedKindAsNotFoundFromTheTryAccessor`.
- **A mismatch is a benign ABSENT.** It reads exactly as a missing key reads, so each affected read falls to the path
  that already existed for a missing value: no scheduled enqueue time, no `To` address, the time to live left as it
  stands, a fresh correlation id, the default Messaging Infrastructure.
- **No numeric coercion in the accessor.** `GetMessageContextByKey<int>` over a stored `long` reads `0`. A reader
  that wants a number whatever kind it arrived as converts it, as `ReceiveAttempts` does, and reads a value it cannot
  convert as `0` (`MustReadReceiveAttemptsAsZeroWhenPersistedKindIsNotConvertible`,
  `...WhenPersistedStringIsNotNumeric`, `...WhenPersistedNumberOverflowsAnInt`).
- **The drain kind-tests its own two reads.** A content type that is absent or not a string reaches the classified
  refusal the drain already raised for a blank one (`MustRefuseAnOutboxMessageWhosePersistedContentTypeIsNotAString`,
  `MustRefuseAnOutboxMessageWhosePersistedContentTypeKeyIsAbsent`). That row still cannot be dispatched and still
  spends its attempts; what changes is that the failure now names the missing content type.
- **The Azure Service Bus reads go through the same door.** `GetScheduledEnqueueTimeUtc` uses the Try;
  `GetToAddress` uses `GetMessageContextByKey<string>`
  (`MustReadScheduledEnqueueTimeUtcAsNullWhenPersistedKindIsNotADateTime`,
  `MustReadToAddressAsNullWhenPersistedKindIsNotAString`,
  `MustBuildAnAzureServiceBusMessageWhenEveryPersistedKindMismatches`).

**Why absent, and not a report.** Three facts carry it. Every production reader of an outbound Message Context — the
five members on `OutboundBrokeredMessage`, the eight Azure Service Bus extensions, and the RabbitMQ translator's and
sender's string reads — wants the value or nothing. The inbound side already reads this way:
`InboundBrokeredMessage.GetMessageContextByKey<T>` has kind-tested since #324 (ADR-0015), so this makes the outbound
side agree with it rather than inventing a second rule. And every affected header already had a defined absent path,
so treating a mismatch as absent introduces no new outcome, only a new way to reach an existing one.

**The Cosmos Outbox Document Contract is a different layer and is unaffected.** It names a non-string messaging
system as a contract VIOLATION and marks the document undeliverable (#361, #416). That is a verification of one
document's persisted bytes, owned by the Cosmos module and evaluated before a Message Context is ever read through
this accessor. It is not a dictionary read, and nothing here changes it.

## Closed-by-Construction Acceptance Test

> What class of future finding does this make impossible, and why?

**ELIMINATED CLASS: a typed read on `OutboundBrokeredMessage` that throws because of the kind a value was stored as.**

The fix changes the PRIMITIVE, not the set of handled keys. There is one place a stored value becomes a `TValue`, and
it is an `is` test, so there is no cast left on the type for a new key or a new kind to reach. A key added tomorrow
is read the same way without anyone remembering to guard it.

**What it does not make impossible** is a cast written OUTSIDE the type, over the `object` the non-generic overload
returns — residual 3 below.

## Tradeoff

**A wrong-kind infrastructure type now misroutes instead of wedging.** The drain reads it as absent and dispatches the
row through the default Messaging Infrastructure, which may not be the one its writer meant
(`MustDispatchViaTheDefaultInfrastructureWhenThePersistedInfrastructureTypeIsNotAString`). A row that is otherwise
dispatchable reaches a broker rather than being refused on every poll forever, and the misroute is made observable: the
drain logs a `Warning` naming the message id and the key (`MustLogThatThePersistedInfrastructureTypeWasUnreadable`).

## Recorded residuals

1. **A wrong-kind value is absorbed silently wherever there is no logger.** *Root cause:* `OutboundBrokeredMessage`
   and the Azure Service Bus extensions hold no logger, so a mismatch at those call sites reads as absent with no
   signal. Only the drain's two reads surface one — the infrastructure type as a `Warning`, the content type as the
   logged refusal. *Bounded impact:* each affected read takes the absent path
   it already had, pinned by the facts above, and a caller that needs to tell absent from present uses the Try.
   *Rejected remediation:* giving the message type a logger — it is a data type applications construct through
   public constructors, not a resolved service, so that would change its public surface for a diagnostic.
2. **A wrong-kind persisted `CorrelationId` is replaced by a fresh `Guid`.** *Root cause:* the constructor stamps a
   new correlation id whenever it reads none, and a mismatch reads as none, so the stored value is overwritten.
   *Bounded impact:* that one message is built and sent, under a correlation id that no longer matches its origin
   (`MustStampAFreshCorrelationIdWhenThePersistedCorrelationIdIsNotAString`). *Rejected remediation:* converting the
   stored value to a string — that is the coercion this decision keeps out of the accessor.
3. **The non-generic `GetMessageContextByKey(string)` overload remains castable by a caller.** *Root cause:* it returns
   `object`, and a `(T)` cast a caller writes over it is outside the accessor's reach. *Bounded impact:* no production
   caller in this repository casts it; its one production reader, `ReceiveAttempts`, converts under an exception
   filter. *Rejected remediation:* Option D.

**Amendment (2026-09-23) — the raw-dictionary reads were casts of the same class.** Three reads outside
`OutboundBrokeredMessage` read a Message Context as a raw dictionary and cast what they found:
`BrokeredMessageDispatcher` read the infrastructure type out of the routing options with `(string)` on both the
diagnostics-off and diagnostics-on paths, `ReplyRouter` read the delivery's infrastructure type with `(string)` to label
the reply's send span, and `RoutingOptions.ContentType` cast its stored content type. The dispatcher's needed no
persisted context to reach: a handler sending through its `IMessageHandlerContext` inherits the delivery's Message
Context, so a non-string infrastructure type made every send from that handler throw — the #464 shape. They are closed
by a second construction point with the same contract, the internal
`MessageContextReadExtensions.TryReadMessageContext<TValue>` over `IDictionary<string, object>`: `true` only when the
key is present AND its value is a `TValue`, and `false` for an absent key, a value of another kind and a stored `null`
alike. A mismatch reads as absent there too. The dispatcher routes via the default Messaging Infrastructure
(`MustRouteViaTheDefaultInfrastructureWhenTheInfrastructureTypeIsNotAString`) and, like the drain, logs a `Warning`
naming the key (`MustLogThatTheInfrastructureTypeWasUnreadable`); it is a resolved service that holds a logger, so
residual 1 does not reach it. The dispatch and reply send spans leave `messaging.system` unset, as for an absent value
(`MustLeaveTheMessagingSystemUnsetOnTheDispatchSpanWhenTheInfrastructureTypeIsNotAString`,
`MustLeaveTheMessagingSystemUnsetOnTheReplySpanWhenTheInfrastructureTypeIsNotAString`). `RoutingOptions.ContentType`
answers `null`, which the dispatcher refuses as it refuses a blank content type
(`MustReadContentTypeAsNullWhenTheStoredKindIsNotAString`). The single construction point of the Decision is therefore
single per surface: the Try on `OutboundBrokeredMessage` for the message type, and the internal extension for a reader
that holds only the dictionary.

## Consequences

- **`OutboundBrokeredMessage` gains one public member**, `TryGetMessageContextByKey<TValue>`. No signature, type or
  wire format changes; a Message Context is persisted and materialized exactly as before.
- **Two reads change what they answer for a wrong kind** — `GetMessageContextByKey<TValue>` answers `default`, and a
  wrong-kind `CorrelationId` is replaced — and both are recorded in the `Chatter.MessageBrokers` changelog.
- **#464 is closed by this change.** The outbound hard casts ADR-0015 recorded as accepted exposure no longer exist; ADR-0015 and
  ADR-0004 carry amendment notes pointing here.

## References

- Issue #418 — *GetMessageContextByKey hard-casts a persisted context value, wedging any replay of a non-string kind*.
- Issue #464 — *Outbound header reads still hard-cast, so a mistyped inherited header throws on dispatch*.
- ADR-0015 — *Inbound header trust*. The by-design context inheritance that made #464 reachable, and the #324
  inbound kind test this decision matches.
- ADR-0004 — *RabbitMQ message translation contract*. The `ExpiryTimeUtc` decode, which now protects the time-to-live
  refresh rather than a cast.
- ADR-0031 — *Outbox selection is derived from durable attempt state*. Why a row that throws on every attempt was
  never given up on by default.
- ADR-0027 — *Invariant prose names the oracle that falsifies it*. Why each claim above names its fact.
- `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Sending/OutboundBrokeredMessage.cs` — the Try, the typed
  accessor, `ReceiveAttempts`, `RefreshTimeToLive` and `GetTimeToLive`.
- `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Reliability/Outbox/OutboxProcessor.cs` — the drain's two
  kind-tested reads and the misroute warning.
- `src/Chatter.MessageBrokers.AzureServiceBus/src/Chatter.MessageBrokers.AzureServiceBus/Sending/OutboundBrokeredMessageExtensions.cs`
  — `GetScheduledEnqueueTimeUtc` and `GetToAddress`.
- `src/Chatter.MessageBrokers/tests/Sending/UsingOutboundBrokeredMessage/WhenAccessingMessageContext.cs`,
  `src/Chatter.MessageBrokers/tests/Reliability/Outbox/UsingOutboxProcessor/WhenProcessingOutboxMessage.cs` and
  `src/Chatter.MessageBrokers.AzureServiceBus/tests/Sending/UsingOutboundBrokeredMessageExtensions/WhenAccessingMessageContext.cs`
  — the facts named throughout.
- `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/MessageContextReadExtensions.cs` — the raw-dictionary Try
  (amendment of 2026-09-23).
- `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Sending/BrokeredMessageDispatcher.cs`,
  `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Routing/ReplyRouter.cs` and
  `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Routing/Options/RoutingOptions.cs` — the three reads it closes.
- `src/Chatter.MessageBrokers/tests/UsingMessageContext/WhenReadingAMessageContextValueByKind.cs`,
  `src/Chatter.MessageBrokers/tests/Sending/UsingBrokeredMessageDispatcher/WhenSending.cs`,
  `src/Chatter.MessageBrokers/tests/Diagnostics/WhenSendingWithoutAnInfrastructureType.cs` and
  `src/Chatter.MessageBrokers/tests/Routing/Options/UsingRoutingOptions/WhenConfiguring.cs` — the amendment's facts.
