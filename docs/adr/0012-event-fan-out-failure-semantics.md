---
status: accepted
date: 2026-09-10
---

# Event fan-out: abort on the first failing handler, documented rather than aggregated

`EventDispatcher` is the `IDispatchMessages` implementation behind an **Event** dispatch. It resolves every
`IMessageHandler<TMessage>` registered for the event and awaits each one in turn inside a single `try`,
whose `catch (Exception)` logs once and rethrows unchanged. The first handler that throws therefore ends
the dispatch, and every handler behind it in the resolution order is never invoked.

Issue #331 observes — correctly — that `src/Chatter.CQRS/src/README.md` said an event is "fanned out to
**all** registered `IMessageHandler<TEvent>` handlers" and then said nothing whatever about failure, so a
reader could fairly take "all" as a promise that every handler runs regardless of what any one of them
does. The code has never behaved that way. The question this ADR settles is not which behaviour is
possible, but which of the two — the loop or the sentence — is the one that is wrong.

## Considered Options

- **Option 1 — Continue past a failing handler, collect the faults, and rethrow: a single fault unchanged,
  two or more wrapped in an `AggregateException`.** This is what the implementation plan for #331
  recommended, and it is not a foolish shape: it would make one subscriber's failure local to that
  subscriber, so an event with three handlers could not lose two of them to the first one's bug. It is
  rejected on the evidence below, and on the costs recorded under *What Option 1 would have cost*.

- **Option 2 — Keep abort-on-first-failure, and correct the documentation that promised otherwise
  (CHOSEN).** No production code changes anywhere. Its weakness is honest and is stated plainly in the
  decision: *which* sibling handlers get skipped follows registration order, and an application that has
  not deliberately ordered its registrations gets an order it did not choose.

## Prior art

The plan's recommendation was overruled by a survey of comparable .NET libraries. The survey does not
decide the question by popularity; it decides it by showing that three of the five buy subscriber
independence somewhere else entirely — at the transport — that the one library built on Chatter's exact
shape, an in-process fan-out with no transport underneath, ships abort-on-first-failure anyway, and that the
one library which does aggregate in-process pays a price Chatter would not want.

- **MediatR** — the default notification publisher is `ForeachAwaitPublisher`, selected by `Mediator`'s
  own single-argument constructor (`: this(serviceProvider, new ForeachAwaitPublisher())`). Its `Publish`
  body is `foreach (var handler in handlerExecutors) { await handler.HandlerCallback(notification,
  cancellationToken).ConfigureAwait(false); }` and nothing else — no `try`, no collection, no wrapper.
  That is Chatter's loop. The in-box alternative, `TaskWhenAllPublisher`, starts every handler and returns
  `Task.WhenAll(tasks)` directly; awaiting that task surfaces only the FIRST fault, so even MediatR's
  run-them-all strategy does not hand a caller an `AggregateException`. Neither shipped publisher
  aggregates. Continue-and-aggregate is reported to exist in MediatR's samples rather than in the package;
  that part is recorded as reported, not verified here.

- **NServiceBus** — "Handling a single message in a given endpoint is treated as a single unit of work,
  regardless of how many handlers handle that message." One handler throwing means the rest do not run and
  the message is retried per the endpoint's recoverability policy, and on retry "*all* matching handlers
  are invoked again, including any that successfully handled the message during previous attempts." So
  handlers must be idempotent or must roll back. No aggregation anywhere.

- **MassTransit** — configuring a consumer configures a receive endpoint for it, so the default topology
  gives each consumer its own queue. A consumer that throws has the exception thrown back up the receive
  pipeline, into retry and then the error queue. Its in-process `IMediator` has no transport to fall back
  on and simply propagates: "If a consumer throws an exception, the *Publish*/*Send* method throws and the
  exception should be handled by the caller."

- **Rebus** — `DispatchIncomingMessageStep` walks its `HandlerInvokers` and does `await invoker.Invoke()`
  with no per-handler `try`/`catch`. A throw leaves the loop.

- **Brighter — the lone dissenter, recorded as dissent and not as consensus.** `CommandProcessor.Publish`
  wraps each handler chain in `catch (Exception e) { exceptions.Add(e); }` and then
  `if (exceptions.Any()) throw new AggregateException(...)`. It aggregates UNCONDITIONALLY — a single
  failing handler still reaches the caller inside an `AggregateException` wrapper. The honest framing of
  the survey is four-to-one with a named exception, not unanimity.

