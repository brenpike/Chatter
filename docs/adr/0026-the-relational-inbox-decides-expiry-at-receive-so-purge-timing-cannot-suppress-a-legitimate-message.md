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
by key with `_inbox.FindAsync` and asks `HasMarkerExpired` whether that marker is still within
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

The refresh is an assignment to a tracked entity — `existingMarker.ReceivedByInboxAtUtc = DateTime.UtcNow` —
which leaves an `EntityState.Modified` entry that `UnitOfWorkBehavior`'s single `SaveChangesAsync` commits
alongside the handler's own work, exactly as the `Added` entry on the fresh-id path already was.
`MustInvokeHandlerAndRefreshTheMarkerWhenDeduplicationWindowHasElapsed` asserts the entry state and that the
table still holds one row. `ReceiveViaInbox` still calls no `SaveChanges` of its own, and
`MustNotDeclareADbContextField` still fails on a reintroduced `DbContext`-typed field.

### A marker that cannot be dated keeps suppressing

`HasMarkerExpired` requires `marker.ReceivedByInboxAtUtc.HasValue` before it compares anything, so a marker
carrying no timestamp is never expired and never re-handled, however the window is configured
(`MustSkipHandlerWhenMarkerHasNoTimestampAndDeduplicationWindowIsSet`). The purge agrees: an undated marker
is never deleted either (`MustNeverPurgeAnInboxMarkerCarryingNoReceivedTimestamp`).

### The disabled default, and its deliberate divergence from the in-memory inbox

Both retention windows on `EntityFrameworkReliabilityOptions` default to `null`, which disables them. A
finite default would change the behaviour of a host already running this package: a late redelivery its
inbox suppresses today would start being handled again the moment its marker aged out. With no window
configured, expiry is unreachable — `HasMarkerExpired` returns `false` before it reads the marker at all —
and every existing marker suppresses however old it is
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

### Accepted residual: two concurrent redeliveries of an expired id both re-run the handler

Both deliveries can call `FindAsync`, both can see the same expired marker, and both can run the handler
before either commits. `InboxMessage`
(`src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Reliability/Inbox/InboxMessage.cs`) declares
`MessageId` and `ReceivedByInboxAtUtc` and no concurrency token, so nothing makes the second refresh lose.
Adding a token is a change to a `Chatter.MessageBrokers` entity that lands as a column in every application's
database, which is a wider change than this one. The residual is reachable only with a window configured and
only for a redelivery arriving outside the window that window was sized for.

This is kin to **#380, which stays OPEN**: the EF inbox is read-then-act on the fresh-id path too, and the
backstop there is the primary key, pinned by
`Integration/WhenDeduplicatingInboxOnSqlServer.MustRejectDuplicateInboxMessageIdAtThePrimaryKeyConstraint`
and `MustPersistExactlyOneInboxRowWhenTwoReceiversRaceForTheSameMessageId`. Claim-first was rejected for the
same reason in both places: claiming an id ahead of the handler requires the inbox to commit its own row,
which is the reverted self-save path that ADR-0006, the `INVARIANT` block at `BrokeredMessageInbox.cs:18-31`,
and the `MustNotDeclareADbContextField` tripwire all exist to prevent. Database side effects stay once-only
through the primary key; only a handler's non-transactional side effects can run twice.

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
states the handler-first order, scopes in-flight reservation to the in-memory inbox, and carries an `_Avoid_`
clause naming `MustInvokeHandlerAndTrackButNotPersistInboxMessageForFreshMessageId` and
`MustInvokeHandlerAndRefreshTheMarkerWhenDeduplicationWindowHasElapsed`
(`tests/UsingBrokeredMessageInbox/WhenReceivingViaInbox.cs`) as the oracles that go red if the order ever
changes — so the claim is pinned to executable behaviour rather than restated as prose.

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

## References

- Issue #382 — inbox rows living forever and a producer-controlled id suppressing a legitimate message; the
  occasion for both the retention purge and this expiry decision.
- Issue #380 (OPEN) — the concurrent-delivery residual this decision narrows but does not close.
- Issue #383 — the cancellation token now threaded through the inbox lookup and insert.
- ADR-0006 — *Two-tier reliability port: relational ambient-tx vs NoSQL stage-then-commit*, source of the
  rule that the relational inbox never commits its own marker.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/BrokeredMessageInbox.cs`
  (`ReceiveViaInbox`, `HasMarkerExpired`, `HasBeenReceived`) — where the expiry decision lives.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/InboxMessageConfiguration.cs`
  — the key declaration that leaves `MessageId` equality to the column's collation.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/ReliabilityRetentionPurgeService.cs`
  (`ExecuteAsync`, `PurgeOnceAsync`) — the hygiene half.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/EntityFrameworkReliabilityOptions.cs`
  — the disabled-by-default windows and the purge interval.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/tests/UsingBrokeredMessageInbox/WhenReceivingViaInbox.cs`
  and `src/Chatter.MessageBrokers.Reliability.EntityFramework/tests/Integration/WhenPurgingRetentionOnSqlServer.cs`
  — the tests named throughout.
