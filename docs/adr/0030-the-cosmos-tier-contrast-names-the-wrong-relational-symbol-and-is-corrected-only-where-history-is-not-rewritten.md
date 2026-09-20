---
status: accepted
date: 2026-09-19
---

# The Cosmos tier contrast names the wrong relational symbol, and is corrected only where history is not rewritten

Five sites in the `Chatter.MessageBrokers.Reliability.Cosmos` package state the Document Tier's side-effect-timing
contrast against the relational tier as *"reads `HasBeenReceived` first and SKIPS the handler entirely"*. The
behaviour the sentence describes HOLDS — the relational tier really does pre-read and really does skip. Only the
SYMBOL is wrong: `HasBeenReceived` has no production call site in this repository, so it names no lookup that runs
on the receive path. The origin site in ADR-0007 was corrected in `1af08d5`. This ADR records the five remaining
sites as an ACCEPTED RESIDUAL, and records that they are residual for TWO DIFFERENT reasons — one of them is not
correctable at all, and the other four are deferred.

## Context

**What is true, and what pins it.** `BrokeredMessageInbox<TContext>.ReceiveViaInbox` pre-reads its own
`_inbox.FindAsync(new object[] { messageId }, cancellationToken)` at
`src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/BrokeredMessageInbox.cs:99`,
and returns without invoking the handler when that lookup finds a marker `HasMarkerExpired` does not age out of the
deduplication window (`:101-105`). Two facts in
`src/Chatter.MessageBrokers.Reliability.EntityFramework/tests/UsingBrokeredMessageInbox/WhenReceivingViaInbox.cs`
would go red if that skip stopped happening: `MustNotInvokeHandlerOrAddSecondRowForDuplicateMessageId` (`:98`) and
`MustSkipHandlerForAnyExistingMarkerWhenDeduplicationWindowIsUnset` (`:179`). **The pre-read-and-skip claim is
therefore correct prose, and nothing below weakens it.**

**What is false, and the two grep forms that show it.** `HasBeenReceived` is the `IInboxDeduplicator` port read, not
that lookup. `grep -rn '\.HasBeenReceived(' --include=*.cs .` returns 18 matches, and EVERY one of them is under a
`/tests/` path; filtering those out — `grep -rn '\.HasBeenReceived(' --include=*.cs . | grep -v '/tests/'` — returns
ZERO. The bare token `grep -rn 'HasBeenReceived'` does match in production files, but only as the interface
declaration (`Chatter.MessageBrokers/.../Reliability/Inbox/IInboxDeduplicator.cs`), the four tier implementations,
and doc comments — never as an invocation. **The distinction matters because the two forms disagree**, and reading
the bare-token result as evidence of a call site is exactly how the wrong symbol survived five copies.

**The origin, already fixed.** ADR-0007's handler-side-effect-timing bullet (`:38`) carried the same sentence and was
amended in commit `1af08d5` to name `ReceiveViaInbox`'s own `FindAsync`, gated by `HasMarkerExpired`, while stating
explicitly that the pre-read-and-skip behaviour holds and no decision moves. ADR-0028 records the same finding about
the same symbol from the relational side. This ADR is the Cosmos-side ledger of what that correction did not reach.

**The five sites, each with the surface it actually reaches.**

| Site | Surface |
| --- | --- |
| `src/Chatter.MessageBrokers.Reliability.Cosmos/src/README.md:22` | PACKAGED. `Directory.Build.props` sets `PackageReadmeFile` and packs `$(MSBuildProjectDirectory)/../README.md` at the package root for every packable library, so this text ships in the nupkg and renders on the package listing. |
| `.../Chatter.MessageBrokers.Reliability.Cosmos/CHANGELOG.md:109` | Repository history, under a TAGGED release heading. Not packed — no pack item includes it. |
| `.../Reliability/CosmosInboxDeduplicator.cs:33` | XML doc comment, in the `<remarks>` of the public `CosmosInboxDeduplicator` type. SOURCE-ONLY: no `GenerateDocumentationFile` or `DocumentationFile` property is set in any csproj, `.props` or `.targets` in this repository, so no XML documentation file is produced or packed and this text reaches a reader through the repository or SourceLink-embedded sources, not through IntelliSense. |
| `.../Reliability/DocumentTierBatchLifecycleBehavior.cs:44` | XML doc comment, in the `<remarks>` of the public `DocumentTierBatchLifecycleBehavior<TMessage>` type. Source-only for the same reason. |
| `.../Reliability/DocumentTierBatchLifecycleBehavior.cs:133` | Inline `//` comment inside the method body, in the SIDE-EFFECT TIMING block immediately above the `TryStampInboxMarker` call. Not a doc comment and not on any published surface. |

