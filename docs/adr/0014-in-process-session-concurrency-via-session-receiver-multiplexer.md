---
status: accepted
date: 2026-09-10
---

# In-process session concurrency: one session-mode receiver multiplexing N single-session receivers

A session-mode Brokered Message Receiver holds exactly ONE Azure Service Bus session and serves at most ONE
message from it at a time. Both halves are enforced deliberately.
`AzureSdkSessionMessageReceiverAdapter` holds a single `ServiceBusSessionReceiver` field and accepts the next
session only once the held one is released
(`src/Chatter.MessageBrokers.AzureServiceBus/src/Chatter.MessageBrokers.AzureServiceBus/Receiving/AzureSdkSessionMessageReceiverAdapter.cs`),
and `PopulateFromDiscoveredReceivers` overwrites a session receiver's live `ReceiverOptions.MaxConcurrentCalls`
with `1`
(`src/Chatter.MessageBrokers.AzureServiceBus/src/Chatter.MessageBrokers.AzureServiceBus/DependencyInjection/ChatterAzureServiceBusExtensions.cs:309`),
because `BrokeredMessageReceiver` sizes its concurrency semaphore from that value
(`src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Receiving/BrokeredMessageReceiver.cs:360`) and every
worker it admits calls `ReceiveAsync` on the same adapter — so anything above `1` would pull from the one held
session concurrently and break FIFO-per-Group-Id ordering and session-state consistency.

The consequence is recorded, in as many words, in the solution architecture document
(`docs/architecture/asb-message-sessions.md`): *"Cross-session parallelism is achieved operationally by running
more receiver instances, not in-process"*, and a max-concurrent-sessions knob is *"intentionally N/A"*. That was
a correct decision for the Initiative that introduced sessions, whose stated scope was FIFO-per-`SessionId`
without a session concept in the broker-agnostic core.

Issue #457 is what it costs. An application whose handler takes on the order of ten seconds, whose backlog is
growing, and whose deployment is capped at two replicas is throughput-bound by replica count alone: two
sessions in flight across the whole deployment, and the only lever is a lever the application does not own.
Session-enabled entities usually have many sessions and slow per-session work — the shape the clamp is worst
for.

The question this ADR settles is not whether one-message-at-a-time is right. It is whether that property
belongs to the SESSION or to the RECEIVER. The clamp made it a property of the receiver because, with one
adapter holding one session, the two were the same thing. They are not the same thing, and separating them is
the whole of the change.

## Considered Options

- **Option 1 — Register N Brokered Message Receivers per session entry, each holding one session.** The
  appealing shape: no new component, N existing adapters, the clamp left exactly as it is. It does not work
  against the core's registration model. `AddReceiver<TMessage>` registers ONE hosted service and ONE retained
  `ReceiverOptions` instance per registration, and `BrokeredMessageReceiver` reads `MaxConcurrentCalls` off
  those options at init to size its own worker pool — so N registrations would each spawn their own
  `MaxConcurrentCalls` workers and the semantics would be N-BY-N rather than N. Making it N would mean
  stamping `1` onto each of the N clones, that is, reintroducing the clamp N times to undo the knob the change
  exists to give back. It also multiplies the receiver's identity: N `ReceiverOptions` for one queue name would
  land N entries in the discovered-receiver registry and N rows in the Azure Service Bus receiver registry that
  the cross-entity-transaction single-top-level-entity guard counts over. Rejected on cost and on requiring a
  core change the session Initiative was scoped to avoid.

- **Option 2 — The native `ServiceBusSessionProcessor`.** Rejected previously and rejected again for the same
  reasons, which are not re-argued here: see *Alternatives considered & rejected* in
  `docs/architecture/asb-message-sessions.md`. It is a PUSH model and every receiver in Chatter is PULL, so
  adopting it forces a push-dispatch seam into the broker-agnostic core and splits the receive architecture in
  two — one push path for sessions and a pull path for everything else, each with its own concurrency,
  recovery and teardown behaviour. That document records it as a possible future direction needing its own
  ADR; this ADR does not take it, and does not reopen it.

