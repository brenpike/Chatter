---
status: accepted
date: 2026-09-10
---

# Context Container: unsynchronized, documented rather than synchronized

`ContextContainer` is the type-keyed bag every **Message Context** carries (`IContainContext.Container`).
It is a plain `Dictionary<string, object>` plus an optional inherited container, and it is mutated on
the dispatch path: `MessageDispatcher` seeds the active `IMessageDispatcher` and `IExternalDispatcher`
into it, the **Brokered Message Receiver** adds the transaction context for a received message, and
application handlers add whatever else they need.

Issue #333 observes — correctly — that this dictionary is unsynchronized, so two dispatches using one
container at the same time would race on it. The question this ADR settles is not whether the race is
real, but whether the answer is to make the container thread-safe or to state, as a requirement the
type places on its callers, that a container is never used from two threads at once.

## Considered Options

- **Option 1 — Document what the type is (an unsynchronized dictionary) and what it requires of a
  caller (never two threads at once) (CHOSEN).** Costs nothing on the dispatch path. Its weakness is
  honest: the requirement is met by the caller, not enforced by the type system.

- **Option 2 — Swap the backing store for a `ConcurrentDictionary<string, object>`, with a `Lazy<T>`
  per entry so `GetOrAdd`'s factory runs exactly once.** This is the *correct* shape for a
  thread-safe type-keyed bag, and it is not dismissed lightly: it would make single-container
  concurrent use safe at the dictionary level, and `Lazy<T>` closes the duplicate-factory-invocation
  hole a bare `ConcurrentDictionary.GetOrAdd` leaves open. It is rejected for three reasons, in order
  of weight.

  1. **The container's VALUES are themselves thread-hostile, so synchronizing the bag fixes almost
     nothing.** What actually goes into a container is a `MessageBrokerContext`, a
     `TransactionContext`, the dispatchers, and application state — none of them thread-safe. Making
     the *lookup* atomic only makes the handoff of a mutable, unsynchronized object atomic; the
     object then races exactly as before. Worse, a container advertised as thread-safe invites
     precisely the sharing the values cannot survive, so this option can leave a consumer less safe
     than the documented rule does.
  2. **`TryGet` spans an inherited-container chain that no concurrent dictionary can make atomic.**
     A miss in the local dictionary recurses into the inherited container, which may itself have a
     parent. Even with every link a `ConcurrentDictionary`, the walk is a sequence of independent
     atomic reads with no atomic "present anywhere in the chain" answer, and `GetOrAdd`'s
     read-the-chain-then-add-locally is a compound operation a per-link primitive cannot close.
     Closing it properly would require a lock spanning the whole chain — a lock ordering across
     containers whose lifetimes are independent of one another.
  3. **It adds allocation to the exact per-dispatch hot path epic #301 is separately trying to
     shrink.** A `ConcurrentDictionary` is a heavier allocation than a `Dictionary`, and a per-entry
     `Lazy<T>` adds an object per stored value. A container is constructed for every dispatch and
     every received message, so this cost lands on the hottest path in the library, in exchange for
     a safety property reason 1 says is largely illusory.

- **Option 3 — An EF-Core-style concurrency detector: a lightweight interlocked entry/exit guard
  around each container operation that throws when a second thread enters while one is inside.**
  This is the genuinely attractive alternative and the closest thing here to closed-by-construction:
  it does not pretend the container is thread-safe, it turns the unsupported usage into a loud,
  immediate, diagnosable failure instead of silent corruption, and it is the pattern `DbContext`
  uses for the same class of "single-threaded by contract" object. It is rejected here on two
  grounds: it introduces new public exception surface on `Chatter.CQRS` (a new exception type, or a
  new throwing condition on existing members, that consumers would then depend on), and it puts an
  interlocked read-modify-write on *every* container operation — again on the per-dispatch hot path
  of #301. **If #333 reopens, Option 3, NOT Option 2, is the route to reconsider**: it addresses the
  actual failure mode (unsupported sharing) at the point of use, without claiming a thread-safety
  the stored values cannot honour.

## Decision

**A `ContextContainer` is not synchronized, and is not going to be.** The type stores a plain
`Dictionary<string, object>`, takes no lock, and offers no synchronization of any kind on any of its
members — that is a fact about the class body, and it is the whole of what the type guarantees.

**What that requires of a caller: never use one container from two threads at the same time.**
Concurrent use of a single container is undefined and can corrupt the underlying dictionary.
Concretely: await each nested dispatch before starting the next, and do not capture a Message Context
(or its container) into work that runs alongside the dispatch that created it.

**A nested dispatch that runs against the caller's OWN container is expected, and is safe when it is
awaited.** `Dispatch(message, context)` deliberately seeds and reuses the container it is handed, and
`context.InMemory()` is exactly that path; one container serving a chain of nested dispatches in
sequence is normal use, not a violation. The hazard is simultaneity, not reuse.

