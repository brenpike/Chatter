---
status: accepted
date: 2026-09-24
---

# A caller-requested cancellation is not a dispatch failure

When a dispatch ends in an `OperationCanceledException` while the cancellation token on the context the caller
supplied is signalled, `Chatter.CQRS` now treats it as the routine end of a cancelled dispatch rather than as a failed
one. The dispatcher logs it at `Debug` instead of `Error`, and neither the `dispatch` span nor the
`chatter.cqrs.dispatch.duration` measurement marks it as failed. Every other cancellation is still a dispatch failure.
The message broker receiver's dispatch seam likewise logs a dispatch cut short by its own shutdown at `Debug` instead
of `Error`. This ADR records why the decision keys on the supplied token rather than on the exception type, why a `Debug` record
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

**Amended 2026-09-25 (#529): query dispatch is now instrumented.** The last sentence above describes the query seams
before #529. Each now has a diagnostics wrapper that emits the `dispatch` span and the `chatter.cqrs.dispatch.duration`
measurement, and applies this decision to both signals. See the 2026-09-25 amendment under *Decision*.

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
(`src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Receiving/BrokeredMessageReceiver.Diagnostics.cs:386-388`),
so a clean shutdown does not show up as a burst of failed receives. The CQRS seam had no equivalent. Nor did the
receiver's own dispatch seam: `BrokeredMessageReceiver.DispatchReceivedMessageAsync` wrapped the dispatch in a single
`catch (Exception e)` that passed the exception to `LogError` and rethrew it, so a dispatch cut short by the
receiver's shutdown was logged at `Error` there before the ladder swallowed it.

**What OpenTelemetry says.** No semantic convention covers in-process CQRS dispatch; ADR-0010 D4 already records that
and names the `chatter.` attributes on that basis. The closest rule is in the HTTP span conventions: "If the HTTP
client instrumentation can detect that the request was cancelled intentionally by the caller (e.g., via context
cancellation or an abort signal), the cancellation SHOULD NOT be treated as an error: the span status SHOULD be left
unset and `error.type` SHOULD NOT be set" (<https://opentelemetry.io/docs/specs/semconv/http/http-spans/>). That rule
is written for HTTP client instrumentation, not for a dispatcher, and a general convention for cancelled spans has
been proposed but is still open (open-telemetry/semantic-conventions#560). This decision follows the HTTP rule's
reasoning; it does not claim conformance with a convention that covers CQRS dispatch, because none does.

## Considered Options

The issue named the first three options and asked that one be applied to all four catches in one change. The fourth,
a stricter form of Option 3, was considered afterwards.

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

### Option 4 — Option 3, but also require the fault's own token to be the caller's token (REJECTED)

A stricter predicate would also require the exception's `OperationCanceledException.CancellationToken` to equal the
token on the supplied context, so that a timeout raised while a shutdown is starting could not be read as the shutdown
(residual R4). It was rejected because the token an exception carries is often not the caller's. A handler that links
the caller's token into its own `CancellationTokenSource`, to add a timeout or to stop its own work, throws with the
linked token, and so does any code between the caller's token and the throw that links it the same way. The stricter
test would classify those cancellations as failures again, and would put the main case this decision exists for, a
shutdown that cancels a handler through such a token, back at `Error`. ADR-0010 D11 made the same choice for the
receiver: `IsShutdownCancellation` tests the worker token and the exception's type, not the token the exception
carries. This option was raised again in local review and the rejection was kept; it was the user's decision.

## Decision

**A fault is routine exactly when it is an `OperationCanceledException` and the token on the supplied context is
signalled.** One internal predicate, `CallerRequestedCancellation.Explains`
(`src/Chatter.CQRS/src/Chatter.CQRS/Context/CallerRequestedCancellation.cs`), makes that decision, with one overload
for `IMessageHandlerContext` (`:16-17`) and one for `IQueryHandlerContext` (`:23-24`). Both return `false` for a
`null` context. `TaskCanceledException` derives from `OperationCanceledException` and is covered. The rule is stated,
with its oracle, in the `INVARIANT:` at `CallerRequestedCancellation.cs:26-29`.

**The log.** Command and event dispatch each classify a fault in one place, a private `LogDispatchFault<TMessage>`
method (`src/Chatter.CQRS/src/Chatter.CQRS/Commands/CommandDispatcher.cs:142-156`,
`src/Chatter.CQRS/src/Chatter.CQRS/Events/EventDispatcher.cs:153-167`). It calls `CallerRequestedCancellation.Explains`
once, before anything is logged. When the predicate explains the fault, it makes one `LogDebug` call with the exception
attached (for a command, "Dispatch of command '{MessageType}' was cancelled by the caller.") and returns `false`;
otherwise it makes the existing `LogError` call and returns `true`. The rule that only a cancellation the caller
requested is routine, and that the caller's token is read there and nowhere else on the fault path, is stated in the
`INVARIANT:` at `CommandDispatcher.cs:144-147` and `EventDispatcher.cs:155-158`.

Each command or event dispatch calls `LogDispatchFault` from exactly one catch. The catch in `DispatchToHandler` and
`DispatchToHandlers` is `catch (Exception e) when (handleFault)` (`CommandDispatcher.cs:85-89`,
`EventDispatcher.cs:96-100`). The path taken when diagnostics are off passes `handleFault: true`, so that catch logs the
fault and rethrows it unchanged. The diagnostics wrapper passes `handleFault: false`, so the fault passes through that
frame unlogged and is logged by the wrapper's own catch, described below. That exactly one frame logs a fault is
stated in the `INVARIANT:` at
`CommandDispatcher.cs:81-84` and `EventDispatcher.cs:93-95`.

Query dispatch is not instrumented, so each of the two awaiting `QueryDispatcher` overloads keeps a filtered clause
ahead of its `catch (Exception e)`:
`catch (OperationCanceledException e) when (CallerRequestedCancellation.Explains(e, queryHandlerContext))`. It makes one
`LogDebug` call with the exception attached and rethrows the same exception unchanged. A fault the predicate does not
explain falls through to the existing `LogError` clause, as before. The filter is the only place a query reads the
caller's token for a fault. The clauses are at `src/Chatter.CQRS/src/Chatter.CQRS/Queries/QueryDispatcher.cs:60-68` and
`:88-95`; the `INVARIANT:` on the first states the rule, and the second cites it.

**Amended 2026-09-25 (#529): query dispatch is instrumented, and each query seam now classifies a fault once, as
command and event dispatch do.** The paragraph above no longer describes the code. Neither awaiting `QueryDispatcher`
overload has a filtered `catch (OperationCanceledException e)` clause. Both classify a fault in one private method,
`LogDispatchFault` (`src/Chatter.CQRS/src/Chatter.CQRS/Queries/QueryDispatcher.cs:220-236`). It reads
`CallerRequestedCancellation.Explains` once, makes one `LogDebug` or one `LogError` call as the command and event
methods do, and returns the verdict that decides whether the span and the measurement are marked failed. It takes the
query's `Type` as an argument, not as a type parameter, so that `Query<TResult>` can pass the runtime query type. Its
`INVARIANT:` at `:222-227` states that the caller's token is read there and nowhere else on the fault path of either
overload.

- **`Query<TQuery, TResult>` has the command shape.** Its uninstrumented dispatch, `DispatchToHandler` (`:154-170`),
  ends in one `catch (Exception e) when (handleFault)` (`:165-169`). The off path passes `handleFault: true`. The
  diagnostics wrapper, `DispatchToHandlerWithDiagnostics` (`:172-209`), passes `false` and calls `LogDispatchFault` from
  its own `catch (Exception e)` (`:183-203`), which holds no predicate: it resolves `error.type` and calls
  `ActivityOutcome.RecordFailure` only when `LogDispatchFault` returns `true`. The `finally` records the duration once
  (`:204-207`). That exactly one frame logs is stated in the `INVARIANT:` at `:161-164`; that one verdict decides the
  log and both signals, at `:185-190`; and that the span and the metric are set together, at `:193-197`.
- **`Query<TResult>` needs no `handleFault` flag.** Its off path, `DispatchByRuntimeType` (`:67-86`), and its
  diagnostics wrapper, `DispatchByRuntimeTypeWithDiagnostics` (`:88-134`), each have exactly one frame that logs, their
  own `catch (Exception e)` (`:81-85` and `:110-124`), because the wrapper awaits the cached invoker directly instead of
  calling the off path. The wrapper acts on the verdict in the same way; its `INVARIANT:` is at `:112-116`, and its
  `finally` (`:125-132`) records the duration once, through `ChatterDiagnostics.RecordDispatchDuration(Type, ...)` with
  the runtime query type, while the span is still current.

The oracles, all in `src/Chatter.CQRS/tests/Diagnostics/WhenChatterTracingIsOptedInto.cs` unless another file is named:

- **One verdict, for `Query<TQuery, TResult>`:** `MustMarkTheSpanAsFailedWhenTheCallerTokenIsSignalledOnlyAfterTheQueryFaultWasLoggedAsAnError`
  and `MustMarkTheMeasurementWithAnErrorTypeWhenTheCallerTokenIsSignalledOnlyAfterTheQueryFaultWasLoggedAsAnError`,
  which go red when the wrapper reads `CallerRequestedCancellation.Explains` again instead of using the verdict. No
  such fact dispatches through `Query<TResult>`, so no test goes red if that wrapper reads the predicate again.
- **One logging frame, for `Query<TQuery, TResult>`:** `MustWriteExactlyOneErrorRecordWhenTheCallerTokenIsSignalledOnlyAfterTheQueryFaultWasLoggedAsAnError`
  and `WhenDispatchingGenericQuery.MustLogTheAsynchronousFaultExactlyOnceWhenDiagnosticsAreEnabled`
  (`src/Chatter.CQRS/tests/Queries/UsingQueryDispatcher/WhenDispatchingGenericQuery.cs`), which go red when the
  `handleFault` filter is deleted so both frames log. For `Query<TResult>`,
  `MustLogTheFaultOnceAndMarkTheSpanAndTheMeasurementWithTheErrorTypeWhenNoInvokerCanBeBuiltForTheRuntimeQueryType` counts one record for
  a fault raised while the invoker is built; no test counts the records a handler fault writes on that overload's
  diagnostics path.
- **A caller-requested cancellation is not marked failed:** `MustLeaveTheSpanStatusUnsetWhenTheCallerCancelledTheQueryDispatch`,
  `MustNotMarkTheMeasurementWithAnErrorTypeWhenTheCallerCancelledTheQueryDispatch` and
  `MustStillRecordOneDispatchDurationWhenTheCallerCancelledTheQueryDispatch` for `Query<TQuery, TResult>`, and
  `MustLeaveTheSpanStatusUnsetAndRecordNoErrorTypeWhenTheCallerCancelledAQueryDispatchedByItsRuntimeType` for
  `Query<TResult>`. A cancellation the caller did not request still marks both, pinned for `Query<TQuery, TResult>` by
  `MustMarkTheSpanAndTheMeasurementAsFailedWhenTheQueryCancellationWasNotRequestedByTheCaller`.
- **The log level:** the two query `MustLogErrorNotDebugWhenTheCancellationWasNotRequestedByTheCaller` facts now go red
  when the condition in `LogDispatchFault` is replaced by a bare `fault is OperationCanceledException`, as the command
  and event ones do.

**The telemetry.** The command and event diagnostics wrappers, `DispatchToHandlerWithDiagnostics` and
`DispatchToHandlersWithDiagnostics`, hold no predicate. Their `catch (Exception e)` calls `LogDispatchFault` and acts
on the value it returns: only when it returns `true` do they resolve `error.type` and call
`ActivityOutcome.RecordFailure` (`CommandDispatcher.cs:103-125`, `EventDispatcher.cs:114-136`). A caller-requested
cancellation therefore skips both: the span status stays `Unset`, the span carries no `error.type` tag and no
`exception` event, and the local `errorType` stays `null`. The `finally` block still records the duration measurement
once, without `error.type` (`CommandDispatcher.cs:126-129`, `EventDispatcher.cs:137-140`). The reason one verdict
decides the log and both signals is stated in the `INVARIANT:` at `CommandDispatcher.cs:105-111` and
`EventDispatcher.cs:116-122`; the reason the span and the metric are set together is stated in the one at
`CommandDispatcher.cs:114-119` and `EventDispatcher.cs:125-130`.

**Why one verdict governs the log and the telemetry.** The log and the telemetry must not give different answers about
the same dispatch. ADR-0010 D4 already requires the span status and the metric's `error.type` to come from one resolved
value; this decision keeps that, since the exempt path resolves nothing and both signals read the same `null`. For a
command or an event the log agrees with both signals by construction: the caller's token is read once per fault, in
`LogDispatchFault`, and the wrapper acts on the value that call returned instead of asking the predicate again. If it
asked again, a token signalled between the two reads would leave an `Error` record beside an unmarked span and
measurement. A query has only the log, so its one filter is its only reader. The receiver's dispatch seam still reads
its token more than once; that is residual R3.

**Amended 2026-09-25 (#529): a query is now classified like a command or an event.** The sentence "A query has only
the log, so its one filter is its only reader" is superseded. A query now has the log and both signals, and its
caller's token is read once per fault, in its own `LogDispatchFault`, whose verdict its diagnostics wrapper acts on.
The shape and the oracles are recorded in the 2026-09-25 amendment above.

**Why a `Debug` record rather than none, or `Information`.** A record at `Debug` is invisible at the default
`Information` level, so it adds nothing to a normal shutdown, and it can be recovered by enabling `Debug` for the
`Chatter.CQRS` categories when someone needs to see which dispatches a shutdown cut short. Silence (Option 1) makes
that impossible, and `Information` would put one record per in-flight dispatch into every shutdown's default log
output. This was the user's choice.

**The receiver's dispatch seam.** `BrokeredMessageReceiver.DispatchReceivedMessageAsync` gains a filtered clause ahead
of its `catch (Exception e)`: `catch (Exception e) when (IsShutdownCancellation(e, receiverTokenSource))`. It makes one
`LogDebug` call with the exception attached ("Dispatch of brokered message was cancelled because the receiver is
shutting down."), and rethrows the same exception unchanged, which the worker's ladder then swallows without a log, as
before. Any other fault still reaches the `LogError` clause. `receiverTokenSource` is the worker token, passed
unchanged from the worker through `ProcessMessageAsync`, so the clause reads the same token as the ladder's
shutdown-swallow filters and the diagnostics exemption. The clause is at `BrokeredMessageReceiver.cs:1052-1056`, and
its rule is stated, with its oracles, in the `INVARIANT:` at `:1042-1051`. It keeps `Debug` for the reason given
above.

**One predicate per bounded context.** Each context decides with its own predicate: `CallerRequestedCancellation.Explains`
in `Chatter.CQRS`, and `IsShutdownCancellation` in the receiver. They differ in one respect. The receiver's predicate
also covers an `ObjectDisposedException` raised while its token is signalled, because ADR-0010 D11 records that a
worker whose receiver is torn down underneath it can observe either exception, so under a signalled worker token a
disposed object is the receiver's own teardown. The CQRS predicate does not cover it, because inside a handler a
disposed object is usually a real bug; the consequence is recorded as residual R1. This was the user's choice.

**The context-less overloads.** `IMessageDispatcher.Dispatch<TMessage>(TMessage)` and the two single-argument
`QueryDispatcher` overloads create a context with the default token, which can never be signalled, so a cancellation
in a dispatch made through them is always a failure. A caller who wants its cancellation treated as routine passes a
context carrying its token.

**No public API changes.** `CallerRequestedCancellation` is internal and `IsShutdownCancellation` is private. The log
level and the emitted telemetry change; no type, member or signature does.

## Closed-by-Construction Acceptance Test

> What class of future finding does this make impossible, and why?

**ELIMINATED CLASS: "the log, the span and the metric classify one cancellation differently because each seam decided
for itself".** The decision lives in one predicate, and every place that classifies a fault calls it: the command and
event `LogDispatchFault` methods and the two query clauses. The telemetry holds no predicate of its own. A seam added
later that copies an existing one copies the call to the predicate, not a restatement of its rule.

**ELIMINATED CLASS: "the log and the telemetry classify one fault differently because each read the token
separately".** For a command or an event the caller's token is read once per fault, in `LogDispatchFault`, and the
diagnostics wrapper marks the span and the measurement from the value that call returns. Exactly one frame logs the
fault, because the inner catch is filtered by `handleFault`. Pinned by
`WhenChatterTracingIsOptedInto.MustMarkTheSpanAsFailedWhenTheCallerTokenIsSignalledOnlyAfterTheCommandFaultWasLoggedAsAnError`,
`MustMarkTheMeasurementWithAnErrorTypeWhenTheCallerTokenIsSignalledOnlyAfterTheCommandFaultWasLoggedAsAnError`,
`MustMarkTheSpanAsFailedWhenTheCallerTokenIsSignalledOnlyAfterTheEventFaultWasLoggedAsAnError` and
`MustMarkTheMeasurementWithAnErrorTypeWhenTheCallerTokenIsSignalledOnlyAfterTheEventFaultWasLoggedAsAnError`, which go
red when the wrapper reads `CallerRequestedCancellation.Explains` again instead of using that value; and by
`MustWriteExactlyOneErrorRecordWhenTheCallerTokenIsSignalledOnlyAfterTheCommandFaultWasLoggedAsAnError`,
`MustWriteExactlyOneErrorRecordWhenTheCallerTokenIsSignalledOnlyAfterTheEventFaultWasLoggedAsAnError` and, for
commands, `WhenDispatching.MustLogTheAsynchronousFaultExactlyOnceWhenDiagnosticsAreEnabled`, which go red when the
`handleFault` filter is deleted so both frames log. A query has only the log, so it has one reader already.

**Amended 2026-09-25 (#529): both classes above now cover query dispatch.** The "two query clauses" named in the first
class are gone; each query overload calls the predicate from its `LogDispatchFault`, as command and event dispatch do.
The last sentence of the second class is superseded: a query now has the log and both signals, and it is classified
by one read of the caller's token. Its oracles, and the gaps in them, are listed in the 2026-09-25 amendment under
*Decision*.

**ELIMINATED CLASS: "a genuine timeout is hidden because it looks like a cancellation".** The predicate requires the
caller's token to be signalled. A timeout, or a handler's own linked token, raises the same exception type while the
caller's token is not signalled, and it still logs at `Error` and still marks the span and the measurement failed. The
one exception is a timeout that races the caller's cancellation, recorded as R4.

**ELIMINATED CLASS: "the receiver's ladder, its receive metric and its dispatch log classify one shutdown cancellation
differently".** The receiver has three readers of one condition. The dispatch seam's log clause and the diagnostics
exemption for a delivery fault both call `IsShutdownCancellation` (`BrokeredMessageReceiver.cs:1052`;
`BrokeredMessageReceiver.Diagnostics.cs:262`), and the ladder's two shutdown-swallow filters
(`BrokeredMessageReceiver.cs:877-882`) test the same thing, which the predicate mirrors as ADR-0010 D11 records. All
three read the worker token. Each reads it at its own point, so the only divergence left is the race recorded as R3.

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
  and in both query files. The command and event ones go red when the condition in `LogDispatchFault` is replaced by a
  bare `fault is OperationCanceledException`; each query one goes red when its seam's filter is widened to
  `catch (OperationCanceledException)` with no predicate.

  **Amended 2026-09-25 (#529): the query seams have no such filter any more.** Each query one now goes red, like the
  command and event ones, when the condition in the query `LogDispatchFault` is replaced by a bare
  `fault is OperationCanceledException`.
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

  **Amended 2026-09-25 (#529): this now holds for a Query dispatch too.** A caller-cancelled query dispatch, through
  either overload, leaves the span status unset and records the measurement once without `error.type`; a cancellation
  the caller did not request marks both. The query oracles are listed in the 2026-09-25 amendment under *Decision*.
- **A Command or Event fault is logged once and classified once, even when the caller's token is signalled after the
  fault was logged at `Error`.** The span and the measurement are then marked failed, in agreement with the `Error`
  record, and no second record is written. Pinned by the six `WhenChatterTracingIsOptedInto` facts whose names end
  `WhenTheCallerTokenIsSignalledOnlyAfterTheCommandFaultWasLoggedAsAnError` or
  `WhenTheCallerTokenIsSignalledOnlyAfterTheEventFaultWasLoggedAsAnError`, named under the second eliminated class
  above, and by `WhenDispatching.MustLogTheAsynchronousFaultExactlyOnceWhenDiagnosticsAreEnabled`
  (`src/Chatter.CQRS/tests/Commands/UsingCommandDispatcher/WhenDispatching.cs`).
- **Applications that alert on `error.type=System.OperationCanceledException` or `System.Threading.Tasks.TaskCanceledException`
  from `chatter.cqrs.dispatch.duration` stop seeing caller-cancelled dispatches in that series.** This is intended: a
  cancelled dispatch is still counted in the histogram, only without `error.type`. A cancellation the caller did not
  request still carries it. This was the user's explicit decision, accepted with the change to the alert series.
- **A broker-delivered dispatch cut short by the receiver's shutdown writes one `Debug` record at the receiver's
  dispatch seam instead of an `Error` record, and the same exception is rethrown.** This holds for an
  `OperationCanceledException` and for an `ObjectDisposedException`. Pinned by
  `WhenDispatchingReceivedMessage.MustLogAShutdownCancelledDispatchAtDebugInsteadOfError`,
  `MustLogAShutdownObjectDisposedExceptionAtDebugInsteadOfError` and `MustRethrowTheShutdownCancellationUnchanged`
  (`src/Chatter.MessageBrokers/tests/Receiving/UsingBrokeredMessageReceiver/WhenDispatchingReceivedMessage.cs`). The
  receiver still logs at `Error` a cancellation or a disposal raised while its token is not signalled, and any other
  fault raised while it is, pinned by `MustStillLogErrorWhenTheCancellationWasNotRequestedByTheReceiverShutdown`,
  `MustStillLogErrorForAnObjectDisposedExceptionWhenTheReceiverIsNotShuttingDown` and
  `MustStillLogErrorForANonCancellationFaultWhileTheReceiverIsShuttingDown`.

### Recorded residuals

These are decisions, not open work, and no issues are filed for them.

- **R1: an `ObjectDisposedException` raised while the caller's token is signalled is still a dispatch failure at the
  CQRS seam.** The receiver's `IsShutdownCancellation` exempts it, and the CQRS predicate does not. Inside a handler, a
  disposed object is usually a real bug, and treating it as routine would hide that bug whenever it happens to coincide
  with a shutdown. This was the user's choice. The consequence is that during a drain the CQRS seam can log such a
  fault at `Error` and mark the `dispatch` span and the measurement failed, while the receiver's own receive metric
  does not mark the same delivery failed. Pinned by `WhenDeciding.MustNotExplainObjectDisposedExceptionEvenWhenTokenIsSignalled`.
  Because the receiver's dispatch seam also reads `IsShutdownCancellation`, the split reaches the log as well: that
  fault gets an `Error` record from `Chatter.CQRS` and a `Debug` record from the receiver. The receiver's half is
  pinned by `WhenDispatchingReceivedMessage.MustLogAShutdownObjectDisposedExceptionAtDebugInsteadOfError`.
- **R2: an `AggregateException` that wraps an `OperationCanceledException` is not unwrapped.** The predicate tests the
  fault's own type, so such a fault stays an `Error` whatever the token says. The case is bounded to a handler that
  blocks on a task with `.Wait()` or `.Result`; one that awaits does not produce this shape. Leaving it unwrapped was the
  user's explicit decision, accepted on its merits. No test pins this case.
- **R3: the receiver's readers of its worker token can disagree about one fault during a shutdown drain.** This is
  inherited from ADR-0010 D11, which gave the receiver more than one reader of its token. The dispatch seam's log
  clause reads it first, and the diagnostics exemption and the ladder's shutdown swallows read it later. A token can
  only go from unsignalled to signalled, so a token signalled between those reads gives an `Error` record at the
  receiver's dispatch seam beside a receive that is not marked failed and is swallowed by the ladder. The reverse, a
  `Debug` record beside a receive marked failed, cannot happen. The skew is bounded to a fault raised while a shutdown
  drain is starting. Restructuring the receiver so that one read decides all three was considered and rejected by the
  user on its merits. No test pins this race. The same race at the CQRS seam is closed by construction, as the second
  eliminated class above records: a command or event fault is classified by one read, and its oracles are named there.
- **R4: a cancellation raised spontaneously is classified as routine if the caller's token is signalled before the
  fault is classified.** The predicate reads the token when the fault is observed, not when it was raised, and does
  not ask which token the exception carries, so a timeout that races a shutdown can be classified as the shutdown:
  logged at `Debug` and not marked failed. The receiver's `IsShutdownCancellation` makes the same trade, since it also
  reads its token when it observes the fault. The stricter test that would close this was rejected as Option 4. No
  test pins this race.

## References

- Issue #453 — *Cancellation is logged as a dispatch error at all four Chatter.CQRS dispatch seams*. The defect this
  ADR resolves, and the source of the three options.
- Issue #415 — the fix that made `CommandDispatcher.DispatchToHandler` `async` in 0.14.0 and so brought the command
  seam into this behaviour.
- ADR-0010 — *Optional BCL-only telemetry: per-assembly instrumentation scopes and the off-guard*. D4 for the one
  resolved `error.type` value shared by the span and the metric, and for the absence of a CQRS semantic convention;
  D11 for the receiver's shutdown-cancellation exemption, and D11's 2026-09-25 amendment for the receiver's `Debug`
  record.
- ADR-0012 — *Event fan-out: abort on the first failing handler, documented rather than aggregated*. A cancelled event
  dispatch still ends at the handler that raised it and rethrows unchanged.
- ADR-0027 — *An `INVARIANT:` comment names the oracle that falsifies it, and states its rationale once*. Rule 2 is
  why this ADR cites the `INVARIANT:` blocks rather than restating them.
- OpenTelemetry semantic conventions, HTTP spans, status —
  <https://opentelemetry.io/docs/specs/semconv/http/http-spans/>.
- open-telemetry/semantic-conventions#560 — *Convention for cancelled spans*, open.
- `src/Chatter.CQRS/src/Chatter.CQRS/Context/CallerRequestedCancellation.cs` — the predicate (`:16-31`) and its
  `INVARIANT:` (`:26-29`).
- `src/Chatter.CQRS/src/Chatter.CQRS/Commands/CommandDispatcher.cs` — the `handleFault` catch in `DispatchToHandler`
  and its `INVARIANT:` (`:81-89`), the diagnostics wrapper's catch (`:103-125`) with its two `INVARIANT:` blocks
  (`:105-111`, `:114-119`), and `LogDispatchFault` (`:142-156`) with its `INVARIANT:` (`:144-147`).
- `src/Chatter.CQRS/src/Chatter.CQRS/Events/EventDispatcher.cs` — the `handleFault` catch in `DispatchToHandlers` and
  its `INVARIANT:` (`:93-100`), the diagnostics wrapper's catch (`:114-136`) with its two `INVARIANT:` blocks
  (`:116-122`, `:125-130`), and `LogDispatchFault` (`:153-167`) with its `INVARIANT:` (`:155-158`).
- `src/Chatter.CQRS/src/Chatter.CQRS/Queries/QueryDispatcher.cs` — the two log clauses (`:60-68`, `:88-95`).

  **Amended 2026-09-25 (#529): re-measured.** The two log clauses are gone. `DispatchByRuntimeType` (`:67-86`) with its
  catch (`:81-85`); `DispatchByRuntimeTypeWithDiagnostics` (`:88-134`) with its catch (`:110-124`) and that catch's
  `INVARIANT:` (`:112-116`); the `handleFault` catch in `DispatchToHandler` and its `INVARIANT:` (`:161-169`); the
  diagnostics wrapper's catch (`:183-203`) with its two `INVARIANT:` blocks (`:185-190`, `:193-197`); and
  `LogDispatchFault` (`:220-236`) with its `INVARIANT:` (`:222-227`).
- `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Receiving/BrokeredMessageReceiver.cs` — the receive call with
  the loop token (`:708`), the ladder's shutdown swallows (`:877-882`) and `DispatchReceivedMessageAsync`
  (`:1034-1062`), with its shutdown log clause (`:1052-1056`) and that clause's `INVARIANT:` (`:1042-1051`).
- `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Receiving/BrokeredMessageReceiver.Diagnostics.cs` —
  `IsShutdownCancellation` (`:386-388`) and the delivery-fault exemption that calls it (`:262`).
- `src/Chatter.MessageBrokers/tests/Receiving/UsingBrokeredMessageReceiver/WhenDispatchingReceivedMessage.cs` — the
  receiver dispatch seam's oracles.