- **Option 3 — Parallelize the core pull loop itself,** so `BrokeredMessageReceiver` issues several
  `ReceiveMessageAsync` calls concurrently. It buys nothing for the case #457 describes. The pull is not the
  bottleneck when the handler dominates: a serialized pull against a warm entity supplies messages at roughly
  twenty a second, and three concurrent ten-second handlers consume them at 0.3 a second — two orders of
  magnitude of headroom already present. What it would cost is exact: the loop owns fault propagation,
  cancellation and `DrainInFlightWorkersAsync`, and those invariants are shared by every shipped
  implementation of `IMessagingInfrastructureReceiver` — `ServiceBusReceiver`, `RabbitMqReceiver` and
  `SqlServiceBrokerReceiver`. Rewriting them to buy throughput for one broker's session mode is the largest
  blast radius of the four options and the smallest return.

- **Option 4 — One session-mode receiver owning a multiplexer over N single-session receivers (CHOSEN).** The
  per-session invariant is preserved by construction rather than by a clamp: each child still holds one
  session and still serves one message at a time. Its weaknesses are real and are stated in the Decision and
  Consequences: handlers now run concurrently in one process, and an application that set a global
  `MaxConcurrentCalls` for its non-session receivers will find it applies to session receivers on upgrade.

## Decision

**In session mode, `MaxConcurrentCalls` means CONCURRENT SESSIONS.** A session-mode receiver owns a
multiplexer that holds up to N single-session children. Each child accepts its own session, serves that
session's messages FIFO one at a time, and settles on the session receiver that delivered the message. The
number of messages in flight per session is still exactly one; only the number of sessions held at once
changes. In non-session mode `MaxConcurrentCalls` keeps its existing meaning — concurrent messages from one
entity — and nothing about a non-session receiver changes.

**At N = 1 the bare single-session adapter is used directly, with no multiplexer in the path.** Today's
behaviour is therefore unchanged, not merely equivalent: the same type, the same call sequence, the same
session lifecycle. The clamp that forced N = 1 is what is removed; the behaviour it produced remains the
default, because `ReceiverOptions.MaxConcurrentCalls` defaults to `1`
(`src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Receiving/ReceiverOptions.cs:53`).

**The two session ports are split by what an implementation can honestly answer.**
`IServiceBusSessionMessageReceiver` is phrased PER MESSAGE — "which held session receiver delivered this
message" — so both a one-session holder and an N-session holder can answer it. `IServiceBusSessionChildReceiver`
carries the single-session facts (held session receiver, held `SessionId`, held lock expiry), which an
N-session holder cannot answer without lying about which of its sessions is meant. A multiplexer composes
children of the second shape and answers the first shape per message.

### The delivery-release signal is a separate optional capability

The multiplexer cannot free a session's slot when `ReceiveAsync` returns — the worker has not run yet — nor
when settlement returns, because a worker can end without settling. It needs to be told when the worker is
finished with a delivery. That is `IDeliveryReleaseSignal`
(`src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Receiving/IDeliveryReleaseSignal.cs`), discovered by a
type check on the infrastructure receiver.

**It is NOT a member of `IMessagingInfrastructureReceiver`.** That port carries the settlement contract, and
ADR-0010 D7 obliges every implementation of it to be contract-tested against all three settlement outcomes.
Widening the port widens that obligation: `RabbitMqReceiver`, `SqlServiceBrokerReceiver` and non-session Azure
Service Bus would each acquire a member, and the tests that go with it, for a capability exactly one
infrastructure wants. An implementation that does not declare the capability interface is never called for it
and sees no change at all.

**It is NOT a default interface member either.** A default implementation would compile everywhere and cost
nothing at runtime, which is precisely the objection: it erases capability DISCOVERY. Every receiver would
report as implementing the interface, and there would be no way left to ask whether an infrastructure actually
WANTS the signal — the question a type check answers and a default member makes unanswerable.

