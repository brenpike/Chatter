---
name: project_core-loop-coverage
description: Non-obvious constraints for BrokeredMessageReceiver<T> loop unit tests (issue #121, branch test/core-loop-coverage)
metadata:
  type: project
---

BrokeredMessageReceiver<T> loop tests: two critical non-obvious constraints discovered during STEP-002.

**Constraint 1 — IRecoveryStrategy mock must cover ALL three TResult shapes.**
The receiver loop calls `ExecuteAsync` with three distinct Func signatures:
- `Func<Task<MessageBrokerContext>>` — ReceiveMessageAsync
- `Func<Task<bool>>` — Ack/Nack/Deadletter/DispatchReceivedMessage/FailedRecoveryAction
- `Func<Task<int>>` — MessageDeliveryCountAsync
If any shape is missing from the Moq setup, Moq returns `null` (default for `Task<T>`), and `await null` crashes the test host process with "Test host process crashed" — no test output, no useful diagnostic.

**Constraint 2 — Drained fires at dequeue, NOT after disposition.**
`InMemoryMessagingInfrastructureReceiver.Drained` completes when the message is dequeued from the ConcurrentQueue inside `ReceiveMessageAsync`. At that point `ProcessMessageAsync` has NOT yet run. If the test cancels the CTS immediately after `await infraReceiver.Drained`, there is a race: the loop's `ThrowIfCancellationRequested()` (line ~150 of the loop) fires before dispatch/ack/nack/deadletter, and no disposition is recorded. Assertions then fail.
Fix: use a `WaitForDispositionAsync` helper that polls `CallLog.Contains(expectedDisposition)` via `await Task.Yield()` before cancelling. A watchdog CTS (10s) bounds the wait.

**Why:** confirmed empirically — test host crash on first attempt (Func shape gap), assertion failure on second (Drained timing race).

**How to apply:** STEP-003 must use the same three-shape mock and the same `WaitForDispositionAsync` pattern. Do not rely on `Drained` alone as the synchronization point for assertions.

**Constraint 3 — NullLogger required for internal generic type parameters (STEP-003).**
`RetryStrategy` and `NoDelayRetry` are `internal` classes. `NullLogger<RetryStrategy>.Instance` must be used instead of `new Mock<ILogger<RetryStrategy>>()` because Castle.DynamicProxy cannot create a proxy for `ILogger<T>` when `T` is a non-public type. Same applies to any other internal production type used as a logger generic parameter. Confirmed by prior observation (3577).

**Constraint 4 — CircuitBreakerOpenException is NOT thrown eagerly when CB is open (STEP-003).**
`CircuitBreaker.ExecuteAsync` when `IsOpen` does NOT immediately throw `CircuitBreakerOpenException`. It waits `Task.Delay(openToHalfOpenWaitTime)` then enters half-open and attempts the action. The `throw new CircuitBreakerOpenException(...)` line in the source is unreachable. `RetryStrategy` swallows `CircuitBreakerOpenException` silently (no attempt increment, no delay), but this path is not exercised via the test seam — instead the CB transitions directly to half-open. Use `openToHalfOpenWaitTimeInSeconds=0` to make this instantaneous in tests.

**Constraint 5 — CB failure-counter exact value is fragile to assert through the loop seam.**
`InMemoryCircuitBreakerStateStore.FailureCount` can be incremented by ANY `ExecuteAsync` call that throws (ReceiveMessageAsync, DeliveryCountAsync, Ack, etc.), not only handler dispatch. Asserting the exact count through the loop seam is fragile. Instead, assert `stateStore.IsClosed` after a successful half-open recovery as a proxy for the Open→HalfOpen→Closed transition. Deferred to unit test if exact counter sequencing must be pinned.

**Why:** discovered during STEP-003 authoring; confirmed by running tests green on net8.0 and net10.0.

**How to apply:** Any future loop-seam recovery test should use NullLogger for internal types, set `openToHalfOpenWaitTimeInSeconds=0`, and assert `stateStore.IsClosed` rather than failure counters.

Related: [[project_messagebrokers-characterization]]
