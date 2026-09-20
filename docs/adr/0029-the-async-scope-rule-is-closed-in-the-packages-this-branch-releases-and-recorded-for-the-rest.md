---
status: accepted
date: 2026-09-19
---

# The async-scope rule is closed in the packages this branch releases, and recorded for the rest

A dependency-injection scope opened inside an asynchronous method and released SYNCHRONOUSLY refuses a scoped
member of its graph that implements only `IAsyncDisposable`: the release throws `InvalidOperationException`
instead of disposing it. `Chatter.MessageBrokers` had already written that rule down and followed it at one
site while violating it at three others; `Chatter.MessageBrokers.Reliability.EntityFramework` violated it at
one. All four are closed. Sites outside those two packages are NOT closed, they are recorded here, and they
do not all share one root — the census below corrects a framing that treated them as a single group.

## Context

**The rule was already stated in-package, and already followed.**
`BrokeredMessageReceiverBackgroundService.ExecuteAsync` carries it as an `INVARIANT:` comment at
`src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Receiving/BrokeredMessageReceiverBackgroundService.cs:112-114`
— *"The release is asynchronous because a scoped member of the graph may implement only IAsyncDisposable"* —
and discharges it at `:115` with `await using var scope = _serviceScopeFactory.CreateAsyncScope();`. The four
sites closed on this branch were therefore a COMPLIANCE gap against a rule their own package had written,
not an open design question. Nothing below decides what the rule is.

**The four sites, closed, each with the fact that would refute it.** Exclusivity was established by reverting
each site one at a time and observing that only its own fact reddened — by execution, not by argument.

| Site | Commit | Fact that reddens on reversion |
| --- | --- | --- |
| `Chatter.MessageBrokers/.../Receiving/ScopedReceivedMessageDispatcher.cs:22` | `1af08d5` | `UsingScopedReceivedMessageDispatcher/WhenDispatching.MustReleaseTheDispatchScopeAsynchronously` |
| `Chatter.MessageBrokers/.../Recovery/CriticalFailureEventDispatcher.cs:26` | `1af08d5` | `UsingCriticalFailureEventDispatcher/WhenNotifying.MustReleaseTheNotificationScopeAsynchronously` |
| `Chatter.MessageBrokers/.../Reliability/Outbox/BrokeredMessageOutboxProcessor.cs:147` | `1af08d5` | `UsingBrokeredMessageOutboxProcessor/WhenSendingOutboxMessages.MustReleaseTheDrainScopeAsynchronously` |
| `Chatter.MessageBrokers.Reliability.EntityFramework/.../ReliabilityRetentionPurgeService.cs:129-130` | `f2a03c1` | `UsingReliabilityRetentionPurgeService/WhenPurgingRetentionOverSqlite.MustReleaseThePurgeScopeAsynchronously` |

The first three are `await using var scope = …CreateAsyncScope();`. The fourth captures the scope into a local
and releases it through `await using var purgeScopeRelease = purgeScope.ConfigureAwait(false);`, declared
BEFORE the `TContext` resolve so a resolve that throws still releases the scope. The outbox fact asserts that
no error is LOGGED rather than that nothing is thrown, because that site catches its own disposal failure — a
throw assertion there would have been a test that cannot fail. All four drive the failure through one shared
double, `tests/.../Support/AsyncOnlyDisposableScopedService.cs`.

**What the branch releases.** `git diff --name-only master..HEAD` over 22 commits touches exactly two packages:
`Chatter.MessageBrokers` and `Chatter.MessageBrokers.Reliability.EntityFramework`. Every remaining site sits in
a package this branch does not touch, so correcting one means opening, validating, changelogging and versioning
a third package inside a review loop that is about the relational tier.

### The census, and the correction it carries