**The ordering guarantee is part of the contract, not an implementation detail.** The signal fires EXACTLY
ONCE per delivery, AFTER the settlement answer for that delivery — acknowledge, acknowledge-failure, negative
acknowledge, dead-letter and poison alike — and BEFORE the receiver returns the concurrency slot the delivery
occupied. The worker's `finally` calls `SignalDeliveryReleased` and then `ReleaseConcurrencySlot`
(`src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Receiving/BrokeredMessageReceiver.cs`), so by the time
the loop is admitted to receive the next message, a capability-aware infrastructure has already freed whatever
the finished delivery held. **The signal MUST NOT throw.** A throw is logged and swallowed, and the slot is
still returned, so a faulty capability can never leak the concurrency slot — but an implementation that relies
on that swallow is relying on damage control, not on a contract.

### An unknown-message settlement THROWS

The multiplexer routes a settlement to the child that delivered the message. When it cannot find one — the
message belongs to no child it currently holds — it throws.

The reason is that there is no other failure channel. The internal port's settlement members
(`IServiceBusMessageReceiver.CompleteAsync` / `AbandonAsync` / `DeadLetterAsync`) return a bare `Task`, which
can express "done" and "threw" and nothing else. Returning a completed task means SUCCESS, so a multiplexer
that quietly returned one for a message it could not route would have the receiver record an acknowledgement
that never happened, report a `Settled` Settlement Outcome, and commit the local transaction for a delivery
the broker is going to redeliver. That is the false `Settled` the Settlement Outcome term exists to prevent —
a settlement that FAILED made indistinguishable from one that SUCCEEDED.

It is thrown as a DETERMINISTIC, NON-RETRIED fault, not a transient one. Retrying cannot help: the message
will not become routable, and the routing table will not change while the retry sleeps. Classifying it that
way is what makes the recovery ladder convert it to a `Failed` Settlement Outcome — terminal for that delivery
and reported as such — instead of retrying forever or feeding failures to the Circuit Breaker for a condition
no cooling period repairs.

### The stale-lock backstop is LOG-ONLY

An earlier design for the multiplexer reclaimed a session slot when the held session's lock expiry had passed,
as insurance against a worker that ended without ever signalling. It is not adopted. The delivery-release
signal fires on EVERY worker path, including the fault paths, so the only situation the reclaim actually
covered is one where the receiver is rebuilt underneath the multiplexer — and in that situation the
message-to-child map is meaningless anyway, because the children it names are gone. Against that, the reclaim
raced live workers: a slow handler whose session lock lapsed while it was still running would have had its
child evicted and its message unrouteable, manufacturing exactly the false `Settled` the paragraph above
refuses.

What the multiplexer does instead is say so. When a held session's lock has expired and the slot is still
occupied, it logs a WARNING naming the receiver path, the `SessionId` and how long the session has been parked,
and frees nothing. The condition becomes observable without becoming a second, racing owner of the slot.

**The check is CONDITIONAL, and its trigger states the bound exactly.** The sweep runs on a pass of the
multiplexer's `ReceiveAsync` loop and from nowhere else — there is no timer and no second caller. The core pull
loop enters that call only while holding a `MaxConcurrentCalls` permit, which it takes BEFORE every receive
(`src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Receiving/BrokeredMessageReceiver.cs:676`), and that
semaphore is sized from the same `MaxConcurrentCalls` the multiplexer sizes its slot count from. So the check is
unavailable precisely while all N slots are simultaneously occupied: every permit is held, the loop parks in
`WaitAsync`, and no pass runs. It is available as soon as any ONE slot is free, and it then reports a stale hold
on ANY slot, because the sweep iterates every slot rather than only the one about to yield — with N = 3 and slot
0 stuck on an expired lock while slots 1 and 2 keep cycling, the warning is raised on a pass driven by a
sibling. Every delivery release is also immediately followed by the permit release and completes the freed
signal that sits in every await set, so a stale-check pass follows every worker completion within one turn.

KNOWN LIMITATION: the undetected state is a TOTAL stall — all N slots occupied at once with no worker ever
releasing — and not a single stuck handler, which N - 1 cycling siblings raise the warning for. An instrumented
host still sees the total stall from outside the log: N receive spans that never close and a flat
`messaging.client.consumed.messages`. This is recorded as the present bound of the backstop, not as a defect
this decision undertakes to fix.

