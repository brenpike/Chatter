---
status: accepted
date: 2026-09-24
---

# A terminal receive outcome ends its conversation, and a deterministic SQL fault is not retried

`SqlServiceBrokerReceiver` had two ways of never finishing. A message it decided not to dispatch was discarded by
committing the `RECEIVE` and logging at `Trace`, leaving the dialog it arrived on open forever — and a Service Broker
`Error` message, which reports a dialog that has already faulted, was not recognised at all and took the dispatch
path. Separately, SQL error `208` "invalid object name" was classified as transient, so a receiver pointed at a queue
that does not exist retried it for the life of the process. This ADR records the two decisions that close those, and
the residual risk each leaves.

Issues #357 and #358, under epic #304.

## Context

**A Service Broker dialog is settled by the application, not by the queue.** `RECEIVE` removes a message from a
queue; it does not end the conversation the message arrived on. The conversation endpoint stays in
`sys.conversation_endpoints` until one side issues `END CONVERSATION`. So a receive outcome that consumes a message
and dispatches nothing leaks one endpoint per occurrence unless it ends the dialog itself.

**Three receive outcomes consume a real message and dispatch nothing.** A Service Broker `Error` message
(`http://schemas.microsoft.com/SQL/ServiceBroker/Error`), a message whose type is neither
`//Chatter/BrokeredMessage` nor `DEFAULT`, and an accepted type whose body is `NULL`. None of them is retried: the
`RECEIVE` is committed, so the message is gone, and nothing later in the pipeline is ever handed the conversation
handle. The end-dialog outcome already ended its conversation; the three discards did not.

**An `Error` message was worse than leaked.** When one side issues `END CONVERSATION ... WITH ERROR`, Service Broker
delivers an `Error` system message to the other side and parks that endpoint in the `ER` state. The classifier had no
arm for it, so it fell through to the type filter, and — being neither an accepted type nor an end-dialog — it was
discarded silently at `Trace` with its diagnostic payload unread, or, on the path that existed before the classifier
was extracted, reached the dispatcher as if it were a Chatter envelope.

**`208` is not transient in a package that provisions nothing.**
`Chatter.MessageBrokers.SqlServiceBroker` is a transport over Service Broker objects the application creates
(README §SQL Setup); it creates none of them. Both predicate providers nevertheless listed `208` alongside the
genuinely transient numbers, and `SqlServiceBrokerReceiver` caught only `102` as critical. A host started before its
queue existed therefore sat in the core recovery pipeline retrying a fault that no retry can change, reporting
nothing an operator could act on.

## Considered Options

### Option A — a terminal outcome ends its conversation; a deterministic error number is terminal (ACCEPTED)

Recorded under *Decision*.

### Option B — end the conversation only for the `Error` outcome (REJECTED)

It fixes the outcome that was reported and leaves the other two discards leaking, because the leak has nothing to do
with `Error`: it follows from consuming a message and dispatching nothing. Its answer to the Closed-by-Construction
question is a list of message types, and the next system message type Service Broker defines is the next finding.

### Option C — enumerate terminal error numbers at each call site (REJECTED)

The receiver's critical filter, the retry provider and the circuit-breaker provider each already knew their own set of
numbers, which is how `208` came to be terminal in one place and transient in two others. Three lists that must agree
are three chances to disagree.

### Option D — an opt-in "retry until the queue is provisioned" option (REJECTED)

It adds public configuration surface to re-create the behaviour being removed, for a scenario a host restart policy
already covers: a receiver that fails fast with a named queue is restarted by its host, and succeeds as soon as the
queue exists. It also leaves the misconfiguration it is designed to tolerate invisible for exactly as long as it
tolerates it.

## Decision

### 1. A terminal receive outcome ends its conversation

**Every outcome that settles a received message ends that message's conversation.** Under a transactional mode the
`END CONVERSATION` and the `RECEIVE` share one transaction and commit together; under `TransactionMode.None`
`SqlServiceBrokerReceiver.CreateTransaction` returns `null`, so each statement is its own autocommit and the two are
not atomic (residual RES-1 below). The rule is one predicate over the classification outcome,
`ServiceBrokerMessageClassifier.EndsConversation`, and `SqlServiceBrokerReceiver.DiscardMessageAsync` issues
`EndDialogConversationCommand` on the receive session's connection and transaction before it commits. The rationale is
recorded once, at that method's `INVARIANT:` (ADR-0027), and the predicate is pinned by
`WhenClassifyingReceivedMessages.MustEndTheConversationForEveryOutcomeThatSettlesAReceivedMessage` with
`MustCoverEveryClassificationOutcomeInTheEndsConversationRows` requiring a stated decision for every outcome that
exists. `Integration.SsbErroredConversationTests.AnErroredConversationIsEndedRatherThanLeftInTheErrorState` and
`.ANullBodyDiscardEndsItsConversation` pin it at the live-SQL edge.

