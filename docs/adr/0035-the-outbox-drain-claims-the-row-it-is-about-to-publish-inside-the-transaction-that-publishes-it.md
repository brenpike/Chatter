---
status: accepted
date: 2026-09-22
---

# The outbox drain claims the row it is about to publish, inside the transaction that publishes it

A drain used to publish first and claim after. Two drains that polled the same row both reached the publish, so the
message went to the broker twice and the second drain's claim merely lost at `SaveChanges` — after the damage. ADR-0031
made `OutboxMessage.NextAttemptAtUtc` the outbox's selection input; this ADR records the decision to make that SAME
column the arbitration point as well, moved by a compare-and-set taken BEFORE the publish and INSIDE the unit of work
that carries it. It also records the belief this work began with, which was false, and the SQL Server measurements that
are the price of believing the replacement.

Issues #444 and #519.

## Context

### The DRAIN CLAIM is not the claim the processed stamp carries

ADR-0031 Option D says of `ProcessedFromOutboxAtUtc`: *"That column is the claim."* That sentence is still true and it
is about a DIFFERENT thing. Two words are needed and this ADR fixes both:

| Term | Column | What it records |
| --- | --- | --- |
| The processed stamp's claim | `ProcessedFromOutboxAtUtc` | This message WAS dispatched. It is the delivered/still-owed distinction, the poll's exclusion predicate, and the retention purge's eligibility rule. |
| The **Drain Claim** | `NextAttemptAtUtc` | Which of several drains that polled this row gets to TRY it. It says nothing about delivery. |

Conflating them is not hypothetical: it broke a test fixture during this work, which is why the distinction is
established here once rather than restated per surface.

### The premise the work started from was false, and a local review caught it

**What was believed.** That the relational store was UNAFFECTED, because a relational drain took arbitration implicitly
from the transaction it drained inside, and that #444 was therefore an in-memory-only defect. That is also how #444 and
#519 both describe the relational tier.

**What is actually there.** Verified against the code rather than inherited:

- The poll runs OUTSIDE any transaction. `BrokeredMessageOutboxProcessor.SendOutboxMessagesAsync` opens a DI scope,
  resolves the store and calls `GetUnprocessedMessagesFromOutbox` directly; nothing begins a transaction around it.
- Both relational drain queries are plain lock-free reads.
  `BrokeredMessageOutbox<TContext>.GetUnprocessedMessagesFromOutbox` and `.GetUnprocessedBatch` are
  `Where(...).ToListAsync(...)` at the connection's default ReadCommitted.
- The real arbiter was the optimistic concurrency token. `OutboxMessageConfiguration` maps
  `ProcessedFromOutboxAtUtc` `IsConcurrencyToken()`, and that predicate is emitted at `SaveChanges` — AFTER the
  publish. **It prevents a double CLAIM. It never prevented a double DISPATCH.**

**And it was already written down on `master`, twice, in places the design never read.** The released `0.27.0`
`Chatter.MessageBrokers` changelog entry states that a duplicate delivery opens "when more than one instance polls the
same relational store concurrently". And
`Integration/WhenClaimingOutboxConcurrentlyOnSqlServer` carries a comment saying in as many words that the real drain
publishes before it stamps, "so duplicate publication is outside what this proves."

**That is why the scope of this decision is Outbox-wide rather than in-memory-only.** The mechanism lands on
`IPollableOutboxStore`, on `OutboxProcessor`, and in BOTH shipped stores.

### The encoding, and it needs no DDL

**This is the headline, and it was the single biggest unknown when the work was sized.**

The Drain Claim is a value of `NextAttemptAtUtc` — the column ADR-0031 already added, already made nullable, and
already made the poll's due input. A drain moves it from the instant the poll reported to an instant one backoff
ahead; a second drain comparing against the instant IT was reported no longer matches, and is denied.

