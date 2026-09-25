---
status: accepted
date: 2026-09-24
---

# A caller-requested cancellation is not a dispatch failure

When a dispatch ends in an `OperationCanceledException` while the cancellation token on the context the caller
supplied is signalled, `Chatter.CQRS` now treats it as the routine end of a cancelled dispatch rather than as a failed
one. The dispatcher logs it at `Debug` instead of `Error`, and neither the `dispatch` span nor the
`chatter.cqrs.dispatch.duration` measurement marks it as failed. Every other cancellation is still a dispatch failure.
This ADR records why the decision keys on the supplied token rather than on the exception type, why a `Debug` record
is kept, how the decision lines up with the message broker receiver's own shutdown handling, and what it leaves as it
was.

Issue #453.

## Context

**All four dispatch catches treated cancellation as a failure.** `CommandDispatcher.DispatchToHandler`,
`EventDispatcher.DispatchToHandlers` and both awaiting `QueryDispatcher` overloads,
`Query<TResult>(IQuery<TResult>, IQueryHandlerContext)` and `Query<TQuery, TResult>(TQuery, IQueryHandlerContext)`,
each ended in one `catch (Exception e)` that passed the exception to `LogError` and rethrew it. An
`OperationCanceledException` or `TaskCanceledException` raised by a cooperating handler during a normal host shutdown
took that path, so each dispatch in flight at shutdown wrote an `Error` record. The command and event diagnostics
wrappers, `DispatchToHandlerWithDiagnostics` and `DispatchToHandlersWithDiagnostics`, did the same on the telemetry
side: they set the span status to `Error`, tagged the span with `error.type`, added the `exception` span event and
recorded the duration measurement with `error.type`. Query dispatch is not instrumented, so the query seams had only
the log.

