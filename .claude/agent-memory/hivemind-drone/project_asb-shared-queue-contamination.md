---
name: asb-shared-queue-contamination
description: ASB integration tests share emulator queues across classes; leftover messages cross-contaminate receivers and produce fast non-null/null flakes (NOT a serializer bug)
metadata:
  type: project
---

ASB integration test classes deliberately REUSE the same emulator queues (Config.json says "reuse an existing emulator queue, no new entity"). `chatter.roundtrip` is shared by FOUR classes: PipelineComplexPayloadTests, PipelineMultiReceiverTests (as QueueA), PipelineRoundTripTests, PipelineScheduledDeliveryTests. `chatter.receiveonly` is shared by MultiReceiver (QueueB) + PipelineTransactionModeTests.

**Why this flakes:** all classes are in one `[Collection(ServiceBusEmulatorCollection.Name)]` (serial, one emulator) but the queue is NOT purged between tests. A leftover/redelivered message from class X can be received by class Y's receiver. Because STJ deserialization is lenient (PropertyNameCaseInsensitive + unknown-props-ignored), a foreign body (e.g. RoundTripCommand `{"Name","Count","Flag"}`) deserializes as a *non-null* ComplexCommand with all members null/default. So `handled.Message.Should().NotBeNull()` PASSES but `handled.Message.Nested.Should().NotBeNull()` FAILS — fast (~170ms, NOT a 30s HandlerWait timeout). PR #194 CI failure (run 27471925852) was exactly this on net10.0.

**Why net10.0 only:** the two TFMs (net8.0/net10.0) run as SEPARATE `dotnet test` processes, each spinning up its OWN emulator container (no Testcontainers reuse, no .testcontainers.properties). So it's NOT cross-process. It's within-process leftover-drain/ordering nondeterminism that differed between the two independent emulators — a genuine flake with a concrete structural cause.

**Proven NOT a serializer bug:** broker-free ComplexCommand round-trip via JsonBodyConverter produces byte-identical wire AND clean round-trip on BOTH net8.0 and net10.0. Full ASB integration suite passes locally on net10.0 (ComplexPayload included, ~171ms).

**Why:** the fix is a zoom-out decision (per-class dedicated queues, or per-test unique queue names, or harness queue-purge), not a one-line patch — patching only ComplexPayload relocates the same flake to the other three sharing classes.

**How to apply:** if an ASB integration test flakes with a fast non-null-but-wrong-shape assertion, suspect shared-queue contamination first, NOT the serializer. See [[project_asb-integration-tests]].