**The architectural point that actually settles it.** Three of the five — NServiceBus, MassTransit and
Rebus — do not buy subscriber independence inside the in-process loop at all; they buy it at the
**transport**, by giving each subscriber its own copy in its own input queue. NServiceBus states that as the
reason the mechanism exists: "To ensure that each subscriber can process and potentially retry the event
independently of other subscribers, NServiceBus ensures that each subscriber receives a copy of the
published event delivered to their input queue."

**MediatR is counted separately, and it is the strongest single data point here.** It has no transport at
all, so it cannot be counted toward a transport consensus — and that absence is exactly what makes it the
closest match to Chatter of anything surveyed: an in-process fan-out with nothing underneath it to isolate
subscribers. Faced with precisely Chatter's problem, its shipped default is abort-on-first-failure, and its
one alternative publisher still surfaces only the first fault. MediatR carries Option 2 on its own, without
being added to the transport count.

**Chatter has no per-subscriber queue.** `BrokeredMessageReceiver.DispatchReceivedMessageAsync` makes ONE
`DispatchAsync` call per received message — it is the sole dispatch call in that method — and that call
reaches `EventDispatcher`, which fans the one message out to N in-process handlers. Chatter is therefore
NServiceBus's *multiple-handlers-in-one-endpoint* case, which is one unit of work, and NOT its
publish-to-many-subscribers case, which is many. Matching the unit-of-work behaviour is matching the case
Chatter is actually in.

## Why the plan's own reasoning did not support Option 1

The plan read `CqrsExtensions.AddEventHandlers`' use of `RegistrationStrategy.Append` — deliberately unlike
`AddCommandHandlers`' `RegistrationStrategy.Replace()` — as implying that event handlers are decoupled
failure domains. It does not. `Append` buys **DI-level** decoupling: subscribers do not know about one
another at registration time, and adding one does not displace another. Every library surveyed above has
exactly that same DI-level decoupling, and every one of them except Brighter still aborts. Failure-domain
decoupling is a different property: the survey shows it is supplied by the transport where there is one and
— as MediatR shows, with no transport and no per-handler `catch` — simply not supplied where there is not.
The registration strategy supplies it nowhere.

## Decision

**An `EventDispatcher` dispatch stops at the first handler that throws.** Each `IMessageHandler<TMessage>`
resolved for the event is awaited in resolution order; when one throws, the dispatcher logs the exception
once and rethrows it unchanged, and no subsequent handler is invoked. The exception reaches the caller as
the handler threw it — it is never wrapped, and it is never joined to another handler's exception. That is
a fact about the method body, and it is the whole of what the type does.

**The dispatcher's one `LogError` call is not always the only one Chatter makes for a failure.** When the
event arrived through a `BrokeredMessageReceiver`, the receiver logs the rethrown exception again before
rethrowing it in turn, so the exception from a failed dispatch of a broker-delivered event is passed to
`LogError` at least twice: once by `EventDispatcher` and at least once more by `BrokeredMessageReceiver`.
`BrokeredMessageReceiver.DispatchReceivedMessageAsync` catches the exception the dispatch rethrows, calls
`LogError`, and rethrows it; the receive worker's error ladder can make a further `LogError` call of its
own. When an event is dispatched directly through `IMessageDispatcher`, with no receiver around the
dispatch, the dispatcher's one `LogError` call is the only one Chatter makes for that dispatch. These count
the calls Chatter makes, not the records an application sees: whether a call produces a record, and how
many, is decided by the log levels and logging providers the application configures.

