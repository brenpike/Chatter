---
status: accepted
date: 2026-09-18
---

# The reserved-id peek binds the last top-level id, because that is what Cosmos persists

`CosmosAtomicWriteHandle.StageCreateItemStream`'s reserved-namespace guard peeks the persisted `id` off the
staged payload's own bytes before handing the stream to the SDK (ADR-0007's Reserved Item-Id Namespace). A
refactor of that peek (#364, #365, epic #305) replaced a `JsonDocument`/`JsonElement` parse with a
forward-only `Utf8JsonReader` scan that materializes no document object model. This ADR records one behaviour
the refactor deliberately PRESERVED rather than changed: when a staged payload carries more than one top-level
`id` property, the guard binds the LAST one, not the first.

## Context

`TryReadIdFromJson` reads the payload into a pooled buffer and hands it to `ScanTopLevelId`
(`CosmosAtomicWriteHandle.cs`), which walks the token stream once and records `id` on every `PropertyName`
token it finds at `RootObjectPropertyDepth` whose name equals `CosmosOutboxDocument.IdField`, overwriting
whatever it recorded on the previous match. A payload with two top-level `id` properties therefore leaves the
loop holding the SECOND one — the same outcome `MustRejectPublicCreateWhenLastDuplicateTopLevelIdIsReserved`
and `MustAllowPublicCreateWhenLastDuplicateTopLevelIdIsNotReserved`
(`tests/UsingDocumentTierBatchLifecycleBehavior/WhenExecutingBatch.cs`) pin from both directions: a reserved
id arriving second wins over a safe one arriving first, and a safe id arriving second overwrites a reserved
one arriving first.

## Root cause

The pre-refactor implementation read the id with `JsonElement.TryGetProperty` over a parsed `JsonDocument`.
That also returns the LAST matching duplicate key, not the first — confirmed directly:
`JsonDocument.Parse("{\"id\":\"first\",\"id\":\"second\"}").RootElement.TryGetProperty("id", out var element)`
yields `element.GetString() == "second"`, while `EnumerateObject()` over the same document still yields both
`id` properties in document order. The behaviour was never CHOSEN when the guard was first written — it fell
out of `JsonDocument`'s duplicate-key resolution, which the guard's author had no occasion to interrogate
before this refactor forced a rewrite of the read entirely. The `Utf8JsonReader` rewrite reproduces it on
purpose: `ScanTopLevelId`'s overwrite-on-each-match loop is the deliberate re-implementation of the same
last-write-wins resolution, not an accident of the new scan's shape.

## Why it is right anyway

Cosmos itself resolves a duplicate top-level JSON key last-one-wins, so binding the last `id` means the guard
tests the value the store will actually persist. Binding the first `id` — the naive reading of "the id" for
a human — would test a value that never reaches the container: a payload smuggling a reserved-prefix id
second, behind an innocuous first `id`, would pass the guard while the id Cosmos actually assigns the
document carries the reserved prefix. That is the exact delivery loss the Reserved Item-Id Namespace exists
to prevent — an application document silently staged under an `inbox:`/`outbox:` id, invisible to this
guard because the guard looked at the wrong occurrence of the key.

## Bounded impact

Only payloads carrying duplicate top-level `id` keys are affected, and a duplicate top-level key is malformed
by convention — a well-formed document carries exactly one. Every single-`id` payload, the overwhelming
normal case, is unaffected: `ScanTopLevelId`'s loop runs to the same single assignment it always would.
`MustAllowReservedIdNestedBelowTheTopLevel` pins the orthogonal boundary this residual does not touch — an
`id` nested inside an object or an array element is never a candidate at all, at any position in the payload,
because `ScanTopLevelId` matches only `PropertyName` tokens at `RootObjectPropertyDepth`.

## Why the obvious remediation was rejected

The obvious fix is to reject a payload carrying a duplicate top-level `id` outright, rather than picking
either occurrence. That is a BEHAVIOUR CHANGE and a TIGHTENING of the guard — today's guard is silent on a
duplicate-key payload whose winning id is safe, and a reject-on-duplicate rule would newly refuse documents
this guard has always accepted. #364/#365 scoped this work as a behaviour-preserving refactor of the peek's
implementation, not a widening of what the peek rejects; introducing a new rejection class inside that PR
would smuggle a policy decision into a change reviewed as a performance/allocation cleanup. Recorded here for
an explicit owner decision instead.

## Decision

The reserved-id peek binds the LAST top-level `id` property a staged payload carries, and tests that one
against the Reserved Item-Id Namespace prefix. No duplicate-key rejection is added; a payload with more than
one top-level `id` continues to stage or reject solely on the value of the last one.

## Consequences

- The refactor from `JsonDocument`/`JsonElement` to `Utf8JsonReader` in `ScanTopLevelId` changes NO staging
  outcome for any payload this guard saw before it, duplicate-keyed or not — the same last-write-wins id is
  tested either way.
- A duplicate top-level `id` payload remains stageable when its LAST id is safe, even though its first id
  might be reserved, or might differ from the last in any other way. This is unchanged from before the
  refactor and is not newly introduced by it.
- A future decision to reject duplicate-key payloads outright is a separate, additive change to the guard's
  scope, not a correction of this one.

## References

- Issue #364, Issue #365, Epic #305 — the refactor that rewrote the peek off `JsonDocument`/`JsonElement` and
  is the occasion for recording this preserved behaviour.
- ADR-0007 — *Cosmos outbox co-resident change-feed relay*, source of the Reserved Item-Id Namespace and the
  stage-time guard this decision refines.
- `src/Chatter.MessageBrokers.Reliability.Cosmos/src/Chatter.MessageBrokers.Reliability.Cosmos/Reliability/CosmosAtomicWriteHandle.cs`
  (`ScanTopLevelId`, `TryReadIdFromJson`, `GuardStreamPayloadNotReserved`) — where the behaviour lives.
- `src/Chatter.MessageBrokers.Reliability.Cosmos/tests/UsingDocumentTierBatchLifecycleBehavior/WhenExecutingBatch.cs`
  (`MustRejectPublicCreateWhenLastDuplicateTopLevelIdIsReserved`,
  `MustAllowPublicCreateWhenLastDuplicateTopLevelIdIsNotReserved`,
  `MustAllowReservedIdNestedBelowTheTopLevel`) — the tests that pin this decision.
