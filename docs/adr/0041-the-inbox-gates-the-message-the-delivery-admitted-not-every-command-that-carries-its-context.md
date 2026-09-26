---
status: accepted
date: 2026-09-25
---

# The Inbox gates the message the delivery admitted, not every command that carries its context

The Inbox deduplicates a delivery. `InboxBehavior<TMessage>` now gates only the Delivery Entry: the first message
instance the delivery's current receive attempt admitted into the Command Pipeline. Every other message dispatched on
that delivery's context in that attempt goes straight to `next()`. Before this change the behaviour gated any Command whose context was an `IMessageBrokerContext`, and a
Command dispatched in-process from inside a received handler carries that same context. This ADR records what that
did on each reliability tier, the options considered, the one that was chosen, and what it leaves as it was.

Issue #534.

## Context

**The gate keyed on the context's type.** At master `77318ff`, `InboxBehavior<TMessage>.Handle`
(`src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Reliability/Inbox/InboxBehavior.cs:22-31`) sent the message
through `IBrokeredMessageInbox.ReceiveViaInbox` whenever `messageHandlerContext is IMessageBrokerContext` (`:25`), and
called `next()` directly otherwise. Nothing else was checked.

**Nested dispatch hands the delivery's context on.** `context.InMemory()`
(`src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Context/MessageHandlerContextExtensions.cs:34-35`) returns an
`InMemoryDispatcher` over the handler's own context, and `InMemoryDispatcher.Dispatch`
(`src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Sending/InMemoryDispatcher.cs:14-22`) dispatches the new
message with that same context (`:18`). A Command dispatched this way from inside a received handler therefore reached
`InboxBehavior` with the outer delivery's `IMessageBrokerContext`, and so with the outer delivery's message id. To the
Inbox it looked like the delivery arriving a second time.

**What each tier did with it.** Each tier's `ReceiveViaInbox` answered "has this message id been received?" for the
nested Command, and the answer was the outer delivery's own claim.

- **In-memory** (`src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Reliability/Inbox/InMemoryBrokeredMessageInbox.cs:116-131`):
  the outer delivery's reservation was still held, so `TryReserve` failed and the method returned without invoking
  the nested handler. The only record was a `Trace` log line (`:129`). The nested Command's work was silently dropped.
- **Entity Framework (relational)** (`src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/BrokeredMessageInbox.cs:120-131`):
  the outer delivery's claim carries no timestamp, so it read as "a claim no handler completed", was claimed again in
  place and the nested handler ran. That nested call then stamped the row as handled (`:154-158`) while the outer
  handler was still running. A second nested Command in the same delivery found that stamp, read it as handled within
  the Deduplication Window (`:114-118`, `IsHandledWithinTheDeduplicationWindow` at `:263-274`) and was skipped with
  only an `Information` log line. Every stamp and claim sits in the unit of work's one transaction, so a failure of
  the outer handler still rolled all of it back; what this tier got wrong was the redundant claim and stamp for the
  first nested Command and the skipped work of every later one.
- **Cosmos standalone inbox** (`src/Chatter.MessageBrokers.Reliability.Cosmos/src/Chatter.MessageBrokers.Reliability.Cosmos/Reliability/CosmosBrokeredMessageInbox.cs:132-157`):
  the create of the nested marker returned 409, the conflicting marker was the outer delivery's pending one, and the
  inbox took it over, ran the nested handler and then `CompleteClaim` (`:177-182`) marked it `Completed` while the
  outer handler was still running. A later nested Command in the same delivery then found a completed marker and was
  skipped (`:139-143`). `WithCosmosInbox` registers no unit of work
  (`src/Chatter.MessageBrokers.Reliability.Cosmos/src/Chatter.MessageBrokers.Reliability.Cosmos/CosmosInboxServiceCollectionExtensions.cs:107-117`),
  so nothing rolled that completion back. If the outer handler then failed, the redelivery found a completed marker,
  was treated as a confirmed duplicate and skipped. The outer delivery's work was lost.

**The behaviour is opt-in.** `InboxBehavior<>` runs only where an application registers it: directly with
`WithBehavior(typeof(InboxBehavior<>))`, through `WithInboxBehavior<TContext>()` in the Entity Framework module
(`Extensions.cs:43`), or through `WithCosmosInbox` in the Cosmos module (`CosmosInboxServiceCollectionExtensions.cs:117`).

## Considered Options

### Option 1 — key the Inbox on the message id and the message type (REJECTED)

It enumerates cases instead of closing them. A nested Command of the same type as the one the delivery admitted would
still collide with the delivery's claim. A nested Command of a different type would write its own reservation under a
key that no redelivery ever reads, so the store fills with entries that deduplicate nothing. It also changes the key
every store writes, which is a contract change on all three tiers.

