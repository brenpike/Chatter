---
name: project-messagebrokers-characterization
description: Non-obvious gotchas discovered while writing characterization tests for Chatter.MessageBrokers Routers/Dispatchers (PHASE-3b)
metadata:
  type: project
---

Constraints/quirks found pinning Chatter.MessageBrokers Routing + Sending + Recovery + Receiving SUTs with Moq.

**Why:** These are not derivable from reading the SUT alone — they come from Moq/runtime interaction and a deferred-iterator in production code. They shaped how the tests had to be written and are candidates for the docs phase.

**How to apply:** Reuse these patterns when extending MessageBrokers tests instead of rediscovering them.

- `BrokeredMessageDispatcher.Dispatch<TMessage,TOptions>(messages, dest, options)` is a deferred (`yield return`) iterator. Per-message body conversion (`IBodyConverterFactory`/`IBrokeredMessageBodyConverter.Convert`) and id generation (`IMessageIdGenerator`) only run when the router enumerates the outbound sequence. A no-op Moq `IRouteBrokeredMessages.Route` mock that returns `Task.CompletedTask` without enumerating means NONE of those collaborator calls happen. To pin build-out behavior, the router setup must enumerate (e.g. `.Callback((m,_,__) => captured = m.ToList())`). The content-type guard, by contrast, surfaces eagerly (its exact deferral timing was ambiguous and not asserted).
- `IServiceScopeFactory.CreateScope()` cannot be mocked with this Moq + DI-abstractions combo (Moq reports it non-overridable). For SUTs that take `IServiceScopeFactory` (`CriticalFailureEventDispatcher`, `ScopedReceivedMessageDispatcher`), build a REAL scope factory via `new ServiceCollection()....BuildServiceProvider().GetRequiredService<IServiceScopeFactory>()` and register the mocked leaf collaborators; verify interaction on those leaves.
- `IBrokeredMessageOutbox.SendToOutbox(single, txn, ct)` is a default interface method delegating to the batch overload, but that delegation does NOT run through a Moq proxy. `OutboxBrokeredMessageRouter.Route(single)` calls the SINGLE overload directly and `Route(batch)` calls the BATCH overload directly — the router does not fan single->batch itself. Set up and verify each overload independently (an unconfigured single overload returns a null Task and throws on await).
- For `InboundBrokeredMessage` (internal ctor, visible to test asm) construct directly inline with a small `CreateInbound()` helper per test class rather than a shared creator — a shared `InboundBrokeredMessageCreator` was attempted and removed because its fluent `WithBody` chain produced unexpected results in this run. `FailureContext` default-creation via a shared `FailureContextCreator` works; tests needing specific Inbound/ErrorQueue/Txn values construct `FailureContext` directly since those are load-bearing in the assertion.
- `MessageContext` (static keys) lives in root namespace `Chatter.MessageBrokers`; needs `using Chatter.MessageBrokers;` since test namespaces under `...Routing`/`...Sending` don't pull it in. `TransactionMode` lives in `Chatter.MessageBrokers.Receiving`.