- **Why ending is safe.** The receiver reads its own queue, so the endpoint it ends is its own side of the dialog;
  ending the target side is always legal, whoever the initiator is.
- **Why ending is necessary.** A discard commits the `RECEIVE`, so the message is never redelivered and no later
  stage is handed the handle. If the discard does not settle the dialog, nothing ever will.
- **Nack is the deliberate exception.** A nack rolls the receive transaction back precisely so the message is
  redelivered, which requires the dialog to stay open. It settles nothing, so the rule does not reach it — as it does
  not reach the two dispatch outcomes, which are settled later by their ack, nack or deadletter.
- **`DiscardNull` is excluded because there is nothing to end.** An empty `RECEIVE` from an idle `WAITFOR` timeout
  yields no message and therefore no conversation handle.
- **The rule is keyed on the outcome, not on the message type.** The set of Service Broker system message types is
  not ours to close; the set of classification outcomes is, so the decision is expressed over the enum. A future
  outcome with no arm in the receiver's switch reaches a fail-closed `default` that throws
  `CriticalReceiverException` rather than falling through into dispatch, which is how the `Error` message reached the
  dispatcher in the first place.
- **An `Error` discard is reported at `Error` with its payload projected into structured fields; every other discard
  stays at `Trace`.** A faulted conversation is an operator-visible fault carrying a Service Broker diagnostic, and
  the routine discards are not. **Those bytes are untrusted.** The dialog peer chooses the `DESCRIPTION` text of
  `END CONVERSATION ... WITH ERROR`, and Service Broker copies it verbatim into the `Error` body it delivers here, so
  a peer that embeds a newline or a carriage return in its description writes whatever it likes into this host's log
  as if the host had logged it. This is the rationale the comments on `ServiceBrokerErrorPayload` and on the
  receiver's `DiscardErroredConversation` arm cite rather than restate (ADR-0027). So
  `ServiceBrokerErrorPayload.Describe` never hands a log a free-form string: it refuses a body over 64 KiB outright,
  decodes it with a strict `UnicodeEncoding` that refuses a body whose bytes are not valid UTF-16 rather than
  repairing them into a replacement character, parses what remains with a hardened `XmlReader` (DTD processing
  prohibited, no resolver, a character bound), and
  accepts a value only from the documented `<Error><Code/><Description/></Error>` shape under the Service Broker
  `Error` namespace — a positive allowlist on both element name and namespace. The `Code` and `Description` it finds
  are then sanitized against a positive per-rune Unicode-category allowlist (Letter, Mark, Number, Punctuation,
  Symbol and Space Separator categories are kept; every other rune collapses to one space) and bounded (32 and 3000
  characters, with a truncation marker), and the receiver logs them as two separate structured parameters rather
  than interpolating them into the message template. A body that is absent or empty projects `<no error payload>`;
  one that is oversized, not valid UTF-16, or does not conform to the documented shape projects
  `<unreadable error payload>`.

**Two live facts this decision rests on**, both observed against SQL Server 2022:

- `END CONVERSATION` on an endpoint in the `ER` state removes its `sys.conversation_endpoints` row outright. The
  integration oracle accepts an absent row or the retained `CD` (closed) state that Service Broker documents for an
  ended conversation, so it holds whichever the server does; what it refuses is the endpoint still sitting in `ER`.
- Service Broker `Error` message bodies are UTF-16LE **with** a byte order mark — the bytes `FF FE 3C 00`.
  The strict UTF-16 decode refuses a body whose bytes are malformed rather than repairing them, and the decode keeps
  a well-formed body's leading byte order mark as `U+FEFF`; `string.Trim()` does not remove it on
  .NET Core, so `ServiceBrokerErrorPayload.Describe` strips it explicitly
  (`WhenDescribingAnErrorPayload.MustParseABomPrefixedBody`).

### 2. A deterministic SQL fault is terminal

**SQL errors `102` "incorrect syntax" and `208` "invalid object name" are terminal, classified by one pure
predicate.** `SqlExceptionHelper.IsErrorNumberTerminal` is that predicate; the rationale is recorded once at its
`INVARIANT:`. Because this package provisions no Service Broker topology, a missing queue or a malformed statement is
deterministic misconfiguration, and retrying cannot make it succeed.

- **The predicate is subtracted from the transient set wherever the transient set is produced** — both
  `SqlRetryExceptionPredicatesProvider` and `SqlCircuitBreakerExceptionPredicatesProvider`, which
  `AddSqlServiceBroker` registers into the core recovery pipeline. The disjointness of the two sets is itself pinned
  (`WhenCheckingErrorNumberTerminality.MustNeverClassifyATerminalErrorNumberAsTransient`,
  `.MustReturnFalseForTransientErrorNumber`), so a number added to one set cannot silently be in both.