`CosmosBrokeredMessageInbox.cs` also mentions `HasBeenReceived` (`:187-196`), and `CHANGELOG.md:97` does too, but
neither makes this claim: both say the Cosmos side's own `HasBeenReceived` throws `NotSupportedException`, which is
true and is pinned by `MustThrowNotSupportedBecauseDocumentTierDedupsViaBatchMarkerNotHasBeenReceived`
(`tests/UsingCosmosInboxDeduplicator/WhenDeduplicating.cs`) and by the equivalent fact at
`tests/UsingCosmosBrokeredMessageInbox/WhenReceivingViaInbox.cs:396`. They are not sites of this residual.

**Bounded impact.** A reader following the contrast is pointed at a method that has no production call site. The
behaviour asserted around it is correct, the Document Tier's own contract described in the same sentences is
correct, and every affected file is prose. No public API, no runtime behaviour, no wire shape, and no packaged
version is affected.

## Considered Options

### Option A — record the residual, correct nothing on this branch (ACCEPTED)

Leave all five sites as they are and make this document the record, with the promotion trigger named below.

### Option B — correct all five now (REJECTED, for two different reasons that do not reduce to each other)

**B1. `CHANGELOG.md:109` is not correctable at all.** It sits under the heading `## [0.3.0] - 2026-06-27`
(`CHANGELOG.md:105`), and `cosmos/v0.3.0` is a tag in this repository — `3d3f01c9c754763a9799e91a58c50dab6f4c77c9`,
dated `2026-06-27`, at which the package csproj reads `<Version>0.3.0</Version>`. The sentence is present in the
CHANGELOG as it stood at that tag:
``git show cosmos/v0.3.0:src/.../CHANGELOG.md | grep -c 'reads `HasBeenReceived` first and SKIPS'`` returns `1`.
Editing that entry rewrites what a shipped release said it shipped. **So a residual here is unavoidable regardless of
what is decided about the other four**, and this ADR would be needed even if the other four were corrected today.

**B2. The other four are deferred, not blocked.** They are correctable — they simply are not this branch's work.
`bugfix/308-ef-reliability-durability` carries 21 commits (`git rev-list --count master..HEAD`) and its diff against
`master` touches exactly two packages, `Chatter.MessageBrokers` and
`Chatter.MessageBrokers.Reliability.EntityFramework`. It does not otherwise touch Cosmos. Opening a third package at
this point in the review loop is the scope creep the remediation doctrine warns against, and it would put Cosmos
source changes in a pull request whose validation, changelog and version story are about the relational tier.

### Option C — file a GitHub issue for the four correctable sites (REJECTED)

The recorded residual is the mechanism. An issue would restate what this document already states, with less of the
verification attached, and would be discovered later than the ADR a Cosmos-touching change already has reason to
read.

## Decision

**Record all five sites here. Correct none of them on this branch. Change no code.**

Three properties hold, and together they are what makes recording sufficient rather than merely convenient:

- **The prose is behaviourally correct at every one of the five sites.** The relational tier pre-reads and skips;
  `MustNotInvokeHandlerOrAddSecondRowForDuplicateMessageId` and
  `MustSkipHandlerForAnyExistingMarkerWhenDeduplicationWindowIsUnset` are the facts that would refute it. A reader
  misled about the symbol is not misled about the behaviour, and no correction may weaken the claim to *"the
  relational tier does not skip"* — that would be false.
- **One site cannot be corrected without rewriting a tagged release's changelog**, so the residual is not a choice
  that a more thorough branch could have avoided.
- **The remaining four cost nothing to defer**, because the impact is a wrong symbol in prose about a method that
  runs nowhere — there is no failure mode that waits on the correction.

No GitHub issue is filed.

## Closed-by-Construction Acceptance Test

> What class of future finding does this make impossible, and why?

**None. This eliminates no class — it records one.**