**What that requires of a caller that needs a subscriber to run independently of its siblings: its own
delivery, dispatched by a service provider in which it is the only handler for the event.** Handlers are
resolved from the service provider by event type, not by the delivery that triggered the dispatch.
`EventDispatcher.DispatchToHandlers` resolves them through `GetServices<IMessageHandler<TMessage>>()`, and
`ScopedReceivedMessageDispatcher` — the default `IReceivedMessageDispatcher` — creates each delivery's scope
from the host's one `IServiceScopeFactory`, so every Brokered Message Receiver in a host dispatches into the
same handler set whatever subscription or queue it receives from. A second broker subscription or queue for
the same event in the same host therefore does not isolate one subscriber: each delivery dispatches into the
same handler set in the same order, stopping at the first handler that throws, so every delivery that reaches
a sibling handler invokes it, duplicating its side effects across those deliveries. A separate delivery is
necessary but not sufficient. A subscriber runs independently of its siblings only when it has its own
delivery — its own broker subscription or queue — and is dispatched by a separate endpoint or host whose
service provider registers that subscriber as the only handler for the event. Its own delivery gives it its
own copy of the event, so it fails, retries and recovers on its own; its own service provider is what stops
that copy from reaching its siblings. One dispatch carrying several handlers is one unit of work and
cannot supply that property. Within a single dispatch, a handler that must not be able to strand its
siblings has to contain its own failures.

**A handler that is invoked more than once for one logical event is expected, and handlers must tolerate
it.** The reliability inbox is COMMAND-scoped — `InboxBehavior<TMessage>` is an `ICommandBehavior<TMessage>`
constrained `where TMessage : ICommand` — so it never deduplicates the event path. A redelivered event
re-runs the fan-out from the start, including the handlers that already succeeded, exactly as NServiceBus
describes for its own retries. Event handlers must be idempotent or must roll back.

**The cost this decision accepts, stated plainly: an application that has not deliberately ordered its
handler registrations gets an invocation order nobody chose, and that order decides which siblings are
skipped.** Handlers run in the order their descriptors sit in the `IServiceCollection`:
`EventDispatcher.DispatchToHandlers` resolves them through `GetServices<IMessageHandler<TMessage>>()`, and
Microsoft's service-registration documentation states that services appear in the order they were
registered when resolved via `IEnumerable<{SERVICE}>`. Descriptors are frozen at `BuildServiceProvider` and
nothing on the dispatch path reorders them, so within one process the invocation ORDER is the same on every
delivery. WHICH siblings get skipped is still decided per delivery: the skipped set is the suffix after
whichever handler actually threw, so a deterministic failure skips the same set every time, while a transient
or conditional one lets those siblings run on a later delivery where a different handler may end the
dispatch. What is not chosen is where that order came from. A handler discovered by
`AddEventHandlers` is appended in scan order — assembly order from `AssemblySourceFilter.Apply()`, then
`Assembly.GetTypes()` order within each assembly, neither of which is contractually specified — and for the
overload that names no assemblies the source is `AppDomain.CurrentDomain.GetAssemblies()` at the moment
`AddChatterCqrs` runs, that is, whatever happened to be loaded. So the sequence — and with it "the prefix
before the first failure" whenever that failure is deterministic — is stable for the life of a process, but
it is an order an application fell into rather than picked, and it can differ across builds and across runs.

**The control an application does have is real and bounded.** It orders its own `Add*` calls relative to
`AddChatterCqrs`, and a hand-registered handler sits where it was registered. It canNOT reposition a
*scanned* handler by re-registering it: `AddEventHandlers` uses `RegistrationStrategy.Append`, which adds a
descriptor unconditionally, so re-registering a handler the scan already found produces a SECOND descriptor
and the handler runs TWICE per event — the double-invocation shape `0.12.0` fixed. NServiceBus documents the
same absence of a chosen sequence for unordered handlers. This is a real cost of Option 2 and it is not
offset by anything above — it is accepted because the alternative costs more, not because it is small. An
application that cannot tolerate it needs the isolation answer in the caller obligation — its own
delivery, dispatched by a service provider in which it is the only handler for the event — which is the
only mechanism that makes a subscriber's fate independent of its siblings'.

`OperationCanceledException` raises no design question under this decision: the `catch (Exception)` treats
it as any other fault, so a cancelled handler ends the dispatch the way a failing one does. Option 1 would
have forced a separate choice about whether cancellation short-circuits the remaining handlers or is
collected alongside ordinary faults.

The caller-facing statement of the contract lives in `src/Chatter.CQRS/src/README.md` (Events: Domain vs
Integration); the CQRS `CONTEXT.md` and the `EventDispatcher.Dispatch` XML remarks point here for the
rationale. The code is unchanged by this decision — the loop it describes is the loop that already shipped.

## What Option 1 would have cost