`grep -rn "CreateScope(" --include=*.cs src | grep -v '/tests/'` returns SEVEN sites. An earlier framing of this
finding described them as two-plus-five under two roots, and attributed the five to ADR-0022. That is wrong on
both the split and the attribution. The seven fall into THREE shapes, and ADR-0022 homes three of them, not five.

**Shape one — the scope is a local of an ASYNCHRONOUS method and its graph does not escape. `CreateAsyncScope`
under `await using` IS the fix. Three sites.**

- `src/Chatter.SqlChangeFeed/src/Chatter.SqlChangeFeed/ChangeFeedReceiver.cs:39`, in
  `ChangeFeedReceiver<TRowChangeData>.DispatchReceivedMessageAsync` (`:35`).
- `src/Chatter.MessageBrokers.SqlServiceBroker/src/Chatter.MessageBrokers.SqlServiceBroker/Receiving/SqlServiceBrokerReceiver.cs:368`,
  in `SqlServiceBrokerReceiver.DeadletterMessageAsync` (`:342`) — the settlement path, NOT the receive-dispatch path.
- `src/Chatter.SqlChangeFeed/src/Chatter.SqlChangeFeed/DependencyInjection/SqlChangeFeedExtensions.cs:149`, in
  `UseChangeFeedSqlMigrationsAsync(this IServiceProvider, Type, CancellationToken)` (`:147`).

**Shape two — the same shape in a SYNCHRONOUS public method, where `await using` is not available at all. One
site.** `SqlChangeFeedExtensions.cs:108`, in the `[Obsolete]`
`UseChangeFeedSqlMigrations(this IServiceProvider, Type, CancellationToken)` (`:106`), which returns
`IServiceProvider` and cannot await. Applying shape one's fix here requires either a signature change on a
public extension method or a sync-over-async release — a DIFFERENT decision, with a compatibility surface shape
one does not have.

**Shape three — a REGISTRATION FACTORY whose resolved graph escapes the delegate. `CreateAsyncScope` is NOT the
fix and these must not be converted. Three sites, already homed in ADR-0022.**

- `src/Chatter.MessageBrokers.SqlServiceBroker/.../DependencyInjection/Extensions.cs:48` and `:53` — both
  delegates of the `IMessagingInfrastructure` descriptor, resolving `SqlServiceBrokerReceiver` and
  `SqlServiceBrokerSender`, each registered `Scoped` at `:30-31`.
- `src/Chatter.MessageBrokers.RabbitMQ/.../DependencyInjection/Extensions.cs:99` — the SENDER delegate only; the
  RabbitMQ receiver already moved off this shape to `ActivatorUtilities.CreateInstance` (ADR-0019).

ADR-0022 names exactly these three as verified bounded residuals, records SqlServiceBroker as the branch-ONE
site needing the scope-factory handoff, and tracks the RabbitMQ sender as issue #371. Their defect is that the
delegate returns an instance whose owning scope is already gone; making the release asynchronous would dispose
the same graph at the same point, just awaited. **Converting them would preserve the defect and remove the
evidence of it.**

### Bounded impact, verified at each shape-one site rather than inherited

Reachable only when the scope tracks a service the CONSUMER registered `Scoped` that implements only
`IAsyncDisposable` — nothing Chatter registers into these graphs does. What happens then:

- **`ChangeFeedReceiver.DispatchReceivedMessageAsync`.** The release runs after every change in the payload has
  been dispatched. The `InvalidOperationException` escapes into
  `BrokeredMessageReceiver.ProcessMessageAsync`'s `_recoveryStrategy.ExecuteAsync` wrapper
  (`BrokeredMessageReceiver.cs:1074-1079`), and on exhaustion into `ProcessReceivedMessageWorkerAsync`'s generic
  processing ladder at `:889`, which logs `"Error processing brokered message"` at Error and then nacks or, past
  `MaxReceiveAttempts`, deadletters. **The delivery is redelivered or dead-lettered, never dropped** — no
  message and no data is lost. The cost is a delivery whose handlers already ran being retried, which the
  receive path is at-least-once about regardless.
