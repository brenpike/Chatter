---
status: accepted
date: 2026-09-20
---

# The reliability registration door stages its mutations, so a refusal cannot be observed

`WithReliabilityRetention<TContext>` bound the reliability context into the caller's `IServiceCollection` and then
validated its three configured spans, so a rejected `PurgeInterval`, `InboxDeduplicationWindow` or
`ProcessedOutboxRetention` threw out of a call that had already registered a descriptor. Two shipped claims — one in
this package's CHANGELOG, one in its `CONTEXT.md` — said the opposite. This ADR records the change that removed the
ordering question entirely: no entry point holds the caller's collection while it does anything that can throw. It
also records why the previous attempt at the same guarantee did not survive the next step, which is the part worth a
decision record.

## Context

**The door that was.** At `bf6094a`'s parent, each of the four public entry points —
`WithUnitOfWorkBehavior<TContext>`, `WithInboxBehavior<TContext>`, `WithOutboxProcessingBehavior<TContext>` and
`WithReliabilityRetention<TContext>` — called `BindReliabilityContext<TContext>(pipelineBuilder.Services)` first and
registered into `pipelineBuilder.Services` from there on. Three of them had no other refusal, so for those three the
claim held. `WithReliabilityRetention` did: its `RefuseSpanOutsideUsableRange` checks run *after* the bind, and each
of the six bounds across its three spans is a throw site reached with `ReliabilityContextBinding` already added to
the caller's collection.

**The door that is.** All four entry points open with
`var staged = StageReliabilityRegistrations(pipelineBuilder.Services);`, perform every mutation against `staged`,
and reach the caller's collection only through `CommitStagedRegistrations(pipelineBuilder.Services, staged)` as
their last statement before returning the builder. `StageReliabilityRegistrations` copies the caller's descriptors
into a fresh `ServiceCollection` held through `IServiceCollection`; `CommitStagedRegistrations` contains no fallible
operation of its own. The shared unit-of-work registration the inbox and outbox doors each bring with them was
extracted to `StageUnitOfWorkBehavior<TContext>`, which takes the staged collection rather than the builder, so
those doors compose it without leaving the staging. `AddReliabilityBehaviorOnce` likewise now takes an
`IServiceCollection`.

### Why the first attempt did not close it

`ea58368` installed a **bind-first convention** — every entry point calls `BindReliabilityContext` before it
registers anything — and a reflection sweep,
`UsingReliabilityPipelineExtensions/WhenBindingReliabilityContext.MustRefuseASecondContextFromEveryContextParameterizedEntryPoint`,
which discovers every public `TContext`-parameterized entry point and drives each one with a second context. The
sweep is real structure: an entry point added later is picked up with nothing listed anywhere. But it sweeps entry
points crossed with **the bind refusal only**. The span refusals were added to the retention door afterwards, below
a bind that had already registered. The convention was void from that moment and the sweep could not see it,
because the axis along which it had been broken was not an axis the sweep varied.

**Convention plus partial sweep is an enumerating fix wearing structure.** The sweep enumerated entry points, which
made the *entry point* axis safe, and left the *refusal* axis fixed at one value. A convention is only as durable
as the oracle that falsifies it, and an oracle that varies one axis pins one axis. Moving the bind below the span
checks would have been the same fix a second time: correct for the six refusals that exist, silent about the
seventh.

### The commit phase writes by absolute index

`CommitStagedRegistrations` removes surplus slots from the back, then walks the staging by index, appending past the
caller's end and otherwise assigning a slot only where `ReferenceEquals(services[index], staged[index])` is false.
Two properties of the surrounding code forced that shape rather than a `Clear()`-and-refill:

- A host may hand these extensions a decorating or side-effecting `IServiceCollection`. A refill replays `Add` for
  every descriptor the caller already had, which such a collection observes. Writing only the differing slots means
  a commit that changes nothing performs no write at all.
- `NormalizeReliabilityBehaviorOrder` permutes the reliability behaviors into the very indices they already occupy,
  by absolute index. Descriptor identity and slot order therefore have to survive the commit intact, or the
  ordering guarantee this package makes — outbox processing wraps the unit of work, which wraps the inbox — would be
  established on the staging and then lost on the way out.

Removing from the back is what keeps a removal from invalidating an index still to be visited.

### The oracle's preconditions are per case, and that is why it has teeth

`MustLeaveTheServiceCollectionExactlyAsItWasWhenAnyRefusalIsRaised` is a `[Theory]` over every discovered entry
point crossed with every refusal that entry point can raise: one mixed-context case per entry point, plus both
bounds of all three spans for the entry point that takes an
`Action<EntityFrameworkReliabilityOptions>`. It captures the caller's descriptors immediately before the refused
call and asserts the collection equals that list slot for slot after the throw.

**Each case carries its own arrange, and that is load-bearing.** A span case driven from an already-bound
collection is **green on the defect**: the bind is then a no-op, so the span throw mutates nothing and the assertion
passes against the very code it exists to catch. The span cases therefore arrive with **nothing** bound — the one
state in which the bind registers — and name the entry point's own context; the mixed-context cases arrive with a
`ReliabilityContextBinding` marker registered directly, so the bind itself refuses. A shared arrange over all ten
cases would have produced ten green cases against the defect.

**Exclusive mutation, measured.** Returning `services` from `StageReliabilityRegistrations` instead of the fresh
collection reddens exactly the six span cases of that theory and nothing else in this package: the commit then finds
every slot already holding the descriptor it would write, writes nothing, and the pre-staging behaviour is restored
exactly.