- **It would have blinded `error.type`.** `ActivityOutcome.ResolveErrorType` returns
  `exception.GetType().FullName`, and `EventDispatcher`'s instrumented path feeds that one resolver's
  result to BOTH the span's `ChatterTelemetryTags.ErrorType` tag and the dispatch-duration metric's
  `error.type` dimension — under a stated invariant that the two signals can never disagree about how a
  dispatch failed (ADR-0010 D4). Wrapping handler faults in an `AggregateException` collapses every
  distinct multi-handler failure class into the single literal string `System.AggregateException`, on both
  signals at once, and the span's status description degrades to the wrapper's generic text. Dashboards and
  alerts keyed on `error.type` would stop distinguishing failure classes. Option 2 keeps the handler's real
  exception type on both signals, because the diagnostics-on path wraps the same inner method rather than
  replacing it.

- **It would have removed the bound on duplicated side effects.** Because the inbox does not cover events,
  redelivery re-runs every handler under either option. Under Option 2 the side effects a failed delivery
  leaves behind are bounded to the prefix before the first failure. Option 1 would have run every handler
  on every attempt, so the duplicated work grows with the handler count and multiplies across retries.

- **It would have added public exception surface.** An `AggregateException` thrown for two-or-more faults
  but not for one is a shape every existing `catch` around a dispatch would have had to learn, and it is a
  breaking change to what a handler's own exception type means at the call site.

## Consequences

- **No production code change, no behaviour change, no new exception surface.** This ADR records what the
  dispatch loop already does; the release that carries it is a documentation correction.
- **The documentation that promised "all" is corrected, not softened.** The README's fan-out paragraph and
  the CQRS `CONTEXT.md` Event term now state the failure contract alongside the fan-out, so "all" can no
  longer be read as a guarantee that survives a throwing handler.
- **The behaviour is pinned by a permanent characterization test.**
  `WhenDispatching.MustNotInvokeSubsequentHandlersOnceAHandlerRaisesException`
  (`src/Chatter.CQRS/tests/Events/UsingEventDispatcher/WhenDispatching.cs`) asserts that the handler before
  the fault ran once, the handler after it never ran, and the exception that surfaced is the same instance
  the handler threw. A future change to Option 1 has to delete or rewrite that test deliberately.
- **Invocation order is fixed within a process, but it is an order nobody chose**, per the cost accepted in
  the decision. Registration order is invocation order, and an application that has not deliberately ordered
  its registrations gets scan order. Which siblings are skipped follows from that order and from whichever
  handler threw on the delivery: a deterministic failure skips the same set every time in one process, a
  transient or conditional one shifts the skipped set between deliveries, and a rebuild can change the order
  itself. An application that wants to reason about *which* handlers ran has to order its own registrations,
  which is something Chatter does not do for it.
- **Event handlers carry the idempotency obligation**, because neither the inbox nor the fan-out
  deduplicates them.
- **The revisit trigger is a transport, not a loop.** If independent per-subscriber failure becomes a
  requirement, the route is a separate delivery per subscriber, dispatched by a separate endpoint or host
  whose service provider registers that subscriber as the only handler for the event — the answer every
  transport-backed library in the survey reached — not per-handler `catch` inside `EventDispatcher`.

## References

- Issue #331 — *Event fan-out aborts on the first failing handler, contradicting the documented 'all will
  be invoked'*. The issue this ADR answers.
- Epic #301 — *Chatter.CQRS: handler registration correctness and per-dispatch hot path*. The parent epic;
  the `Append`-versus-`Replace()` registration strategies discussed above are its subject matter.
- ADR-0010 — *Optional BCL-only telemetry: per-assembly sources and the off-guard*. Source of the D4
  invariant that the span status and the metric's `error.type` come from one resolver.
- ADR-0011 — *Context Container: unsynchronized, documented rather than synchronized*. The sibling
  decision on the same epic, and the precedent for stating a contract as a type-local fact plus a caller
  obligation.
- MediatR `ForeachAwaitPublisher`, `TaskWhenAllPublisher`, `Mediator`; NServiceBus *Multiple handlers for a
  single message* and *Publish-Subscribe*; MassTransit *Consumers* and *Mediator*; Rebus
  `DispatchIncomingMessageStep`; Brighter `CommandProcessor.Publish`. The survey above.
