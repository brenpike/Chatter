---
status: accepted
date: 2026-09-19
---

# The relational inbox decides expiry at receive, so purge timing cannot suppress a legitimate message

`ReceiveViaInbox` treated any existing inbox marker as a suppression, whatever the marker's age, and inbox
markers were never reclaimed (#382). Retention alone does not close that: a purge reclaims the row, but it
runs on its own interval, so the period a marker actually suppresses a redelivery would be the configured
window plus however long the next pass takes to arrive. This ADR records why the relational inbox decides
expiry for itself, at receive time, and what that decision leaves unfixed.

## Context

Two pieces landed together. `ReliabilityRetentionPurgeService<TContext>`
(`src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/ReliabilityRetentionPurgeService.cs`)
deletes aged rows from both reliability tables, waiting `EntityFrameworkReliabilityOptions.PurgeInterval` —
five minutes by default — between passes. `BrokeredMessageInbox<TContext>.ReceiveViaInbox` reads the marker
by key with `_inbox.FindAsync` and asks `IsHandledWithinTheDeduplicationWindow` — named `HasMarkerExpired` when
this ADR was written, and inverted in sense by ADR-0033 — whether that marker is still within
`EntityFrameworkReliabilityOptions.InboxDeduplicationWindow`.

The purge is hygiene: it bounds table growth. It is not the rule, and it cannot be, because the loop that
drives it swallows a failed pass — `ExecuteAsync`'s `catch` logs at Error and waits out the interval before
retrying — so its cadence is not something a receive can depend on.

## Considered Options

**Purge-only.** Let retention be the whole mechanism and leave `ReceiveViaInbox` reading marker existence.
Rejected because the effective Deduplication Window would then be the configured window plus the purge lag:
a pass delayed by a long interval, or lost to the `catch` above, silently lengthens deduplication, and
nothing in the receive path reports that the suppression it just applied was past its window.

**Filter-then-`AddAsync`.** Treat an expired marker as absent, ignore it, and insert a fresh row. Not
available: `InboxMessageConfiguration.Configure` declares `builder.HasKey(t => t.MessageId)`
(`InboxMessageConfiguration.cs:11`), so `MessageId` **is** the primary key and a second row for the same id
is a primary-key violation, not a second marker. Expiry has to refresh the existing row.

**A knob on the shared `ReliabilityOptions`.** Rejected in favour of an EF-local
`EntityFrameworkReliabilityOptions`, so that the only property this work adds to the shared
`Chatter.MessageBrokers` options surface is `OutboxPollBatchSize`, the outbox poll cap. The relational
inbox's retention is relational mechanics; the in-memory inbox has its own window and does not read this one.

## Decision

Semantic expiry is decided at receive, and the purge is kept as separate hygiene. A marker whose
`ReceivedByInboxAtUtc` is older than `InboxDeduplicationWindow` is treated as spent: the handler runs, and
the marker is refreshed in place rather than inserted again.

This eliminates the class *a marker older than the window suppresses a legitimate redelivery, whatever the
purge timing*. `MustInvokeHandlerAndRefreshTheMarkerWhenDeduplicationWindowHasElapsed` pins it from the
positive side and `MustSkipHandlerWhenMarkerIsWithinTheDeduplicationWindow` from the negative one
(`tests/UsingBrokeredMessageInbox/WhenReceivingViaInbox.cs`). `HasBeenReceived` answers on the same terms:
`MustNotReportReceivedForAMarkerOlderThanTheDeduplicationWindow` and
`MustReportReceivedForAMarkerWithinTheDeduplicationWindow` pin the two sides of that read.

### The refresh does not give the inbox a commit point

The refresh writes `ReceivedByInboxAtUtc` on the existing row rather than inserting a second one, and
`UnitOfWorkBehavior`'s single commit remains the only commit, alongside the handler's own work.
`MustInvokeHandlerAndRefreshTheMarkerWhenDeduplicationWindowHasElapsed` is the fact that pins the refresh.

**Amended 2026-09-21.** This paragraph used to add that `ReceiveViaInbox` calls no `SaveChanges` of its own and
that `MustNotDeclareADbContextField` fails on a reintroduced `DbContext`-typed field. Both are now false:
`ReceiveViaInbox` FLUSHES twice — a claim before the handler and a stamp after it — and that tripwire fact was
deleted along with the field it forbade. The section's own claim is unchanged and still holds: the inbox flushes
but never COMMITS, now pinned by counting commits through an `IDbTransactionInterceptor` in
`MustFlushTheClaimWithoutCommittingIt` rather than by denying a field. The mechanism and its rationale are
ADR-0033's.

### A marker carrying no timestamp is a CLAIM, and is re-handled

**Amended 2026-09-21 — this section is INVERTED, and the inversion is the point of ADR-0033.** It used to record
that an undated marker keeps suppressing for good and is never purged. A NULL `ReceivedByInboxAtUtc` now means a
delivery CLAIMED the message id and its handler never completed, so:

- The receive path re-handles it. `IsHandledWithinTheDeduplicationWindow` — which replaced `HasMarkerExpired` —
  returns `false` for a marker with no timestamp before it reads the window at all, so no setting of the window
  can make one suppress and none can expire one either. Oracles:
  `MustInvokeHandlerForAClaimWithNoTimestampWhenDeduplicationWindowIsSet` and
  `.MustInvokeHandlerForAClaimWithNoTimestampWhenDeduplicationWindowIsUnset`.
- The purge takes it. A NULL column has no age, so no cutoff ages one out and sparing them would accrue rows for
  the life of the table. Oracles:
  `Integration/WhenPurgingRetentionOnSqlServer.MustPurgeAnInboxMarkerClaimedButNeverHandled` and
  `UsingReliabilityRetentionPurgeService/WhenPurgingRetentionOverSqlite.MustPurgeAnInboxMarkerClaimedButNeverHandledWhileSparingAFreshOne`.

The facts this section used to name — `MustSkipHandlerWhenMarkerHasNoTimestampAndDeduplicationWindowIsSet` and
`MustNeverPurgeAnInboxMarkerCarryingNoReceivedTimestamp` — were deleted with the behaviour they pinned. This
ADR's own decision is untouched: expiry is still decided at receive, and the purge is still hygiene that cannot
change a suppression decision. What changed is which rows are eligible, not who decides.

### The disabled default, and its deliberate divergence from the in-memory inbox

Both retention windows on `EntityFrameworkReliabilityOptions` default to `null`, which disables them. A
finite default would change the behaviour of a host already running this package: a late redelivery its
inbox suppresses today would start being handled again the moment its marker aged out. With no window
configured, expiry is unreachable — `IsHandledWithinTheDeduplicationWindow` returns `true` for any marker
carrying a stamp, whatever its age — and every HANDLED marker suppresses however old it is
(`MustSkipHandlerForAnyExistingMarkerWhenDeduplicationWindowIsUnset`,
`MustSkipHandlerForAnyExistingMarkerWhenRetentionIsExplicitlyDisabled`,
`MustReportReceivedForAnyExistingMarkerWhenDeduplicationWindowIsUnset`).

This diverges from the in-memory inbox, whose window is mandatory: `InMemoryBrokeredMessageInbox` refuses a
`ReliabilityOptions.InMemoryInboxDeduplicationWindowInMinutes` below `MinimumDeduplicationWindowInMinutes`,
and `ReliabilityOptionsBuilder` refuses the same value while the options are being built. The divergence is
deliberate, and the reason is that persistent rows differ in what reclaims them. The in-memory store's
window is its only reclamation rule, so a window that could be switched off would leave entries nothing was
able to remove. A relational marker is reclaimable by the purge instead, and a marker nothing has purged
costs a row rather than a leak.

### What the purge predicates actually carry

Both predicates in `PurgeOnceAsync` carry a `!= null` conjunct. Those conjuncts state the rule; they are not
what carries it. On a relational provider, SQL three-valued logic already excludes a NULL column from
`column < @cutoff`, so removing either conjunct changes no purge outcome. What carries the guarantee that an
undrained outbox row is never deleted is the column the predicate keys on — `ProcessedFromOutboxAtUtc`, the
stamp the drain writes, rather than `SentToOutboxAtUtc`, the stamp the enqueue writes.
`Integration/WhenPurgingRetentionOnSqlServer.MustPurgeOnlyTheRowsPastTheirRetentionWindow` is what pins
that: it seeds an `outbox-unprocessed-stale` row whose send stamp is well past the cutoff and whose
processed stamp is null, and asserts that row survives the pass.

### A producer-controlled message id, bounded honestly

The id the inbox keys on is `messageBrokerContext.BrokeredMessage.MessageId` — a wire value, so whatever
produced the message chose it. A forged or merely reused id therefore suppresses a legitimate message. Two
things bound that. Setting the id at all requires the ability to send to the broker the receive path is
reading from, so it is not reachable by anything that cannot already publish to it. And with a window configured,
the suppression is bounded to that window rather than permanent, so a window sized at or above the worst-case
redelivery horizon spends its suppression on redeliveries and releases the id afterwards. Suppression is also
observable now rather than silent: `ReceiveViaInbox` logs it at Information with the message id
(`MustLogSuppressionAtInformationWithTheMessageId`), and logs the expired-marker decision at Information too.

### Accepted residual: the store's collation, not the application, decides message-id equality

`ReceiveViaInbox` reads the marker with `_inbox.FindAsync(new object[] { messageId }, ...)` and
`HasBeenReceived` reads it with `AnyAsync(m => m.MessageId == messageId)`. Both emit an equality predicate on
the `MessageId` column, so the comparison is performed by the DATABASE under that column's collation, not by
the application under an ordinal comparison. `InboxMessageConfiguration.Configure` declares
`HasKey(t => t.MessageId)` and `IsRequired()` and no collation, so the column inherits the database default —
on SQL Server typically a case- and accent-insensitive one. Two ordinally distinct broker identifiers that
the deployed collation treats as equal therefore share one marker, and the second is suppressed without its
handler running.

**Root cause.** Message-id equality is delegated to the store's collation, and this package never states what
equality it needs. **Inherited, not introduced:** the pre-branch code read the same column with
`AnyAsync(m => m.MessageId == messageId)`. This change replaced an existence test with a key lookup — the
query SHAPE — and left the equality SEMANTICS exactly where it found them.

**Bounded impact.** Reaching it needs two ordinally distinct ids that collide under the deployed collation,
which broker identifiers in practice are not. For a GUID rendered as hex — the common case — a case-only
difference is the SAME identifier, so a case-insensitive collation is correct there rather than harmful; the
residual needs an id scheme whose distinctness genuinely rests on case or accent. As with a forged id above,
setting the colliding id requires the ability to publish to the queue the receive path reads from, so it is
not reachable by anything that cannot already publish. With a window configured the suppression is bounded to
that window rather than permanent, and it is observable rather than silent: suppression logs at Information
with the message id (`MustLogSuppressionAtInformationWithTheMessageId`).

**Why the obvious remediation was rejected.** Pinning a binary or case-sensitive collation on the column is
the closed-by-construction fix, and it is not this package's to make. This package ships
`IEntityTypeConfiguration` types that the application applies inside its OWN `DbContext.OnModelCreating`, and
the application generates and owns the migration — the same boundary that makes
`OutboxMessagePollIndexConfiguration` opt-in. `UseCollation` is also provider-specific while this package
targets EF Core generally, so a collation named in shipped configuration would be wrong or unsupported on
some providers. Changing the collation of an existing primary-key column is in any case a breaking schema
migration on every deployed consumer.

The cheaper in-process remediation — re-check `string.Equals(marker.MessageId, messageId,
StringComparison.Ordinal)` after the lookup and treat a non-ordinal match as absent — was rejected on the
merits, because it converts a silent suppression into an unrecoverable one. The handler would run, then
`AddAsync` would insert a second row whose key the database still considers a duplicate, so the unit of work
fails on its primary key on every delivery until retention purges the colliding marker or the broker
dead-letters the message. It also never reaches `HasBeenReceived`, whose `AnyAsync` decides in the database.

An application that needs ordinal message-id equality declares the collation it wants on the `MessageId`
column in its own `OnModelCreating`, alongside the configuration this package ships.

### Residual RESOLVED: two concurrent redeliveries of an expired id both re-run the handler

**Amended 2026-09-21.** This was recorded as an accepted residual on the reasoning that `InboxMessage` declares
no concurrency token, so nothing made the second refresh lose, and that claim-first was unavailable because
claiming an id ahead of the handler would require the inbox to commit its own row. **Both premises are now
false, and the residual is resolved rather than merely narrowed.** ADR-0033 owns the decision; what it does to
this residual is recorded here.

- **`ReceivedByInboxAtUtc` IS a concurrency token now**, mapped by `InboxMessageConfiguration` as an annotation
  on the model rather than a column, so the loser of a concurrent refresh fails its write with a
  `DbUpdateConcurrencyException` instead of overwriting the winner. Oracle:
  `UsingInboxMessageConfiguration/WhenConfiguring.MustTreatReceivedDateAsTheOnlyConcurrencyToken`. The wider
  change this ADR declined — a column in every application's database — was not needed, because the existing
  nullable column carries the state.
- **Claim-first does NOT require the inbox to commit its own row.** The claim is FLUSHED into the ambient
  transaction and left for `UnitOfWorkBehavior`'s single commit, so ADR-0006's relational-tier rule is intact:
  the marker is still atomic with the handler's work and the inbox still commits nothing. That distinction —
  flush versus commit — is what the original rejection missed.
- **The store orders the two deliveries**, because the flushed claim takes the row's lock before the handler
  runs. Oracles: `Integration/WhenDeduplicatingInboxOnSqlServer.MustInvokeTheHandlerOnceWhenTwoRedeliveriesRaceAnExpiredMarker`
  and `.MustMakeASecondDeliverysClaimWaitOnTheFirstsRowLock`.

**#380 stays OPEN** for the residual ADR-0033 does not close: a handler that completes and whose stamp does not
commit re-runs on redelivery, so a handler's NON-TRANSACTIONAL side effects can still repeat. Database side
effects remain once-only. The fresh-id race itself is ordered by the same lock, pinned by
`.MustInvokeTheHandlerOnceWhenTwoDeliveriesRaceForTheSameMessageId` — which REPLACED
`MustPersistExactlyOneInboxRowWhenTwoReceiversRaceForTheSameMessageId`, a fact that asserted the ROW count the
primary key already guaranteed and was therefore green on a completely unordered race. The primary-key backstop
is still pinned by `.MustRejectDuplicateInboxMessageIdAtThePrimaryKeyConstraint`.

### Accepted residual: a purge against a model mapping neither entity logs an error every cycle

Retention is opt-in and the `IEntityTypeConfiguration` types are applied by the application, so a `TContext`
that maps neither `InboxMessage` nor `OutboxMessage` throws on every pass. `ExecuteAsync` catches that, logs
at Error, and waits for the next interval. This is deliberate: a context mapping neither entity must not take
the host down. The cost is that a misconfigured model reports itself once per interval, for as long as the
host runs, rather than once at startup.

### The core CONTEXT.md drift, corrected rather than carried

The **Inbox Deduplicator** term in `src/Chatter.MessageBrokers/CONTEXT.md` used to say that on the relational
tier `ReceiveViaInbox` "reserves the message id before the handler runs rather than after it completes", and
built an in-flight-reservation paragraph on that premise. That described the in-memory realization, not this
one: `BrokeredMessageInbox<TContext>` stages its marker AFTER the handler returns and commits nothing itself,
so no reservation is ever visible to a concurrent delivery. The drift predated this work and was first filed
here as a carried residual; it was corrected during this initiative's pre-PR review instead. The term now
stated the handler-first order and scoped in-flight reservation to the in-memory inbox, pinned to executable
behaviour rather than restated as prose.

**Amended 2026-09-21 — the corrected text has itself been superseded, and the ORIGINAL sentence is closer to
the truth than the correction was.** The relational inbox now DOES reserve the message id before the handler
runs: `ReceiveViaInbox` flushes a claim carrying no timestamp into the ambient transaction ahead of the handler,
so a reservation IS visible to a concurrent delivery — it holds the row's lock. One of the two oracles that
correction named, `MustInvokeHandlerAndTrackButNotPersistInboxMessageForFreshMessageId`, was deleted with the
handler-first behaviour it pinned. The term's current wording in `src/Chatter.MessageBrokers/CONTEXT.md` is
maintained there, against ADR-0033; what is recorded here is that this ADR's account of that drift describes a
state neither before nor after, and is superseded on both sides.

### Accepted residual: the purge service's loop is covered by reasoning only

`PurgeOnceAsync` is the tested seam, driven directly from a real container in
`Integration/WhenPurgingRetentionOnSqlServer`, which covers the column-keying rule above, the undated marker,
and both windows disabled (`MustPurgeNothingWhileBothRetentionWindowsAreDisabled`). `ExecuteAsync` itself —
the disabled-and-return branch, the `Task.Yield` that forces suspension before the first pass, the per-cycle
`catch`, and the interval wait — is covered by the `INVARIANT` comments on it and by nothing executable. No
test in this repository starts the hosted service.

## Consequences

- The Deduplication Window is now a property of the receive decision, so the window an application
  configures is the window it gets, independent of how often — or whether — a purge pass succeeds.
- An application that configures a window accepts that a redelivery arriving after it is handled again. That
  is the point of the window, and it is why the default is disabled.
- The purge remains load-bearing for table growth and for nothing else. Disabling it, or losing passes to
  the `catch`, costs rows and never changes a suppression decision.
- `HasBeenReceived` and `ReceiveViaInbox` share `InboxDeduplicationWindow`, so the tier-neutral
  `IInboxDeduplicator` read and the relational wrap seam cannot disagree about whether an id is spent.

## Amendment — a database-owned retention stamp, recorded as a named future direction

Every retention stamp this package writes is the WRITING host's `DateTime.UtcNow`: `ReceivedByInboxAtUtc` on the
stamp that follows the handler, in `BrokeredMessageInbox.ReceiveViaInbox`, and `ProcessedFromOutboxAtUtc` on the
drain's claiming update, in `BrokeredMessageOutbox.UpdateProcessedDate`. Every eligibility decision compares one
of those stamps against a cutoff taken from the DECIDING host's clock — `HasBeenReceived`'s cutoff and
`IsHandledWithinTheDeduplicationWindow`'s comparison, and the `inboxCutoffUtc` and `outboxCutoffUtc` the purge
derives in `PurgeOnceAsync`. Writer and decider need not be the same process. (Sites are named by METHOD rather
than by line, because the line anchors this paragraph originally carried drifted with ADR-0033's change; a
claim's own state, written as NULL, carries no stamp and so is outside this premise entirely.)

So retention eligibility — and through it how much work a purge pass finds to do — is EMERGENT FROM CLOCK
AGREEMENT rather than owned anywhere. No type in this package is the authority on when a row became eligible:
the answer is a function of two hosts' clocks agreeing closely enough relative to the configured window, and a
writer whose clock trails the deciding host by more than the whole retention window writes rows that are
already eligible. How much work one purge pass performs is the purge's own concern and is stated where that
loop lives; what is recorded here is the premise beneath it.

**The root kill is a single database-generated clock.** If both stamps were generated by the database — the
columns defaulted server-side and mapped `ValueGeneratedOnAdd` — one clock would write every stamp and one
clock would be compared against, and no arrangement of host clocks could make a freshly written row eligible.
That is the closed-by-construction answer to this class, and it is named here so a future proposal starts from
it rather than rediscovering it.

**It is out of scope for this decision, for three reasons.**

- It is a SCHEMA change plus a `ValueGeneratedOnAdd` mapping change to `InboxMessageConfiguration` and
  `OutboxMessageConfiguration` — `IEntityTypeConfiguration` types the APPLICATION applies inside its own
  `DbContext.OnModelCreating`, and whose migration the application generates and owns. That is the same
  boundary that left pinning a collation to the application under *the store's collation, not the application,
  decides message-id equality* above, and the same boundary that makes `OutboxMessagePollIndexConfiguration`
  opt-in.
- It CONTRADICTS the decision this ADR exists to record. *Decision* above places expiry in the receive path,
  decided in the process from `DateTime.UtcNow`. A database-generated stamp moves the write side of that
  comparison to the server while the read side stays in the process, which is a different answer to where the
  clock lives rather than a refinement of this one. Adopting it means revisiting that decision, not amending
  it.
- It breaks PROVIDER PARITY. A server-side default is provider-specific — SQL Server's `SYSUTCDATETIME()` and
  SQLite's `CURRENT_TIMESTAMP`, the two providers this package's own facts run over, differ in precision and
  in what they return — while the package targets EF Core generally. A stamp whose meaning varies by provider
  is a worse premise than one that varies by host clock, because it varies in a way a configured window cannot
  be sized against.

**Why this is recorded here rather than in a tracker.** This ADR already owns the clock premise: it is the
document recording that the relational inbox decides expiry at RECEIVE, and that the purge is hygiene that
cannot change a suppression decision. A database-generated stamp is precisely a revision of that premise, so
this is the paragraph a future proposal has to argue against.

**No oracle, and none is claimed.** Nothing in this repository pins clock-skew behaviour — no fact writes a
stamp from one clock and decides eligibility against another. Per ADR-0027 this amendment is therefore stated
as reasoning over the stamp sites and comparison sites named above, not as behaviour a named test would
falsify.

## References

- Issue #382 — inbox rows living forever and a producer-controlled id suppressing a legitimate message; the
  occasion for both the retention purge and this expiry decision.
- Issue #380 (OPEN) — the concurrent-delivery residual this decision narrows but does not close.
- Issue #383 — the cancellation token now threaded through the inbox lookup and insert.
- ADR-0006 — *Two-tier reliability port: relational ambient-tx vs NoSQL stage-then-commit*, source of the
  rule that the relational inbox never commits its own marker.
- ADR-0033 — *The relational inbox claims before the handler and stamps handled after it, in the same row*. The
  decision every amendment above records the effect of: the two-state marker, the concurrency token, the claim's
  row lock, and the purge's NULL-stamp eligibility. Expiry is still decided at receive; this ADR's decision is
  unchanged.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/BrokeredMessageInbox.cs`
  (`ReceiveViaInbox`, `IsHandledWithinTheDeduplicationWindow`, `HasBeenReceived`) — where the expiry decision
  lives. `IsHandledWithinTheDeduplicationWindow` is the symbol this ADR originally called `HasMarkerExpired`.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/InboxMessageConfiguration.cs`
  — the key declaration that leaves `MessageId` equality to the column's collation.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/ReliabilityRetentionPurgeService.cs`
  (`ExecuteAsync`, `PurgeOnceAsync`) — the hygiene half.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/EntityFrameworkReliabilityOptions.cs`
  — the disabled-by-default windows and the purge interval.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/tests/UsingBrokeredMessageInbox/WhenReceivingViaInbox.cs`
  and `src/Chatter.MessageBrokers.Reliability.EntityFramework/tests/Integration/WhenPurgingRetentionOnSqlServer.cs`
  — the tests named throughout.