Every one of the five sites is byte-identical before and after this document. A future reader grepping the bare token
`HasBeenReceived` across the Cosmos package will find the same five sentences and can raise the same finding; what
changes is that the finding then has a recorded answer, with the verification attached and with the distinction
between the correctable four and the uncorrectable one already drawn.

The class *Cosmos prose names a relational symbol that runs nowhere* is closed only by correcting the four
correctable sites together, in one change, in a pull request that touches the Cosmos package for its own reasons.
**A per-file correction is not closure**: four files carry the same sentence for the same reason, so fixing one is,
by construction, an invitation to the identical finding against the next.

## Consequences

- **Promotion trigger.** The next pull request that touches the `Chatter.MessageBrokers.Reliability.Cosmos` package
  for its own reasons should correct the four correctable sites — `src/README.md:22`, `CosmosInboxDeduplicator.cs:33`,
  and `DocumentTierBatchLifecycleBehavior.cs:44` and `:133` — in ONE change, naming `ReceiveViaInbox`'s own
  `FindAsync` gated by `HasMarkerExpired`, exactly as `1af08d5` did for ADR-0007. `CHANGELOG.md:109` stays as it is
  permanently, and this ADR is its record.
- **A "fix the symbol in file N+1" delegation is the cluster shape and should not be dispatched narrowly.** This is
  the judgment the prior step reached and the planner endorsed, and it is recorded here so the next reviewer sees
  it: four remaining instances share one fix framing, so the next such request belongs in planning, not in a
  single-file patch.
- **The packaged README is the one site a package consumer can reach without the repository.** It is also the
  cheapest of the four to correct, and correcting it alone would leave the three source sites disagreeing with it —
  which is why the promotion trigger asks for all four together.
- **ADR-0007 and this ADR now disagree about nothing.** ADR-0007 names the correct symbol; this ADR names where the
  old one survives and why.
- **No version impact.** This ADR is prose, is not packaged, and no packaged artifact changed.

## References

- ADR-0007 — *Cosmos outbox: co-resident change feed relay*. The origin site of the sentence, amended in `1af08d5`
  to name `ReceiveViaInbox`'s `FindAsync`; the amendment this ADR's promotion trigger asks the other four sites to
  match.
- ADR-0028 — *The unit of work reports an indeterminate commit as a failure*. Records the same symbol finding from
  the relational side, and names `ReceiveViaInbox`'s lookup as the read that actually suppresses.
- ADR-0027 — *Invariant prose names the oracle that falsifies it*. Why each behavioural claim above names the fact
  that would refute it.
- ADR-0026 — *The relational inbox decides expiry at receive*. Owns `HasMarkerExpired` and the deduplication window
  that gates the skip.
- ADR-0006 — *Two-tier reliability: relational ambient-tx vs NoSQL stage-then-commit*. The two-tier split whose
  contrast these five sentences are drawing.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/BrokeredMessageInbox.cs`
  (`ReceiveViaInbox`, `:99` and `:101-105`) — the pre-read and the expiry gate that together are the skip.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/tests/UsingBrokeredMessageInbox/WhenReceivingViaInbox.cs`
  (`MustNotInvokeHandlerOrAddSecondRowForDuplicateMessageId`,
  `MustSkipHandlerForAnyExistingMarkerWhenDeduplicationWindowIsUnset`) — the facts that hold the behavioural half of
  the claim.
- `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Reliability/Inbox/IInboxDeduplicator.cs` — where
  `HasBeenReceived` is declared, and the only production appearance of the token that is not a tier implementation
  or a comment.
- `src/Chatter.MessageBrokers.Reliability.Cosmos/src/README.md` (`:22`),
  `src/Chatter.MessageBrokers.Reliability.Cosmos/src/Chatter.MessageBrokers.Reliability.Cosmos/CHANGELOG.md` (`:109`,
  under the `[0.3.0] - 2026-06-27` heading at `:105`),
  `.../Reliability/CosmosInboxDeduplicator.cs` (`:33`),
  `.../Reliability/DocumentTierBatchLifecycleBehavior.cs` (`:44`, `:133`) — the five sites.
- `Directory.Build.props` (`PackageReadmeFile`, the `None Include="$(MSBuildProjectDirectory)/../README.md"` pack
  item) — why `src/README.md` is a packaged surface and the changelog is not.