**How long each seam has behaved this way.** The event seam and both query seams have logged cancellation as an error
since before 0.14.0, because those methods were already `async` and so already caught faults raised after an `await`.
The command seam joined them in 0.14.0. Before that release `CommandDispatcher.DispatchToHandler` returned the
handler's `Task` from inside its `try` without awaiting it, so a fault raised after an `await` was logged nowhere
(#415). Making it `async` fixed that and, as a side effect, routed asynchronous cancellations into the catch along
with every other asynchronous fault. The issue was found in pre-PR review of that work and filed rather than patched
there, so that all four catches would change together.

**What the token on the context is, under a broker receiver.** `BrokeredMessageReceiver` passes its receive loop's
token to `IMessagingInfrastructureReceiver.ReceiveMessageAsync`
(`src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Receiving/BrokeredMessageReceiver.cs:708`), and the Azure
Service Bus, RabbitMQ and SQL Service Broker receivers build the `MessageBrokerContext` for the delivery with that
token. `MessageBrokerContext` hands it to the `MessageHandlerContext` base constructor
(`src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Context/MessageBrokerContext.cs:26-27`), and it is that
context the receiver dispatches with. So for a broker-delivered Command or Event, the token on the context at the
CQRS dispatch seam is the receiver's shutdown token.

**What the receiver already does at shutdown (ADR-0010 D11).** The worker's error ladder swallows an
`OperationCanceledException` or an `ObjectDisposedException` when its worker token is signalled, with an empty catch
body and no log (`BrokeredMessageReceiver.cs:877-882`). The receiver's diagnostics exempt exactly that case from the
receive metric's `error.type` through `IsShutdownCancellation`
(`src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Receiving/BrokeredMessageReceiver.Diagnostics.cs:382-384`),
so a clean shutdown does not show up as a burst of failed receives. The CQRS seam had no equivalent.

**What OpenTelemetry says.** No semantic convention covers in-process CQRS dispatch; ADR-0010 D4 already records that
and names the `chatter.` attributes on that basis. The closest rule is in the HTTP span conventions: "If the HTTP
client instrumentation can detect that the request was cancelled intentionally by the caller (e.g., via context
cancellation or an abort signal), the cancellation SHOULD NOT be treated as an error: the span status SHOULD be left
unset and `error.type` SHOULD NOT be set" (<https://opentelemetry.io/docs/specs/semconv/http/http-spans/>). That rule
is written for HTTP client instrumentation, not for a dispatcher, and a general convention for cancelled spans has
been proposed but is still open (open-telemetry/semantic-conventions#560). This decision follows the HTTP rule's
reasoning; it does not claim conformance with a convention that covers CQRS dispatch, because none does.

## Considered Options

The issue named three options and asked that one be applied to all four catches in one change.

### Option 1 — catch `OperationCanceledException` ahead of the general catch and rethrow without logging (REJECTED)

It removes the only record that a dispatch was cut short, and it cannot tell a shutdown from a handler that cancelled
itself for its own reasons. Both kinds would vanish from the log.

### Option 2 — the same, but log every cancellation at `Debug` or `Information` (REJECTED)

It demotes every cancellation, including the ones that are real failures. An `HttpClient` timeout surfaces as a
`TaskCanceledException`, and a handler that cancels its own linked `CancellationTokenSource` surfaces as an
`OperationCanceledException`, while the caller's token is not signalled. Those are outages, and demoting them would
hide them from error-level logging and from any alert keyed on `error.type`.

### Option 3 — treat a cancellation as routine only when the supplied token is signalled (ACCEPTED)

Recorded under *Decision*.

## Decision

**A fault is routine exactly when it is an `OperationCanceledException` and the token on the supplied context is
signalled.** One internal predicate, `CallerRequestedCancellation.Explains`
(`src/Chatter.CQRS/src/Chatter.CQRS/Context/CallerRequestedCancellation.cs`), makes that decision, with one overload
for `IMessageHandlerContext` (`:16-17`) and one for `IQueryHandlerContext` (`:23-24`). Both return `false` for a
`null` context. `TaskCanceledException` derives from `OperationCanceledException` and is covered. The rule is stated,
with its oracle, in the `INVARIANT:` at `CallerRequestedCancellation.cs:26-29`.

**The log.** Each of the four dispatch catches gains a filtered clause ahead of `catch (Exception e)`:
`catch (OperationCanceledException e) when (CallerRequestedCancellation.Explains(e, context))`. It makes one `LogDebug`
call with the exception attached (for a command, "Dispatch of command '{MessageType}' was cancelled by the caller."),
and rethrows the same exception unchanged. A fault the predicate does not explain falls through to the existing `LogError`
clause, as before. The clauses are at `src/Chatter.CQRS/src/Chatter.CQRS/Commands/CommandDispatcher.cs:81-89`,
`src/Chatter.CQRS/src/Chatter.CQRS/Events/EventDispatcher.cs:92-100` and
`src/Chatter.CQRS/src/Chatter.CQRS/Queries/QueryDispatcher.cs:60-68` and `:88-94`. The rule that only a cancellation
the caller requested is routine is stated in the `INVARIANT:` on each of the first three; the second query clause
cites the first.

**The telemetry.** The command and event diagnostics wrappers gain the same filtered clause ahead of their
`catch (Exception e)`, with a bare `throw;` as its body (`CommandDispatcher.cs:108-111`, `EventDispatcher.cs:119-122`).
A caller-requested cancellation therefore skips both the `error.type` resolution and `ActivityOutcome.RecordFailure`:
the span status stays `Unset`, the span carries no `error.type` tag and no `exception` event, and the local `errorType`
stays `null`. The `finally` block still records the duration measurement once, without `error.type`
(`CommandDispatcher.cs:124-126`, `EventDispatcher.cs:135-137`). The reason one clause skips both signals is stated in
the `INVARIANT:` at `CommandDispatcher.cs:114-119` and `EventDispatcher.cs:125-130`.

**Why one predicate governs both.** The log and the telemetry must not give different answers about the same
dispatch. ADR-0010 D4 already requires the span status and the metric's `error.type` to come from one resolved value;
this decision keeps that, since the exempt path resolves nothing and both signals read the same `null`. Using the same
predicate for the log means the three signals classify a cancellation by one rule. The one way they can still differ
is recorded as residual R3.

**Why a `Debug` record rather than none, or `Information`.** A record at `Debug` is invisible at the default
`Information` level, so it adds nothing to a normal shutdown, and it can be recovered by enabling `Debug` for the
`Chatter.CQRS` categories when someone needs to see which dispatches a shutdown cut short. Silence (Option 1) makes
that impossible, and `Information` would put one record per in-flight dispatch into every shutdown's default log
output. This was the user's choice.

**The context-less overloads.** `IMessageDispatcher.Dispatch<TMessage>(TMessage)` and the two single-argument
`QueryDispatcher` overloads create a context with the default token, which can never be signalled, so a cancellation
in a dispatch made through them is always a failure. A caller who wants its cancellation treated as routine passes a
context carrying its token.

**No public API changes.** `CallerRequestedCancellation` is internal. The log level and the emitted telemetry change;
no type, member or signature does.

## Closed-by-Construction Acceptance Test

> What class of future finding does this make impossible, and why?

**ELIMINATED CLASS: "the log, the span and the metric classify one cancellation differently because each seam decided
for itself".** The decision lives in one predicate, and every seam that classifies a fault as routine calls it: the
four log clauses and the two telemetry clauses. A seam added later that copies the existing clause copies the call to
the predicate, not a restatement of its rule. The only divergence left is the race recorded as R3, which comes from
reading a token twice, not from two rules.

**ELIMINATED CLASS: "a genuine timeout is hidden because it looks like a cancellation".** The predicate requires the
caller's token to be signalled. A timeout, or a handler's own linked token, raises the same exception type while the
caller's token is not signalled, and it still logs at `Error` and still marks the span and the measurement failed.

**What it does NOT close** is listed under *Recorded residuals*.

## Consequences

- **A dispatch the caller cancelled writes one `Debug` record instead of an `Error` record, and the same exception
  still reaches the caller.** Pinned for commands by
  `WhenDispatching.MustRethrowACallerRequestedCancellationAndLogItOnceAtDebugInsteadOfError` and
  `MustAttachTheCallerRequestedCancellationToTheDebugRecordWithoutWritingItsStackTraceIntoTheMessage`
  (`src/Chatter.CQRS/tests/Commands/UsingCommandDispatcher/WhenDispatching.cs`); for events, whether the first handler
  or a later one raised it, by `WhenDispatching.MustRethrowACallerRequestedCancellationFromTheFirstHandlerAndLogItOnceAtDebugInsteadOfError`
  and `MustLogACallerRequestedCancellationFromALaterHandlerOnceAtDebugInsteadOfError`
  (`src/Chatter.CQRS/tests/Events/UsingEventDispatcher/WhenDispatching.cs`); and for each query overload by
  `MustRethrowACallerRequestedCancellationAndLogItOnceAtDebugInsteadOfError` in `WhenDispatchingStrongTypedQuery` and
  `WhenDispatchingGenericQuery` (`src/Chatter.CQRS/tests/Queries/UsingQueryDispatcher/`).
- **A cancellation the caller did not request still logs at `Error`.** Pinned at every seam by
  `MustLogErrorNotDebugWhenTheCancellationWasNotRequestedByTheCaller`, in the command and event `WhenDispatching` files
  and in both query files. Each goes red when its seam's filter is widened to `catch (OperationCanceledException)`
  with no predicate.
- **The predicate's rule is pinned directly** by `WhenDeciding`
  (`src/Chatter.CQRS/tests/Context/UsingCallerRequestedCancellation/WhenDeciding.cs`):
  `MustExplainOperationCanceledExceptionWhenMessageHandlerContextTokenIsSignalled`,
  `MustExplainOperationCanceledExceptionWhenQueryHandlerContextTokenIsSignalled`,
  `MustExplainTaskCanceledExceptionWhenTokenIsSignalled`, `MustNotExplainOperationCanceledExceptionWhenTokenIsNotSignalled`,
  `MustNotExplainOperationCanceledExceptionWhenQueryHandlerContextTokenIsNotSignalled`,
  `MustNotExplainANonCancellationFaultEvenWhenTokenIsSignalled`, `MustNotExplainObjectDisposedExceptionEvenWhenTokenIsSignalled`,
  `MustNotExplainANullFaultEvenWhenTokenIsSignalled`, and the two `null`-context cases.
- **A caller-cancelled Command or Event dispatch is not marked failed on the span or the measurement.** Pinned for
  commands by `WhenChatterTracingIsOptedInto.MustLeaveTheSpanStatusUnsetWhenTheCallerCancelledTheCommandDispatch`,
  `MustNotTagTheSpanWithAnErrorTypeWhenTheCallerCancelledTheCommandDispatch`,
  `MustNotAddTheExceptionEventWhenTheCallerCancelledTheCommandDispatch`,
  `MustNotMarkTheMeasurementWithAnErrorTypeWhenTheCallerCancelledTheCommandDispatch` and
  `MustStillRecordOneDispatchDurationWhenTheCallerCancelledTheCommandDispatch`; for events by
  `MustNotMarkTheSpanAsFailedWhenTheCallerCancelledTheEventDispatch` and
  `MustRecordOneDispatchDurationWithoutAnErrorTypeWhenTheCallerCancelledTheEventDispatch`
  (`src/Chatter.CQRS/tests/Diagnostics/WhenChatterTracingIsOptedInto.cs`). A cancellation the caller did not request
  still marks both, pinned by `MustMarkTheSpanAndTheMeasurementAsFailedWhenTheCommandCancellationWasNotRequestedByTheCaller`
  and `MustMarkTheSpanAndTheMeasurementAsFailedWhenTheEventCancellationWasNotRequestedByTheCaller`.
- **Applications that alert on `error.type=System.OperationCanceledException` or `System.Threading.Tasks.TaskCanceledException`
  from `chatter.cqrs.dispatch.duration` stop seeing caller-cancelled dispatches in that series.** This is intended: a
  cancelled dispatch is still counted in the histogram, only without `error.type`. A cancellation the caller did not
  request still carries it.
- **A broker-delivered dispatch cancelled at shutdown is still passed to `LogError` once by the receiver.**
  `BrokeredMessageReceiver.DispatchReceivedMessageAsync` wraps the dispatch in its own `catch (Exception e)` that calls
  `LogError` and rethrows (`BrokeredMessageReceiver.cs:1034-1047`), and it has no cancellation clause, so it logs the
  rethrown cancellation at `Error` before the worker's ladder swallows it without a log. This decision changes the
  four CQRS catches, as #453 scoped it; the receiver's catch in `Chatter.MessageBrokers` is unchanged. No test pins the
  receiver's record for this case.

### Recorded residuals

These are decisions, not open work, and no issues are filed for them.

- **R1: an `ObjectDisposedException` raised while the caller's token is signalled is still a dispatch failure at the
  CQRS seam.** The receiver's `IsShutdownCancellation` exempts it, and the CQRS predicate does not. Inside a handler, a
  disposed object is usually a real bug, and treating it as routine would hide that bug whenever it happens to coincide
  with a shutdown. This was the user's choice. The consequence is that during a drain the CQRS seam can log such a
  fault at `Error` and mark the `dispatch` span and the measurement failed, while the receiver's own receive metric
  does not mark the same delivery failed. Pinned by `WhenDeciding.MustNotExplainObjectDisposedExceptionEvenWhenTokenIsSignalled`.
- **R2: an `AggregateException` that wraps an `OperationCanceledException` is not unwrapped.** The predicate tests the
  fault's own type, so such a fault stays an `Error` whatever the token says. A handler that blocks on a task with
  `.Wait()` or `.Result` produces this shape; one that awaits does not. No test pins this case.
- **R3: a cancellation raised spontaneously is classified as routine if the caller's token is signalled before the
  predicate reads it.** The predicate reads the token when the fault is observed, not when it was raised, so a timeout
  that races a shutdown can be classified as the shutdown. The receiver's `IsShutdownCancellation` makes the same
  trade, since it also reads its token when it observes the fault. When diagnostics are on, the predicate is read
  twice for one fault, first in the log clause and then in the diagnostics wrapper, and a token can only go from
  unsignalled to signalled. So if the token is signalled between the two reads, the dispatch is logged at `Error` but
  not marked failed on the span or the measurement. The opposite split, a `Debug` record with a failed span, cannot
  happen. No test pins the race.

## References

- Issue #453 — *Cancellation is logged as a dispatch error at all four Chatter.CQRS dispatch seams*. The defect this
  ADR resolves, and the source of the three options.
- Issue #415 — the fix that made `CommandDispatcher.DispatchToHandler` `async` in 0.14.0 and so brought the command
  seam into this behaviour.
- ADR-0010 — *Optional BCL-only telemetry: per-assembly instrumentation scopes and the off-guard*. D4 for the one
  resolved `error.type` value shared by the span and the metric, and for the absence of a CQRS semantic convention;
  D11 for the receiver's shutdown-cancellation exemption.
- ADR-0012 — *Event fan-out: abort on the first failing handler, documented rather than aggregated*. A cancelled event
  dispatch still ends at the handler that raised it and rethrows unchanged.
- ADR-0027 — *An `INVARIANT:` comment names the oracle that falsifies it, and states its rationale once*. Rule 2 is
  why this ADR cites the `INVARIANT:` blocks rather than restating them.
- OpenTelemetry semantic conventions, HTTP spans, status —
  <https://opentelemetry.io/docs/specs/semconv/http/http-spans/>.
- open-telemetry/semantic-conventions#560 — *Convention for cancelled spans*, open.
- `src/Chatter.CQRS/src/Chatter.CQRS/Context/CallerRequestedCancellation.cs` — the predicate (`:16-31`) and its
  `INVARIANT:` (`:26-29`).
- `src/Chatter.CQRS/src/Chatter.CQRS/Commands/CommandDispatcher.cs` — the log clause (`:81-89`), the telemetry clause
  (`:108-111`) and its `INVARIANT:` (`:114-119`).
- `src/Chatter.CQRS/src/Chatter.CQRS/Events/EventDispatcher.cs` — the log clause (`:92-100`), the telemetry clause
  (`:119-122`) and its `INVARIANT:` (`:125-130`).
- `src/Chatter.CQRS/src/Chatter.CQRS/Queries/QueryDispatcher.cs` — the two log clauses (`:60-68`, `:88-94`).
- `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Receiving/BrokeredMessageReceiver.cs` — the receive call with
  the loop token (`:708`), the ladder's shutdown swallows (`:877-882`) and `DispatchReceivedMessageAsync` (`:1034-1047`).
- `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Receiving/BrokeredMessageReceiver.Diagnostics.cs` —
  `IsShutdownCancellation` (`:382-384`).
