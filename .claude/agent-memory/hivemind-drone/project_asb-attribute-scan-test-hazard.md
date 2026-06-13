---
name: asb-attribute-scan-test-hazard
description: Why ASB cross-entity guard tests must NOT use real [BrokeredMessage] classes; use core AddReceiver route instead
metadata:
  type: project
---

ASB DI tests that exercise attribute-registered receivers (the [BrokeredMessage] assembly scan) must NOT declare a real `[BrokeredMessage]`-decorated `IMessage` class in the test assembly.

**Why:** `AddChatterCqrs(config, markerType)` + `AddMessageBrokers` run `FindBrokeredMessagesWithReceiversInAssembliesByType` over `AssemblySourceFilter.Apply()`. With no namespace selector that resolves to the WHOLE app domain (every loaded assembly), so any `[BrokeredMessage]` `IMessage` type anywhere in the test assembly is discovered by EVERY test that builds services. STEP-003's `PopulateFromDiscoveredReceivers` then folds each into the ASB `ServiceBusReceiverRegistry`, corrupting entity counts / tripping the cross-entity guard in unrelated tests.

**How to apply:** To cover the "attribute receivers reach the guard" path (F3), register via the CORE `MessageBrokerOptionsBuilder.AddReceiver<T>(receiverPath, senderPath:, infrastructureType: ASBMessageContext.InfrastructureType)` route. It converges on the SAME `AddReceiverImpl` seam as the attribute scan (both retain live `ReceiverOptions` in `IDiscoveredReceiverRegistry`) but is scoped per-test, not assembly-global. Trigger the guard the same way the existing tests do: resolve `ServiceBusClient` (the shared-client singleton factory runs `ResolveEffectiveCrossEntityTransactions`). See [[asb-integration-tests]].
