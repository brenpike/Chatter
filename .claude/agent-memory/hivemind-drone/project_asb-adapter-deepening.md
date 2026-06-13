---
name: project-asb-adapter-deepening
description: Non-obvious constraints found while deepening the Azure Service Bus adapter (issue #122) with characterization-first refactor
metadata:
  type: project
---

Constraints/quirks discovered executing the ASB adapter deepening refactor (issue #122, plan candidates 1/4/5).

**Why:** These come from the legacy Microsoft.Azure.ServiceBus 5.2.0 SDK runtime behavior and Moq/internal-type interaction — not derivable from reading the SUT. They shaped how characterization tests had to be written and what coverage ceiling is reachable.

**How to apply:** Reuse when extending ASB receiver/sender tests or doing further deepening here.

- The legacy `Microsoft.Azure.ServiceBus.Message.SystemProperties` getters (`DeliveryCount`, `LockToken`) throw `InvalidOperationException` via an internal `ThrowIfNotReceived` guard unless the message was actually received. To build a "received" `Message` in a unit test, set the internal `sequenceNumber` field > 0 (satisfies the guard), then the internal `set_DeliveryCount` setter and the `lockTokenGuid` field — all via reflection. See `tests/Receiving/ServiceBusMessageFactory.cs`.
- `MessageReceiver` (SDK) opens a connection ON CONSTRUCTION — so any ServiceBusReceiver path that touches the inner receiver is integration-only and cannot be unit-tested directly. The fix was an internal `IServiceBusMessageReceiver` port + in-memory double driving receive/ack/transient-rethrow/disposed-reset.
- `MessageSender` (SDK) by contrast is LAZY — its ctor does NOT open a connection (connection opens on first `SendAsync`). So pool checkout/Return/reuse IS unit-testable with real un-sent senders; only the 3 construction branches inside the production factory stay integration-only. An owns-connection sender has `Path` = the entity path passed to ctor and `ViaEntityPath` = null, so the Return key matches the GetOrCreate key `(path, (null, null))`.
- `MessageBrokerOptions.TransactionMode` and `ServiceBusOptions.TokenProvider` are `internal set` in the CORE `Chatter.MessageBrokers` assembly; the ASB test assembly only has IVT to the ASB assembly, NOT to core. So you cannot set `MessageBrokerOptions.TransactionMode` from ASB tests — construct in default (None => ReceiveAndDelete) and flip to PeekLock via `InitializeAsync(ReceiverOptions{TransactionMode=...})`.
- Making `ServiceBusReceiver` internal broke Moq's `ILogger<ServiceBusReceiver>` proxy creation ("type not accessible ... strong-named"). Fix is to add `[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2, PublicKey=0024...cc7")]` (Castle's fixed key) to the ASB `Properties/AssemblyInfo.cs` — NOT to relax the access modifier.
- `ServiceBusMessageSender.Dispatch` has a null/empty-Destination guard that is UNREACHABLE through the real `OutboundBrokeredMessage` type: that type's own ctor rejects a null/empty destination with `ArgumentException` first. The sender's guard is shadowed/dead via the public message type and cannot be pinned behavior-preservingly.