- **`SqlServiceBrokerReceiver.DeadletterMessageAsync`.** The `using var scope` is declared inside the method's
  `try`, so its release runs AFTER `session.CommitAsync` and after `return SettlementResult.Settled()` has been
  evaluated. A throw there discards that result and propagates into `TrySettleWithRecoveryAsync`
  (`BrokeredMessageReceiver.cs:1120-1125`), **the single place a settle-path failure is swallowed into a return
  value**, which logs `"Unable to deadletter message"` at Error and returns `SettlementResult.Failed`. The
  dead-letter write itself already committed, so the message is in the dead-letter queue; only the REPORTED
  outcome is wrong. Because the result is not `IsSettled`, the `deliveryCount >= MaxReceiveAttempts` branch
  (`:907`) skips `TryExecuteFailedRecoveryAction` at its `deadletterResult.IsSettled` guard (`:916`), so the
  wrong report does not produce a second Error Queue copy.
  Again no loss.
- **`UseChangeFeedSqlMigrationsAsync`.** The release runs after `InstallSqlDependencies` has completed. The
  throw surfaces to the host's own startup/migration caller. No message is in flight; the SQL objects the call
  installed are already installed.

## Considered Options

### Option A — close the rule in the two packages this branch releases, record the rest (ACCEPTED)

The four in-scope sites are fixed with their own facts. The remaining seven are recorded here, split by the
shape that determines their fix, with the promotion trigger named below.

### Option B — fix every shape-one and shape-two site now (REJECTED)

The three shape-one sites and the one shape-two site live in `Chatter.SqlChangeFeed` and
`Chatter.MessageBrokers.SqlServiceBroker`. Touching either means this pull request ships a package whose
validation, changelog and version story is about neither the relational tier nor the async-scope rule. The
shape-two site is worse than out of scope: it is a compatibility decision on a public `IServiceProvider`
extension method, which this delegation does not carry.

### Option C — convert all seven to `CreateAsyncScope` (REJECTED, and it would have been a defect)

Shape three is not this root. ADR-0022 establishes that a registration factory must open NO scope; awaiting the
release of a scope that must not exist leaves the caller holding the same disposed graph.

### Option D — file GitHub issues for the remaining sites (REJECTED)

No issue is filed. ADR-0022 already tracks shape three, including issue #371 for the RabbitMQ sender, and this
record carries more verification than a restatement would. Filing was also explicitly excluded from this work.

## Decision

**Close the rule inside `Chatter.MessageBrokers` and `Chatter.MessageBrokers.Reliability.EntityFramework`.
Record the seven remaining sites, split by shape. Change no code outside the two released packages, and convert
no shape-three site.**

Three properties make recording sufficient here:

- **The rule is not in dispute and is not new.** It was in-package prose before this branch, at
  `BrokeredMessageReceiverBackgroundService.cs:112-114`, and every fix on this branch matches the shape that
  comment already described.
- **Every remaining site degrades to a logged Error with no message and no data loss**, established at each
  site above by following the actual escape path to the handler that catches it, not by generalising from the
  four that were fixed.
- **The remaining sites do not share one fix.** A single "convert the rest" delegation would have been wrong at
  four of the seven. The split is the part of this record that has to survive.

## Closed-by-Construction Acceptance Test

> What class of future finding does this make impossible, and why?

**The class is eliminated inside the two packages this branch releases, and nowhere else.** The claim is
grep-refutable rather than a list of cases:

```
grep -rn "CreateScope()" src/Chatter.MessageBrokers/src src/Chatter.MessageBrokers.Reliability.EntityFramework/src
```

returns NOTHING. Any future reintroduction of a synchronous scope release in either package's source has to
reintroduce that call, so it shows up in that one command rather than in a review that happens to look. Outside
those two package sources the command still matches, and the same finding can be raised again — with, from now
on, a recorded answer and the shape it belongs to.

