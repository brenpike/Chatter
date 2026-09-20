# Changelog

All notable changes to this project will be documented in this file.

This project follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

### Changed

### Fixed

## [0.9.0] - 2026-09-19

### Added

- `EntityFrameworkReliabilityOptions`, carrying `InboxDeduplicationWindow` and `ProcessedOutboxRetention` (both `TimeSpan?`, null and therefore **disabled by default**) and `PurgeInterval` (default 5 minutes). A finite default would change the behaviour of a host already running this package — a late redelivery its inbox suppresses today would start being handled again the moment its marker aged out — so retention is something an application asks for. This deliberately diverges from the in-memory inbox, whose deduplication window is mandatory.
- `WithReliabilityRetention<TContext>(Action<EntityFrameworkReliabilityOptions>)` — one door covering both tables, so it does not matter which reliability behaviour was registered first. A non-positive `InboxDeduplicationWindow`, `ProcessedOutboxRetention` or `PurgeInterval` is refused with `ArgumentOutOfRangeException` at registration rather than at the first purge. The last call wins; the retention purge itself is an internal hosted service registered once, and this option is the only door to it. A pass issues exactly one bounded `DELETE` per configured table — at most a thousand rows, oldest first — and then returns; rows beyond that chunk wait for the next pass. The chunk size is fixed and not configurable, so a table is reclaimed at no more than a thousand rows per `PurgeInterval` — 288,000 per table per day at the five-minute default — and a table accruing eligible rows faster than that is never drained; `PurgeInterval` is the operator's dial on that rate. Pinned by `WhenPurgingRetentionOverSqlite.MustPurgeAnOutboxBacklogOneBoundedStatementPerPass` and `.MustPurgeAnInboxBacklogOneBoundedStatementPerPass`. A purge pass that throws is logged and the loop waits for the next one, so a `TContext` whose model maps neither entity does not take the host down. An inbox marker with no `ReceivedByInboxAtUtc` is never purged, and an outbox row is eligible only once it carries `ProcessedFromOutboxAtUtc`, so an unprocessed row cannot be deleted while it still has work to do. Each pass opens its scope through `IServiceScopeFactory.CreateAsyncScope` and releases it under `await using`, carrying `ConfigureAwait(false)` like every other await in this package, so a scoped member of the `TContext` graph implementing only `IAsyncDisposable` no longer makes the release throw `InvalidOperationException` after the pass's deletes have run. Pinned by `WhenPurgingRetentionOverSqlite.MustReleaseThePurgeScopeAsynchronously`, which registers an async-only disposable into the scope the pass opens (#308).
- `OutboxMessagePollIndexConfiguration`, an **opt-in** `IEntityTypeConfiguration<OutboxMessage>` indexing `ProcessedFromOutboxAtUtc` then `SentToOutboxAtUtc` — the column both unprocessed-message polls filter on, followed by the one a drain orders by. `OutboxMessageConfiguration` still declares no index, so an application opts in by applying this configuration in its own `OnModelCreating`; doing so is a model change like any other, and the application generates and owns the migration for it. The index is neither filtered nor unique, so it stays provider-neutral.
- `BrokeredMessageOutbox<TContext>(TContext, ILoggerFactory, ReliabilityOptions)` — the constructor the container resolves, capping the poll at `ReliabilityOptions.OutboxPollBatchSize` and refusing a batch size below 1 at construction. The two-argument constructor remains as the uncapped path for a caller that constructs the outbox by hand.
- `BrokeredMessageInbox<TContext>(TContext, ILogger<BrokeredMessageInbox<TContext>>, ReliabilityOptions, EntityFrameworkReliabilityOptions)`. The three-argument constructor remains and delegates to it with retention disabled.

### Changed

- **Breaking.** `UnitOfWork<TContext>.ExecuteAsync` now throws `InvalidOperationException` when the context's execution strategy reports `RetriesOnFailure`. The check runs before the strategy executes and before any transaction begins, so it refuses on every call, including through `BrokeredMessageOutbox<TContext>`'s explicit `IUnitOfWork.ExecuteAsync`. Re-execution was never safe here: the first attempt's `SaveChangesAsync` accepts every tracked change, so a retry after a failed commit saved nothing and committed an empty transaction while the caller saw success. Migration: remove `EnableRetryOnFailure` from that `DbContext`, or stop registering the unit of work for it (`WithUnitOfWorkBehavior`, `WithInboxBehavior`, `WithOutboxProcessingBehavior`). Nothing is lost by refusing — Chatter's recovery pipeline and broker redelivery still retry at their own layers. This supersedes 0.8.0's statement that the release "documents that boundary; it does not detect or work around it": the combination is now detected and refused.
- **Breaking.** Registering the reliability extensions with more than one `TContext` now throws `InvalidOperationException` at registration, naming both contexts. All four entry points — `WithUnitOfWorkBehavior`, `WithInboxBehavior`, `WithOutboxProcessingBehavior` and `WithReliabilityRetention` — bind the context as their first statement, before mutating the service collection, so a refused call leaves the collection exactly as it found it. Previously the last call silently won, leaving the unit of work committing one context while the inbox wrote its marker into another. Migration: name the same `TContext` in every reliability extension call on a pipeline.
- **Breaking.** Transaction handle semantics. `IUnitOfWork.CurrentTransaction` read with no transaction active now returns a handle whose `TransactionId` is `Guid.Empty`, whose disposal is a no-op, and whose `CommitAsync` and `RollbackAsync` throw `InvalidOperationException`. A `PersistanceTransaction` that has been disposed throws `ObjectDisposedException` from `CommitAsync` and `RollbackAsync`, and one can no longer be created around a null transaction. All of these previously threw `NullReferenceException`.
- The unit of work now runs every terminal step through one guarded helper, on both paths — rollback and dispose on failure, dispose on success — under `CancellationToken.None` so that a cancelled operation still rolls back. A cleanup failure is logged at Warning and swallowed: on the failure path the exception that caused the failure is the one rethrown, and on the success path a faulting disposal no longer turns a committed unit of work into a throw, which would have the caller compensate for work that is durable. The guarantee is that no terminal step reports a failure that is not the causal one. It does not resurrect the transaction — a provider whose own disposal aborts still leaves it attached. `SaveChangesAsync` and the commit stay inside the guarded region, so save and commit failures still propagate; `BeginAsync` stays outside it, so a failure to begin propagates untouched.
- `ReceiveViaInbox` and `HasBeenReceived` are window-aware only when `InboxDeduplicationWindow` is configured. A marker older than the window is treated as spent: the handler runs and the marker is refreshed in place within the same transaction, because `MessageId` is the primary key and a second row for the same id would not insert. With no window configured — the default — a marker present still suppresses and a fresh message id still runs the handler and stages a marker, as in 0.8.0. Receive now reads the marker by key rather than testing for existence, and threads the message broker context's cancellation token into the lookup and the insert.
- Inbox suppression now logs at Information with the message id instead of Trace. A message dropped because something else claimed its id is worth knowing about.
- `GetUnprocessedMessagesFromOutbox` now returns at most `ReliabilityOptions.OutboxPollBatchSize` rows, oldest staged first, translated to the database rather than materialising the whole backlog. The query stays **tracked**, and deliberately so: `UpdateProcessedDate` stamps `ProcessedFromOutboxAtUtc` and calls `Update`, and the claim-check that makes claim-before-dispatch safe under concurrency needs both the tracked original value and that property's `IsConcurrencyToken()` mapping in `OutboxMessageConfiguration` — tracking alone would not emit the predicate, and `AsNoTracking` would leave original and current both stamped, matching no row. `GetUnprocessedBatch` is deliberately left uncapped: its caller dispatches one unit of work's staged messages with no re-poll behind it, so a cap there would drop the remainder permanently rather than defer it.
- Every await in this package that can carry `ConfigureAwait(false)` now does — in `BrokeredMessageInbox.cs`, `BrokeredMessageOutbox.cs`, `UnitOfWork.cs`, `PersistanceTransaction.cs` and `ReliabilityRetentionPurgeService.cs`. The one exception is the `await Task.Yield()` that keeps the purge's first pass off the host's startup path: `YieldAwaitable` exposes no `ConfigureAwait` overload at all, so the call is unavailable there rather than omitted.
- Bundled dependency uplift to Chatter.MessageBrokers 0.32.0 (an in-repo `ProjectReference`, so the pack-time package dependency moves with it).

### Fixed

- #381 (this package's half) — the outbox poll materialised every unprocessed row, so one poll dragged the whole backlog into memory, and the shipped schema had no index supporting that poll.
- #382 — inbox markers and processed outbox rows accumulated forever, and because `MessageId` is producer-controlled, a forged or merely reused id suppressed a legitimate message permanently.
- #383 — bare awaits in this package's async paths, and a receive path that ignored the cancellation token its handler context had carried all along.
- #484 — the unit of work saved and committed inside a retrying execution strategy's delegate, so a retry after a failed commit emitted no SQL, committed an empty transaction, and reported success while the writes were gone.
- #485 — a rollback that threw on the failure path replaced the exception that had actually caused the failure. The `await using` disposal did the same, which #485 does not mention.
- #481 — the reliability extensions accepted a different `TContext` per call, leaving the unit of work committing a different `DbContext` than the one holding the inbox marker, with nothing raised.

## [0.8.0] - 2026-09-15

### Removed

- The public `BrokeredMessageOutbox<TContext>.SaveOutboxAsync` member. Migration: callers that relied on it to persist should let the surrounding unit of work commit, or call `DbContext.SaveChangesAsync` themselves.

### Changed

- The enqueue (`SendToOutbox`) and the processed stamp (both `UpdateProcessedDate` overloads) are now stage-only; they no longer save.
- The durability precondition that already governed inbox markers now governs outbox rows as well: an enqueue with no surrounding unit of work is staged, not persisted.
- `UnitOfWork<TContext>.ExecuteAsync` — and `BrokeredMessageOutbox<TContext>`'s explicit `IUnitOfWork.ExecuteAsync` — no longer commit, roll back or dispose a transaction they did not begin. A transaction the unit of work adopts from a caller is participated in (changes are still saved into it) but left open. Migration: a consumer who opened their own transaction on the same `DbContext` around dispatch must now commit or roll it back themselves, on both the success path and the failure path. Previously the library did it for them. Boundary: that adoption path exists only under a non-retrying execution strategy. If you have configured `EnableRetryOnFailure`, EF's execution strategy throws `InvalidOperationException` before the operation runs rather than adopting your transaction — whether it is open on the `DbContext`, enlisted on it, or an ambient `TransactionScope` — identically on `net8.0` and `net10.0`. EF Core's default SQL Server strategy does not retry, so default-strategy consumers are unaffected. This release documents that boundary; it does not detect or work around it.

### Fixed

- #480 — the outbox was a commit-capable participant that is not the unit of work, calling `SaveChangesAsync` from inside the unit of work's execution strategy, so a retrying strategy could silently discard work the handler had already performed.
- The library committing and disposing a caller-owned transaction mid-flight. A consumer who opened a transaction on the same `DbContext` around dispatch had it committed and disposed before their own code resumed.
- A `NullReferenceException` replacing the real `DbUpdateConcurrencyException` when the conflicted outbox row had been concurrently deleted, because the compensating `GetDatabaseValuesAsync` read returned null and was indexed immediately. The original concurrency exception now propagates.
- A failing compensating `GetDatabaseValuesAsync` read no longer replaces the original `DbUpdateConcurrencyException` either. The per-entry resync is best-effort diagnostics: a read that throws is logged and the concurrency exception propagates as the reported cause.

## [0.7.0] - 2026-09-14

### Changed

- `WithUnitOfWorkBehavior`, `WithInboxBehavior` and `WithOutboxProcessingBehavior` now guarantee a fixed resolved order — outbox processing wraps the unit of work, which wraps the inbox — regardless of call order or call count. Migration: consumers who called `WithInboxBehavior` before `WithOutboxProcessingBehavior` previously resolved inbox, outbox, unit-of-work and need no code change. Consumers who register their own behaviours between the reliability extension calls keep their behaviour's slot, but its position relative to the reliability behaviours may differ from before. This guarantee covers only descriptors registered through `WithUnitOfWorkBehavior<TContext>()`, `WithInboxBehavior<TContext>()` and `WithOutboxProcessingBehavior<TContext>()`; later direct `WithBehavior` calls, and closed-generic, factory, keyed, or decorated registrations of these behaviour types, are intentionally outside normalization and are not reordered.
- **Precondition (durability, not ordering):** the ordering above is independent of `TContext`, but the commit-together guarantee is not. The guarantee holds whenever every reliability extension call names the same `TContext`, including a lone `WithInboxBehavior<TContext>()` call, which registers the matching unit of work itself. `IUnitOfWork` resolves to the `TContext` of the last call to any of `WithUnitOfWorkBehavior<TContext>()`, `WithInboxBehavior<TContext>()`, or `WithOutboxProcessingBehavior<TContext>()`; `IBrokeredMessageInbox` resolves to the `TContext` of the last `WithInboxBehavior<TContext>()`. It is void only when a later `WithUnitOfWorkBehavior` or `WithOutboxProcessingBehavior` call names a different `TContext`, leaving the unit of work committing a different `DbContext` than the one holding the marker.

### Fixed

- Reliability behaviours could resolve in an order that depended on call order or call count, rather than the intended outbox-wraps-unit-of-work-wraps-inbox order (#379).
- Keyed registrations of the reliability behaviour types are now skipped before `ServiceDescriptor.ImplementationType` is read. That property throws on a keyed descriptor in `Microsoft.Extensions.DependencyInjection.Abstractions` 8.0.0 and returns `null` from 8.0.2 onward, and that assembly ships inside the ASP.NET Core shared framework, so which of the two a consumer hits follows their host's patch level rather than the restored package version. This repo resolves 8.0.2 and 10.0.x, so no failure was reachable here; the exposure was for consumers on unpatched 8.0.x hosts. The documented guarantee that keyed registrations are outside normalization and are not reordered now holds across the whole supported 8.0.x range instead of only on patched hosts.

No schema change and no migration is required for this release.

## [0.6.1] - 2026-09-14

### Changed

- `BrokeredMessageOutbox<TContext>` now serializes the outbound message context through `Chatter.MessageBrokers`'s public `ChatterJson.Serialize` method instead of reaching into the internal `ChatterJson.Options` field directly. The persisted outbox payload is byte-identical, so existing outbox rows round-trip unchanged and there is no behaviour change for consumers of this package.
- Bundled dependency uplift to Chatter.MessageBrokers 0.31.0 (an in-repo `ProjectReference`, so the pack-time package dependency moves with it). This uplift must be taken together with this release: `Chatter.MessageBrokers` 0.31.0 removes the internal grant this package previously used to reach `ChatterJson.Options`, so an older `Chatter.MessageBrokers.Reliability.EntityFramework` binary composed against `Chatter.MessageBrokers` 0.31.0 or later would fail at runtime. Republishing this package with a `Chatter.MessageBrokers` >= 0.31.0 floor closes that gap.

## [0.6.0] - 2026-09-02

### Changed

- The relational outbox drain this package's `BrokeredMessageOutbox<TContext>` stores rows for is now instrumented: each drain cycle emits a send span, a sent-messages count, and a duration measurement. The drain logic itself lives in `Chatter.MessageBrokers`; this package's own storage and polling behavior is unchanged (#407).
- Bundled dependency uplift to Chatter.MessageBrokers 0.18.0 (an in-repo `ProjectReference`, so the pack-time package dependency moves with it).

## [0.5.0] - 2026-09-01

### Added

- Published packages now ship a symbol package (`.snupkg`), an embedded `README`, a project URL, and are built deterministically. Package builds are now reproducible CI builds with SourceLink-resolvable sources, so a debugger can step into this package's original source from a consuming application.

### Changed

- Conforms to the split core reliability port: `BrokeredMessageOutbox<TContext>` now implements `IPollableOutboxStore` and `BrokeredMessageInbox<TContext>` now implements `IInboxDeduplicator`. Behavior-preserving — the polling and dedup bodies are unchanged; the secondary reliability facets (`IPollableOutboxStore`, `IInboxDeduplicator`) are no longer independent DI registrations; poll consumers obtain them by casting the single resolved primary (`IBrokeredMessageOutbox` / `IBrokeredMessageInbox`) at the consumption site. A custom store must implement both facets on one concrete or the cast throws `InvalidCastException` at the poll site. Split-store is impossible by construction: there is exactly one resolved reliability-store instance per pair; no descriptor inspection, lifetime reconciliation, or fail-fast registration. Requires Chatter.MessageBrokers 0.17.0 (#216).

## [0.4.1] - 2026-06-07

### Changed

- Ported outbox message-context serialization to System.Text.Json via the shared serializer options; persisted wire format unchanged. Dropped the transitive Newtonsoft.Json dependency.

## [0.4.0] - 2026-05-30

### Changed

- Target frameworks migrated from `netstandard2.1;net5.0;net6.0` to `net8.0;net10.0`.
- EF Core version is now target-framework-conditional: `net8.0` uses EF Core 8.0.x and `net10.0` uses EF Core 10.0.x.

### Removed

- Dropped the `net5.0`, `net6.0`, and `netstandard2.1` target-framework monikers. This is a breaking change for consumers pinned to those in-box assets. Consumers on modern runtimes resolve the `net8.0` or `net10.0` asset.