## Considered Options

### Option A — stage every mutation and commit through an infallible phase (ACCEPTED)

The entry point never holds the caller's collection while doing fallible work, so where a refusal sits within a door
stops being a question anyone has to keep answering.

### Option B — move the bind below the span checks (REJECTED)

This is `ea58368` a second time. It restores the claim for the six refusals that exist today and says nothing about
the next fallible step added to any of the four doors. The defect being fixed *is* a step added below a guarantee
that depended on step order.

### Option C — restate the convention and widen the sweep to both axes (REJECTED)

Widening the sweep to entry points × refusals is genuinely stronger than `ea58368`, and that sweep was in fact
built — it is the oracle above. But as the *whole* of the fix it still leaves the guarantee resting on a convention:
the sweep can only vary refusals it can name, and a step whose refusal it cannot construct is outside it again. The
sweep is retained as the oracle; it is not the mechanism.

### Option D — copy the caller's collection, then `Clear()` and refill on commit (REJECTED)

Simpler to write and wrong for two reasons already in this package: a decorating or side-effecting
`IServiceCollection` supplied by the host would see every descriptor re-`Add`ed, and `Clear()`-and-refill discards
the slot identity that `NormalizeReliabilityBehaviorOrder`'s absolute-index permutation depends on.

### Option E — point `CommandPipelineBuilder` at the staging (REJECTED, and unavailable)

`CommandPipelineBuilder.Services` has a private setter, so the builder cannot be aimed at the scratch collection.
Widening that setter would be a change to `Chatter.CQRS`'s public surface in service of one consumer's registration
mechanics. The staging is instead driven through the public
`ServiceCollectionExtensions.AddPipelineBehavior(IServiceCollection, Type)` extension, which is what
`CommandPipelineBuilder.WithBehavior` itself calls — so **no `Chatter.CQRS` change was needed**.

## Decision

**A reliability entry point stages every mutation onto a scratch `IServiceCollection` and reaches the caller's
collection only through `CommitStagedRegistrations`, which contains no fallible operation.** Ordering within a door
is free: a refusal raised anywhere in any door lands with the caller's collection exactly as it was handed over.

## Closed-by-Construction Acceptance Test

> What class of future finding does this make impossible, and why?

**Eliminated class: a reliability entry point mutating the caller's `IServiceCollection` before, or despite, a
refusal.**

Findings of the shape *"entry point X threw and left descriptor Y behind"* cannot exist — for any X among the four
doors, any Y, and any refusal, including one raised by a step added later — because no door holds the
caller's collection while it can throw. A step added anywhere in any door runs against the staging by construction,
since the staging is the only collection in scope between the first statement and the commit. This is what makes the
next finding of this shape unrepresentable rather than merely absent, and it is the difference from `ea58368`, which
made the finding absent for one refusal axis.

**What this does NOT close.** An `IServiceCollection` the host has made read-only refuses the write in the commit
phase, exactly as it refused the registration before staging existed. The failure is identical to today's and is
not made worse, but it is also not a refusal this package raises before mutating — and **no test pins it**. It is
written down here and on `CommitStagedRegistrations` so a reader does not assume one does.

## Consequences

- **Each entry point copies the caller's descriptors once per call.** The staging is a shallow copy of
  `ServiceDescriptor` references into a fresh `ServiceCollection`, taken at registration time on the startup path,
  and a commit that changes nothing writes nothing.
- **A door that calls another door composes through the staging, not through the builder.** `WithInboxBehavior` and
  `WithOutboxProcessingBehavior` call `StageUnitOfWorkBehavior<TContext>(staged)` rather than
  `pipelineBuilder.WithUnitOfWorkBehavior<TContext>()`, so the unit-of-work registrations land in the same staging
  and one commit publishes them all.
- **`BindReliabilityContext`'s guarantee moved.** What a refused call leaves behind is now
  `StageReliabilityRegistrations`' property rather than a consequence of the bind being the first statement, so per
  ADR-0027 the reasoning is written once on `StageReliabilityRegistrations` and cited — not restated — on
  `BindReliabilityContext` and here.
- **The mixed-context sweep is unchanged in role.** Deleting the `BindReliabilityContext` call from any one door
  still reddens `MustRefuseASecondContextFromEveryContextParameterizedEntryPoint` for that door alone; what the
  staging adds is the second axis, not a replacement for the first.

## References

- ADR-0027 — *An `INVARIANT:` comment names the oracle that falsifies it*. Why the staging's reasoning lives once on
  `StageReliabilityRegistrations` and is cited elsewhere.
- ADR-0026 — *The relational inbox decides expiry at receive*. The inbox marker mechanism the retention door
  configures; unchanged by this decision.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/Extensions.cs`
  — `StageReliabilityRegistrations`, `CommitStagedRegistrations`, `StageUnitOfWorkBehavior` and the four entry
  points.
- `src/Chatter.MessageBrokers.Reliability.EntityFramework/tests/UsingReliabilityPipelineExtensions/WhenBindingReliabilityContext.cs`
  — `MustLeaveTheServiceCollectionExactlyAsItWasWhenAnyRefusalIsRaised` and
  `MustRefuseASecondContextFromEveryContextParameterizedEntryPoint`.
- `ea58368` — the bind-first convention and the single-axis sweep this decision supersedes.