- **The receiver's critical filter is keyed on the same predicate**, and surfaces a terminal number as
  `CriticalReceiverException` naming the configured queue
  (`Integration.SsbMissingQueueTests.ReceivingFromAMissingQueueSurfacesACriticalReceiverExceptionNamingTheQueue`).
- **The `SqlException.IsTransient` guards are defence-in-depth and unpinned.** `Microsoft.Data.SqlClient` owns
  `IsTransient`, and it was observed to report `208` as NOT transient, so guarding the driver's answer with the
  terminal predicate reddens nothing today. It is there so a future driver reclassification cannot shadow the
  decision, and the receiver's transient filter sits above its critical filter. `SqlException` cannot be constructed
  without a live SQL connection, which is why no unit test pins those three guards (ADR-0027).

## Closed-by-Construction Acceptance Test

> What class of future finding does this make impossible, and why?

**ELIMINATED CLASS 1: a receive outcome that consumes a message and leaves its conversation open.** The rule is a
total function over `ClassificationOutcome`, and a coverage fact fails if a newly added outcome has no stated
ends-conversation decision. A new outcome cannot ship without someone answering the question, and one that reaches the
receiver with no arm throws rather than dispatching.

**ELIMINATED CLASS 2: one error number classified terminal in one place and transient in another.** There is a single
predicate, the two sets are pinned disjoint, and every site that classifies calls it. Adding a number changes one
switch.

**ELIMINATED CLASS 3: untrusted broker bytes surfaced as a free-form diagnostic string.** Two failure modes are closed
by construction, not merely fixed. First, a body whose bytes are not valid UTF-16 can no longer be repaired into a
credible diagnostic: the decode throws instead of substituting a replacement character, so a malformed body projects
the unreadable sentinel rather than text manufactured to look like something the peer said. Second, a log-unsafe code
point can no longer reach the log as itself for want of an enumeration that missed it: the permitted set is positive
— a rune is kept only when its Unicode category is one `IsPrinting` names, and every other category, including one no
one has yet thought to name, collapses to a space by falling out of the switch rather than by matching an entry in
it. The only type that can carry an `Error` payload to a log is `ServiceBrokerErrorPayload.ErrorDescription`, whose
constructor is private and whose sole producer sanitizes and bounds both values, so an unsanitized or unbounded
projection is unrepresentable rather than merely unwritten. What reaches the log is two structured parameters drawn
from an allowlisted document shape, so a peer cannot forge a log record and a new field cannot be added without
passing the same producer.

**What neither makes impossible** is a wrong membership decision: a message type that should have been dispatched
classified as a discard still ends its conversation correctly, and an error number wrongly added to the terminal set
is wrongly terminal everywhere at once.

## Consequences

- **A foreign application sharing a Chatter queue has its dialogs ended.** If it holds long-lived, multi-message
  dialogs and sends over them with a message type this package does not accept, each such message now ends its
  dialog instead of being dropped from the queue and left open. Its message was already being consumed and never
  delivered; what changes is that the dialog no longer survives that.
- **`208` is no longer retried anywhere under Chatter's recovery pipeline.** Both providers are registered globally,
  not scoped to Service Broker operations, so any `SqlException` numbered `208` reaching the pipeline is now terminal.
- **A host started before its queue exists stops instead of waiting.** It fails with `CriticalReceiverException`
  naming the configured queue, and is restarted by its host, rather than retrying silently forever.
- **No public surface changes.** `SqlExceptionHelper`, the classification outcome, the classifier and the
  error-payload decoder are all internal to the package, so nothing above is a compatibility surface. The README's
  recovery and receive-filtering sections are corrected to match the new behaviour.

### Recorded residuals

Both were raised by the local review of this change (finding `c03c94f2`) and are accepted rather than fixed.

**RES-1 — under `TransactionMode.None` the discard and its `END CONVERSATION` are not atomic (inherited).** The root
cause is `SqlServiceBrokerReceiver.CreateTransaction`: under `TransactionMode.None` it returns `null`, and
`ReceiveSession` is written to be null-transaction-safe (its own `INVARIANT:` says so), so the `RECEIVE`, the
`END CONVERSATION` and the "commit" are three independent statements of which only the first two do anything. This is
not new to the discard path. Ack, deadletter and the end-dialog outcome have issued `END CONVERSATION` outside any
transaction under this mode since before this change, and nack's rollback is likewise a no-op — both facts are stated
at `AckMessageAsync` and `NackMessageAsync`. The worst case is a process that dies between the two autocommits: the
message is gone and its conversation is still open, which is exactly the pre-#357 leak for that one message, not a
regression introduced here. **Refusing `TransactionMode.None` for discards was rejected**: it would single out one
settlement path for a non-atomicity every settlement path in this receiver shares, and would add a new failure mode —
a host that cannot receive at all — to correct what is, on the discard path, a documentation defect.