**No new column. No `ALTER TABLE`. No migration.** This package ships no migrations at all — applications own their
schema through the `IEntityTypeConfiguration` types it ships (`src/README.md`, *Reliability*) — and there is nothing
here for an application to generate one for. The model is unchanged: no property added, no mapping altered, no
annotation added or removed.

**One due instant, one writer per transition, one predicate.** That is the whole reason the claim rides an existing
column instead of a new one, and it is what keeps the claim from becoming a SECOND durable selection input — which
ADR-0031 forbids, for the reasons given there and not restated here. It is also why there is **NO REAPER and NO LEASE
EXPIRY CLOCK** — but that follows from what a granted claim's lifetime IS, which is not one rule holding on both
tiers and is not this document's to state. It is recorded once, in the `<remarks>` on
`IPollableOutboxStore.TryClaimForDispatch`, with an oracle per leg, and cited here.

### Why ADR-0033's inbox encoding cannot be borrowed

The relational inbox reached the same shape — claim in the row, before the work — and its three states are
{no row, row + NULL, row + stamp}. **None of that transfers, and the reason is the row's lifecycle.** An outbox row
ALWAYS pre-exists: `SendToOutbox` wrote it inside the handler's own transaction, long before any drain polled it. So
there is no ABSENT state available to mean *fresh*, and the only two-valued column on offer,
`ProcessedFromOutboxAtUtc`, already spends its NULL on *still owed*. Stamping it early to mean *being tried* is
ADR-0031 Option D, rejected there: it marks an unpublished message delivered and makes it purge-eligible. The outbox's
spare state had to come from a column with a RANGE rather than from a presence bit, and `NextAttemptAtUtc` is that
column. Stated once here.

### The durability rule that reconciles two writers of one column

`NextAttemptAtUtc` is now written from two places that sit on opposite sides of the unit of work. That looks
inconsistent and is not. **A write to `NextAttemptAtUtc` is exactly as durable as the fact it records.**

- `RecordDispatchAttempt` records something that HAPPENED — a publish that failed — and must outlive the rollback that
  failure caused. It goes STRAIGHT to the store, OUTSIDE any unit of work (ADR-0031).
- The Drain Claim records something ABOUT TO happen and must NOT outlive a publish that did not. It goes INSIDE the
  unit of work.

**They meet on the failure exit, in that order.** The rollback discards the claim; the attempt write lands after it.
The row is therefore scheduled by the FAILURE rather than by a claim the failure already voided, and the two writes
never compete. That order is intended. Oracle:
`UsingOutboxProcessor/WhenProcessingOutboxMessage.MustRecordTheDispatchAttemptAfterTheRollbackDiscardsTheDrainClaim`,
which reads the SEQUENCE of writes rather than the row — both instants are one backoff ahead of now, so the row alone
does not tell them apart.

### The grant-by-default member, documented rather than enforced

`IPollableOutboxStore.TryClaimForDispatch` is a default interface implementation whose body answers `true`. It follows
the precedent `RecordDispatchAttempt` set on the same interface: a third-party pollable store written against the
previous shape still compiles, still satisfies the cast at the poll site, and keeps exactly today's behaviour. Granting
is the only default that does that — a default that DENIED would silently stop such a store dispatching anything.

The member's contract is DOCUMENTED on the interface, not enforced on implementers. Nothing inspects what a store
answers, exactly as nothing inspects a poll's size, order or dueness. Oracle:
`WhenResolvingReliabilityStores.OutboxCustomPrimaryImplementingBoth_TryClaimForDispatchGrantsByDefault`, whose store
deliberately does not implement the member.

The observed instant is an EXPLICIT PARAMETER rather than a read of the message taken at claim time. A store may hand a
poll THE STORED INSTANCES THEMSELVES — the shipped in-memory one does — so a second drain reading the observed value
off the message would read the FIRST drain's claim, compare it against itself and be granted the claim too. Oracle:
`WhenProcessingOutboxMessage.MustClaimAgainstTheDueTimeThePollRead`.

### The L/D asymmetry, named and justified

