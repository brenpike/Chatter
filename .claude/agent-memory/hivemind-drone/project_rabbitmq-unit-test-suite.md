---
name: rabbitmq-unit-test-suite
description: Non-obvious constraints for the RabbitMQ adapter UNIT test suite (in-memory double, IChannel fake, predicate-exception ctors)
metadata:
  type: project
---

STEP-007 authored the RabbitMQ adapter UNIT test suite under `src/Chatter.MessageBrokers.RabbitMQ/tests/` — broker-free, no Docker, 106 tests green on net8.0 + net10.0. Linchpin is `InMemoryRabbitMqConnectionSource` + a hand-rolled `RecordingChannel : IChannel`.

**Why:** Pin receive/ack/nack/epoch/delivery-count/send behavior without a live RabbitMQ. The seam (`IRabbitMqConnectionSource`) is what every receiver/sender test drives.

**How to apply (non-obvious gotchas for future RabbitMQ test work):**
- `RecordingChannel` hand-implements `IChannel` (27 methods + 7 props + 6 events in RabbitMQ.Client 7.2.1). Only `BasicAckAsync`/`BasicNackAsync`/`BasicPublishAsync<T>`/`BasicConsumeAsync`/`BasicQosAsync` are real; everything else throws `NotImplementedException` (reaching one = untested production path). It reports `IsOpen==false` deliberately.
- Deliveries are pushed by capturing the receiver's `AsyncEventingBasicConsumer` at `BasicConsumeAsync` and calling `consumer.HandleBasicDeliverAsync(...)` (public) to raise `ReceivedAsync` — that is how a buffered delivery enters `RabbitMqReceiver` without a broker.
- `RabbitMqPublishChannelRental` ctor is internal `(RabbitMqConnectionSource, IChannel)` and its `DisposeAsync` calls back into the REAL source's `ReturnPublishChannel`, which `Release()`s the publish-pool `SemaphoreSlim`. A bare Release on a full semaphore throws `SemaphoreFullException`. The double owns ONE real `RabbitMqConnectionSource` and reflectively `Wait()`s its `_publishPoolGate` once per rental so the dispose Release is balanced. Do NOT pass a null source to the rental ctor — receiver republish/deadletter await-using disposes it and NREs.
- Epoch staleness is forced via `InMemoryRabbitMqConnectionSource.AdvanceEpoch()`; `RunOnReceiveChannelAsync` passes `Interlocked.Read` of the CURRENT epoch (matching production), so a settlement carrying the registration-time epoch (0) becomes a no-op after one advance.
- Predicate-provider transient exceptions: `new OperationInterruptedException()` (parameterless) — the `(ShutdownEventArgs reason)` overload NREs on null. `BrokerUnreachableException(Exception inner)`, `AlreadyClosedException(ShutdownEventArgs)` (build the args via `ShutdownEventArgs(ShutdownInitiator.Library, code, text, cause:null, ct)`), `ConnectFailureException(string,Exception)`. Note `BrokerUnreachableException : IOException` and `AlreadyClosedException : OperationInterruptedException`, so they match MULTIPLE predicates — test asserts "at least one predicate matches," not a specific one.
- `MessageBrokerOptions.TransactionMode` has an INTERNAL setter; the DI FullAtomicity-rejection test sets it via reflection. `RejectFullAtomicity` reads `MessageBrokerOptions` and `IDiscoveredReceiverRegistry` off the `IServiceCollection` `ImplementationInstance` (registered via `AddSingleton(instance)`).
- Test namespace `Chatter.MessageBrokers.RabbitMQ.Tests.*` nests under the production namespace, so `WithRabbitMqRouting` (lives in `Chatter.CQRS.Context`) needs an explicit `using Chatter.CQRS.Context;`.
- Structural analog is the SSB test suite (`UsingXxx/WhenYyy`, xUnit + FluentAssertions + Moq, `: Testing.Core.Context`). RabbitMQ body converter is UTF-8 (SSB is UTF-16).
