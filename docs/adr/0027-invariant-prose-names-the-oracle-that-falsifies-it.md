---
status: accepted
date: 2026-09-19
---

# An `INVARIANT:` comment names the oracle that falsifies it, and states its rationale once

Three findings in one pre-PR review resolved to one root: a load-bearing prose assertion, restated across
several surfaces, with nothing binding any copy of it to the behaviour it asserted. The three instances are
fixed (`5c90a0e`, `525de35`, `feaac1b`). This ADR records the two conventions adopted so the class stops
recurring, states honestly which of the three each convention would actually have caught, and records the
mechanism that was NOT built and why.

## Context

**The three findings, and what each one actually was.**

- `ca5a7a9e` — the outbox drain. `BrokeredMessageOutboxProcessor.DrainOutboxAsync` compared consecutive poll
  batches position by position, but `GetUnprocessedMessagesFromOutbox` orders by `SentToOutboxAtUtc` alone and
  then takes a page, so a poison batch handed back in a different tie order read as progress. The defect was in
  the code; the prose defect was the ANTECEDENT the prose rested on — "re-fetched unchanged / re-fetched
  identically on the next poll" — which appeared verbatim in the `INVARIANT:` remark on `DrainOutboxAsync`
  (`:59` before the fix), at `src/Chatter.MessageBrokers/src/README.md:213`, and in the `CHANGELOG.md` entry at
  `:25`. That antecedent was false for any store that orders by a non-unique column.

- `a7abdc8e` — the relational unit of work. `UnitOfWork<TContext>.ExecuteAsync` let a disposal fault on the
  SUCCESS path throw after the commit had already stood, turning a durable success into a failure. The prose
  that accompanied it asserted that swallowing the fault "would hide a commit-time failure".

- `802ca9d5` — retention purge. `ReliabilityRetentionPurgeService<TContext>.PurgeOnceAsync` issued one
  unbounded `DELETE` per table. The prose describing it was ACCURATE; the defect was that the behaviour it
  accurately described was unbounded.

**The measured shape of the cluster.** Remediating the two findings that carried a prose defect rewrote
ELEVEN whole paragraphs across SIX documentation files — `5c90a0e` rewrote three `CONTEXT.md` terms, three
`CHANGELOG.md` bullets and two `README.md` paragraphs; `525de35` rewrote one of each — on top of the code
comments themselves. One claim, three-to-four surfaces, every surface restating the RATIONALE rather than
citing it.

**The corpus this lands in.** `INVARIANT:` appears 704 times across 237 tracked `.cs` files: 454 occurrences
in 118 production source files, and 250 in 119 test files. That is the scope of what the conventions below do
NOT retroactively audit.

## Considered Options

### Sweep the corpus (REJECTED)

Visit all 704 occurrences and add an oracle to each. Rejected because it is enumeration: it fixes the
occurrences that exist, makes no future occurrence any better, and the next comment written after the sweep
starts the count again. It also spans all nine packages, which makes it a separate cross-module initiative
rather than part of this fix.

### A `grep` lint over `INVARIANT:` blocks (REJECTED)

A check that every `INVARIANT:` block contains an identifier matching a `[Fact]` name. Rejected on the merits,
not on effort: such a lint can verify that an oracle is NAMED, but it cannot judge whether the named oracle
pins THAT claim. The pre-fix `README.md:213` paragraph named
`MustStopRepollingWhenAFullOutboxPollBatchRepeatsUnchanged` — a real, passing, genuinely relevant test — and
was false anyway, because the test pinned the consequent while the falsity lived in the antecedent. The lint
would have passed it. A check that passes the exact instance that motivated it is not a check.

### Two conventions carried by review judgment (ACCEPTED)

Stated under *Decision*. Their cost is stated under *Closed-by-Construction Acceptance Test*, where they fail
the gate.

## Decision

**Rule 1 — oracle and mutation.** An `INVARIANT:` comment may carry a behavioural claim only when it names the
oracle that pins the claim AND the mutation that turns that oracle red. A comment that makes no falsifiable
claim — a note on ordering, a pointer, a piece of context — uses `NOTE:` instead, and carries no obligation.
Where no test pins the claim, the comment says so explicitly rather than leaving the reader to assume one
exists.

The rule keys on CONTENT, not on a keyword. Both `Pinned by ...` and `Oracle: ...` are in use in this
repository and both satisfy it; neither spelling satisfies it on its own.

**Rule 2 — single source.** The rationale for a mechanism lives ONCE, in the code comment adjacent to that
mechanism. `CONTEXT.md`, `README.md` and `CHANGELOG.md` cite the mechanism and the oracle's NAME, and do not
restate the reasoning. A reader who wants to know why reads the comment; a reader who wants to know whether it
still holds runs the named test.