### Option 2 — move the gate to the receive seam (REJECTED)

Gating in `ScopedReceivedMessageDispatcher.DispatchAsync`
(`src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Receiving/ScopedReceivedMessageDispatcher.cs:18-26`), before
the message enters the Command Pipeline, would gate only the delivery by construction. It does not work for the
relational tier: `ClaimMessageIdAsync` refuses to claim when its `DbContext` has no active transaction
(`BrokeredMessageInbox.cs:189-197`), and that transaction is opened by `UnitOfWorkBehavior` inside the pipeline. The
gate has to stay inside the unit of work, so it has to stay in the pipeline. It would also stop honouring the opt-in:
the Inbox would run for every received message whether or not the application registered it, and every existing
`WithBehavior` registration would become meaningless. A receiver that overrides `DispatchReceivedMessageAsync`, as
`ChangeFeedReceiver` does, would bypass it altogether.

### Option 3 — make `InMemory()` dispatch with a context that is not a broker context (REJECTED)

It would stop nested Commands from reaching the Inbox, but it would also take them out of the delivery they run
inside. `UnitOfWorkBehavior` (`Reliability/UnitOfWorkBehavior.cs:19`) and `OutboxProcessingBehavior`
(`Reliability/Outbox/OutboxProcessingBehavior.cs:26`) find the delivery's `TransactionContext` in the context's
container, where the receiver put it (`BrokeredMessageReceiver.cs:1096`). The Cosmos document tier finds the delivery
through `GetInboundBrokeredMessage()` (`DocumentTierBatchLifecycleBehavior.cs:83`), which is the same
`is IMessageBrokerContext` test (`MessageHandlerContextExtensions.cs:161-169`). A nested Command given a fresh
non-broker context would find neither, so the reliability behaviours would treat it as work outside the delivery's
unit of work. `GetInboundBrokeredMessage()` would also return `null` inside the nested handler, which breaks
routing-slip reads and the send and publish options that `BrokeredMessageDispatcher` derives from the inbound message
(`BrokeredMessageDispatcher.cs:268`, `:271`).

### Option 4 — a re-entrancy flag: skip the Inbox while a gated dispatch is running (REJECTED)

It fixes the reported case, but it keys on timing rather than on which message was admitted, and two cases fall
outside it. When the delivered payload is an Event, no gated dispatch encloses the Commands its handlers dispatch, so
the first of them is gated, clears the flag when it returns, and every later sibling is gated again under the same
message id and, finding the first one's receipt, skipped on every tier. And a handler that re-dispatches the very instance it received would
pass the gate and run itself again.

### Option 5 — gate only the Delivery Entry (ACCEPTED)

Recorded under *Decision*.

### Option 6 — stamp the attempt ordinal on the delivery's container (REJECTED)

The receiver would stamp each Recovery attempt's ordinal on the delivery's container, and `InboxDeliveryEntry` would
rebind whenever the ordinal it was bound under differs from the stamped one. It needs the same cooperation from the
receive seam as Option 5, which installs a fresh entry at that seam instead, and it has more parts: a second container
value, the ordinal's bookkeeping and a comparison in the entry. It closes no case that Option 5 leaves open.

### Option 7 — key the Delivery Entry on the deserialized payload instance (REJECTED)

The receiver would record the payload instance it deserialized, and the Inbox would gate only that instance. A retry
re-presents that instance, so it would be gated again. But `InboxBehavior` is a Command behaviour, so when the
delivered payload is an Event the payload never reaches the Inbox, and no Command dispatched during the delivery would
be gated at all. The same holds for every `ChangeFeedReceiver` delivery: its override of `DispatchReceivedMessageAsync`
never dispatches the payload and dispatches Events built from it instead. Those deliveries would lose the
deduplication they had on master, where the first Command their handlers dispatched was gated under the delivery's
message id (see G2).

## Decision

**This design is interim.** It is superseded when the design of epic #539 lands. The real fix is an idempotent
receiver at the delivery boundary: the unit of work, the Inbox and the outbox wrapped around the whole delivery. Until
then, the guarantee this design gives is stated, with its limits, under *Scope limits*.

