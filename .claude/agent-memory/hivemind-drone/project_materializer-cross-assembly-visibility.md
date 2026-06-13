---
name: materializer-cross-assembly-visibility
description: MessageContext.MaterializePersistedContext is internal — SSB production+test assemblies reach it via InternalsVisibleTo, not by making it public
metadata:
  type: project
---

`MessageContext.MaterializePersistedContext` (the centralized STJ deserialize+materialize seam in `src/Chatter.MessageBrokers/.../MessageContext.cs`) is `internal` — both overloads: the `(string json)` one and the `(IDictionary<string,object>)` one whose values are raw `JsonElement`s. `MaterializePersistedContextValue(JsonElement)` is also `internal`. None are public — to avoid committing new public API surface and keep MessageBrokers a PATCH bump (0.10.1).

**Why:** An earlier pass made these `public` because AssemblyInfo.cs was then out of scope and SSB (a separate production assembly) had to call them. The structural-remediation 2nd pass (STEP-006) reversed that: minimizing public API surface won out, and AssemblyInfo.cs was in scope. `Chatter.MessageBrokers/Properties/AssemblyInfo.cs` now grants `InternalsVisibleTo` to four assemblies: `Chatter.MessageBrokers.Tests`, `Chatter.MessageBrokers.AzureServiceBus.Tests`, `Chatter.MessageBrokers.SqlServiceBroker` (production — `SqlServiceBrokerReceiver.cs:180` calls the dict overload), and `Chatter.MessageBrokers.SqlServiceBroker.Tests` (`WhenReceivingMessageWithTypedHeaders.cs` calls it). Plus `Chatter.Testing.Core`.

**How to apply:** When a helper in `Chatter.MessageBrokers` must be consumed by a broker adapter assembly (SqlServiceBroker, AzureServiceBus), `internal` + InternalsVisibleTo is the preferred path over `public` (keeps API surface minimal / bump as PATCH). Grant BOTH the adapter's production AND its test assembly — a delegation that only names the production assembly will break the adapter's TEST compile if those tests also call the now-internal member. AssemblyInfo.cs must be in scope to do this.
