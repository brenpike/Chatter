---
status: accepted
date: 2026-09-25
---

# The Inbox gates the message the delivery admitted, not every command that carries its context

The Inbox deduplicates a delivery. `InboxBehavior<TMessage>` now gates only the Delivery Entry: the message instance
the delivery admitted into the Command Pipeline. Every other message dispatched on that delivery's context goes straight
to `next()`. Before this change the behaviour gated any Command whose context was an `IMessageBrokerContext`, and a
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
container, where the receiver put it (`BrokeredMessageReceiver.cs:1077`). The Cosmos document tier finds the delivery
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

## Decision

**The Inbox deduplicates a delivery, and gates only that delivery's Delivery Entry.** The Delivery Entry is the
message instance the delivery admitted into the Command Pipeline. An internal type, `InboxDeliveryEntry`, binds it on
the delivery's `ContextContainer`: the first message to reach `InboxBehavior` on a delivery's context becomes the
Delivery Entry, and only a message that is that same instance is sent through `ReceiveViaInbox`. Every other message
dispatched on that context, at any depth and by any dispatcher, goes straight to `next()`.

**A recovery retry is gated again.** The receiver deserializes the payload once (`BrokeredMessageReceiver.cs:1081`) and
its recovery strategy re-dispatches that same instance on the same context (`:1089-1094`). The retry therefore
presents the Delivery Entry again, and the Inbox gates it again, exactly as it did before this change.

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
- `MustReceiveViaInboxAgainWhenTheAdmittedMessageIsRedispatchedAfterItsHandlerFailed`: a recovery retry of the
  Delivery Entry is gated again.

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
- **`InboxDeliveryEntry` takes no lock.** The context container is unsynchronized by decision (ADR-0011), and a
  delivery's nested dispatches are awaited one at a time on that delivery's context.
- **`ChangeFeedReceiver` fans one delivery out as many Events on one context.** `DispatchReceivedMessageAsync`
  (`src/Chatter.SqlChangeFeed/src/Chatter.SqlChangeFeed/ChangeFeedReceiver.cs:35-79`) dispatches one Event per changed
  row on the delivery's context (`:57`, `:63`, `:69`). The first Command any of those Events' handlers dispatches
  takes the Delivery Entry and is gated; every later one runs. Before this change every later one was gated under the
  same message id and, once the first one's receipt existed, skipped on every tier.

### Recorded residuals

These are decisions, not open work.

- **R1: a broker-received Event is not itself deduplicated, so the first Command its handler dispatches takes the
  Delivery Entry.** `InboxBehavior` is a Command behaviour and never sees the Event. When the delivered payload is an
  Event, the first Command dispatched on the delivery's context is the first message to reach the Inbox, and it is
  gated under the delivery's message id. This is inherited, not introduced: that Command was gated before this change
  as well. It is bounded: later sibling Commands now run, and nothing is silently dropped. The obvious remedy, gating
  at the receive seam, was rejected as Option 2. No issue is filed for it.

Related work that is tracked: the Cosmos document tier has its own gate with the same root class, a nested participant
Command being treated as the delivery, and it is not changed here. That is #538.

## References

- Issue #534 — *In-memory Inbox silently skips a Command dispatched with context.InMemory() from inside a received
  handler*. The defect this ADR resolves.
- Issue #538 — *Cosmos document tier silently discards a nested participant command's writes*. The same root class on
  the document tier, tracked separately.
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
- `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Receiving/BrokeredMessageReceiver.cs` — the transaction
  context placed on the delivery's container (`:1077`), the single deserialization (`:1081`) and the recovery
  re-dispatch (`:1089-1094`).
- `src/Chatter.MessageBrokers/tests/Reliability/Inbox/UsingInboxBehavior/WhenHandling.cs` — the oracles.
