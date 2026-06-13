---
name: project_asb-sdk-migration
description: ASB module SDK migration from deprecated Microsoft.Azure.ServiceBus 5.2.0 to Azure.Messaging.ServiceBus 7.20.1 — branch, scope, and dotnet restore side-effect gotcha
metadata:
  type: project
---

Active refactor on branch `refactor/azureservicebus-sdk-azure-messaging` replacing `Microsoft.Azure.ServiceBus 5.2.0` with `Azure.Messaging.ServiceBus 7.20.1` across `Chatter.MessageBrokers.AzureServiceBus`.

**Why:** Old SDK is deprecated; new SDK is the current Azure Service Bus client for .NET.

**How to apply:** The migration is multi-step (STEP-001 through ~007). STEP-001 is the dependency swap only — C# call sites still reference old-SDK types after that step and will NOT compile until later steps fix them. Do not attempt `dotnet build` as a self-check for STEP-001.

**Restore side-effect:** Running `dotnet restore --force-evaluate` on the ASB csproj — OR a plain `dotnet build Chatter.sln` (the implicit restore) — causes dotnet to also write sibling packages.lock.json files (e.g. Chatter.MessageBrokers.AzureServiceBus.Auth src + tests, Chatter.CQRS, Chatter.MessageBrokers, Chatter.MessageBrokers.Reliability.EntityFramework). These are outside step scope. Revert them with `git checkout --` before reporting done. Confirmed again on STEP-006 (2026-06-08): the Auth module's two lock files were rewritten by a solution-wide build and had to be reverted.

**Shared-client wiring (STEP-006, landed 2026-06-08):** The receiver and sender MUST share ONE `Azure.Messaging.ServiceBus.ServiceBusClient` per namespace — cross-entity transactions (`EnableCrossEntityTransactions=true`, set on the client in `ChatterAzureServiceBusExtensions.CreateSharedClient`) only provide send+settle atomicity when both sides use the same client object. Wiring: the client is `AddSingleton`; `ServiceBusReceiver` is `AddScoped<ServiceBusReceiver>()` and its ctor now TAKES `ServiceBusClient` as its first param, so DI injects the singleton (no explicit factory needed); the sender factory is built directly with the same `sharedClient`. The receiver no longer self-constructs a client (the old lazy `Client` property + STEP-003 TODO are gone).

**Pre-existing STEP-004 test compile breaks (blocks ASB test project, discovered STEP-006 2026-06-08):** Two committed STEP-004 test files fail to compile because the SDK port left them missing a `using` for a type they newly reference: `tests/Extensions/UsingMessageExtensions/WhenMutatingMessage.cs` uses `BinaryData` (needs `using System;`), and `tests/Sending/UsingServiceBusMessageSender/WhenDispatching.cs` uses `TransactionMode` (needs the `Chatter.MessageBrokers` namespace). Same fix-framing: missing `using` in a STEP-004-refactored test file. The ASB test project will NOT compile (and ASB unit tests cannot run) until both are fixed — out of scope for the receiver step.