**The Inbox deduplicates a delivery, and gates only the Delivery Entry of each receive attempt.** The Delivery Entry
is the first message instance the delivery's current receive attempt admitted into the Command Pipeline. An internal
type, `InboxDeliveryEntry`, binds it on the delivery's `ContextContainer`, and the receiver installs a fresh
`InboxDeliveryEntry` there at the start of every Recovery attempt (`BrokeredMessageReceiver.BeginReceiveAttempt`). The
first message to reach `InboxBehavior` on the delivery's context in an attempt becomes that attempt's Delivery Entry,
and only a message that is that same instance is sent through `ReceiveViaInbox`. Every other message dispatched on that
context in the attempt, at any depth and by any dispatcher, goes straight to `next()`.

**A recovery retry is gated again, whoever constructs the Command.** The receiver runs every Recovery attempt on the
same context (`BrokeredMessageReceiver.cs:1108-1113`), and each attempt first calls `BeginReceiveAttempt`
(`:1069-1081`), which installs a fresh Delivery Entry before the attempt dispatches anything. The first Command a retry
dispatches is therefore that attempt's Delivery Entry, and the Inbox gates it again. That holds whether the Command is
the payload the receiver deserialized once (`:1100`) or a fresh Command that a handler or an overriding receiver builds
on each attempt. An entry bound by an earlier attempt would never admit such a fresh Command, so the retry would bypass
the Inbox.

**One edit fixes all three tiers.** The change is in `InboxBehavior`, above every store. No store's contract,
`IBrokeredMessageInbox.ReceiveViaInbox`, or any store implementation changes.

**No public API changes.** `InboxDeliveryEntry` is internal. `InboxBehavior<TMessage>` keeps its type, constructor and
signature.

**The ADR-0006 behaviour order is unchanged.** The relational canonical order
`[OutboxProcessingBehavior, UnitOfWorkBehavior, InboxBehavior]` still holds, and the Inbox still runs inside the unit
of work, alongside the handler.

The rule and its rationale are stated once, in the `INVARIANT:` comments on `InboxBehavior.Handle` and on
`InboxDeliveryEntry`, which name the oracles that falsify them. This ADR cites them rather than restating them
(ADR-0027).

## Closed-by-Construction Acceptance Test

> What class of future finding does this make impossible, and why?

**ELIMINATED CLASS: "a message the delivery did not admit is gated by the Inbox as if it were the delivery".** The gate
no longer keys on the context's type, which every nested dispatch inherits and which any future nested-dispatch API
would inherit too. It keys on the admitted message instance, which nested dispatch cannot produce: a Command created
inside a handler is a different instance, whatever its type and whatever context it carries. That holds at any depth,
for Commands dispatched from Command handlers and from Event handlers, and through any dispatcher.

Pinned by these facts in `src/Chatter.MessageBrokers/tests/Reliability/Inbox/UsingInboxBehavior/WhenHandling.cs`:

- `MustInvokeTheNestedHandlerWhenAGatedHandlersOwnDispatchReEntersTheBehavior`: a Command dispatched from inside the
  gated handler reaches its own handler.
- `MustReceiveViaInboxOnlyTheMessageTheDeliveryAdmitted`: only the Delivery Entry is sent through `ReceiveViaInbox`.
- `MustInvokeTheHandlerOfASiblingDispatchedAfterTheAdmittedMessageCompleted`: a Command dispatched on the same context
  after the Delivery Entry's handler returned still runs.
- `MustReceiveViaInboxAgainWhenTheAdmittedMessageIsRedispatchedAfterItsHandlerFailed`: the instance the entry bound,
  dispatched again on the same context after its handler failed, is gated again.

**ELIMINATED CLASS: "a receive attempt inherits gate state bound by an earlier attempt of the same delivery".**
`BeginReceiveAttempt` installs a fresh `InboxDeliveryEntry` on the delivery's container at the start of every Recovery
attempt, before the attempt dispatches anything, so no attempt can find an entry that an earlier attempt bound. The seam
is in `ProcessMessageAsync`, above the virtual `DispatchReceivedMessageAsync`, so a receiver that overrides that
method, as `ChangeFeedReceiver` does, is covered by construction.

Pinned by these facts in
`src/Chatter.MessageBrokers/tests/Receiving/UsingBrokeredMessageReceiver/WhenGatingTheInboxAcrossRecoveryAttempts.cs`,
which drive the real receiver loop, a real retry strategy and a real `InboxBehavior`:

- `MustGateTheFirstCommandOfEveryRecoveryAttemptWhenTheHandlerBuildsAFreshOne`: when the handler builds a fresh
  Command on every attempt, each attempt's Command is sent through `ReceiveViaInbox`.
- `MustGateTheFirstCommandOfEveryAttemptWhenAReceiverOverridesTheDispatch`: the same holds when a receiver overrides
  `DispatchReceivedMessageAsync` and builds its own Command on every attempt.

## Consequences

