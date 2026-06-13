---
name: receiver-startup-signal-placement
description: Why the receiver go-live signal lives on internal IReceiverStartupSignal, not public IReceiveMessages
metadata:
  type: project
---

The receiver go-live startup-completion signal (`Task ReceivingStarted`) lives on the INTERNAL `IReceiverStartupSignal` interface in `Chatter.MessageBrokers.Receiving`, NOT on the public `IReceiveMessages` contract.

**Why:** F3 work (issue #146 / 0.12.0) originally added `ReceivingStarted` as an abstract member to the PUBLIC `IReceiveMessages` interface — a source+binary-breaking change to a shipped contract, but it was released as a non-breaking MINOR under `### Added`. SV-001 remediation (plan-2026-06-09) moved the signal to a new internal interface so the public surface reverts to its pre-F3 shape (`IReceiveMessages` exposes only `bool IsReceiving`) and 0.12.0 stays a legitimate non-breaking MINOR. Producer: concrete `BrokeredMessageReceiver<TMessage>` (TaskCompletionSource, completed once at the `IsReceiving = true` seam in StartReceiverImpl). Sole consumer: internal `BrokeredMessageReceiverBackgroundService<TMessage>`, which casts `_receiver` (typed `IBrokeredMessageReceiver<TMessage>`, which does NOT expose the signal) to `IReceiverStartupSignal` to await it.

**How to apply:** Do NOT re-add `ReceivingStarted` to `IReceiveMessages` or `IBrokeredMessageReceiver<TMessage>` — that re-introduces the breaking change. A guard test in `WhenReceiving.cs` (`MustNotExposeReceivingStartedOnPublicReceiveMessagesContract`) asserts `typeof(IReceiveMessages).GetProperty("ReceivingStarted")` is null and will fail if it returns. Tests reach the internal interface via `InternalsVisibleTo("Chatter.MessageBrokers.Tests")`.