### What the rules would have caught, and what they would not

This is the most important property of this decision, and it is a limitation.

**Rule 1 alone would have caught exactly ONE of the three findings.**

- `a7abdc8e` — CAUGHT. Writing the required oracle and mutation for "swallowing would hide a commit-time
  failure" is impossible, because the commit runs inside the `try` the disposal follows: there is no mutation
  that makes a swallowed disposal fault hide a commit failure, so the attempt to write the sentence exposes
  that the sentence is false. The rule catches it by making the author try.

- `ca5a7a9e` — NOT caught. The claim the `INVARIANT:` made — the drain stops when a batch repeats unchanged —
  was TRUE, and `MustStopRepollingWhenAFullOutboxPollBatchRepeatsUnchanged` pinned it. An oracle on a
  consequent never tests its antecedent, so a correctly-written Rule 1 comment sat directly above the defect.

- `802ca9d5` — NOT caught. The prose was accurate. Accurate prose about unbounded behaviour is still
  unbounded behaviour; no prose convention reaches it.

**Rule 2 is what addresses the cluster's measured shape.** The eleven rewritten paragraphs above are the cost
of restatement, and they are the cost whether the original claim was true or false: a claim restated on four
surfaces has to be re-verified on four surfaces every time the mechanism moves, and three of those surfaces
have no test anywhere near them. Rule 2 removes three of the four.

### The strongest available form is not a comment at all

`802ca9d5`'s fix went further than either rule requires. `DeleteInChunksAsync` takes its query as
`IOrderedQueryable<TEntity>` rather than `IQueryable<TEntity>`, so an unordered chunked delete is a COMPILE
ERROR rather than a documented rule. The accompanying `INVARIANT:` then explains why the parameter type is
what it is, and additionally names `MustPurgeThroughAContextThatRefusesUnorderedRowLimiting` and the widening
that reddens it. When a claim can be moved into the type system, that beats any comment, and the comment's job
shrinks to explaining the type.

### The in-repo exemplars

`ReliabilityRetentionPurgeService.cs` carries both halves of Rule 1 on `PurgeOnceAsync` (remarks block
`:86-106`). Its first `INVARIANT:` names
`Integration/WhenPurgingRetentionOnSqlServer.MustPurgeOnlyTheRowsPastTheirRetentionWindow` and the mutation
that reddens it — keying the outbox predicate on `SentToOutboxAtUtc` instead. Its second names two facts in
`UsingReliabilityRetentionPurgeService/WhenPurgingRetentionOverSqlite`, says why they count statements rather
than rows, and distinguishes the mutation that reddens both from the one that reddens a single table.

`UnitOfWork.cs:53-60` carries the other honest form. It names its oracle
(`MustNotSurfaceADisposeFailureAfterTheCommitSucceeds`) and its mutation for the claim that HAS one, and then
states plainly that the transaction is begun outside the `try` because the `catch` clause reads the scope, so
"the compiler holds that placement, no test does."

### Evidence: the rules were applied before they were written

Three separate workers on this initiative reached these conventions independently, which is the substantiation
for adopting them rather than inventing them.

1. **A withdrawn oracle.** A worker wrote an `INVARIANT:` citing `MustCreateScopePerDrainPass` as the oracle
   for scope-per-poll, then withdrew it: that fact asserts `CreateScope` with `Times.AtLeastOnce` against a
   single-poll drain, so hoisting one scope to span a whole drain leaves it green. The remark on
   `SendOutboxMessagesAsync` now pins only what an oracle genuinely covers — that the identity set outlives a
   poll, red the moment the set is reset or pruned between polls. **Residual: scope-per-poll has no oracle in
   this repository.** `MustCreateScopePerDrainPass` proves a scope is created; nothing proves one is created
   per poll.

2. **A rejected oracle.** A worker considered `MustThrowFromTransactionBeginBeforeRunningOperationThatThrows`
   as the oracle for the begin-outside-the-`try` clause and rejected it, because the mutation that would
   redden it does not compile. The resulting comment says the placement is compiler-enforced and that no test
   pins it, which is what Rule 1 asks for when no oracle exists.

3. **A claim moved into the type system.** The `IOrderedQueryable<TEntity>` parameter described above.

## Closed-by-Construction Acceptance Test

> What class of future finding does this make impossible, and why?

**It makes none impossible, and this ADR does not claim otherwise.**

Both rules are conventions carried by review judgment. No compiler, analyzer or CI step enforces either one. A
comment that names no oracle still compiles; a `CONTEXT.md` term that restates a rationale still renders. The
honest answer to the gate question is that Rule 1 raises the cost of WRITING an unbacked claim — it makes the
author attempt a sentence that is impossible to complete when the claim is false, which is how it caught
`a7abdc8e` — and Rule 2 reduces the number of places a claim must be re-verified from four to one. Neither is
elimination.