The caller-facing statement of the requirement lives in `src/Chatter.CQRS/src/README.md` (Message
Context → Threading); the CQRS `CONTEXT.md` and the `ContextContainer` XML remarks point here for the
rationale. The code is unchanged by this decision.

**The ownership framing was tried, and is rejected — do not reintroduce it.** An earlier revision of
this ADR stated the rule as cardinality: that a container belongs to exactly one dispatch. That
framing fails twice. It is false against `Dispatch(message, context)`, which runs a second dispatch
against the container an outer dispatch already holds. And it is stronger than the code needs: it
condemns the awaited nested dispatch above, which is safe, so it does not discriminate between the
safe case and the racing one. Stating the rule in the concurrency primitive names the actual hazard
and leaves the safe case permitted.

What the code builds, as construction facts only — they do not license a "therefore it is safe"
conclusion:

- `MessageDispatcher.Dispatch` creates a fresh `MessageHandlerContext`, and therefore a fresh
  container, for every dispatch that does not supply one.
- `MessageHandlerContext` and `QueryHandlerContext` each construct their own container on
  construction — there is no shared or static container anywhere in the library.
- The **Brokered Message Receiver** builds a `MessageBrokerContext` per received message and hands it
  to that message's own processing worker, so concurrent per-message workers never touch a common
  container.
- The routing path chains a NEW container to the current dispatch's container as its inherited parent
  rather than sharing one mutable container between threads.

**Reachability of the #333 race is first-party and non-zero.** The `Dispatch(message, context)`
overload SEEDS the container it is handed — `Container.GetOrAdd(() => _externalDispatcher)` and
`Container.GetOrAdd<IMessageDispatcher>(() => this)` are a read-modify-write on the CALLER'S OWN
container. `Chatter.MessageBrokers`' `context.InMemory()` extension hands that overload the caller's
own context object: `InMemoryDispatcher` holds the context it was constructed with and forwards it
straight through. `WhenDispatching.MustDispatchViaContainedMessageDispatcherWhenPresent`
(`src/Chatter.MessageBrokers/tests/Sending/UsingInMemoryDispatcher/WhenDispatching.cs:34`) pins that
forwarding: it verifies the dispatch is made with the caller's own context object. So a handler that
starts two `context.InMemory()` dispatches WITHOUT AWAITING each one reaches the race on a single
container, without ever passing a context object around explicitly. No deliberate sharing is
required — a missing `await` is enough.

That is recorded here as a fact, not as an argument to synchronize now. The decision above stands,
and the revisit trigger is the one already stated under Option 3.

## Consequences

- **No public API change, no synchronization added, no cost on the dispatch path.** The
  concurrency decision is documentation; the dispatch path keeps the allocation profile epic #301
  is working to reduce. This is scoped to the concurrency decision only — it is not a claim that
  the release is behaviorally inert. The sibling #332 fix shipped alongside it changes
  `GetOrAdd<T>`'s hit/miss semantics observably, as the bullet below and the 0.13.1 changelog
  both record.
- **The contract is a convention a caller can violate UNINTENTIONALLY, not an invariant that holds by
  construction.** Un-awaited nested dispatch is the cheap way in: it needs no shared static, no
  context object passed between threads, and no intent to share — only a forgotten `await`. The
  reviewable signal is narrow and specific: a nested dispatch whose `Task` is not awaited before the
  next one starts, or a context object (or its container) captured by more than one concurrent
  dispatch or worker.
- **An application that runs two dispatches against one context concurrently is out of contract**, and
  the resulting corruption is its own to avoid — Chatter neither prevents nor detects it today.
- **Option 3 remains on the table.** If #333 reopens, or a consumer reports the corruption in the
  field, the concurrency detector is the next step.
- **`GetOrAdd`'s presence rule is part of the same contract surface** and is documented alongside it: a
  stored `null` is a PRESENT value and is returned as-is rather than re-running the factory, with
  `GetOrNew<T>()` the deliberate exception that always returns an instance.

## References

- Issue #333 — *`ContextContainer` is an unsynchronized `Dictionary` mutated per dispatch: concurrent
  dispatches sharing a context race*. The issue this ADR answers.
- Issue #332 — *`ContextContainer.GetOrAdd` gates on `is null`: value-type context silently returns
  `default(T)`*. The sibling defect on the same type; source of the presence rule documented above.
- Epic #301 — *Chatter.CQRS: handler registration correctness and per-dispatch hot path*. The parent
  epic, and the reason per-dispatch allocation is weighed as heavily as it is in Options 2 and 3.
- ADR-0008 — *Document-tier participation model and multi-container via a per-command container
  registry*. Precedent for the closed-by-construction framing used above.