Three ways of removing that bound were considered and rejected:

- **An independent timer inside the multiplexer.** It adds a second concurrent actor to a class whose
  correctness already rests on its `INVARIANT:` comments, plus a cadence constant that is de-facto
  configuration and an injectable clock to make the cadence testable — all to emit a log line.
- **Checking staleness on delivery release.** Provably redundant: the permit release and freed-signal wake that
  follow every release already drive a sweep within one turn, so the check would run at moments it already runs.
- **Reusing the child's existing session-lock renewal loop as the ticker.** That loop STOPS at
  `MaxSessionLockRenewalDuration`, which is one lock duration BEFORE the lock it last renewed actually lapses,
  so it would still need a fresh delay and a time seam of its own. Its production half also cannot be driven
  test-first: `ServiceBusSessionReceiver` is sealed, with no accessible constructor and no model-factory entry
  point, so the renewal loop's session-facing side has no test double.

The backstop stays LOG-ONLY on every one of these readings: it never reclaims a slot.

### `ServiceBusReceiver` closes the inner receiver before nulling it

`ServiceBusReceiver.ReceiveMessageAsync` catches `ObjectDisposedException` when the inner receiver is closing
and nulls `_innerReceiver` under the lock so the lazy `InnerReceiver` property rebuilds it on the next turn
(`src/Chatter.MessageBrokers.AzureServiceBus/src/Chatter.MessageBrokers.AzureServiceBus/Receiving/ServiceBusReceiver.cs:175`).
It never called `CloseAsync` first.

With one single-session adapter behind the port that orphaned one held session and one renewal loop. With a
multiplexer it orphans N children, N renewal loops, and up to N - 1 armed session accepts that keep locking
sessions no worker will ever process — sessions that stay locked until their own locks lapse, while the
rebuilt multiplexer competes with its own abandoned predecessor for them. That is a different and larger
defect than the one that was there before, created by this change, so it is fixed by this change: the inner
receiver is closed before it is nulled.

### Configuration: SPECIFICITY BEATS SOURCE

`MaxConcurrentCalls` is settable GLOBALLY on Service Bus Options and PER RECEIVER at REGISTRATION, as an
argument to the registration entry points. **When a receiver states its own value at registration, that value
wins — whatever source the GLOBAL value came from.** A stated per-receiver value beats a global value set
fluently and beats a global value bound from configuration alike. Specificity decides; the source of the
global does not enter into it.

This sits ALONGSIDE the module's existing fluent-beats-configuration rule for Service Bus Options, and does
not contradict it. That rule resolves a conflict between two SOURCES for the SAME value — the nullable
backing fields in `ServiceBusOptionsBuilder` that let a fluent call override a bound section. This rule
resolves a conflict between two SCOPES. They are different axes and both apply: the global value is resolved
fluent-first from its two sources, and then a stated per-receiver value, if there is one, wins.

KNOWN LIMITATION: a per-receiver `MaxConcurrentCalls` cannot currently be expressed in CONFIGURATION at all.
`ServiceBusOptions` carries global scalars with no receivers collection, the core `ReceiverOptions` is not on
the bindable surface (`src/Chatter.MessageBrokers/src/README.md`), and `BrokeredMessageAttribute` carries no
concurrency property. Registration is therefore the only place a per-receiver value can be stated, and a
receiver discovered by the attribute scan can only ever inherit the global. This is recorded as the present
boundary of the rule, not as a defect this decision undertakes to fix.

`MaxConcurrentCalls` must still be at least `1`; the core rejects anything lower at receiver init
(`src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Receiving/BrokeredMessageReceiver.cs:281`).

## Consequences