- **Work that the in-memory tier silently dropped now runs.** A Command dispatched with `context.InMemory()` from
  inside a received handler reaches its handler.
- **The Entity Framework tier no longer claims and stamps the delivery's row for a nested Command.** The row is
  stamped only when the Delivery Entry's handler returns, so a second nested Command is no longer skipped.
- **The Cosmos standalone inbox no longer completes the delivery's marker early.** Only the Delivery Entry's own
  `ReceiveViaInbox` call completes it, after the outer handler returns, so an outer failure leaves the marker pending
  and the redelivery takes it over. A second nested Command is no longer skipped either.
- **The Inbox is not a loop guard.** A handler that dispatches a new Command of its own type now dispatches it for real
  on every tier, so a handler that does so unconditionally recurses without end. The Entity Framework and Cosmos tiers
  already did this; the in-memory tier used to stop it by accident, by dropping the nested Command.
- **`InboxDeliveryEntry` takes no lock.** The context container is unsynchronized by decision (ADR-0011). One worker
  running a delivery's Recovery attempts one after another is a construction fact; an attempt's nested dispatches
  being awaited one at a time on that delivery's context is not — ADR-0011 places that on the application as a
  requirement it deliberately does not enforce, and records that a missing `await` is enough to break it. An
  application that breaks it corrupts the container's own dictionary before this entry's field is ever read, so the
  entry adds no exposure the container does not already carry, and reopening the decision belongs to #333 rather than
  here.
- **`ChangeFeedReceiver` fans one delivery out as many Events on one context.** `DispatchReceivedMessageAsync`
  (`src/Chatter.SqlChangeFeed/src/Chatter.SqlChangeFeed/ChangeFeedReceiver.cs:35-79`) dispatches one Event per changed
  row on the delivery's context (`:57`, `:63`, `:69`). What the Inbox guarantees for those deliveries is G2, under
  *Scope limits*.

### Scope limits

This design is interim (see *Decision*). It covers a delivery fully only when the delivered payload is a Command. The
limits below are closed by epic #539's idempotent receiver, not by further work on this design.

- **G1: a delivered Command, and every Command it dispatches in-process, is fully covered by the delivery's own
  inbox receipt.** The payload is the attempt's Delivery Entry, so no nested Command is ever gated on its own and the
  delivery's receipt is what covers all of them. Coverage is what G1 claims; atomicity is a per-tier property and is
  not. Only the relational tier runs those nested Commands inside the delivery's ambient transaction, so only there
  does an outer failure roll their writes back (ADR-0006). The in-memory tier and the standalone Cosmos inbox register
  no unit of work — `WithCosmosInbox` is cited for that at *Context* above — so an outer failure rolls nothing back
  and a nested Command's effects re-run when the delivery is redelivered. That is the at-least-once posture ADR-0009
  already records for non-batched effects, and this design leaves it exactly as it was. Pinned by the facts listed
  under *Closed-by-Construction Acceptance Test*. G4, below, qualifies this claim: when another behaviour dispatches a
  Command on the delivery's context before `InboxBehavior` sees the delivered Command, coverage shifts to that other
  Command instead.
- **G2: a delivered Event is not itself deduplicated.** The first Command its handler(s) dispatch in each receive
  attempt is gated; later Commands in that attempt are at-least-once on retry or redelivery (strictly better than
  master, which silently dropped them). This covers `ChangeFeedReceiver`, which fans one delivery into N Events on one
  context. `InboxBehavior` is a Command behaviour and never sees the Event, so the first Command dispatched on the
  delivery's context in a receive attempt is the first message to reach the Inbox in that attempt, and it is gated
  under the delivery's message id. A retry starts with a fresh entry, so its first Command is gated again. This is
  inherited, not introduced: that Command was gated before this change as well. It is bounded: later sibling Commands
  in the attempt now run, and nothing is silently dropped. Gating at the receive seam alone was rejected as Option 2,
  because the unit of work opens inside the Command Pipeline. Tracked by epic #539.
- **G3: a direct call to the public virtual `DispatchReceivedMessageAsync` bypasses `BeginReceiveAttempt`, so it
  reuses whatever entry the context holds, outside the guarantee.** `DispatchReceivedMessageAsync` is public and
  virtual (`BrokeredMessageReceiver.cs:1035`), and `BeginReceiveAttempt` runs in `ProcessMessageAsync` before it, not
  inside it. A caller that invokes it directly installs no fresh entry, so `InboxBehavior` reuses whatever
  `InboxDeliveryEntry` the context already holds, or creates one through `GetOrNew` when the context holds none. The
  root cause is that the seam that scopes the entry to an attempt belongs to the receive loop, and the public dispatch
  method can be reached without passing through it. It was raised as finding 2cc65ed4 in the local review of #534. It
  is bounded: it needs a caller that drives the dispatch by hand and reuses one delivery context across calls, and
  even then the gate behaves as it did in the previous revision of this change, where the entry was scoped to the
  delivery. It is never worse than that revision. No test pins it. Tracked by epic #539.
