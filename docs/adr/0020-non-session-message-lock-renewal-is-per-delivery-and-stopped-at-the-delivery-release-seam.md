---
status: accepted
date: 2026-09-17
---

# Non-session message lock renewal is per delivery and stopped at the delivery-release seam

A PeekLock delivery arrives holding a lock with a finite duration set on the Azure Service Bus entity, and the
handler owes a settlement before that lock expires. The session receive path renewed the lock it held; the
NON-SESSION path renewed nothing. Any handler that outran the entity's lock duration therefore lost its lock
mid-flight, its settlement threw `ServiceBusException(MessageLockLost)`, and Azure Service Bus redelivered a
delivery that had in fact been processed — repeatedly, until the delivery count exhausted and it was
dead-lettered. This ADR records why the fix is ONE shared bounded renewal policy with PER-DELIVERY state, and
why the stop is anchored at the settlement funnel plus the Delivery Release signal rather than at settlement
alone.

## Context

**What the session path already had.** `AzureSdkSessionMessageReceiverAdapter` renewed the SESSION lock on a
private loop with ONE `CancellationTokenSource` and ONE task per adapter. That shape is correct there and only
there: a single-session child holds exactly one session at a time, so "the adapter's renewal" and "this
session's renewal" are the same thing, and the release path can cancel the one source before closing the held
receiver.

**Why that shape does not transplant.** A non-session adapter wraps ONE long-lived
`Azure.Messaging.ServiceBus.ServiceBusReceiver` and serves up to `MaxConcurrentCalls` deliveries in flight at
once against it (see the Max Concurrent Calls term: one knob, read as concurrent MESSAGES in this mode). One
source per adapter would mean the first delivery to settle cancels every other in-flight delivery's renewal,
silently reintroducing the defect for every delivery but one — and doing so only under concurrency, which is
exactly where lock expiry is most likely. The renewal lifetime has to be the DELIVERY's, not the adapter's.