This is precedented. ADR-0024 records a defense-in-depth check as defense-in-depth rather than as soundness,
and ADR-0025 records a deferred option with its unverified parts named as unverified. Recording judgment AS
judgment is the house form; dressing a convention as a structural fix would be the failure mode.

## Recorded residual: the existing corpus is unaudited and no lint exists

**Root cause.** A load-bearing prose assertion has no binding to the behaviour it asserts, and the same
rationale is restated across three to four documentation surfaces per claim, so a claim that drifts drifts
silently on every surface at once.

**Bounded impact, measured.** 704 `INVARIANT:` occurrences across 237 tracked `.cs` files — 454 in 118
production source files, 250 in 119 test files — are unaudited against Rule 1 as of this ADR. The impact is
bounded by what a comment can do: none of these occurrences executes, so a stale one misleads a reader and
changes no behaviour. The three findings that motivated this ADR each had a real code defect underneath; the
prose was what let the defect read as intended.

**Why the obvious remediations were rejected on the merits.** Both are recorded under *Considered Options* and
neither is deferred work: a sweep is enumeration and belongs to a separate cross-module initiative, and a
`grep` lint would have PASSED `src/Chatter.MessageBrokers/src/README.md:213` — the exact false sentence that
produced `ca5a7a9e` — because that sentence named a real and relevant test while being false about the
antecedent the test never touched.

**The trigger that promotes this residual to filed work.** A THIRD finding in a later review whose root is an
`INVARIANT:` naming an oracle that does not pin the claim the comment makes. That shape is the one a lint
cannot reach and a convention has already been asked to reach, so its recurrence is evidence the convention
is insufficient rather than merely unenforced — and the response then is to question what the comment is
keyed on, not to add a check. Restatement drift alone does not trigger it; Rule 2 addresses that shape and
has not yet been given a chance to fail.

No tracker entry is opened. This is a decision with a stated reason, not outstanding work.

## Consequences

- An `INVARIANT:` comment is now a claim with an obligation attached, and `NOTE:` is the unobligated form. A
  reviewer can ask "which test, and what reddens it?" of any `INVARIANT:` and expect an answer in the comment.
- "No test pins this" is an acceptable answer and a required one when it is true. A comment that silently
  omits its oracle is indistinguishable from one whose oracle was withdrawn, which is the state
  `MustCreateScopePerDrainPass` left scope-per-poll in until it was written down.
- `CONTEXT.md`, `README.md` and `CHANGELOG.md` become thinner on rationale and denser on citations. They lose
  the ability to explain a mechanism in full at each surface; that is the point, and a reader following a
  citation reaches prose that sits next to the code and is checked by the same review that changes it.
- The conventions apply to comments written from here on. Nothing in this ADR obliges a change to an existing
  comment that is not otherwise being edited.
- A claim that can be expressed in the type system should be, and the comment then explains the type rather
  than restating the rule.

## References

- Commit `5c90a0e` — *end the outbox drain on identity, not on batch order* (finding `ca5a7a9e`), and the
  eight documentation paragraphs its prose defect required rewriting.
- Commit `525de35` — *stop a disposal fault from failing a committed unit of work* (finding `a7abdc8e`), the
  one finding Rule 1 would have caught unaided.
- Commit `feaac1b` — *delete retention in chunks rather than one statement* (finding `802ca9d5`), whose fix
  moved the claim into the parameter type.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/ReliabilityRetentionPurgeService.cs`
  (`PurgeOnceAsync` remarks, `:86-106`; `DeleteInChunksAsync` remarks and signature, `:144-169`) — the
  oracle-and-mutation exemplar, and the type-system form.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/UnitOfWork.cs`
  (`:53-60`) — the honest "no test pins this" form alongside a named oracle.
- `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Reliability/Outbox/BrokeredMessageOutboxProcessor.cs`
  (`DrainOutboxAsync` and `SendOutboxMessagesAsync` remarks) — the claim whose antecedent was false, and the
  remark narrowed to what its oracle actually covers.
- `src/Chatter.MessageBrokers/tests/Reliability/Outbox/UsingBrokeredMessageOutboxProcessor/WhenSendingOutboxMessages.cs`
  (`MustCreateScopePerDrainPass`, `:196`) — the `Times.AtLeastOnce` assertion that does not pin
  scope-per-poll.
- ADR-0024 — precedent for recording a check as defense-in-depth rather than as soundness.
- ADR-0025 — precedent for recording a deferred option with its unverified parts named as unverified.
