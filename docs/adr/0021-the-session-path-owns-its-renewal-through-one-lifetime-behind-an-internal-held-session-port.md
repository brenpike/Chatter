---
status: accepted
date: 2026-09-17
---

# The session path owns its renewal through ONE lifetime, recorded before it begins, behind an internal held-session port

`AzureSdkSessionMessageReceiverAdapter` held its session's lock renewal as TWO loose fields — a
`CancellationTokenSource` and the loop's `Task` — assigned at two different moments, one inside the lock that
records the held session and one outside it. Two issues were filed against that shape and both are the same
defect: a release could reach a session whose renewal was not yet recorded (#500), and a renewal that failed in
an unrecognised way could make the release's `await` throw and jump over BOTH remaining cleanups — the source's
disposal and the held session's close — leaking the AMQP link and holding the session lock to its expiry
(#499). This ADR records why the fix gives the session path ONE `RenewalLifetime`, recorded in the same lock
acquisition that records the session and strictly before that renewal begins, and why getting there required an
internal port over the held session.

## Context

**The defect is an OWNERSHIP defect, not two timing bugs.** Before this change, `AcquireSessionAsync` created
the renewal's `CancellationTokenSource` UNCONDITIONALLY inside the lock that recorded the accepted session, and
then — OUTSIDE that lock, and only when `LockRenewalLoop.IsEnabled` — started the loop and assigned
`_renewalTask`. `ReleaseSessionAsync` snapshotted all three fields under the lock and then cancelled, awaited,
disposed and closed. Two windows follow directly from splitting one thing across two assignments:

- A release interleaving between the lock's exit and the `_renewalTask` assignment snapshots a null task, so it
  disposes the source while the acquire is still about to run a loop on that source's token — #500.
- The `await` of the renewal was wrapped in a `try` that caught `OperationCanceledException` and nothing else,
  which is correct for the expected outcomes and wrong for every other one. `LockRenewalLoop` FAULTS its task
  for an unrecognised failure DELIBERATELY (ADR-0020, Decision 1), so an unrecognised renewal failure raised
  out of that `await` and skipped the two statements after it: `renewalCts.Dispose()` and
  `toClose.CloseAsync()` — #499.

**Why neither had a test.** `Azure.Messaging.ServiceBus.ServiceBusSessionReceiver` is sealed, has no accessible
constructor and no `ServiceBusModelFactory` entry point, so the adapter's session-facing half could not be
faked. ADR-0014 recorded exactly that, as one of two reasons for rejecting the renewal loop as a staleness
ticker. Both issues live entirely in that unreachable half: acquiring a session, arming its renewal, and
releasing it. Opening it is therefore not preparation for the fix — it IS the first half of the fix.

**What the adapter actually uses an SDK session receiver for.** Ten things, and they were enumerated from the
call sites rather than designed: the session's `SessionId` and `SessionLockedUntil`, whether it `IsClosed`, the
single-message receive with the idle timeout, the three settlements, the session-lock renewal, the close — and
the concrete SDK receiver itself, which two members of this module's PUBLIC-facing surface still have to answer
with.

## The invariant

**A held session and the renewal of its lock are recorded TOGETHER, in ONE lock acquisition, and the renewal is
recorded BEFORE it begins. Every release ends that renewal, awaits it, and only then closes the session — on a
path that needs no catch clause, because nothing BEFORE the close can throw.**

The two halves answer the two issues and they are not separable. Recording together is what makes a release
unable to see a session without its renewal. Recording before beginning is what makes that true even for a loop
that ends synchronously. And a release that cannot throw BEFORE the close is what makes the close unskippable.

The close ITSELF carries no such guarantee, and the invariant deliberately does not claim one: it is the LAST
statement on the path, so a throw there jumps over nothing. What a failing close does cost is recorded under
*Accepted residual: a close that fails is not retried*.

## Considered Options

- **(i) Move the `_renewalTask` assignment inside the lock (REJECTED).** The minimal patch, and it is rejected
  ON THE RECORD rather than merely passed over. It closes #500's window and leaves #499 entirely: the release
  would still `await` a task that faults for an unrecognised failure, and still skip the dispose and the close
  when it does. It also leaves the two loose fields and the overloaded sentinel described below in place, so
  the next edit to this path starts from the same shape that produced two issues. Closing one of two issues
  arising from one cause, by moving a line, is the definition of the enumerated patch this repo's
  closed-by-construction rule exists to refuse.

- **(ii) Make the port the return type everywhere, so `SessionReceiverFor` and `HeldSessionReceiver` answer
  `IServiceBusHeldSession` (REJECTED).** The tidier-looking shape, and it silently breaks the PUBLIC Session
  State API. `ContextContainer.Include<T>(T t)` keys the container by `typeof(T).FullName` — the STATIC type of
  the argument, not the runtime type
  (`src/Chatter.CQRS/src/Chatter.CQRS/Context/ContextContainer.cs`). `ServiceBusReceiver` writes the held
  session receiver into the transaction container with `Container.Include(heldSessionReceiver)`, and
  `MessageHandlerContextExtensions.GetHeldSessionReceiver` reads it back with
  `TryGet<ServiceBusSessionReceiver>`. Widening either answer changes the key the write uses, the read misses,
  and every `GetSessionStateAsync` / `SetSessionStateAsync` / `ClearSessionStateAsync` call throws the
  `InvalidOperationException` that exists to report a NON-session message. Nothing pinned that before this
  work; it does now.

- **(iii) Adopt `ServiceBusSessionProcessor` / `ServiceBusProcessor` and inherit the SDK's renewal
  (REJECTED).** Out of scope here for exactly the reasons ADR-0014 (Option 2) and ADR-0020 (Option i) already
  record, which are not re-argued: it is a PUSH model against a PULL core, and it would take ownership of the
  receive cadence, the concurrency admission, the recovery ladder and the settlement surface this module owns
  deliberately. Reaching for it to fix a field-ownership defect is not proportionate.

- **(iv) ONE `RenewalLifetime` owned by the adapter, behind an internal held-session port (ACCEPTED).** The
  renewal becomes one object with one owner, recorded before it begins; the port is what makes both the defect
  and the fix reachable from a test.

## Decision

1. **`IServiceBusHeldSession` is an internal port over the ONE held session, DERIVED from the adapter's call
   sites.** Ten members, each answering a real one:
   `SessionId` (the renewal's description), `SessionLockedUntil` (the child port's `HeldSessionLockedUntil`
   and the renewal loop's expiry delegate), `IsClosed` (`IsClosedOrClosing`), `SdkSessionReceiver` (see
   Decision 2), `ReceiveMessageAsync` (the idle-timeout receive), `CompleteMessageAsync` /
   `AbandonMessageAsync` / `DeadLetterMessageAsync` (the three settle members), `RenewSessionLockAsync` (the
   renewal loop's renew delegate) and `CloseAsync` (the release). `AzureSdkHeldSession` is the production
   implementation: a pass-through over one accepted `ServiceBusSessionReceiver` holding no state of its own.

2. **`SdkSessionReceiver` on the port is a DELIBERATE escape hatch, and it is the load-bearing constraint of
   this whole design.** It exists solely so `HeldSessionReceiver` and `SessionReceiverFor` keep answering the
   CONCRETE `ServiceBusSessionReceiver`, for the container-keying reason in Option (ii). It answers null for a
   held session that has none — which is what a test fake is — so the port is honest about a session with no
   SDK receiver behind it rather than pretending one exists.

3. **Session acceptance, the clock and the delay are injected.** The adapter no longer calls
   `client.AcceptNextSessionAsync` from `AcquireSessionAsync`; it calls an injected
   `Func<CancellationToken, Task<IServiceBusHeldSession>>`, bound ONCE at construction by
   `CreateSdkSessionAcceptor`, which captures the `ServiceBusClient` and the prefetch count rather than leaving
   them as now-unread fields. A `TimeProvider` and a `Func<TimeSpan, CancellationToken, Task>` delay are
   injected alongside it. The EXISTING 7-argument public constructor keeps its signature exactly and chains to
   a new internal one; its `client` null-check still throws `ArgumentNullException(nameof(client))` at
   construction, from the acceptor factory instead of from the constructor body.

4. **ONE `RenewalLifetime` field, used DIRECTLY.** `_renewalCts` and `_renewalTask` are deleted and replaced by
   a single `_renewal`. There is no registry and no wrapper: a single-session adapter holds exactly one session,
   so it needs exactly one renewal, and one field is the whole of the bookkeeping. `RenewalLifetime` is the
   same type ADR-0020 introduced for the non-session path — the two paths now share the renewal OWNER as well
   as the renewal POLICY.

5. **Record-then-begin, in ONE lock acquisition.** `AcquireSessionAsync` builds the `LockRenewalLoop` and its
   description OUTSIDE the lock, because building them is not what the lock protects. Inside a single
   acquisition it reads `_closed`, records the accepted session, records the renewal, and only THEN calls
   `Begin`. The ORDER is not stylistic: a loop that ends without ever awaiting ends INSIDE `Begin`, so a
   renewal recorded after beginning can already be over before it is recorded. This mirrors
   `MessageLockRenewalRegistry.Start`'s stated `INVARIANT:` exactly, which is the point — the two paths get one
   arming shape, not two that happen to agree.

6. **The raced-the-close answer is its OWN bool, carrying no renewal meaning.** Previously the release-race
   branch was keyed off `renewalCts == null`, and that sentinel was OVERLOADED: it meant both "this acquire
   raced a close" and "there is no renewal source". It was only safe because the source was created
   UNCONDITIONALLY — `IsEnabled` gated the LOOP, not the SOURCE. A `RenewalLifetime` that exists only when
   renewal is ENABLED breaks the overload outright: reading renewal-nullness as the race would SILENTLY CLOSE
   EVERY JUST-ACCEPTED SESSION on any receiver configured with `MaxSessionLockRenewalDuration` at or below
   zero. A dedicated `racedTheClose` bool is what keeps the two facts apart, and it is pinned by a `Theory` at
   both `0` and `-1` (`MustHoldAnAcceptedSessionWhenRenewalIsDisabled`).

7. **`ReleaseSessionAsync` has NO catch clause at all.** It snapshots and nulls both fields under the lock,
   ends the renewal, awaits its completion, then closes the held session. It needs no guard because nothing
   BEFORE the close can throw: `RenewalLifetime.End()` never throws, and `RenewalLifetime.Completion` never
   faults — its `RunToEndAsync` wraps the loop, the exit callback and the source's disposal in a single
   `catch (Exception)` with no filter, which absorbs `OperationCanceledException` along with everything else,
   and the report it makes there goes through `Report`, which guards its own sink. That is the whole of the
   claim, and it is exactly what #499 needs: the close is the LAST statement, so nothing can jump over it.
   `IServiceBusHeldSession.CloseAsync` is NOT covered by it and is deliberately left unguarded — a fault there
   skips no cleanup, because there is none after it. Deleting a catch clause is what makes #499
   unrepresentable; adding a broader one would only have widened the set of failures that silently reached the
   close.

### Why the injected clock is load-bearing rather than polish

`LockRenewalLoop` floors its renewal delay at one second. Without an injected delay, the #499 test — which must
get the loop INSIDE the broker's renew call, fail that call in an unrecognised way, and then close — would have
to wait that floor out in wall-clock time on every run, and the #500 test would be racing a real scheduler for
an interleaving it is supposed to assert deterministically. With the delay and the clock injected, the clock
never moves, every renewal wait PARKS, and the tests drive the interleavings directly: `#500`'s release is
triggered from inside the adapter's own `SessionId` read, which is the last thing that happens before the lock
is taken. The seam is what makes the assertions about ordering assertions rather than hopes.

### What did NOT change, and why that is the evidence

`IServiceBusSessionChildReceiver.cs` and `SessionReceiverMultiplexer.cs` are untouched by this work. The
multiplexer only ever reads `HeldSessionReceiver`, `HeldSessionId` and `HeldSessionLockedUntil` from its
children, and those three answers are unchanged in both type and meaning. That untouchedness is the evidence
the port stayed a seam over ONE held session instead of growing into an abstraction over the SDK: a port that
had ballooned would have reached the multiplexer.

### Accepted residual: one property read happens under the lock

`Begin` must run under the lock (Decision 5), and `LockRenewalLoop.RunAsync`'s synchronous prefix reads the
expiry delegate — `SessionLockedUntil`, through the port — before its first `await`. So exactly one property
read on the held session happens while `_syncLock` is held. This is recorded rather than omitted, and it is
accepted for four reasons: the loop, its description and the `SessionId` read are all hoisted OUTSIDE the lock,
so this read is all that is left; the property is a cached in-memory value on the SDK receiver, not a broker
round trip; nothing is AWAITED under the lock; and `MessageLockRenewalRegistry.Start` has the identical
residual, accepted there on the record when ADR-0020 landed. A future edit that made the expiry delegate do
real work would invalidate this, which is why the delegate's cost is stated as part of the reasoning and not
assumed.

### Accepted residual: a close that fails is not retried

`IServiceBusHeldSession.CloseAsync` carries no totality guarantee, and `AzureSdkHeldSession` forwards straight
to `ServiceBusSessionReceiver.CloseAsync`, which can fault or be cancelled. Both close sites attempt the close
EXACTLY ONCE against a reference they have already dropped — `ReleaseSessionAsync` nulls `_heldSession` under
the lock before awaiting the close, and the raced-the-close branch awaits `accepted.CloseAsync()` on a session
it never recorded. So a failing close leaves the session FORGOTTEN: no later release can retry it, the AMQP
link is reclaimed when the client's connection scope is disposed rather than here, and the session's lock
lapses at its own expiry instead of being handed back.

**This ordering is INHERITED, not introduced.** The release before this decision nulled `_sessionReceiver`
under the same lock before the same unguarded `await toClose.CloseAsync()`, and its race branch made the same
single unguarded attempt. Nothing here makes a failing close reachable where it was not, or costlier than it
was.

It is accepted rather than fixed because the obvious repair is not obviously an improvement. Retaining a
session whose close FAILED keeps a receiver that answers `IsClosed == true` behind `AcquireSessionAsync`,
`TrySettlingSession` and `IsClosedOrClosing`, so the adapter would re-serve a dead session indefinitely —
every receive throwing, every settlement unreachable — instead of forgetting it and accepting a fresh one
while the abandoned lock lapses on its own in at most one lock duration. Choosing between forget, retry and
retain-until-closed is its own decision about teardown ownership, with its own failure modes to test; it is
not a consequence of giving the renewal one owner, and it is deliberately NOT taken here. What #499 and #500
close is narrower and stands on its own: a release can no longer see a session without its renewal, and no
renewal fault can jump over the close.

## Closed-by-Construction Acceptance Test

> Which class of defect is made impossible, and why?

TWO classes, and they are classes rather than the two reported cases.

**"A release reaches a held session whose renewal it cannot see."** The renewal is recorded in the SAME lock
acquisition that records the session it renews, and strictly BEFORE that renewal begins. A release takes the
same lock and snapshots both fields together. There is no interval in which one is visible and the other is
not, because there is no second assignment to interleave with — and the before-beginning ordering extends that
to the loop that ends synchronously, which is the one case a naive "assign both inside the lock" fix still gets
wrong. Nothing is awaited while the lock is held, so the acquisition itself is not a place a release can wedge
into.

**"A cleanup that a renewal fault can jump over."** The old release had cleanups AFTER an `await` that could
throw, so being right depended on the catch clause enumerating every way a renewal can fail — and
`LockRenewalLoop` faults deliberately for the ones nobody enumerated. What closes the class is that there is no
longer anything to enumerate: `End()` never throws and `Completion` never faults, so the statements after them
are not conditionally reachable. The release path carries NO catch clause, which is the observable form of the
claim — there is no longer a RENEWAL-fault catch clause that can be wrong, because no renewal fault reaches
this path at all.

The class this closes is precisely "a cleanup a RENEWAL FAULT can jump over", and it is closed because the
cleanups were moved AHEAD of the only statement that can still throw. A throw from the close itself is not a
member of this class: it jumps over nothing, since nothing follows it. What it does cost is a separate,
pre-existing residual, recorded above.

**The honest residual.** Both rest on `RenewalLifetime`'s totality rather than on a type-level guarantee: its
`catch (Exception)` and its guarded sink are code a future edit could narrow, and narrowing them would
re-open the second class here and in `MessageLockRenewalRegistry` at the same time. What this change does is
RELOCATE the property into one type that both receive paths go through, where a reader can check it once
instead of at every caller — which is what makes it checkable, not what makes it unwritable. It is the same
qualification ADR-0020 states for the same type, and it applies with the same force.

## Consequences

- **The session path and the non-session path now share the renewal OWNER as well as the renewal POLICY.** They
  already shared `LockRenewalLoop` (ADR-0020, Decision 3); they now also share `RenewalLifetime`. A change to
  how a renewal discharges its obligations changes BOTH paths. That coupling is deliberate — the two had drifted
  apart once already — but it makes `RenewalLifetime` a shared-behaviour surface, and the Renewal Lifetime term
  in `src/Chatter.MessageBrokers.AzureServiceBus/CONTEXT.md` now covers both paths rather than the non-session
  one alone.
- **No public surface changes, and no behaviour changes for a correctly renewing session.** The 7-argument
  public constructor keeps its signature, the two session ports keep their concrete return types, the
  settlement outcomes are untouched, and a session whose renewal runs and is cancelled behaves exactly as
  before. What changes is only what happens on the two paths the issues describe.
- **The session path's session-facing half is now reachable from a test.** ADR-0014's claim that "the renewal
  loop's session-facing side has no test double" is FALSIFIED by this work and a pointer is added there. The
  rest of that rejection stands on its other, independent reason.
- **`RenewalLifetime.Stopped` is unused on the session path.** It exists for the registry, whose membership is
  a dictionary; the session adapter's single field IS its membership, written by exactly two events, so an
  ended renewal has nothing to unrecord. The property is not removed — the non-session path uses it — but a
  reader of the session path should not expect to find it set.
- **Faking the held session means keeping the fake honest about `SdkSessionReceiver`.** A test double answers
  null there, so the adapter answers null from both session ports under test. That is accurate, not a gap: a
  test with no live namespace genuinely holds no SDK session receiver. It does mean the container-keying
  behaviour of Option (ii) cannot be exercised end-to-end from a fake, which is why it is pinned by a
  reflection assertion on the two return types (`MustAnswerTheConcreteSdkSessionReceiverFromBothSessionPorts`)
  rather than by a round trip through the container.

## References

- Issue #499 — *A session lock renewal that faults with an unrecognised exception leaks the held session
  receiver and its lock*. One of the two defects this decision closes, and the one ADR-0020 deliberately
  deferred out of its own scope.
- Issue #500 — *A session's renewal task is published outside the lock that published its CTS, so teardown can
  dispose the source first*. The other.
- ADR-0020 — *Non-session message lock renewal is per delivery and stopped at the delivery-release seam*.
  Source of `LockRenewalLoop`, of `RenewalLifetime`, of the deliberate fault-on-unrecognised-failure behaviour
  that #499 falls out of, and of the record-before-begin invariant this decision mirrors onto the session path.
  Its per-delivery-versus-per-session CARDINALITY decision is unchanged by this work: the session path still
  holds ONE renewal per adapter.
- ADR-0014 — *In-process session concurrency via the Session Multiplexer*. Source of the two session ports this
  decision leaves untouched, and of the record that the renewal loop's session-facing half had no test double.
- `src/Chatter.CQRS/src/Chatter.CQRS/Context/ContextContainer.cs` (`Include<T>` / `TryGet<T>`) and
  `src/Chatter.MessageBrokers.AzureServiceBus/src/Chatter.MessageBrokers.AzureServiceBus/Context/MessageHandlerContextExtensions.cs`
  — the static-type key and the public Session State API it serves, which together are why the SDK escape hatch
  on the port exists.
- ADR-0018 — *Context Container lookup is type-checked*. The decision that governs how that container answers a
  lookup, and the reason a widened static type fails as a MISS rather than as a cast error.
- The Azure Service Bus context's *Session*, *Session State*, *Max Session Lock Renewal Duration* and *Renewal
  Lifetime* terms (`src/Chatter.MessageBrokers.AzureServiceBus/CONTEXT.md`).