**RES-2 — under a transactional mode, an `END CONVERSATION` that keeps failing stalls the queue (introduced).** The
root cause is that `DiscardMessageAsync` runs `EndDialogConversationCommand` before `CommitAsync`, so a throwing
command propagates out of `ReceiveMessageAsync` with the session disposed unsettled; the transaction rolls back, the
message returns to the queue, and the next `RECEIVE` classifies it identically and fails identically. The realistic
trigger is a deterministic `END CONVERSATION` failure rather than a transient one — for example
`CleanupOnEndConversation = true` under a login without the permission `WITH CLEANUP` requires — and such a failure
already breaks `AckMessageAsync` for every successfully dispatched message, because ack issues the same command with
the same `enableCleanup`. The loop is bounded and loud, not silent: `BrokeredMessageReceiver.MessageReceiverLoopAsync`
runs the receive inside `_recoveryStrategy.ExecuteAsync` (`RetryWithCircuitBreakerStrategy` by default) and catches
the non-critical throw, logging "Error receiving brokered message" at `Error` before continuing, so retry backoff and
the circuit breaker pace the retries and every turn is visible in the log. The outcome is a stalled queue an operator
can see, not a message lost or a tight spin. **Committing the `RECEIVE` anyway when the `END CONVERSATION` fails was
rejected**: it silently reinstates the leak this ADR exists to close, and contradicts the rule stated above.
**Rethrowing as `CriticalReceiverException` was also rejected**: that extends Decision 2's terminal classification
from "the queue is misconfigured" to conversation and permission errors, which is a wider blast radius than the risk
justifies, and it is not reachable from the Docker harness, so it would ship unpinned.

## References

- Issue #357 — *Discarded messages — including Service Broker Error messages — commit the RECEIVE without ending the
  conversation*.
- Issue #358 — *SQL error 208 classified as transient — a missing queue retries forever instead of failing fast*.
- Epic #304 — *SqlServiceBroker — errored conversations leak, error 208 retries forever, and attempt counts die with
  the process*.
- ADR-0016 — *T-SQL identifier quoting: a round-trip parse*. The other half of the `102` story: a configured queue
  name is quoted rather than interpolated, so a malformed name is refused before it reaches the server.
- ADR-0027 — *Invariant prose names the oracle that falsifies it*. Why each rationale above is recorded once, at its
  mechanism, and named rather than restated here.
- `src/Chatter.MessageBrokers.SqlServiceBroker/src/Chatter.MessageBrokers.SqlServiceBroker/Receiving/ServiceBrokerMessageClassifier.cs`
  — the outcome set and `EndsConversation`.
- `src/Chatter.MessageBrokers.SqlServiceBroker/src/Chatter.MessageBrokers.SqlServiceBroker/Receiving/SqlServiceBrokerReceiver.cs`
  — the discard path, the fail-closed default arm and the critical filter.
- `src/Chatter.MessageBrokers.SqlServiceBroker/src/Chatter.MessageBrokers.SqlServiceBroker/Receiving/ServiceBrokerErrorPayload.cs`
  — the UTF-16 decode and the byte-order-mark strip.
- `src/Chatter.MessageBrokers.SqlServiceBroker/src/Chatter.MessageBrokers.SqlServiceBroker/Receiving/SqlExceptionHelper.cs`,
  `.../Receiving/Retry/SqlRetryExceptionPredicatesProvider.cs` and
  `.../Receiving/CircuitBreaker/SqlCircuitBreakerExceptionPredicatesProvider.cs` — the terminal predicate and its two
  subtractions.
- `src/Chatter.MessageBrokers.SqlServiceBroker/tests/Receiving/UsingSqlServiceBrokerReceiver/WhenClassifyingReceivedMessages.cs`,
  `src/Chatter.MessageBrokers.SqlServiceBroker/tests/Receiving/UsingSqlExceptionHelper/WhenCheckingErrorNumberTerminality.cs`,
  `src/Chatter.MessageBrokers.SqlServiceBroker/tests/Receiving/UsingServiceBrokerErrorPayload/WhenDescribingAnErrorPayload.cs`,
  `src/Chatter.MessageBrokers.SqlServiceBroker/tests/Integration/SsbErroredConversationTests.cs` and
  `src/Chatter.MessageBrokers.SqlServiceBroker/tests/Integration/SsbMissingQueueTests.cs` — the facts named
  throughout. The integration facts are Docker-gated and skipped when Docker is absent.