- **An application that set a global `MaxConcurrentCalls` for its non-session receivers gets it applied to its
  session receivers on upgrade.** This is a real behaviour change for an existing deployment and it is
  accepted deliberately rather than defended away: the alternative — a separate session-only knob defaulting
  to `1` — would mean the term `MaxConcurrentCalls` had two unrelated meanings depending on which registration
  entry point was used, and would leave the clamp's surprise in place for anyone who did not discover the
  second knob. The mitigation is a LOUD release-note callout, not a silent default. It ships as a MINOR bump,
  following the precedent set when the module shipped startup-fail-loud behaviour as a minor release carrying
  an explicit BREAKING callout in the changelog (`2.2.0`).
- **Handlers now run CONCURRENTLY within one process for session receivers.** Ordering within a Group Id is
  untouched, but handler state shared across sessions — a static field, a cached client with per-call mutable
  state, a non-thread-safe collection captured in a closure — is now reached from several threads at once
  where it previously was not. Handler state must be thread-safe or per-scope. This is the same obligation
  non-session receivers have carried since `MaxConcurrentCalls` became live for them; session handlers are
  simply no longer exempt.
- **A receiver holding N sessions issues up to N concurrent long-poll session accepts when sessions are
  scarce.** A child with no session available waits on its accept and consumes nothing but an idle connection
  until one appears. That is acceptable back-pressure, not a fault: the surplus children cost waiting, and
  they are already positioned when a session arrives.
- **Across M replicas the deployment now holds up to M-by-N locked sessions.** Sizing is a product, not a sum.
  An operator moving from the clamp to N should expect the entity's locked-session count to rise by that
  factor and should size N against the entity's session population, not against the handler alone.
- **`PrefetchCount` is not a throughput lever here, and is unchanged by this decision.** A prefetched message
  starts ageing against the session lock the moment it is fetched, not the moment it is handled, so with a
  slow handler a prefetched batch can exhaust its lock while it waits its turn behind its own siblings. The
  knob keeps its current meaning and its current default; this is documented so an operator reaching for
  throughput reaches for `MaxConcurrentCalls` instead.
- **`docs/architecture/asb-message-sessions.md` is amended, not replaced.** Its Decision and Configuration
  sections pointed at operational scaling and recorded a max-concurrent-sessions knob as N/A; those two claims
  are superseded by this ADR and now say so. The rest of that document — the pull-not-push rationale, the
  renewal design, the settlement and teardown rules — stands, and composes per child at N.
- **The revisit trigger is a push core, not a bigger N.** If per-session throughput itself becomes the
  constraint — one session that cannot keep up, rather than too few sessions — the answer is not more
  concurrency inside a session, which FIFO forbids. It is Option 2 and the push-dispatch core seam that
  document already names, with its own ADR.

## References

- Issue #457 — *In-process multi-receiver concurrency for session and non-session receivers*. The issue this
  ADR answers.
- `docs/architecture/asb-message-sessions.md` — the solution architecture for session support. Source of the
  one-session-per-receiver decision this ADR partially overturns, and of the `ServiceBusSessionProcessor`
  rejection this ADR reuses rather than re-argues.
- ADR-0010 — *Optional BCL-only telemetry: per-assembly sources and the off-guard*. Source of the D7
  obligation that every implementation of `IMessagingInfrastructureReceiver` is contract-tested against all
  three settlement outcomes, which is why the delivery-release signal is a separate capability.
- ADR-0012 — *Event fan-out: abort on the first failing handler, documented rather than aggregated*, and
  ADR-0013 — *Query invoker cache: process-lifetime retention documented rather than weakened*. Precedent for
  stating a decision as a type-local fact plus an obligation the caller carries.
- `src/Chatter.MessageBrokers/CONTEXT.md` — *Settlement*, *Settlement Outcome* and *Brokered Message Receiver*;
  `src/Chatter.MessageBrokers.AzureServiceBus/CONTEXT.md` — *Session*, *Session Queue Receiver* and
  *Group Id ↔ SessionId realization*. Both are updated separately to carry the multiplexed meaning of
  `MaxConcurrentCalls`.
- The `Chatter.MessageBrokers.AzureServiceBus` changelog entry for the release carrying this change — a minor
  bump from `2.3.0`, with the global-`MaxConcurrentCalls` upgrade callout stated there in full.