The two shipped stores implement the same compare-and-set with different lifetimes, and the divergence is deliberate.
The *Lifetime* row below states no rule of its own: it is the rule on `IPollableOutboxStore.TryClaimForDispatch` read
once per tier, from what each tier's caller's unit of work can discard.

| | Relational (`BrokeredMessageOutbox<TContext>`) | In-memory (`InMemoryBrokeredMessageOutbox`) |
| --- | --- | --- |
| Statement | One `ExecuteUpdateAsync` compare-and-set, seeking the row by `Id` | One monitor-guarded compare-and-set on the stored instance |
| Lifetime (the seam's rule, per tier) | TRANSACTION-SCOPED: invisible until commit, discarded by rollback | IMMEDIATE and SELF-EXPIRING: visible at once, expires when the claimed instant passes |
| A losing drain | BLOCKS on the row's lock for the winner's whole publish, then matches zero rows | Is denied at once and walks away |
| Due gate at claim time | Absent | PRESENT |

**The in-memory store cannot express the transaction-scoped form.** Three legs, each verified in the code:

1. **There is nothing to enlist in.** It declares `IUnitOfWork.HasActiveTransaction => false`,
   `CurrentTransaction => null`, and its `IUnitOfWork.ExecuteAsync` is `=> operation(cancellationToken)` — a bare
   pass-through. No transaction exists for a claim to be scoped to.
2. **Blocking would buy nothing in-process.** A losing drain that parked would park for the winner's whole publish and
   then discover the row taken — which is precisely where it arrives by walking away, sooner and without holding a
   thread.
3. **The recovery a transaction gives for free is supplied by self-expiry instead.** A relational drain that dies
   mid-publish hands the row back by rollback; an in-memory drain that dies hands it back when the claimed instant
   passes. And a CRASHED process loses the dictionary entirely, so there is no durable stranding for a reaper to
   recover from.

The due gate exists only on the in-memory side for a reason particular to it: `GetUnprocessedBatch` is deliberately
NOT due-gated, so an in-request drain can read a row after another drain claimed it, observe the claim's own value and
satisfy a bare comparison. Refusing a row that is not due AT CLAIM TIME is what closes that. A store whose claim is
invisible until it commits does not have that window and must not carry the gate. Oracle:
`UsingInMemoryBrokeredMessageOutbox/WhenManagingOutbox.MustRefuseADrainClaimOnAMessageThatIsNotDue`, the only fact that
stages it; dropping the gate reddens it and nothing else.

### The measurement

**The claim-inside-the-unit-of-work position is pinned by a REAL SQL SERVER fixture, not by a mock one.** That is the
strongest citation this decision has, and it is why the position is recorded as a measured relational property rather
than as a mock-ordering artifact.

`Integration/WhenArbitratingOutboxDrainsOnSqlServer` runs the REAL `OutboxProcessor` over the REAL
`BrokeredMessageOutbox<TContext>` against the PRODUCTION model on a real SQL Server container, with the Messaging
Infrastructure as the only test double. Every fact runs TWICE — `READ_COMMITTED_SNAPSHOT` ON and OFF, set explicitly in
both directions on a per-case database — because row versioning changes what a READ sees and the arbitration is decided
by an UPDATE's lock, which versioning does not release. That covers both deployments: SQL Server defaults the setting
OFF, Azure SQL Database defaults it ON. Both target frameworks, `net8.0` and `net10.0`.

**Evidence is the server's own view, never a stopwatch.** Each fact that claims a drain waited reads
`sys.dm_exec_requests` through `SqlServerBlockedRequestProbe` and asserts on the waiting session, the session it waits
on, the wait type and the request status. A slow machine and a real block are indistinguishable from elapsed time, so a
timing assertion would pass on a race that never happened.

| | Measured | Fact |
| --- | --- | --- |
| **M1** | The second drain's claim WAITS on the winner's row lock: `blocking_session_id` is the winner's own session id (not merely non-zero), the waiter's session is not the winner's, `status` is `suspended`. | `MustMakeASecondDrainsClaimWaitOnTheWinningDrainsRowLock` |
| **M2** | Once the winner COMMITS, the waiting claim matches ZERO rows: the denied drain publishes nothing, the row carries the winner's processed stamp, and `DispatchAttempts` stays `0`. | `MustDenyTheWaitingDrainsClaimOnceTheWinningDrainCommits` |
| **M3** | Once the winner ROLLS BACK, the waiting claim is GRANTED and that drain publishes the message and stamps the row. | `MustGrantTheWaitingDrainsClaimOnceTheWinningDrainRollsBack` |
| **M4** | A DIFFERENT row stays claimable WHILE a drain is blocked: it publishes during the block, and exactly ONE request is waiting on the winner — the one racing its row. | `MustLeaveADifferentRowClaimableWhileADrainIsBlockedOnTheRacedRow` |

**One precision about M1, because the fact is narrower than it is tempting to say.** The assertion is
`WaitType.Should().StartWith("LCK")`. It pins that the wait is on a LOCK rather than on the network or on a latch; it
does NOT pin which lock mode. No fact in this repository names `LCK_M_U`.

The mutations were measured by making the change and counting, not predicted. Hoisting the claim OUT of the unit of
work, so it commits on its own, reddens EVERY case in the class — eight, four facts times two settings, on both target
frameworks — because with nothing held there is no wait for any of them to observe. M1 is the one whose red names the
wait; M3 is the one whose red names the rollback. The class is also the breadth behind the core-suite mutation
*move the claim BELOW the publish*, which reddens five facts in `Chatter.MessageBrokers` plus all four of these in both
of their cases.

### The single-row PK seek, and its caveat stated as a limitation

The claim predicate seeks a single row by `OutboxMessage.Id`, the outbox's primary key under
`OutboxMessageConfiguration`. **The whole design rests on that**, because a fleet-wide block would let one wedged
publish stall the outbox for every drain — and M4 is the fact that pins it, over a real server, at the point where it
matters: a drain blocked on the raced row leaves a different row claimable DURING the block rather than after it lifts.
Dropping `Id` from the predicate reddens M4's two cases and
`UsingBrokeredMessageOutbox/WhenClaimingForDispatch.MustClaimOnlyTheMessageItWasHanded`, three over the project.

**The caveat travels with the rule, and it is a limitation rather than a bound.** Everything measured above covers a
SINGLE-ROW CLUSTERED-PK SEEK only. A range or scan claim predicate takes locks BEYOND the one row, and nothing in this
decision or this repository says what a drain blocked behind THOSE locks would do. That shape is UNMEASURED and out of
scope.

### Three findings with no oracle, stated plainly

Per ADR-0027, where nothing pins a claim this says so rather than leaving a reader to assume one exists.

1. **The in-memory claim gate's MUTUAL EXCLUSION is pinned by no test.** Removing the monitor from all four write sites
   while leaving each site's read-then-write order intact reddens NOTHING in either suite, on either target framework.
   The interference seam the fixture drives fires BEFORE the monitor is entered, so every deterministic
   single-threaded fact reaches the same answer with the gate gone. What the gate buys is a torn read of a nullable
   `DateTime` — wider than a machine word, so not atomically writable — which a single-threaded fact cannot stage. The
   measurement was taken against a compile proved fresh by an added warning, because a GREEN run is the one result a
   stale binary can fake.
2. **The claim's READ-COMPARE-WRITE ORDER is pinned by no test.** Hoisting the read and the compare out of the monitor
   and leaving only the write inside reddens nothing either, measured the same way.
3. **`object.Equals` versus `==` in the relational claim is pinned by no test.** Rewriting the observed-value
   comparison as `object.Equals(...)` reddens NOTHING, re-measured on both suites and both target frameworks with the
   assemblies checked newer than the edit. EF translates both to the identical null-safe SQL on EF 8 and EF 10, so
   which is written is a free choice and no oracle separates them. What IS pinned is the SHAPE the chosen spelling
   emits: `WhenClaimingForDispatch.MustCompareANullObservedNextAttemptAsIsNull` and
   `.MustCompareANonNullObservedNextAttemptAsAParameter`, which capture the emitted predicate rather than inferring it
   from the outcome.

## Considered Options

### Option A — move the existing due column by a compare-and-set, inside the publishing unit of work (ACCEPTED)

One column, one predicate, one writer per transition. The claim is a value of the row it claims, so there is no second
place for the answer to live and nothing to reconcile. Its durability is the publishing transaction's, which is what
makes a drain that dies mid-publish hand the row back rather than strand it.

### Option B — a dedicated `ClaimedAtUtc` column (REJECTED)

It would be a SECOND durable selection input, needing reconciliation against attempt state, and a REAPER to release a
claim whose drain died — the second expiry clock ADR-0031 forbids. It would also cost every deployment an
`ALTER TABLE` and an application-generated migration. It buys nothing the existing column does not, and buys back a
loss hazard: a reaper that releases too early duplicates, and one that never runs strands.

### Option C — stamp the processed date before dispatch (REJECTED)

Already rejected as ADR-0031 Option D. It marks an unpublished message delivered, excludes it from every later poll,
and makes it eligible for the retention purge to delete.

### Option D — a side dictionary or side-record of claimed ids (REJECTED, on the record)

This is PR #511's exact mistake, and it is rejected on the RECORD rather than argued against in the abstract. That
branch held claim state OUTSIDE the row it described — registers keyed on the transaction object, and the EF identity
map — each written non-atomically with the write it described. Every window between *the row is written* and *the
side-record agrees* was a fresh defect, so each review iteration closed one window and opened the next. Seven
iterations, no convergence, closed unmerged. #519 states the lesson as a design constraint in as many words: a claim
stored anywhere other than the entry it claims repeats that mistake.

### Option E — `UPDLOCK` / `READPAST`, or `FOR UPDATE SKIP LOCKED` (REJECTED)

SQL Server only. PostgreSQL and MySQL want different syntax, SQLite has neither, EF Core exposes none of them
portably, and this package names no provider — `BrokeredMessageOutbox<TContext>` is written against `TContext` and
nothing else. A provider-specific hint would make the arbitration true on one store and absent on the rest, silently.

### Option F — raise the isolation level (REJECTED)

It addresses the wrong scope entirely. The poll runs OUTSIDE any transaction, so the drain transaction's isolation
level is irrelevant to what a poll selects. Raising it changes the cost of the publish and not the collision.

### Option G — declare the both-drains combination unsupported and refuse it at startup (REJECTED)

This is #444's own second suggestion. It fails the Closed-by-Construction gate: it names no eliminated class, it only
refuses one configuration, and the collision it refuses is reachable over a relational store across PROCESSES where no
startup check can see it. It would also need DI descriptor inspection, and
`src/Chatter.MessageBrokers/CONTEXT.md` records a deliberate decision against that for the reliability-store family,
under *Reliability-Store Facet Resolution*: "no descriptor inspection, lifetime reconciliation, or fail-fast
registration is required."

## Decision

**A drain CLAIMS the row it is about to publish, BEFORE it publishes, and the claim is a value of the row's own
`NextAttemptAtUtc`.** The claim is a compare-and-set from the instant the POLL reported to an instant one backoff
ahead, taken as the FIRST statement inside the unit of work that carries the publish.

- **`IPollableOutboxStore.TryClaimForDispatch`** is a grant-by-default default interface implementation, following
  `RecordDispatchAttempt`'s precedent, so a store that ignores it compiles and keeps today's behaviour.
- **A denial RETURNS rather than throws.** A throw would land in the drain's generic catch and spend a dispatch attempt
  on a row this drain never attempted. A denied drain publishes nothing, stamps nothing and costs the row nothing.
  Oracles: `WhenProcessingOutboxMessage.MustDispatchNothingWhenTheDrainClaimIsDenied` and
  `.MustNotSpendADispatchAttemptWhenTheDrainClaimIsDenied`, joined over a real server by M2.
- **The instant claimed is the one a FAILED attempt would have been scheduled by**, so this decision introduces no
  number of its own, no new option, and no new knob anywhere. What that instant then does to the row is settled by the
  unit of work the claim was taken inside, under the rule recorded on `IPollableOutboxStore.TryClaimForDispatch` and
  cited rather than repeated here.
- **Relational**: one `ExecuteUpdateAsync` over `Id == id && ProcessedFromOutboxAtUtc == null &&
  NextAttemptAtUtc == observed`, which enlists in whatever transaction the caller has open. Because the caller opens
  one, the relational transaction genuinely ENFORCES the arbitration — that is the one place in this decision where
  the word is honest, and it is pinned by M1, M2 and M3.
- **In-memory**: a monitor-guarded compare-and-set on the stored instance, mutating it IN PLACE rather than swapping in
  a replacement, plus the due gate at claim time. In-place is required by the store's write-through reference
  identity: the dictionary holds the very instances a poll hands back.

**Everywhere else, the member is DOCUMENTED and not enforced.** Nothing inspects what a store answers. A store that
takes the grant-by-default arbitrates nothing and is exactly as it is today.

## Closed-by-Construction Acceptance Test

> What class of future finding does this make impossible, and why?

**ELIMINATED CLASS: a drain claim whose state disagrees with the row it claims.**

Findings of the shape *"the claim said X, but the row said Y"* cannot exist — for any interruption, any number of
concurrent drains, and any store — because the claim and the row are not two facts that have to be kept in step. There
is ONE fact, a value of `NextAttemptAtUtc`, written by the statement that takes the claim. There is no register, no
side-record, no identity-map state and no lease table for the answer to also live in, so there is no span in which two
locations can disagree, and therefore no NEXT window for a subsequent finding to be about.

Three properties follow from that and are not separate mechanisms:

- **No reaper and no lease-expiry clock can be needed**, because there is no release to miss on EITHER tier — and not
  for the same reason on both. The two derivations, one per tier from what that tier's caller's unit of work can
  discard, are the ones recorded on `IPollableOutboxStore.TryClaimForDispatch`, each with its own oracle; the two
  lifetimes they land on are the *Lifetime* row of *The L/D asymmetry* above.
- **No second selection input is created**, so ADR-0031's rule that selection derives from durable attempt state is
  not merely respected but unreachable to violate: the claim IS attempt state's own column.
- **No schema surface is added**, so there is no migration, backfill or column-drift finding available either.

This is the difference from Option D's seven review iterations. Each of those made one window absent. This removes the
second location the windows were counted over.

**What it does not make impossible is arbitration itself**, which is a store's to implement and is recorded under
*What this does NOT close*.

## What this does NOT close

- **Cross-process duplicates over a store that takes the grant-by-default.** The member is documented, not enforced.
  A store that answers `true` unconditionally is told it won every time, and nothing then holds two of its drains off
  one message. That is today's behaviour, deliberately preserved. Oracle:
  `WhenResolvingReliabilityStores.OutboxCustomPrimaryImplementingBoth_TryClaimForDispatchGrantsByDefault`.
- **A third-party store that ignores the member entirely.** Same mechanism, stated separately because it is the more
  likely case: such a store compiles unchanged and never learns the member exists.
- **Issue #508.** `OutboxProcessor`'s host-cancellation arm never consults the `published` flag, so a shutdown arriving
  after the publish returned but during the claim leaves the row unclaimed and immediately due. Untouched here, and
  the re-claim primitive cannot simply be wired into that arm because it is handed the very token whose cancellation
  raised it.
- **Range or scan claim predicates.** Every lock measurement above covers a single-row PK seek. A predicate that
  stopped being a seek takes locks beyond one row and this decision says nothing about a drain blocked behind them.
- **The in-memory tier's ONE-BACKOFF window.** The in-memory claim self-expires at the claimed instant, so a publish
  that runs LONGER than one backoff leaves the row due again while the claiming drain is still publishing, and a third
  drain can then claim and publish it. That window is strictly NARROWER than today's, which is always open, and the
  outcome inside it is a DUPLICATE DISPATCH under the documented at-least-once contract — never a lost message. The
  mitigation is the one this context already names: enable the Inbox for a handler that is not naturally idempotent.
- **The `<=` / `<` boundary against now**, at the in-memory claim's due gate. Unpinned, for the same reason ADR-0031
  records for the poll: the instant is read from the wall clock inside the method, so no test can name a row due at
  exactly it.
- **The three no-oracle findings above.** Restated as residuals rather than implied coverage: the in-memory gate's
  mutual exclusion, the claim's read-compare-write order, and `object.Equals` versus `==`.

## Migration

**No schema change, no migration, no backfill, and no coordinated upgrade order.** No column is added, widened or
retyped; no mapping or annotation moves. `NextAttemptAtUtc` already exists, is already nullable, and is already
written by `RecordDispatchAttempt` on every deployed binary that carries ADR-0031. An upgraded binary reads and writes
the same column it read and wrote before, with one more statement in front of the publish.

**Both directions are safe.** A new binary's claim is invisible outside the transaction that took it:
`WhenClaimingForDispatch.MustEnlistTheClaimInTheAmbientTransaction` asserts that the claim never lands on its own,
reading the row back at the value the poll found after the unit of work that took the claim was abandoned — what a
concurrent old binary would see is an entailment of that rather than something the fact measures. And a claim that
rolled back leaves the row as the poll found it, measured over a real server by
`Integration.WhenArbitratingOutboxDrainsOnSqlServer.MustGrantTheWaitingDrainsClaimOnceTheWinningDrainRollsBack`, where
the drain waiting on that row is granted the claim it took against its OWN poll's value. A new binary against rows an
old one wrote sees `NULL` or a past instant, both of which are claimable.

**The one behavioural note for an operator** is not a migration step: a drain now takes a brief exclusive lock on one
row for the duration of its own publish. Drains of DIFFERENT rows do not contend, pinned by M4.

## Consequences

- **A drain publishing over a relational store holds that row's lock for the whole publish.** A second drain that
  polled the same row BLOCKS there. That is the arbitration, not a side effect of it.
- **A blocked loser can hit the command timeout, and that is a consequence rather than a knob.** A publish slower than
  the `DbContext` command timeout — 30 seconds by the provider's default — makes the waiting drain's claim statement
  throw, and that drain then spends a dispatch attempt it never used. It is bounded: `OutboxMaxDispatchAttempts`
  defaults to ABSENT (ADR-0031), so nothing is given up on, and the practical outcome is a deferred row — which is
  what you want, because the winner is publishing it. **No option was added anywhere in this change**, here or
  elsewhere. No oracle pins the timeout figure; it is the provider's default, not this package's.
- **`OutboxProcessor.Process` now opens its unit of work with a statement that can DENY.** The denial path returns
  early, publishes nothing and stamps nothing, so a drain can now complete having done nothing at all — a new, quiet
  and correct outcome.
- **The re-claim path takes NO Drain Claim.** By the time `TryReClaimPublishedMessage` runs, the message is already on
  the broker, so there is nothing left to arbitrate and a denial there would abandon the processed stamp on a row that
  WAS published. No oracle separates that from a re-claim that took one, because every store grants an uncontended
  claim; it is stated at the mechanism.
- **The in-memory store's claim gate is now on the write path of the processed stamp and the attempt record too**,
  because both write fields the claim arbitrates on. The gate is held for a few field reads and writes and NEVER
  across an `await` or a dispatch.
- **Selection stays advisory while arbitration becomes atomic**, within a store that implements it. A torn read in the
  ungated poll can at worst hand back a row whose claim the gated compare-and-set then refuses.
- **No public type, member or signature is removed or changed.** One member is ADDED to `IPollableOutboxStore`, with a
  default body, which is the same compatibility shape `RecordDispatchAttempt` used.

## Correction

Four passages of this ADR once gave a TIER-NEUTRAL account of what ends a drain claim, written as one rule holding on
both tiers, while the *Lifetime* row of *The L/D asymmetry* gave the right one — so the document contradicted itself.
The same account had spread: eleven sentences across seven files carried some version of it. The canonical rule now
lives in one place, the `<remarks>` on `IPollableOutboxStore.TryClaimForDispatch`, and every surface that needs it
cites it there, this ADR included. The status stays `accepted` because the drift was in the prose and never in the
mechanism or the measurements.

## References

- Issue #444 — *In-memory outbox: concurrent drains can dispatch the same row twice*; the occasion for this decision,
  and the source of the in-memory-only framing this ADR records as false.
- Issue #519 — *`IPollableOutboxStore` gives a non-transactional store no way to claim a row before a drain dispatches
  it*; the tracked home for the interface gap, and the source of the grant-by-default and no-lease constraints.
- Issue #508 (OPEN) — the host-cancellation arm that never consults the published flag; untouched here.
- <https://github.com/brenpike/Chatter/pull/511> — the side-record design's seven review iterations, cited as the
  EVIDENCE for Option D's rejection rather than as an argument against it.
- ADR-0031 — *Outbox selection is derived from durable attempt state*. Owns `NextAttemptAtUtc`, the due gate, the
  backoff schedule, the no-second-expiry-clock rule, and Option D's rejection of an early processed stamp.
- ADR-0033 — *The relational inbox claims before the handler and stamps handled after it, in the same row*. The
  claim-in-the-row shape this decision shares, and whose three-state encoding it cannot borrow.
- ADR-0034 — *A rolled-back unit of work reconciles its context's change tracker*. What makes the rollback that
  discards a Drain Claim leave no tracked residue behind it.
- ADR-0027 — *An `INVARIANT:` comment names the oracle that falsifies it*. Why every behavioural claim above names the
  fact that would refute it, and why the three findings with none say so.
- ADR-0006 — *Two-tier reliability*. The tier split that makes this a relational-tier decision: the Document Tier
  implements no `IPollableOutboxStore` and dispatches through the change-feed Outbox Relay, so nothing here reaches it.
- `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Reliability/Outbox/IPollableOutboxStore.cs` —
  `TryClaimForDispatch`, its grant-by-default body, why the observed instant is a parameter, and the canonical
  statement of a granted claim's lifetime with an oracle per leg — which this ADR cites rather than owns.
- `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Reliability/Outbox/OutboxProcessor.cs` — the claim's position
  as the first statement inside the unit of work, the denial's early return, and the durability rule's two sides.
- `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Reliability/InMemoryBrokeredMessageOutbox.cs` — the
  monitor-guarded in-place compare-and-set, the claim-time due gate, and the unpinned-gate residual.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/BrokeredMessageOutbox.cs`
  — the single-statement `ExecuteUpdateAsync` claim, its PK seek, and the write-through onto the supplied message.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/tests/Integration/WhenArbitratingOutboxDrainsOnSqlServer.cs`
  — M1 through M4, the `READ_COMMITTED_SNAPSHOT` doubling, and `SqlServerBlockedRequestProbe` as the evidence source.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/tests/UsingBrokeredMessageOutbox/WhenClaimingForDispatch.cs`,
  `src/Chatter.MessageBrokers/tests/Reliability/Outbox/UsingOutboxProcessor/WhenProcessingOutboxMessage.cs`,
  `.../UsingOutboxProcessor/WhenDrainingTheDefaultInMemoryOutbox.cs` and
  `.../UsingInMemoryBrokeredMessageOutbox/WhenManagingOutbox.cs` — the facts named throughout.
