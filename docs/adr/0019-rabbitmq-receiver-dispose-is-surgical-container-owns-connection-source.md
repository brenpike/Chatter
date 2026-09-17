---
status: accepted
date: 2026-09-16
---

# RabbitMQ receiver dispose is surgical: the receiver never tears down the connection source

`RabbitMqReceiver.Dispose`/`DisposeAsync` used to ESCALATE past `StopReceiver`'s surgical receive teardown to
the connection source's FULL teardown — the `IConnection` and the publish-channel pool — as ADR-0005 section 3
records. `IRabbitMqConnectionSource` is a process SINGLETON the SENDER shares (ADR-0003), so that escalation
made a receiver's dispose a process-wide messaging teardown. This ADR records why the escalation is removed
OUTRIGHT rather than gated more carefully, and what owns the source's lifetime instead.

## Context

Two distinct call paths reached the escalation. Issue #367 reported one of them, and the fix it received
closed only that one.

**The stray consumer scope (#367 as reported).** `RabbitMqReceiver` WAS registered `Scoped`
(`src/Chatter.MessageBrokers.RabbitMQ/src/Chatter.MessageBrokers.RabbitMQ/DependencyInjection/Extensions.cs`)
and publicly resolvable, and the DI container disposes scoped disposables at scope end. So a health check,
or any request-scoped service that merely INJECTED the receiver, tore down process-wide messaging when its
scope ended — sender included. The first fix latched an initialization claim (`_initializationClaimed`, set at
the top of `InitializeAsync`) and gated the escalation on it. That closed the reported case: an instance the
core never drove through startup disposes as a no-op. It left the escalation itself intact for the one
instance that IS claimed. Neither the registration nor the latch survives — option (iv) below records why —
so the Decision states the END state, not that intermediate one.

**The hosted service stopping (the path that survived).** The core hands its hosted service the
infrastructure receiver ITSELF as the subscription it await-usings.
`BrokeredMessageReceiver.StartReceiver(ReceiverOptions, CancellationToken)` returns `Task<IAsyncDisposable>`
and returns `this`, and `BrokeredMessageReceiverBackgroundService` writes
`await using var subscription = await receiver.StartReceiver(_options, stoppingToken)`
(`src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Receiving/BrokeredMessageReceiverBackgroundService.cs:134`).
So the core's Dispose-strength teardown reached `RabbitMqReceiver.DisposeAsync()` on EVERY receiver shutdown,
with the latch SET — and the receiver disposed the singleton. The AMQP connection closed and the publish pool
drained while hosted services that stop LATER (`IHostedService` shutdown runs in reverse registration order)
could still be publishing, faulting their sends with `ObjectDisposedException`. This was a live production
shutdown path, present on `master` as much as on the branch that added the latch; the latch changed nothing
about it, because on this path the latch is exactly what is set.

The sibling adapters already had the shape this ADR adopts. `ServiceBusReceiver.DisposeAsync` runs
`StopReceiver` and disposes its OWN inner `ServiceBusReceiver`, never the shared `ServiceBusClient`
(`src/Chatter.MessageBrokers.AzureServiceBus/src/Chatter.MessageBrokers.AzureServiceBus/Receiving/ServiceBusReceiver.cs:405-419`);
`SqlServiceBrokerReceiver.Dispose` only cancels its own receive loop
(`src/Chatter.MessageBrokers.SqlServiceBroker/src/Chatter.MessageBrokers.SqlServiceBroker/Receiving/SqlServiceBrokerReceiver.cs:471-490`).
RabbitMQ was the sole outlier.

## The invariant

**An adapter `IMessagingInfrastructureReceiver` tears down only the resources it CREATED. A process-scoped
resource shared with the dispatcher belongs to the container that created it, and is disposed there.**

That sentence is the whole decision, and it is stated here once so no future change has to re-derive it from
the lifetimes. The receiver creates a consumer and a delivery buffer; it does not create the connection, the
receive channel, or the publish pool — the source does, and the container creates the source. "This receiver
was the one that started receiving" is a statement about a CONSUMER REGISTRATION. It has never been, and must
not be read as, a claim on the shared source's lifetime.

## Considered Options

- **(i) A four-state CAS-advanced receiver lifecycle machine plus a teardown gate (REJECTED).** The proposal
  was to give `RabbitMqReceiver` its own monotonic `Created -> Initializing -> Initialized -> Disposed`
  authority advanced by `Interlocked.CompareExchange`, serialized by a `SemaphoreSlim(1,1)`, so that the
  escalation could be admitted exactly once against concurrent initialize/stop/dispose schedules. It is
  rejected because it defends a schedule that is unreachable while duplicating, one layer down, the
  startup-owned-local and gated-handoff model the core's `BrokeredMessageReceiver` already runs above this
  type — and it would have carried real regression risk into the connection-release path, which is the path
  whose failure faults the sender. It also answers the wrong question: no amount of admission control over the
  escalation makes the escalation correct, because the resource being escalated to was never the receiver's.
  Once the escalation is gone there is nothing left for the machine to adjudicate.

- **(ii) A receive-registration handle returned by `StartReceivingAsync` (REJECTED).** The proposal was to
  have the source return an opaque registration token that the receiver presents back on stop, so the source
  could compare identities and refuse a stop issued by an instance that is not the current registrant. It
  carries exactly the information the boolean latch already carries — "this instance registered a consumer" —
  and the extra id-compare only discriminates the two-receivers-on-one-source case, which is already rejected
  at DI by `RejectMultipleReceivers` and whose full support is deferred to issue #195. `StopReceivingAsync`
  stays public on the `IRabbitMqConnectionSource` seam either way, because `StopReceiver` calls it. So the
  option breaks a public seam shape for a defect class it does not close.

  Option (iv) settled the same defect class by other means, so those three reasons are re-examined here
  against what was actually built. They did not hold up equally.

  - *Reason 1 — "the same information as the latch" — is CORRECTED.* It is true only of an ADDITIVE handle,
    one presented back alongside a `StopReceivingAsync` that stays callable without it. A handle that
    REPLACES `StopReceivingAsync` is an unforgeable CAPABILITY, and a capability is not the same information
    as a latch. Forgetting a latch check at a new call site COMPILES — that is precisely how `StopReceiver`
    came to run the teardown ungated while the two dispose members were gated. Forgetting a capability does
    not compile. The original wording conflated the two forms and understated the replacement one.
  - *Reason 2 — "the id-compare only discriminates the two-receiver case" — is CONFIRMED, and stronger than
    it was stated.* The id-compare does not discriminate even that case. The source holds ONE
    `_registerConsumer` and ONE `_consumerTag`. A second `StartReceivingAsync` overwrites the delegate under
    the receive gate, and `EnsureReceiveChannelAsync` returns the already-open channel WITHOUT re-running it
    (`src/Chatter.MessageBrokers.RabbitMQ/src/Chatter.MessageBrokers.RabbitMQ/Receiving/RabbitMqConnectionSource.cs:347-352`),
    so `_consumerTag` remains the FIRST receiver's. The second receiver is then the current registrant, its
    id-compare MATCHES, and its stop cancels the first receiver's consumer tag. The handle fixes nothing that
    `RejectMultipleReceivers` and the #195 deferral do not already cover.
  - *Reason 3 — "it breaks a public seam for a class it does not close" — is DECISIVE only for the ADDITIVE
    form*, where `StopReceivingAsync` stays public regardless and the handle is pure addition. The
    REPLACEMENT form is a different proposal and is declined on COST rather than on redundancy: it would edit
    `IRabbitMqConnectionSource.cs` and `RabbitMqConnectionSource.cs`, the in-memory test double
    (`src/Chatter.MessageBrokers.RabbitMQ/tests/Receiving/InMemoryRabbitMqConnectionSource.cs`) and five
    further test files — and every one of those edits is paid on the CONNECTION-RELEASE path, where a
    regression leaks the AMQP connection. Option (iv) closes the same category at the composition layer for
    the cost of one deleted descriptor.

- **(iii) Delete the escalation and leave the source to its owner (ACCEPTED).** Remove both `source.Dispose`
  and `source.DisposeAsync` call sites from the receiver, so dispose becomes the same surgical stop
  `StopReceiver` performs, and let the container that created the singleton dispose it.

- **(iv) Unpublish `RabbitMqReceiver` from the container and construct it at its single call site (ACCEPTED).**
  The DI publication was the ROOT, and options (i)-(iii) all treated it as fixed. `AddRabbitMq` registered the
  CONCRETE type — `AddIfNotRegistered<RabbitMqReceiver>(ServiceLifetime.Scoped)` — for the benefit of exactly
  ONE internal caller: the `IMessagingInfrastructure` factory delegate registered a few lines below it. That
  publication is what created the whole CATEGORY of unentitled instances. Any scope could resolve the type,
  and every resolved instance was a scoped `IDisposable` the container would dispose at scope end, so every
  such instance was a dispose the receiver's teardown path had to survive. The latch was the per-instance
  patch for that category, applied SITE BY SITE — which is exactly why `StopReceiver` was left ungated and
  still compiled: nothing in the type system says a new path into the teardown owes a latch check. Deleting
  the descriptor deletes the category. The factory delegate constructs the instance itself
  (`ActivatorUtilities.CreateInstance<RabbitMqReceiver>(sp)`) and closes over it, so every `Create()` hands
  back the same one, and the type is `internal`, so no consumer can construct one either.

## Decision

Adopt options (iii) and (iv):

1. **The receiver never disposes the connection source.** Both call sites are DELETED from
   `RabbitMqReceiver`, along with the `is IDisposable` cast the synchronous path used to prefer the source's
   synchronous teardown. No path out of the receiver reaches `IRabbitMqConnectionSource.Dispose`/`DisposeAsync`.

2. **Dispose is an UNCONDITIONAL surgical stop. There is no registration latch.** `Dispose`, `DisposeAsync`
   and `StopReceiver` all run the identical unconditional pair — `StopReceivingAsync(CancellationToken.None)`,
   then complete the delivery buffer — byte for byte the work ADR-0005 sections 2 and 3 define, which
   deliberately leaves the connection, the publish pool, the gates and the source's `_lifecycle` untouched.
   Whether a stop has work to do is the SOURCE's question, not a per-instance flag's: `StopReceivingAsync`
   reads and clears `_registerConsumer` and `_consumerTag`, and a stop issued when nothing was ever registered
   finds neither and no-ops. It is gate-serialized and idempotent, so stop-then-dispose and double-dispose are
   clean no-ops. Terminal either way: a disposed receiver does not restart.

3. **The container owns the source's lifetime.** `AddRabbitMq` registers the source by implementation TYPE —
   `AddIfNotRegistered<IRabbitMqConnectionSource, RabbitMqConnectionSource>(ServiceLifetime.Singleton)` — so
   the root provider constructs it and the root provider disposes it at host shutdown. That is what releases
   the AMQP connection, and it happens after every hosted service has stopped rather than when the first
   receiver stops. No receiver scope participates: under option (iv) the messaging-infrastructure factory
   opens none, it constructs the single receiver at its own composition site from the root provider.

4. **A consumer-registered INSTANCE is owned by that consumer.** The registration is `AddIfNotRegistered`, so
   a caller who registers their own `IRabbitMqConnectionSource` wins. If they register a pre-built INSTANCE,
   nothing in Chatter disposes it — the receiver does not, and the container does not either (see below).
   Registering by type or by factory hands ownership to the container instead.

5. **The old latch's positional constraint is DISSOLVED, not relocated.**
   `RabbitMqConnectionSource.StartReceivingAsync` stores `_registerConsumer` BEFORE it calls
   `EnsureReceiveChannelAsync`
   (`src/Chatter.MessageBrokers.RabbitMQ/src/Chatter.MessageBrokers.RabbitMQ/Receiving/RabbitMqConnectionSource.cs:205-223`),
   so an initialization that FAULTS mid-registration still leaves the delegate stored on the source, and only
   a stop clears it (ADR-0005 section 2). A latch was therefore correct ONLY at the top of `InitializeAsync`:
   set below the startup gates, it would leave a faulted startup disposing without stopping, stranding a
   stored delegate that a later connection recovery re-runs — re-registering a consumer that writes into a
   completed buffer, which is precisely the class ADR-0005 closed. An UNCONDITIONAL stop has no position to
   get wrong. Dispose clears the stored delegate regardless of where the fault landed, so the defect the
   latch's placement defended against cannot be reintroduced by moving anything. Deleting the latch is
   therefore SAFER than keeping it correctly positioned, not merely simpler: a latch's correctness rests on a
   placement a future edit can silently break, and an unconditional stop's does not.
   `MustStopReceivingWithoutDisposingConnectionSourceWhenInitializationThrew` pins the faulted-startup case
   (stop count 1, dispose count 0) and
   `MustNotReRegisterConsumerOnRecoveryAfterDisposingAnUninitializedReceiver` pins the recovery consequence
   (`src/Chatter.MessageBrokers.RabbitMQ/tests/Receiving/UsingRabbitMqReceiver/WhenDisposingReceiver.cs`).

### Why the registration-shape assertion is not redundant with the lifetime assertion

`MustDisposeContainerCreatedConnectionSourceWithRootProvider`
(`src/Chatter.MessageBrokers.RabbitMQ/tests/DependencyInjection/UsingExtensions/WhenAddingRabbitMq.cs:116-138`)
asserts three things about the descriptor — `Lifetime` is `Singleton`, `ImplementationType` is
`RabbitMqConnectionSource`, `ImplementationInstance` is `null` — before it asserts that `provider.Dispose()`
leaves the resolved source throwing `ObjectDisposedException`. The last two are not restating the first.

`Microsoft.Extensions.DependencyInjection` disposes only the instances it CREATED. A descriptor registered
with an `ImplementationInstance` is never disposed by the provider, whatever its lifetime, because the
provider did not construct it. So the lifetime alone does not establish container ownership — the
implementation SHAPE does, and a refactor to an instance registration would silently hand ownership back to
the caller while every lifetime assertion still passed. That is also why pre-existing spy-based tests in this
suite, which swap a fake source in via an instance registration to observe what the receiver calls, can never
observe container disposal: their registration shape excludes it by construction. The ownership proof needs
its own test, over the real registration, and that is what this one is.

## Closed-by-Construction Acceptance Test

> Which class of defect is made impossible, and why?

**"A receiver's dispose tears down messaging the SENDER is still using."** It is closed by construction rather
than merely unobserved: there is no code path from `RabbitMqReceiver` to `IRabbitMqConnectionSource.Dispose`
or `DisposeAsync` at all. The two call sites are gone, not guarded, so the defect has no site to reappear at
under a different schedule or a different caller — the receiver cannot express the
teardown. The previous fix guarded WHEN the escalation ran and therefore had to be right about every schedule
that reaches it; this one removes the capability, so no schedule can reach it.

What replaces the escalation is not a weaker guarantee about connection release but a different owner for it.
The connection is still released deterministically at shutdown — by the root provider disposing the singleton
it created, which is the point in the shutdown sequence AFTER every hosted service has stopped publishing.

## Closed-by-Construction Acceptance Test: the eliminated instance category

> Which class of defect is made impossible, and why?

**"A `RabbitMqReceiver` instance that did not register this consumer exists."** That category is what every
per-instance latch check existed to survive, and option (iv) eliminates the category instead of the symptom.
With no descriptor, no consumer scope can obtain a receiver: `GetService` returns `null` and
`GetRequiredService` throws, pinned by `MustNotResolveTheReceiverFromAConsumerScope`, and the absence of the
descriptor itself by `MustNotRegisterTheReceiverInTheContainer`. The factory's `Create()` returns the one
captured instance every time, pinned by `MustHandTheCoreTheSameReceiverInstanceOnEveryCreate`, so reading
`IMessagingInfrastructure.ReceiveInfrastructure` cannot mint a second one
(`src/Chatter.MessageBrokers.RabbitMQ/tests/DependencyInjection/UsingExtensions/WhenAddingRabbitMq.cs`).

**The honest residual.** The composition layer makes such an instance UNOBTAINABLE, not HARMLESS IF
CONSTRUCTED. `internal` narrows construction to one in-assembly site; it does not make the constructor
unreachable. A future edit inside this assembly could construct a second receiver and the compiler would
accept it, and the test assembly constructs receivers directly today through `InternalsVisibleTo` — that is
how the dispose tests drive dispose at all. What holds is narrower and should be claimed no wider: no
CONSUMER can obtain an instance, the module constructs exactly one at exactly one site, and
`RejectMultipleReceivers` fails registration when more than one RabbitMQ receiver is discovered. The
unconditional stop of decision 2 is what makes a hypothetical extra instance harmless, and it is stated as
correct-by-totality rather than as a guarantee that no such instance can be written.

## Consequences

- **The connection is released later in shutdown, and that is the fix.** Previously the first receiver to stop
  closed it; now the root provider does, once. Anything that depended on the receiver's dispose to close the
  connection early was depending on the defect.
- **A caller-supplied `IRabbitMqConnectionSource` INSTANCE is now nobody's but the caller's.** The receiver's
  escalation used to dispose it incidentally. This is a behaviour change for that registration shape and is
  recorded in the module CHANGELOG under `Changed`, with the migration path: dispose it yourself, or register
  by type or factory so the container owns it.
- **`RabbitMqReceiver` is no longer container-registered and no longer publicly visible.** The `Scoped`
  descriptor is deleted and the type is `internal`. A consumer that resolved or referenced the concrete
  receiver no longer compiles or resolves; the supported surfaces are `IMessagingInfrastructure` and the
  `IRabbitMqConnectionSource` seam. Recorded in the module CHANGELOG.
- **The long-lived receiver scope is gone.** The infrastructure factory used to open a scope that lived for
  the singleton infrastructure's lifetime purely so the `Scoped` receiver would outlive the factory delegate.
  With the receiver constructed directly there is no such scope, and therefore no scope that is created and
  never disposed. Root construction is legal only because every receiver constructor dependency is a
  SINGLETON, which `MustConstructTheReceiverFromTheRootProviderUnderScopeValidation` pins under
  `ValidateScopes`.
- **`StopReceiver` and dispose now do the same work.** They are deliberately not collapsed into one member:
  the seam requires both, `StopReceiver` is callable without disposing, and `StopReceivingAsync`'s idempotence
  makes the overlap free.
- **The synchronous `Dispose()` blocks on the async stop** (`GetAwaiter().GetResult()`). The source awaits
  with `ConfigureAwait(false)` throughout so there is no captured context to deadlock against, and the core
  sets the same precedent in its own `Dispose(bool)`. In production the receiver is always disposed through
  `DisposeAsync`; the synchronous path exists for a synchronous container teardown only.
- **There is no `_initializationClaimed` latch.** The field and both of its dispose-path checks are deleted.
  Any future guard proposed for this teardown should first be read against option (iv): the question it
  answers — "did THIS instance register the consumer?" — is one no surviving path has to ask, because there
  is one instance and the source already knows its own registration state.
- **`RejectMultipleReceivers` is unchanged and still needed.** Not for lifetime reasons any more — the source
  holds ONE receive channel and ONE consume-registration delegate (ADR-0002, ADR-0005), so two receivers would
  still contend over them, and as option (ii) records an identity handle would not save them. Multi-receiver
  support stays deferred to issue #195.
- **Teardown quiescence stays deferred to issue #494.** The stop cancels the consumer and then completes the
  delivery buffer, which does not un-park a `BufferDeliveryAsync` write already blocked on a full bounded
  buffer; completion faults that write. Under `TransactionMode.None` (autoAck) the broker has already removed
  that delivery, so it is LOST — at-most-once, which is what that mode documents. Every other mode keeps
  manual ack and the delivery is redelivered. The ordering is deliberate; #494 tracks the quiesce.

## References

- ADR-0005 (RabbitMQ receiver teardown: terminal, surgical consumer-cancel + receive-channel dispose) —
  superseded IN PART by this ADR: its section 3 recorded the dispose escalation this decision removes.
  Everything else it records — consumer-tag ownership in the source, the surgical `StopReceivingAsync`
  contract, the prefetched-unacked redelivery rule — is preserved and is what dispose now runs.
- ADR-0003 (RabbitMqConnectionSource single monotonic lifecycle authority) — the shared-singleton ownership
  this decision stops violating, and the source-side teardown the container now invokes exactly once.
- ADR-0002 (RabbitMQ receive-channel epoch lifecycle) — the recovery re-registration that made a latch's
  position load-bearing, and that an unconditional stop makes moot.
- Issue #367 — *Disposing a scoped `RabbitMqReceiver` tears down the singleton connection source*. Reported
  the stray-scope path; this ADR closes the hosted-service path the first fix left open, and option (iv)
  closes the stray-scope path at its root rather than at each teardown site.
- Issue #195 — *Multiple RabbitMQ receivers on one connection source*. The deferral that makes a
  registration-identity handle unnecessary.
- Issue #494 — the deferred teardown quiescence: a parked buffer write faulted by completion, and the
  at-most-once loss that follows under `TransactionMode.None`.
- Core `BrokeredMessageReceiver.StartReceiver` / `BrokeredMessageReceiverBackgroundService` — the
  `return this` + `await using` pair that makes adapter dispose a routine shutdown event rather than a
  process-end event.
