---
status: accepted
date: 2026-09-18
---

# Registration factories open no scope: the component that bounds the graph owns it

The Azure Service Bus `IMessagingInfrastructure` descriptor built its folded receiver/dispatcher factory out of
two delegates that each opened a DI scope, resolved the scoped infrastructure service, and disposed the scope as
they returned — handing the caller an instance whose owning scope was already gone (#376). This is not a new
class of defect in this repository. It is the SAME defect core closed under #314 (epic #297), where the receiver
hosted-service factory disposed its scope before the singleton it built ever used the graph. This ADR records
the Azure Service Bus SPECIALIZATION of the rule core already wrote down, so the remaining sibling modules
inherit ONE shape rather than growing a third.

## Context

**The rule was NOT invented here.** `ChatterMessageBrokerExtensions.AddReceiverImpl`
(`src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/DependencyInjection/ChatterMessageBrokerExtensions.cs`)
carries it as an explicit `INVARIANT:` comment over the hosted-service registration:

> a registration factory must never open a DI scope whose resolved graph escapes the factory delegate — the
> scope, and every scoped member of the graph, would be disposed as the factory returns while the singleton it
> built keeps the already-disposed graph for the process lifetime. The scope belongs to the component whose
> lifetime bounds that graph.

Core discharged it by handing the hosted background service the scope FACTORY, so the component that bounds the
graph creates and disposes its own scope. What #376 reports is that the Azure Service Bus infrastructure
descriptor never inherited that rule: its two delegates kept the `using var scope = scopeFactory.CreateScope();
return scope.ServiceProvider.GetRequiredService<T>();` shape, justified in the comment above them only by
"reproducing the former `ServiceBusReceiverFactory` / `ServiceBusMessageSenderFactory` behavior exactly" — a
statement of provenance, not of correctness.

**Why it did not surface earlier.** Not because the disposal was inert. `ServiceBusReceiver` was registered
`Scoped` and is `IDisposable`, so the factory's scope tracked the very instance it had just created and ran
that instance's `Dispose` as it went out — the caller received an already-disposed receiver. That much was
LIVE, and it is the defect #376 reports.

What was latent is narrower than the disposal, and it is two things. First, the injected graph: every
constructor dependency of both types is registered by this module or by core as a SINGLETON — the shared
`ServiceBusClient`, `ServiceBusOptions`, `MessageBrokerOptions`, `ILogger<>`, `IBodyConverterFactory`
(`ChatterMessageBrokerExtensions`), `IServiceBusMessageSenderFactory` and `ServiceBusReceiverRegistry` — so
the scope tore down nothing the receiver had been injected WITH, and the next scoped dependency added to
either graph, by this module or by a consumer override, would have been stranded too. Second, the `Dispose`
the scope ran was nearly empty AT THAT INSTANT: `_innerReceiver` is not built until `InitializeAsync`, which
runs well after the delegate returns, so there was no AMQP link yet to close, and neither `InitializeAsync`
nor the receive path carried a disposed-state guard that would have refused the already-disposed instance.

What the premature `Dispose` did cost is the receiver's own idempotence: `_disposedValue` was left `true`
before the receiver had been used at all, so any later SYNCHRONOUS `Dispose()` on it was a no-op and could
never close the inner receiver `InitializeAsync` went on to build. Only the `StopReceiver()` /
`DisposeAsync()` path — which closes before it consults that flag — could still release the link. A quiet
leak on one teardown path rather than a fault at startup is why the refused shape survived this long.

**Why the two are not one decision.** `ServiceBusReceiver` is `IDisposable`/`IAsyncDisposable` and is driven
through `InitializeAsync`, `StopReceiver()` and `Dispose` entirely AFTER the factory delegate returns.
`ServiceBusMessageSender` implements `IMessagingInfrastructureDispatcher` and nothing else — it is not
disposable at all, so its scope-disposal was inert. The receiver is what makes #376 a defect; the sender is
fixed alongside it because leaving one delegate in the refused shape leaves the shape.

## The invariant

**A registration factory opens NO scope. Either the resolved graph escapes the delegate — and then the
COMPONENT whose lifetime bounds that graph owns the scope, and is handed the scope factory to create and
dispose it — or the graph has nothing scoped in it, and then it is resolved from the provider the factory was
handed, at the registration lifetime the consumer's actual instance count calls for.**

The two halves are one rule read at two call sites, and which half applies is decided by the GRAPH, not by
taste.

## Considered Options

- **(i) Keep the scope and make it outlive the delegate (REJECTED).** Capturing the `IServiceScope` in the
  factory's closure and never disposing it converts a use-after-dispose into a leak: one scope per broker, held
  for the process lifetime, with no component owning its teardown. It also keeps asserting an ownership the
  factory does not have. Core rejected the same shape under #314 and handed the scope factory to the component
  instead; nothing about Azure Service Bus argues for the opposite answer.

- **(ii) Wrap the resolved receiver in a scope-owning decorator that disposes the scope when the receiver is
  disposed (REJECTED BY CONSTRUCTION, not by preference).** This is the textbook fix and it is unavailable
  here. `BrokeredMessageReceiver` discovers the delivery-release capability with a TYPE TEST —
  `if (_infrastructureReceiver is not IDeliveryReleaseSignal signal) { return; }` in
  `SignalDeliveryReleased` — and that check is itself a stated `INVARIANT:`. Any wrapper that does not
  re-declare `IDeliveryReleaseSignal` SILENTLY fails that test and the core simply never signals release: a
  session receiver's slots are never freed and its lock renewals are never ended, with no error anywhere. A
  wrapper that DOES re-declare it has to re-implement every optional capability this and every future
  infrastructure receiver may declare, and is wrong the moment one is added. The decorator is not rejected for
  being heavy; it is rejected because a correct one cannot be written against a capability surface discovered
  by type test.

- **(iii) Register both types as SINGLETON and resolve once (REJECTED).** `ServiceBusReceiver.InitializeAsync`
  writes the receiver's `ReceiverOptions` onto INSTANCE state, and `MessagingInfrastructure.ReceiveInfrastructure`
  calls `Create()` once per receiver entity. One shared instance would therefore cross-wire entities: the last
  entity to initialize would own the options every earlier one then receives against. Singleton is not merely
  unnecessary here, it is incorrect.

- **(iv) TRANSIENT registration, resolved from the provider the factory was handed, opening no scope
  (ACCEPTED).** Each `Create()` yields a fresh, fully-owned instance with no scope to strand it, which is what
  the caller's per-entity usage already assumes.

## Decision

1. **Both delegates open no scope, and resolve from the provider the descriptor's factory was handed.**
   `sp.GetRequiredService<ServiceBusReceiver>()` and `sp.GetRequiredService<ServiceBusMessageSender>()`
   replace the open-resolve-dispose pairs. The `IServiceScopeFactory` capture is deleted, not retained unused.

2. **`ServiceBusReceiver` and `ServiceBusMessageSender` are registered TRANSIENT.** Transient is the lifetime
   the caller's usage already implies — one instance per receiver entity, each carrying its own initialized
   options — and it is the only one of the three that is correct: singleton cross-wires entities (Option iii),
   and a SCOPED instance cannot be handed out by the singleton `IMessagingInfrastructure` without outliving the
   scope that owns it, which is the defect itself.

3. **The safety of root resolution is a claim about the GRAPH, and it is stated rather than assumed.** Every
   dependency of both transient types is registered as a singleton (enumerated in *Context*), so there is no
   scoped member for a scope to bound. This is what puts Azure Service Bus in the second branch of the
   invariant rather than the first.

### The two branches, and how to tell which one a site is in

- **Branch one — the resolved graph contains anything scoped, or anything a consumer can replace with
  something scoped that the module then depends on.** The COMPONENT whose lifetime bounds the graph owns the
  scope: the factory is handed the scope FACTORY, and the component creates and disposes its own scope. This is
  core's shape at `AddReceiverImpl`, and it is the default answer.

- **Branch two — nothing in the graph is scoped.** Open no scope at all, resolve from the provider the factory
  was handed, and pick the registration lifetime from how many instances the consumer actually needs. Azure
  Service Bus is branch two.

Branch two carries one qualification worth stating, because the tests exercise it directly. A CONSUMER can
re-register a dependency of either graph at `Scoped` — `IBodyConverterFactory` for the receiver,
`IServiceBusMessageSenderFactory` for the sender — and root resolution then either throws
"Cannot resolve scoped service from root provider" under host scope validation, or root-captures the instance
without it. Neither outcome is the #376 defect, which is the point: nothing is handed out already disposed. The
two probe facts `MustNotDisposeAScopedDependencyOfTheReceiverOnReceiveInfrastructureAccess` and
`MustNotDisposeAScopedDependencyOfTheDispatcherOnDispatchInfrastructureAccess` pin exactly that, one level
below the receiver itself: a scoped disposable standing in for a dependency can only be disposed by a scope
that created AND closed it, so its survival is direct evidence no scope was opened.

### Scoped inheritance: the sibling sites this rule reaches, and why they are not fixed here

Both sites below were VERIFIED against the tree as of this decision. They are recorded as bounded residuals —
this is defer-with-scope, and it is the reason this ADR exists at all rather than the reason it is incomplete.

- **`Chatter.MessageBrokers.SqlServiceBroker`
  (`src/Chatter.MessageBrokers.SqlServiceBroker/src/Chatter.MessageBrokers.SqlServiceBroker/DependencyInjection/Extensions.cs`).**
  BOTH delegates of its `IMessagingInfrastructure` descriptor carry the identical scope-open-resolve-dispose
  shape, under a near-verbatim copy of the same "reproducing the former ... behavior exactly" comment Azure
  Service Bus had, and with no stated justification beyond that provenance. `SqlServiceBrokerReceiver` and
  `SqlServiceBrokerSender` are both registered `Scoped`. It carries the same latent use-after-dispose-by-contract
  as #376 — with one difference that makes it STRICTLY less safe rather than more: `ISqlConnectionSource` is
  registered `Scoped` there, so this site's graph genuinely contains a scoped member. It is a BRANCH ONE site,
  not a branch two one, and the Azure Service Bus answer must not be copied onto it verbatim.

- **`Chatter.MessageBrokers.RabbitMQ`
  (`src/Chatter.MessageBrokers.RabbitMQ/src/Chatter.MessageBrokers.RabbitMQ/DependencyInjection/Extensions.cs`).**
  The RECEIVER already moved off the scope shape: it is constructed once per registration from the root
  provider via `ActivatorUtilities.CreateInstance<RabbitMqReceiver>(sp)`, for reasons its own comment states at
  length (ADR-0019). The SENDER still uses the scope shape, with a stated justification — `RabbitMqSender` is
  not disposable, so the disposal is inert — which may make it benign by construction, exactly as the Azure
  Service Bus sender turned out to be. Tracked as issue #371, whose title names both the per-message scope
  creation and the already-disposed-scope resolution.

Neither is fixed in this PR because the scope of this work was Azure Service Bus only, to close epic #307's
remaining Azure Service Bus items, and widening it across two further modules — one of which is a branch ONE
site needing a different fix — was not pre-approved. The bounded impact of leaving them is that each keeps a
delegate asserting an ownership it does not hold; neither is currently handing out a disposed graph, and the
SqlServiceBroker one is the one to look at first.

### Accepted residual: no throw-if-disposed guard on the receiver

**Root cause.** A transient `ServiceBusReceiver` handed out by `ReceiveInfrastructure` can be called after it
has been disposed, and nothing on it says so.

**Why the obvious remediation is REJECTED.** `IDeliveryReleaseSignal.DeliveryReleased` carries a documented
MUST-NOT-THROW contract — `ServiceBusReceiver.DeliveryReleased`'s own remarks state it, and core's
`SignalDeliveryReleased` calls it on teardown paths and swallows `ObjectDisposedException` precisely because
one is expected there. A blanket `ObjectDisposedException` guard across the receiver's surface would therefore
violate a PUBLISHED contract on the one member most likely to be reached after disposal.

**Bounded impact, and what is provided instead.** Rather than a guard, the disposed transition is made
OBSERVABLE: `internal bool IsDisposed => _disposedValue` is the single seam both `Dispose` overloads fold into,
so a test — and a future guard, if one is ever warranted on a member not bound by that contract — can see the
transition landed instead of inferring it. That seam is an UNSYNCHRONIZED read of the same plain `bool` the
transition writes; it is sound for the sequential teardown this library drives and for the tests that assert over
it, and the reason no barrier is added is the separate residual below.

### Accepted residual: the synchronous Dispose path closes the inner receiver unawaited

**Root cause.** `Dispose(disposing: true)` cannot await, so it calls `CloseWithoutAwaiting(_innerReceiver, ...)`,
which starts `CloseAsync()` and attaches a continuation that reports. The close is unawaited-but-OBSERVED:
anything other than a successful close is logged rather than left unobserved.

**CORRECTION — the guarantee this section first claimed was FALSE.** An earlier revision of this ADR, and the
code it described, stated the observation was "TOTAL over the fault shapes a `Task`-returning member permits".
It was not. It ENUMERATED two shapes — a faulted task, and a throw before a task is ever returned — and an
enumeration of two is not the partition it called itself. A `CloseAsync()` returning `null` dereferenced out of
the helper as a `NullReferenceException` that no continuation could ever observe, and a CANCELLED task matched
neither the `OnlyOnFaulted` filter nor the synchronous `catch`, so an abandoned close read as a clean teardown.
The local reviewer found both on the very next pass over the same code.

That is recorded here rather than quietly rewritten, because the failure mode IS the lesson. The earlier fix was
complete-the-known-set wearing closed-by-construction's name: it added the one shape somebody had thought of,
called the result total, and the class re-emitted immediately. It is one instance of the evidence bar ADR-0003
names — a fix that closes an instance without closing the class — met ONCE. One instance is why the answer here
is a structural partition of this one helper, and not the monotonic lifecycle authority three instances bought
there.

**The invariant now, and the three things that hold it.**

1. **A two-region partition with no gap between the regions.** Region one is a single guarded synchronous block
   spanning BOTH obtaining the task and attaching the continuation, with the missing-task case folded in as
   `?? throw` so it is answered by the region rather than by a check that names it. Region two is the
   continuation, attached with `TaskContinuationOptions.ExecuteSynchronously` and NO outcome filter, so it
   DECIDES for every terminal state what to report. Whether that decision is DELIVERED is the residual below.
2. **A one-state ALLOWLIST.** The continuation body returns early only on `TaskStatus.RanToCompletion`. Success
   is the single exemption; every other status falls through to the report, and the status travels in the
   message because a cancelled close carries no exception to travel in.
3. **A non-throwing terminal reporter.** `ReportCloseFailure` swallows a failure of the logger DELIBERATELY. The
   logger IS the channel a close failure is reported through, so a failure of that channel has nowhere left to
   be reported, and an observer that can itself fail would need its own observer without end. That is where the
   regress terminates, and it is what makes the never-throws property hold for the reporting step as well.

*Which class is made impossible, and why.* **"A teardown-close outcome nobody hears about."** The helper names
no outcome it reports — it names the one outcome it does NOT. An outcome that did not exist when this code was
written is therefore already in the reported set, and a new `TaskStatus`, or a new way for the port's
`Task`-returning signature to end badly, needs no new conjunct anywhere. That is exactly what the previous shape
could not say: it had to be extended once per shape somebody remembered.

Because the helper does not throw, both callers' remaining work always runs — nulling the inner receiver and
latching `_disposedValue` on the Dispose path, returning `null` so the next receive rebuilds on the recovery
path. Every production `IServiceBusMessageReceiver` implements `CloseAsync` as an `async` method, which captures
its failure into the returned task, so only the faulted-task ending is reachable through today's three
implementations; the guarantee is stated on the helper rather than inferred from them, because the helper is
what the contract is written on.

**Why both obvious remediations are REJECTED.** Awaiting it means blocking on async work inside `Dispose`, i.e.
sync-over-async on the host-shutdown path, with the deadlock and shutdown-stall failure modes that carries.
Dropping the close instead regresses an AMQP link leak: synchronous `ServiceProvider.Dispose` is now the only
remaining reacher of that branch — every other teardown path goes through `DisposeAsync`, which awaits
`StopReceiver()` first — so removing it would leave those receivers' links and any sessions they hold orphaned
until their locks expire.

**Bounded impact.** The close may not have completed when `Dispose` returns, so a failure is learned from the
log rather than from the caller. That is the whole of it.

### Accepted residual: the reporting continuation may not run before the teardown is gone

**Root cause.** The partition above decides WHICH close outcomes are reported; it does not guarantee the decided
report LANDS. Neither teardown path waits for the deciding continuation, and that continuation fails to run in
two ways. (i) No terminal status is reached — an AMQP link whose teardown hangs past the client's whole retry
budget ends in no status at all, so the continuation never runs. Reporting-by-default reports terminal states; it
does not manufacture one. (ii) A terminal status IS reached, but `TaskContinuationOptions.ExecuteSynchronously`
is a HINT and not a guarantee: the TPL declines to inline when the current thread is not a valid inline location
— a different ambient `TaskScheduler`, or the stack-depth guard — and QUEUES the continuation to
`TaskScheduler.Default` instead, so on the synchronous `Dispose` path the process can exit before the queued
continuation is ever scheduled and a decided report is lost. Cause (ii) is `Dispose`-specific: on the
receive-recovery path the process is still alive, so a queued continuation still runs and still reports.

**Why all four available remediations are REJECTED.**

- **Await it (REJECTED).** The same rejection as above: sync-over-async inside `Dispose` on the host-shutdown
  path. Waiting on a hang converts a missing log line into a stalled shutdown.

- **An observational timeout — `Task.WhenAny(closeAttempt, Task.Delay(threshold))` (REJECTED).** It buys a log
  line, not a bound. `Dispose` already returns immediately and the close is already unawaited, so THERE IS NO
  WAIT TO SHORTEN: the hung link, the sessions it holds and the renewals still running are identical with and
  without the timer. The framing "bounding this needs a sync-over-async stall" is IMPRECISE — a combinator would
  not block anything — but the correct rejection is the stronger one: it bounds nothing. And no threshold works.
  Below the client's own retry budget it fires on slow-but-successful closes, and this module cannot sit above
  that budget because it is consumer-configurable — the `RetryPolicy` configuration section and the fluent
  `WithNoRetry()` / `WithExponentialDelay(...)` calls both set it. Above it, on the Dispose path the process has
  usually exited before the timer fires, so the line is never written at all.

- **A cancellable close — widening `IServiceBusMessageReceiver.CloseAsync` to take a `CancellationToken`
  (REJECTED).** Cancelling an SDK close ABANDONS the attempt rather than completing it, so the AMQP link is left
  exactly as the hang left it and the sessions stay held until their locks expire either way. It buys reporting
  — the same thing the timeout buys — at the cost of widening an internal port and changing its three production
  implementations plus every test double that stands in for it.

- **Wait for the continuation (REJECTED).** The only way to turn the inlining HINT into delivery is to await the
  continuation before teardown returns, which is the sync-over-async await already rejected above: on the Dispose
  path it is the same host-shutdown block, bought for the same single log line.

**Bounded impact — DIAGNOSTIC ONLY.** On the Dispose path the process is exiting and the logging sink is being
torn down alongside it, so a line written at that moment may not survive regardless — which is equally true of a
report the TPL queued rather than inlined. On the receive-recovery path the discard is ALREADY reported one
statement earlier: the `ObjectDisposedException` catch logs
`"Service Bus receiver connection was closed."` at Warning immediately before calling the helper, and the
sessions a hung close orphans stay held at the broker until their locks expire, where they are observable as
held sessions rather than only here. There is no correctness consequence beyond the missing second line.

**Promotion trigger.** An observed hung close in the field, an observed report lost because the continuation was
queued rather than inlined, or any future path that starts WAITING on the close rather than discarding it. Any
of them makes a real bound worth buying instead of a log line, and this residual is PROMOTED then.

### Accepted residual: the receiver's disposal transition is not synchronized

**Root cause.** `ServiceBusReceiver.Dispose(bool)` reads `_disposedValue`, closes the inner receiver, nulls
`_innerReceiver` and latches `_disposedValue` with NO synchronization, while the receive path's
`ObjectDisposedException` recovery captures-and-clears the SAME `_innerReceiver` field under `_syncLock`. The two
do not compose: teardown does not take the lock the recovery path takes, `_disposedValue` is a plain `bool` with
no memory barrier on either side, and `internal bool IsDisposed => _disposedValue` reads it unsynchronized too.
Two genuinely concurrent `Dispose()` calls could therefore both observe `false` and close the same inner receiver
twice, and a teardown racing a recovery could null the field the recovery is mid-way through replacing.

**This is INHERITED, byte-for-byte.** The `if (!_disposedValue) { ...; _innerReceiver = null; _disposedValue =
true; }` transition is unchanged from before this decision; #376's change swapped only what is invoked INSIDE the
`if (disposing)` block. The `IsDisposed` seam is new, but it only READS a field that was already raced.

**Bounded impact.** The racing schedule is not reachable through any path this library drives. Core's
`BrokeredMessageReceiver` disposes the infrastructure receiver once, behind its own lifecycle CAS and teardown
gate, and reaches `DisposeAsync()` — which awaits `StopReceiver()` and then runs `Dispose(disposing: false)`,
taking neither the close branch nor a second close. The .NET Generic Host stops hosted services to completion
BEFORE disposing the provider, so the provider's synchronous dispose of the root-captured receiver is sequenced
AFTER the pump has stopped, not concurrent with it; and a provider disposes each tracked disposable once. The
sequential double dispose that does occur — an explicit dispose followed by provider teardown — is exactly what
the `_disposedValue` guard makes idempotent, and sequential visibility needs no barrier. What is left is an
out-of-band caller disposing the receiver while its pump is still running, which is a misuse of an `internal`
type with no public registration.

**Why the obvious remediation is REJECTED here.** The obvious fix is the one ADR-0003 adopted for
`RabbitMqConnectionSource`: collapse liveness to one monotonic authority advanced by `Interlocked.CompareExchange`
and compose `Dispose`, `DisposeAsync`, `StopReceiver` and the receive-recovery catch through a single gate. That
is the right answer WHEN the class has demonstrated it recurs — ADR-0003 adopted it after three successive
non-closing fixes. It has not here. ADR-0019 rejected the same machinery on `RabbitMqReceiver` (option (i)) for
precisely the reason that applies again: it defends a schedule that is unreachable, duplicates one layer down the
gated-handoff model core's `BrokeredMessageReceiver` already runs ABOVE this type, and carries real regression
risk into the teardown path — the path whose failure orphans AMQP links and session locks. Adding it as an
unforced change inside a PR scoped to a registration defect would put that risk in the least-reviewed place.
Recorded here so the next reader inherits the argument rather than re-deriving it; if a genuinely concurrent
caller ever appears, this residual is PROMOTED to an issue and the ADR-0003 shape is what it should adopt.

### Accepted residual: the transient receiver is root-captured for the provider's lifetime

**Root cause.** `ServiceBusReceiver` is `IDisposable`, and Microsoft DI tracks every disposable transient it
creates against the scope that created it. Resolved from the root provider, that is the root — so each
instance is retained until provider teardown.

**Why it is accepted.** The alternative is the scope this decision exists to remove. The count is bounded to
about one instance per receiver entity, resolved once per `ReceiveInfrastructure` access during startup, not
per message. `StopReceiver()` is one-way and terminal, and the second dispose at provider teardown is
idempotent through the `_disposedValue` guard, so the retained instance costs retention and nothing else.

**Where that bound comes from, stated rather than assumed.** It is a property of the CORE CALL GRAPH, not
something this registration enforces. `MessagingInfrastructure.ReceiveInfrastructure` is a property that calls
`Create()` on EVERY read, exactly as `DispatchInfrastructure` does; what bounds the count is that the only
production reader is `BrokeredMessageReceiver.StartReceiverImpl`
(`src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Receiving/BrokeredMessageReceiver.cs`), which sits
behind a `NotStarted -> Starting` `Interlocked.CompareExchange` that admits one startup per receiver instance,
and one `BrokeredMessageReceiver<TMessage>` is registered per configured entity. Option (iii) above says
"`ReceiveInfrastructure` calls `Create()` once per receiver entity" — read that as the CALLER accessing it once
per entity, which is what the CAS makes true, not as a property of the property.

**The delta this introduces, and why it is still accepted.** Under the scoped shape each access created a
receiver AND disposed it with the scope, so repeated reads retained nothing (they handed back dead instances —
the #376 defect). Under root resolution repeated reads ACCUMULATE. Chatter never makes them, but
`IMessagingInfrastructureProvider.GetReceiver` and `IMessagingInfrastructure.ReceiveInfrastructure` are
reachable API, so a consumer hand-driving them in a loop grows root-tracked disposables for the provider's
lifetime. That is the honest shape of the bound: enforced by how core calls it, not by this site. Bounding it
here would mean caching one receiver per configured entity behind this factory — which is option (ii)'s
ownership problem wearing a different hat, since the cache would then own a lifetime the factory cannot end,
and the factory has no entity key to cache on (it is handed none; `InitializeAsync` supplies the options
AFTER the delegate returns). Rejected on those merits, not on cost.

### Accepted residual: a sender is allocated per routed message

**Root cause.** `MessagingInfrastructure.DispatchInfrastructure` is a PROPERTY that calls
`_dispatchInfrastructure.Create()` on every access
(`src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/MessagingInfrastructure.cs`), so a transient
registration yields a fresh `ServiceBusMessageSender` per routed message.

**Why it is not repaired here, and deliberately NOT re-filed.** The root is in CORE — a property that creates on
every read — not in this module's registration, and it is already tracked there. This decision makes the
allocation VISIBLE by moving the sender from `Scoped` to `Transient`; it does not introduce it, because the
previous shape allocated a scope AND a sender per access. `ServiceBusMessageSender` is not disposable, so the
allocations are collectable and nothing is retained.

## Closed-by-Construction Acceptance Test

> Which class of defect is made impossible, and why?

**"A registration factory hands out a graph its own scope has already disposed."** The class is closed for this
module by DELETION: there is no `IServiceScopeFactory` in either delegate and no scope for a resolved instance
to outlive, so the defect has nothing to be built out of. That is stronger than ordering the disposal
correctly, which would leave the shape in place for the next edit to get wrong.

The two probe facts are what make the claim checkable rather than merely asserted: a scoped disposable planted
on each graph's resolution path SURVIVES the corresponding infrastructure access, and a scoped disposable can
only be disposed by a scope that both created and closed it. `MustYieldUndisposedReceiverFromReceiveInfrastructure`
asserts the same thing at the receiver itself, and `MustRegisterServiceBusReceiverAsTransient` /
`MustRegisterServiceBusMessageSenderAsTransient` pin the lifetime the argument in Decision 2 depends on.

**The honest residual.** Branch two's safety rests on a PROPERTY of the graph — every dependency of both types
is a singleton — not on a type-level guarantee. A future edit that gives either type a genuinely scoped
dependency moves this site into branch one, and nothing in the type system will say so. Decision 3 states the
property explicitly, and the probe facts are positioned one level below the receiver precisely so that a reader
checking it has somewhere to check. That is what makes it checkable, not what makes it unwritable.

## Consequences

- **`ReceiveInfrastructure` and `DispatchInfrastructure` now yield fresh, fully-owned instances.** Per-call
  fresh-instance semantics are unchanged from the scoped-per-call shape they replace — what changes is that the
  instance no longer belongs to a scope that is already gone.
- **One shape for all four folded infrastructure descriptors, once the siblings follow.** Core states the rule,
  Azure Service Bus records its branch-two specialization, RabbitMQ's receiver already sits in branch two by a
  different route (ADR-0019), and SqlServiceBroker remains in branch one and unconverted. A reader now has ONE
  rule and a branch test, instead of three module-local justifications.
- **No public surface changes.** `ServiceBusReceiver` and `ServiceBusMessageSender` are both `internal`; their
  lifetimes are not part of any published contract, and no consumer-visible type, member or behavior changes.
- **A consumer scoped override of a receiver or sender dependency now behaves like any other root resolution.**
  Under host scope validation it throws at first resolution rather than being silently stranded; without
  validation it is root-captured. Both are visible failures of the consumer's own registration, which is an
  improvement on a graph disposed out from under them.

## References

- Issue #376 — *ServiceBusReceiver and sender are resolved from a DI scope that is disposed before use*. The
  defect this decision closes.
- Issue #314 (closed) and epic #297 (closed) — *Receiver hosted-service factory disposes the DI scope before
  the singleton uses its graph*. Where the rule was established, and the `INVARIANT:` comment at
  `ChatterMessageBrokerExtensions.AddReceiverImpl` that states it.
- Issue #371 — *Per-message DI scope creation on the publish path, and sender resolved from an already-disposed
  scope*. The RabbitMQ sender residual recorded above; open, and NOT re-filed by this decision.
- Epic #307 — *Azure Service Bus — no message-lock renewal; session settlement misreports success*. The epic
  whose remaining Azure Service Bus items bounded this PR's scope.
- ADR-0019 — *RabbitMQ receiver dispose is surgical; the container owns the connection source*. Why the
  RabbitMQ receiver left the scope shape by a different route, and why its single root-constructed instance is
  the correct answer THERE and not a template for here. Its option (i) is also the precedent for rejecting a
  receiver-level lifecycle state machine that defends an unreachable schedule.
- ADR-0003 — *`RabbitMqConnectionSource` single monotonic lifecycle authority*. The shape the unsynchronized
  disposal-transition residual would adopt if that class ever demonstrates it recurs, and the evidence bar
  (three successive non-closing fixes) that justified adopting it there.
- `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Receiving/BrokeredMessageReceiver.cs`
  (`SignalDeliveryReleased`) — the `IDeliveryReleaseSignal` type test that rules out the decorator, and the
  MUST-NOT-THROW contract the missing disposed-guard defers to.
- `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/MessagingInfrastructure.cs` — the create-per-access
  properties behind the sender-allocation residual.
- The Azure Service Bus context's *Infrastructure Factory Scope* term
  (`src/Chatter.MessageBrokers.AzureServiceBus/CONTEXT.md`).