**Which stop seams exist for a delivery.** Three settle members (`CompleteAsync`, `AbandonAsync`,
`DeadLetterAsync`); the Message Brokers context's Delivery Release signal, which the core's worker raises from
a `finally` for every delivery whether or not it settled
(`src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Receiving/BrokeredMessageReceiver.cs`,
`SignalDeliveryReleased` in the worker's `finally`); and the adapter's own `CloseAsync`. Settlement alone is
not enough: a handler that throws and whose recovery ALSO fails to settle reaches no settle member at all, and
that path is precisely the one where a leaked renewal loop would keep renewing a lock nobody will ever settle.

**The signal was on the wrong interface.** `DeliveryReleased` was declared on
`IServiceBusSessionMessageReceiver`, and `ServiceBusReceiver.DeliveryReleased` forked on
`_innerReceiver is IServiceBusSessionMessageReceiver` before forwarding. Having the non-session adapter answer
the same signal did not ADD a second fork — it REMOVED the existing one: the member was hoisted to
`IServiceBusMessageReceiver`, which every inner receiver implements, and the forward is now unconditional on a
non-null inner receiver.

## The invariant

**A lock-renewal loop is owned by the thing whose lock it renews and ends when that thing ends. For the session
path that owner is the held session; for the non-session path it is the DELIVERY. Every terminal path for the
owner ends the renewal before the resource it renews against goes away.**

The corollary is what the code is shaped around: renewal ends BEFORE the settlement it precedes reaches Azure
Service Bus, and BEFORE the receiver it renews against is closed. Ending it afterwards would be racing a
renewal against the very call that makes the lock meaningless.

## Considered Options

- **(i) Adopt `ServiceBusProcessor` and inherit its `MaxAutoLockRenewalDuration` (REJECTED).** The SDK's
  processor renews message locks for free. It also owns the receive cadence, the concurrency admission, the
  error handling and the settlement surface — all of which this module owns deliberately, on a bare
  `ServiceBusReceiver`: Chatter drives its own receive loop, its own `MaxConcurrentCalls` admission, its own
  recovery ladder, its own PeekLock Settlement outcomes, and its own rebuild of the inner receiver after an
  `ObjectDisposedException`. Adopting the processor to obtain one renewal knob would mean rewriting every one
  of those, and would hand the processor decisions this context has already made differently. That is a far
  larger change than this defect warrants, and it is why the `_Avoid_: "auto lock renewal"` alias exists — the
  processor's vocabulary would imply a mechanism that is not running here.

- **(ii) Copy the session shape: one renewal source per adapter (REJECTED).** Rejected for the reason recorded
  in Context — it cancels the wrong delivery's renewal as soon as two deliveries are in flight.

- **(iii) A per-delivery renewal registry over one shared bounded renewal policy (ACCEPTED).** Renewal state
  keyed per delivery; the cadence, ceiling and failure handling factored into a single type both receive paths
  run.

- **(iv) Make zero mean "do not renew, but DETECT the lost lock and surface it" (REJECTED).** The proposal was
  that a ceiling of zero should still arm something per delivery — a watchdog comparing the lock's expiry
  against the clock — so an operator who turns renewal off still learns when a lock lapses. It is rejected on
  both halves. First, it defeats the setting: zero exists so that a delivery costs nothing extra, and a
  watchdog puts a timer, a task and a cancellation source back onto every delivery in the one configuration
  whose entire purpose is to have none. Second, the diagnostic already exists and is strictly better sourced:
  a lapsed lock makes the eventual settle call throw `ServiceBusException(MessageLockLost)`, which travels the
  core's settlement-recovery ladder and is logged as a failed settlement against the delivery that suffered
  it. A watchdog would be a second, inferential report of a fact the broker already reports directly.

- **(v) Refuse an out-of-range ceiling when the option is built, instead of saturating where the ceiling is
  computed (REJECTED).** A `MaxMessageLockRenewalDuration` large enough that `now + maxRenewalDuration` leaves
  `DateTimeOffset`'s range faulted the loop with an `ArgumentOutOfRangeException` before it renewed once.
  Validating the option does not close that class: `LockRenewalLoop` takes the duration as a constructor
  argument and both receive paths construct one directly, so a duration that never passed through
  `ServiceBusOptionsBuilder` reaches the same arithmetic unchecked. It would also add a new startup throw to a
  knob that previously accepted any value, turning a setting a deployment may already carry into a refusal to
  start. Saturating at the arithmetic site is total where validating the option is positional: the single
  expression that computes a ceiling cannot be reached with a value it rejects.

## Decision

Adopt option (iii), with zero as a total off-switch per option (iv):

1. **One bounded renewal policy serves BOTH paths.** `Receiving/LockRenewalLoop.cs` renews a lock at the
   halfway point between now and its expiry, floored at one second so a near-expired or already-expired lock
   renews promptly rather than spinning or waiting a negative span, and stops at a ceiling of
   `now + maxRenewalDuration` — SATURATED at `DateTimeOffset.MaxValue`, so every `TimeSpan` admits a ceiling
   and no configured duration can fault the loop before it renews once — computed ONCE at loop start. It
   takes no Azure SDK type: the expiry is read through a delegate re-read every iteration (it advances on
   each successful renewal) and the renewal itself is a delegate, with the clock and the delay injected —
   which is what makes cadence and ceiling testable without a live namespace. It completes without throwing
   for the three expected outcomes — cancellation of its own token, a `ServiceBusException` carrying the
   caller's lock-lost reason, and a concurrently disposed receiver — and faults the returned task for
   anything else, because a loop that quietly stopped renewing on an unrecognised failure would look
   identical to one that ran to its ceiling.

2. **`LockRenewalLoop.IsEnabled` is the SINGLE definition of "renewal off".** Both paths read it, so they
   agree by construction rather than by two matching comparisons that a later edit could separate.

3. **The session path migrated onto it.** `AzureSdkSessionMessageReceiverAdapter`'s two private renewal
   members are deleted and replaced by a constructed loop: 12 lines added against 52 removed, a net reduction
   of 40. Its behaviour is unchanged — the catch set is the same three clauses with the same predicates, with
   `SessionLockLost` passed in as the path's lock-lost reason.

4. **ONE object owns ONE renewal, and renewal state is PER DELIVERY, reference-keyed.**
   `Receiving/RenewalLifetime.cs` is that owner: it constructs the renewal's cancellation source, runs the
   loop on it, leaves the registry's tracking and reports a renewal failure, all inside the nested `finally`
   of ONE `async` flow, and its completion IS that flow's task. Nothing else can reach the cancellation
   source, `End()` is its only cancellation surface, and `End()` owns nothing and never throws.
   `Receiving/MessageLockRenewalRegistry.cs` holds one such renewal per delivery in ONE dictionary keyed by
   `ServiceBusReceivedMessage` REFERENCE — for the same reason the Session Multiplexer keys its slots by
   reference: settlement is by received-message object, and two distinct deliveries may carry equal field
   values, so value equality would conflate two in-flight deliveries onto one renewal. Membership in that
   dictionary means exactly "this renewal has not ended", written by an insert in `Start` and by the owning
   renewal removing itself as it ends; `ActiveRenewalCount` is a projection computed over it rather than a
   second collection beside it. `Start` is a no-op when renewal is disabled, when the registry has closed, or
   when that reference is already renewing.

5. **Renewal starts at receive, for PeekLock only, against the receiver that delivered the message.**
   `AzureSdkMessageReceiverAdapter.ReceiveAsync` captures the SDK receiver it received from and closes over it
   in the renew delegate rather than re-reading the field inside, so a receiver REBUILT after an
   `ObjectDisposedException` is never handed a message it never delivered. A `ReceiveAndDelete` delivery is
   removed as it is received, so there is no lock to renew — the same reason it owes no settlement.

6. **All three settle members funnel through ONE private `SettleAsync` that stops renewal BEFORE awaiting the
   SDK settle.** The members are one-liners over it, so a future settle path cannot be added without also
   ending renewal.

7. **The Delivery Release signal is the backstop, through the same idempotent `Stop`.** `DeliveryReleased` is
   hoisted to `IServiceBusMessageReceiver` and the type-check fork in `ServiceBusReceiver.DeliveryReleased` is
   gone. `Stop` is idempotent, never throws, and is a SILENT no-op for a delivery the registry does not hold —
   which is a normal outcome, not a fault: after an inner-receiver swap, a delivery still in flight against the
   DISCARDED adapter has its release routed to the NEW adapter, which has never seen that reference.

8. **`CloseAsync` awaits the registry close before closing the SDK receiver**, ending and awaiting every
   delivery's renewal — including one already stopped but still ending — so no renewal outlives the receiver
   it renews against. A failing renewal cannot leave the rest running or strand the close: each renewal
   reports its own failure as it ends and its completion never faults, and every caller of the close observes
   the SAME completion, published in the same expression that starts it.

9. **The ceiling is an operator knob with zero as a total off-switch.** `MaxMessageLockRenewalDuration` /
   `WithMaxMessageLockRenewalDuration`, default 5 minutes, fluent winning over configuration per this
   context's precedence rule, with an explicit zero surviving a configured non-zero value. Zero or negative
   means no renewal at all and nothing else — no watchdog, no per-delivery timer, no allocation: `Start`
   returns on the `IsEnabled` check before it constructs anything.

## Closed-by-Construction Acceptance Test

> Which class of defect is made impossible, and why?

**"A renewal loop outlives the delivery it renews."** This is an ELIMINATED CLASS, not an enumeration of
handled cases, and the distinction is the whole point of anchoring the stop where it is anchored.

The naive version of this fix stops renewal on settlement, and is then obliged to be right about every way a
delivery can end — which fails for the delivery whose handler threw and whose recovery ALSO failed, because
that delivery reaches no settle member at all. What closes the class instead is that the stop does not depend
on HOW the delivery ended. Every terminal path for a delivery runs through `Stop` or `CloseAsync`: a settled
delivery through the single settle funnel, before the settle call leaves the process; a closed receiver through
`CloseAsync`, which is awaited before the SDK receiver closes; and EVERY delivery — settled, unsettled, or
abandoned by a failed recovery — through the Delivery Release signal, which the core raises from a `finally`
exactly once per delivery, before it returns the concurrency slot. There is no fourth way for a delivery to
end, because the core's worker cannot exit its `finally` without raising it. The registry's `Stop` being
idempotent is what lets the settle path and the release path both fire for the same delivery without either
needing to know whether the other ran.

**The honest residual.** This closes the class of an ONGOING loop, not of a single in-flight broker call. A
`RenewMessageLockAsync` already awaiting Azure Service Bus when the cancellation lands may still complete
afterwards; it is bounded at one outstanding call per delivery, and the SDK answers an already-settled delivery
with the same `ServiceBusException(MessageLockLost)` an expired lock does, which the loop already absorbs.
Ending a delivery's renewal does not wait for that call: a release ends the renewal and returns, and the
renewal releases what it owns whenever it actually ends.

`CloseAsync` is the one path that does wait, and what it waits for is BOUNDED. Azure.Messaging.ServiceBus
7.20.2 does not carry the renewal's cancellation token into the AMQP request — `AmqpReceiver`'s renew members
hand the token to the retry policy, and the retry lambda discards it in favour of the policy's per-try
timeout — so a renew attempt already in flight runs to `ServiceBusRetryOptions.TryTimeout` (default one
minute) rather than ending with the cancellation, while the retry policy DOES observe the cancellation between
attempts. A close therefore waits for at most ONE outstanding renew attempt per delivery, not for an unbounded
loop. That is a property of the SDK rather than of this module, and it applies equally to the pre-existing
session path, whose release awaits its renewal the same way.

What is guaranteed is that no renewal is ever ISSUED after its delivery ended, and that `CloseAsync` awaits
what is outstanding.

## Closed-by-Construction Acceptance Test: the eliminated cleanup-obligation category

> Which class of defect is made impossible, and why?

**"A renewal cleanup obligation a throwing step can SKIP, and a teardown completion a throwing step can
STRAND."** The first shape of this fix spread one renewal's obligations across its callers: the registry
cancelled the source, a continuation disposed it, a second collection recorded which renewals were still
running, and a close published a completion it then had to remember to complete. Each of those is a step that
can raise before reaching the next, and each raise leaves a different wreck — a source never disposed, an
ended renewal recorded as running forever, a close every caller awaits and nothing completes.

Four structural facts close the category rather than enumerating its cases:

- **Cleanup has ONE site, and a `finally` guarantees it.** `RenewalLifetime` constructs the cancellation
  source, and disposing it, leaving tracking and reporting a renewal failure all happen in the nested
  `finally` of the single `async` flow that ran the loop. There is no second site to forget and no ordering
  for a caller to get wrong, because no caller participates in the cleanup at all.
- **No caller owns cleanup, so a failing cancel skips nothing.** `End()` cancels and returns; it holds
  nothing and releases nothing. `CancellationTokenSource.Cancel()` runs registrations and raises whatever one
  of them raises, but the token is signalled before any registration runs, so the loop ends either way and
  the flow that ends it discharges every obligation whether the cancel returned or threw. `End()` therefore
  never throws, which is what lets the Delivery Release path and the close path both call it unguarded.
- **The strandable primitive is deleted.** A close is an `async` method's task, published in the same
  expression that starts it, inside the same lock that snapshots the renewals it owns; the language
  guarantees that task reaches a terminal state, so "published but never completed" has no representation. A
  renewal's completion is likewise its own flow's task and never faults, so a failing renewal cannot stop a
  close from finishing.
- **ONE structure means the contradictory states have no representation.** Two collections could disagree: a
  renewal in both, in neither, or added to one after a teardown had snapshotted the other. There is now a
  single dictionary whose membership means exactly "this renewal has not ended", `ActiveRenewalCount` is
  computed over that dictionary rather than kept beside it, and a closed registry starts nothing further — so
  a close's snapshot is the whole population, and there is no second structure for it to differ from.

**The honest residual.** Two of these rest on an argument rather than on a type-level guarantee, and should be
read as such. `Stopped` is an ordinary mutable field written and read under the registry's lock; nothing in
its type prevents a future edit touching it outside that lock, so "no contradictory state" holds because every
write is inside the lock, not because the field cannot express one. And "membership means this renewal has not
ended" rests on the exit callback running exactly once — which it does, because it runs in one `finally` on
one flow — rather than on a structure incapable of holding an ended renewal. Both are RELOCATIONS of a
property into a single place a reader can check, which is what makes them checkable; neither makes the
property unwritable.

## Consequences

- **This changes DEFAULT behaviour.** Renewal is now ON for every non-session PeekLock receiver, bounded at 5
  minutes. A deployment that configured nothing previously renewed nothing and now renews; set
  `MaxMessageLockRenewalDuration` to zero to restore the old behaviour exactly.
- **Renewal is bounded by the ceiling the operator sets, so lock loss is deferred, not abolished.** A handler
  still running past the ceiling loses its lock exactly as it did before, and its settlement throws exactly as
  it did before. Handlers that legitimately run longer need a higher ceiling.
- **A saturating ceiling gives `TimeSpan.MaxValue` a meaning: renew until the delivery ends.** This ADR first
  said that a handler running unboundedly needs a different design, "not a higher ceiling"; the arithmetic no
  longer refuses one, so that advice is narrowed to what survives. An unbounded ceiling is now expressible,
  and it does not make a renewal unowned — the stop seams above are unchanged, so such a renewal still ends at
  the delivery's settlement, at its Delivery Release, or at the receiver's close. What it gives up is the
  ceiling as a backstop for a handler that never ends, which then holds its lock for as long as it runs.
- **The common case costs no extra broker call.** The first renewal is scheduled at half the delivery's
  remaining lock, so a handler that settles well inside the entity's lock duration — which is most of them —
  stops renewal before any renewal request is issued.
- **Per-delivery cost is one Renewal Lifetime — a cancellation source and a loop task — per IN-FLIGHT
  delivery**, bounded by `MaxConcurrentCalls`, and zero when the knob is zero.
- **The two receive paths now share a policy type.** A change to cadence, ceiling arithmetic or the catch set
  in `LockRenewalLoop` changes SESSION lock renewal too. That coupling is the point — the two had drifted
  apart once already — but it makes that type a shared-behaviour surface rather than a private helper.
- **`DeliveryReleased` is now part of the non-session inner-receiver contract.** Any future
  `IServiceBusMessageReceiver` implementation owes the member's stated obligations: it must not throw, and a
  delivery it does not hold must be a silent no-op.

## References

- Issue #373 — *Message locks are never renewed for non-session PeekLock receivers*. The defect this decision
  closes.
- Issue #307 — the parent epic this defect was raised under.
- Issue #499 — a session-path release that skips its cleanups when a renewal faults with an unrecognised
  exception. Pre-dates this decision, deliberately deferred out of it, and the reason the session path is
  named separately wherever this ADR describes a release awaiting its renewal. NOW CLOSED, by ADR-0021,
  which gives the session path one Renewal Lifetime recorded before it begins. That decision does not
  change this one: renewal is still PER DELIVERY on the non-session path and per held session on the
  session path.
- ADR-0014 (in-process session concurrency via the Session Multiplexer) — where the Delivery Release signal is
  first answered in this context, and the source of the reference-keyed, per-delivery dictionary this registry
  mirrors.
- `IDeliveryReleaseSignal` and `BrokeredMessageReceiver`'s worker `finally`
  (`src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Receiving/`) — the once-per-delivery guarantee the
  Closed-by-Construction claim rests on, and the settlement-recovery ladder that already logs a lost lock as a
  failed settlement.
- The Azure Service Bus context's PeekLock Settlement, Max Concurrent Calls and Max Session Lock Renewal
  Duration terms (`src/Chatter.MessageBrokers.AzureServiceBus/CONTEXT.md`) — the settlement contract renewal
  precedes, the concurrency that forces per-delivery state, and the sibling ceiling this one is modelled on.
