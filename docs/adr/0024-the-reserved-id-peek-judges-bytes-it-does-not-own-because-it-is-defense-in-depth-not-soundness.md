---
status: accepted
date: 2026-09-18
---

# The reserved-id peek judges bytes it does not own, because it is defense-in-depth, not soundness

`CosmosAtomicWriteHandle.StageCreateItemStream`'s reserved-namespace guard peeks the persisted `id` off the
staged payload's own bytes before handing that SAME stream to the SDK (ADR-0007's Reserved Item-Id Namespace).
The payload is the CALLER's object. The guard inspects it in place, rewinds it, and stages it — trusting that
object for its advertised length, for where its content ends, and for its content staying stable across the
inspect-then-stage window. A refactor of the peek (#365, epic #305) withdrew the first of those three trusts.
This ADR records why the remaining two are ACCEPTED RESIDUALS rather than defects to close.

## Context

`GuardStreamPayloadNotReserved` (`CosmosAtomicWriteHandle.cs`) buffers the payload only when it is NOT
seekable — `payload.CanSeek ? payload : BufferStream(payload)` — reads an id out of it through
`TryReadIdFromJson`, restores the position it started at, and returns THAT stream for `_batch.CreateItemStream`
to read later. The bytes the guard judged and the bytes the SDK reads are one object's bytes, read twice, at
two different times, out of a stream the framework does not own.

One of the three trusts is now withdrawn. `TryReadIdFromJson` treats the advertised `Length` as a
BUFFER-SIZING HINT and never as a verdict: the pooled fast path requests one byte MORE than advertised, so a
stream yielding that extra byte is known to under-report and its truncated read is discarded; a length that
cannot size a buffer at all falls through to a read to end-of-stream. That closes a CLASS — a count a caller
advertises is never the guard's answer — rather than an enumeration of length shapes, and
`MustRejectReservedIdWhenStreamLengthUnderReportsThePayload`,
`MustRejectReservedIdWhenStreamLengthReportsNothingRemaining`,
`MustRejectReservedIdWhenStreamLengthExceedsInt32Range` and `MustRejectReservedIdDeliveredInSingleByteReads`
(`tests/UsingDocumentTierBatchLifecycleBehavior/WhenExecutingBatch.cs`) pin it.

Two trusts remain, and this ADR is about them: (a) the inspect-then-stage window, in which a caller may mutate
the stream's content between the peek and the SDK's read, and (b) the read-to-end gather, which is uncapped.

## Root cause

All three symptoms have ONE root: the peek is an in-place inspection of a CALLER-OWNED object rather than a
judgement of bytes the framework owns. Every property the guard needs — how long the payload is, where it
ends, what it contains when the SDK finally reads it — is a property of somebody else's object, and none of
the three is a property the guard can establish once and rely on. Withdrawing the length trust removed the
only one of the three that the guard was reading as an ANSWER; the other two are properties it cannot help
depending on for as long as it inspects the caller's object in place.

Neither residual is introduced by this branch. `GuardStreamPayloadNotReserved` and `BufferStream` are
byte-identical to `master` — `git diff master..HEAD` touches neither — so the inspect-then-stage shape is
exactly the one that shipped before. The gather is the same boundedness class as what it replaced, and is
reached STRICTLY LESS OFTEN: `master`'s `JsonDocument.Parse(Stream)` read every payload to end through its own
`ReadToEnd`, renting `max(bomLength, Length - Position) + 1` and then read-looping WITH GROWTH until `Read`
returned `0`, whereas the current fast path stops at the advertised bound plus one byte and only falls back to
a full gather when that bound could not be trusted. The one shape whose treatment changed is a `Length` too
large to size a buffer from: `master` threw `OverflowException` out of the stage on that shape (`ReadToEnd`
casts its rent size with a `checked` conversion, and the guard catches only `JsonException`), where the
current peek gathers to end and judges the bytes. That change is the point of the length fix — a count that
cannot size a buffer must not decide anything — and it moves that shape from an unrelated arithmetic throw to
the ordinary gather, not into a new unbounded class.

## Bounded impact

ADR-0007 already settled what this guard IS. It "REMAINS as useful DEFENSE-IN-DEPTH (parity with the trusted
`_chatterType` field), but it is NO LONGER the soundness basis — confirmation is." The soundness basis for
document-tier dedup is CONFIRMING the conflicting document on the marker 409: a bare 409 is never inferred to
be a duplicate, so a reserved-prefix document the guard failed to stop is detected at execute time and
REDELIVERED rather than silently swallowed.

That is what bounds both residuals. The actor who can mutate a staged stream mid-window is, by construction,
an actor holding this handle — and this handle exposes `public Container Container { get; }`. ADR-0007 states
the consequence in terms: the application "OWNS the container (it registers the `CosmosClient` the
`CosmosContainerFactory` derives the container from, and the document-tier atomic-write handle exposes the raw
`Container`), so an app can author a document with an `inbox:{encoded(MessageId)}` id through a non-staging
path no staging guard can close". Authoring a reserved-id document directly through that `Container` is
strictly easier than timing a mutation against the SDK's read, so the inspect-then-stage window is a STRICT
SUBSET of a bypass this module already accepted and already documented. It opens no delivery-loss class that
is not already open, and the confirm-on-409 path answers both the same way.