The four fixed sites additionally cannot regress silently, because each one has a fact that reddens for it
alone: reverting any one of the four fails exactly one test.

## Consequences

- **Promotion trigger.** The next pull request that touches `Chatter.SqlChangeFeed` or
  `Chatter.MessageBrokers.SqlServiceBroker` for its own reasons should close that package's shape-one sites in
  ONE change, matching `ScopedReceivedMessageDispatcher.cs:22`, and give each one a fact of its own. Shape two
  (`SqlChangeFeedExtensions.cs:108`) is NOT part of that change: it needs a decision about a public synchronous
  extension method first. Shape three moves only under ADR-0022 and #371.
- **A "convert the next file" delegation is the cluster shape and should not be dispatched narrowly.** Three
  shape-one sites share one fix framing across two packages; a per-file patch is by construction an invitation
  to the identical finding against the next file, and it would also invite the shape-three conversion this ADR
  refuses.
- **No version impact from this ADR.** It is prose, is not packaged, and no packaged artifact changed for it.
  The four code fixes it records were versioned with their own commits.
- **ADR-0022 and this ADR now partition the finding rather than overlap it.** ADR-0022 owns the escaping-graph
  root; this one owns the synchronous-release root and states, explicitly, that the two fixes are not
  interchangeable.

## References

- ADR-0022 — *Registration factories open no scope: the component that bounds the graph owns it*. Owns shape
  three; names the SqlServiceBroker pair and the RabbitMQ sender as verified residuals and tracks #371.
- ADR-0019 — *RabbitMQ receiver dispose is surgical: the container owns the connection source*. Why the
  RabbitMQ receiver is constructed unregistered and is not one of the seven.
- ADR-0027 — *Invariant prose names the oracle that falsifies it*. Why each behavioural claim above names the
  fact that would refute it.
- ADR-0030 — *The Cosmos tier contrast names the wrong relational symbol*. The sibling residual recorded on this
  branch; same defer-with-scope mechanism, different finding.
- `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Receiving/BrokeredMessageReceiverBackgroundService.cs`
  (`:112-115`) — where the package stated the rule and followed it before this branch.
- `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Receiving/BrokeredMessageReceiver.cs`
  (`ProcessReceivedMessageWorkerAsync` `:844` and its processing ladder `:889`, the deadletter branch `:907-916`,
  `ProcessMessageAsync` `:1049` and its dispatch recovery wrapper `:1074-1079`, `TrySettleWithRecoveryAsync`'s
  single settle-path swallow `:1120-1125`) — the handlers that bound the impact of the two receiver sites.
- `src/Chatter.SqlChangeFeed/src/Chatter.SqlChangeFeed/ChangeFeedReceiver.cs` (`:39`),
  `src/Chatter.MessageBrokers.SqlServiceBroker/src/Chatter.MessageBrokers.SqlServiceBroker/Receiving/SqlServiceBrokerReceiver.cs`
  (`:368`),
  `src/Chatter.SqlChangeFeed/src/Chatter.SqlChangeFeed/DependencyInjection/SqlChangeFeedExtensions.cs`
  (`:149` shape one, `:108` shape two) — the four sites this branch does not reach that share the synchronous-release root.
- `src/Chatter.MessageBrokers.SqlServiceBroker/src/Chatter.MessageBrokers.SqlServiceBroker/DependencyInjection/Extensions.cs`
  (`:48`, `:53`),
  `src/Chatter.MessageBrokers.RabbitMQ/src/Chatter.MessageBrokers.RabbitMQ/DependencyInjection/Extensions.cs`
  (`:99`) — the three shape-three sites, which must not be converted.
- `src/Chatter.MessageBrokers/tests/Support/AsyncOnlyDisposableScopedService.cs` — the async-only-disposable
  double all four facts drive the refusal through.