- **G4: a Command another behaviour dispatches before `InboxBehavior` sees the delivered Command takes the Delivery
  Entry, and the delivered Command is then at-least-once.** A behaviour registered outside `InboxBehavior` in the
  Command Pipeline that dispatches a Command on the delivery's context before it invokes `next()` sends that Command
  through `InboxBehavior` first, and `Admits` binds the attempt's entry to it. The delivered Command then goes
  straight to `next()` and never passes through `ReceiveViaInbox`. On redelivery the Inbox skips only the other
  Command's receipt, and the delivered handler runs again. The root cause is that the entry binds on arrival order at
  the Inbox, not on the message the receive seam admitted, and only the receive seam knows which message that is. It
  is bounded: it needs an application behaviour ordered outside `InboxBehavior` that dispatches before `next()`. No
  behaviour this package ships does so, and ADR-0006's relational canonical order puts `InboxBehavior` innermost, so
  any application behaviour sits outside it. The cost is a duplicate run of the delivered handler, never lost work.
  This is inherited, and strictly better than master: there, the nested Command took the delivery's claim and the
  delivered Command was then read as a duplicate and skipped, so its work was lost. Seeding the entry with the
  delivered payload at the receive seam was rejected as Option 7 above, because `ChangeFeedReceiver` never dispatches
  its payload, so its deliveries would lose the deduplication they have today. Raised in the review of PR #540. No
  test pins it. Tracked by epic #539.

Related work: the Cosmos document tier has its own gate with the same root class, a nested participant Command being
treated as the delivery, and it is not changed here. That was #538, which is folded into epic #539.

## References

- Issue #534 — *In-memory Inbox silently skips a Command dispatched with context.InMemory() from inside a received
  handler*. The defect this ADR resolves.
- Issue #538 — *Cosmos document tier silently discards a nested participant command's writes*. The same root class on
  the document tier. Folded into epic #539.
- Epic #539 — *Idempotent receiver: move the unit of work, inbox and outbox processing to the delivery boundary*. The
  design that supersedes this one and closes G2, G3 and G4.
- ADR-0006 — *Two-tier reliability*. The relational canonical behaviour order this decision leaves unchanged, and the
  inbox-plus-handler atomicity inside the unit of work that rules out Option 2.
- ADR-0008 — *Document-tier participation model*. The document tier's own gate, which this decision does not change.
- ADR-0009 — *Standalone Cosmos inbox: confirm, not infer, and fail loud*. The pending-marker take-over that let a
  nested Command complete the delivery's marker early.
- ADR-0011 — *Context Container: unsynchronized, documented rather than synchronized*. Why `InboxDeliveryEntry` takes no
  lock.
- ADR-0027 — *An `INVARIANT:` comment names the oracle that falsifies it, and states its rationale once*. Why this ADR
  cites the `INVARIANT:` comments on `InboxBehavior.Handle` and `InboxDeliveryEntry` rather than restating them.
- `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Reliability/Inbox/InboxBehavior.cs` — the gate
  (`Handle`, `:22-31` at master `77318ff`).
- `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Reliability/Inbox/InboxDeliveryEntry.cs` — the Delivery Entry
  binding.
- `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Context/MessageHandlerContextExtensions.cs` — `InMemory()`
  (`:34-35`) and `GetInboundBrokeredMessage()` (`:161-169`).
- `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Sending/InMemoryDispatcher.cs` — nested dispatch on the
  handler's own context (`:14-22`).
- `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Receiving/BrokeredMessageReceiver.cs` — the public virtual
  `DispatchReceivedMessageAsync` (`:1035`), `BeginReceiveAttempt` (`:1069-1081`), which installs each attempt's fresh
  Delivery Entry (`:1080`), the transaction context placed on the delivery's container (`:1096`), the single
  deserialization (`:1100`) and the Recovery attempt that calls `BeginReceiveAttempt` before the dispatch
  (`:1108-1113`).
- `src/Chatter.MessageBrokers/tests/Reliability/Inbox/UsingInboxBehavior/WhenHandling.cs` — the gate's oracles.
- `src/Chatter.MessageBrokers/tests/Receiving/UsingBrokeredMessageReceiver/WhenGatingTheInboxAcrossRecoveryAttempts.cs`
  — the attempt-scope oracles.