The uncapped gather is bounded by the same commit the payload is staged into. ADR-0007 records the physical
constraint: "a batch is bounded at 100 operations, 2 MB, and 5 seconds". A payload past that bound cannot
commit whatever the peek does with it, so the gather buys an attacker nothing the batch will honor. And a
payload that never ends exhausts memory on `master` too — through `BufferStream` for a non-seekable stream, or
through `JsonDocument.Parse`'s own read-to-end for a seekable one. Same class, narrower reach.

## Why the obvious remediation was rejected

The obvious fix is to stop inspecting a caller-owned object at all: snapshot every staged payload into
framework-owned storage, judge the snapshot, and hand the SNAPSHOT to the SDK. That would eliminate the class
outright and subsume the length fix as a special case. It was rejected on cost. Today a seekable payload — the
common path — is read into a POOLED buffer that is returned before the guard returns, so nothing the peek
allocates outlives the stage; `master` copies into a framework-owned `MemoryStream` only for a NON-SEEKABLE
payload. A snapshot reinstates that `MemoryStream` for EVERY staged payload, on every message, and it must
survive until the SDK has read it, so it is a per-message heap allocation that no pooling can reclaim at the
guard's exit. Paying that on the hot path, inside the very change whose purpose is removing per-message
allocation from this path, to harden a guard ADR-0007 already demoted BELOW the confirm path, is the wrong
trade.

The cheaper remediation — capping the gather at the batch's own 2 MB bound — was rejected for a different
reason. The cap itself changes no verdict for any payload that could commit, but it requires an OVERFLOW
VERDICT, and both candidates are POLICY. Passing an over-cap payload as idless is a NEW fail-open, in a guard
whose entire recent history is the removal of one. Rejecting it is a NEW rejection class this guard has never
had, which would refuse documents it has always accepted. ADR-0023 deferred exactly this shape of change —
"introducing a new rejection class inside that PR would smuggle a policy decision into a change reviewed as a
performance/allocation cleanup" — and the same reasoning applies here. The cap is recorded as an explicit
future owner decision, not taken inside this refactor.

## Decision

The reserved-id peek continues to inspect the CALLER's stream IN PLACE. No snapshot into framework-owned
storage, and no cap on the read-to-end gather. The inspect-then-stage window and the uncapped gather are
ACCEPTED RESIDUALS, accepted on ADR-0007's footing: this guard is defense-in-depth, and CONFIRMING the
conflicting document on the marker 409 is what carries soundness.

## Consequences

- The way to reduce this guard's exposure is to reduce what a reserved-id collision COSTS — the confirm path —
  not to harden the peek. A change that makes the confirm path more certain reduces the residual; a change
  that makes the peek more expensive does not.
- A caller that mutates a staged payload between the peek and the SDK's read can stage a reserved-prefix
  document past this guard. That document's marker collision is still CONFIRMED and redelivered, never
  inferred to be a duplicate, so the outcome is the already-documented ADR-0007 bypass rather than a silent
  delivery loss.
- A payload whose advertised `Length` cannot bound it is read to end-of-stream with no cap. This is the same
  boundedness class as the whole-document parse it replaced, reached less often, and nothing past the batch's
  2 MB bound commits regardless.
- A future snapshot, or a future gather cap with a stated overflow verdict, is an ADDITIVE decision on this
  guard's scope — not a correction of this one.
- Revisit triggers, any one of which invalidates the footing above: the confirm-on-409 path being weakened, so
  that this guard is promoted back to the soundness basis; `Container` ceasing to be public on the
  document-tier atomic-write handle, which would make the inspect-then-stage window the NARROWEST remaining
  bypass rather than a subset of the widest one; or a consumer report of a misbehaving payload stream in the
  wild.

## References

- Issue #365, Epic #305 — the refactor of the peek that withdrew the advertised-length trust and is the
  occasion for recording the two residuals it did not close.
- ADR-0007 — *Cosmos outbox co-resident change-feed relay*, source of the Reserved Item-Id Namespace, of this
  guard's defense-in-depth standing, of CONFIRM-not-INFER as the soundness basis, and of the app-owned
  `Container` bypass this residual is a subset of.
- ADR-0023 — *The reserved-id peek binds the last top-level id*, the sibling record from the same refactor and
  the precedent for deferring a new rejection class out of an allocation cleanup.
- `src/Chatter.MessageBrokers.Reliability.Cosmos/src/Chatter.MessageBrokers.Reliability.Cosmos/Reliability/CosmosAtomicWriteHandle.cs`
  (`GuardStreamPayloadNotReserved`, `BufferStream`, `TryReadIdFromJson`) — where the residuals live.
- `src/Chatter.MessageBrokers.Reliability.Cosmos/tests/UsingDocumentTierBatchLifecycleBehavior/WhenExecutingBatch.cs`
  (`MustRejectReservedIdWhenStreamLengthUnderReportsThePayload`,
  `MustRejectReservedIdWhenStreamLengthReportsNothingRemaining`,
  `MustRejectReservedIdWhenStreamLengthExceedsInt32Range`,
  `MustRejectReservedIdDeliveredInSingleByteReads`) — the tests that pin the trust this ADR does NOT accept.
