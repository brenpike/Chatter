---
status: accepted
date: 2026-08-29
---

# Optional BCL-only telemetry: per-assembly instrumentation scopes and the off-guard

Chatter ships no tracing and no metrics today — only scattered `ILogger` calls. Issue #274 asks for
OpenTelemetry-compatible tracing and metrics that are **optional and opt-in**, with **no dependency
on any `OpenTelemetry.*` NuGet package**, using only `System.Diagnostics.ActivitySource` and
`System.Diagnostics.Metrics.Meter` from the BCL.

Two properties dominate every decision below.

1. **Nine independently-versioned packages, one dependency graph.** Every package other than
   `Chatter.CQRS` reaches it transitively through a single `ProjectReference` chain, so where the
   shared telemetry surface lives determines whether this change costs zero new packages or one
   more package plus a CI workflow, a version-check entry, a tag prefix and a NuGet listing.

2. **Off must mean OFF.** An application that never opts in must pay no measurable per-message cost
   and must observe **no change on the wire**. This is the acceptance criterion the design is built
   around, and it is subtle enough that a plausible-looking guard gets it wrong — see
   [The off-guard](#the-off-guard) below.

Throughout this ADR, "a .NET BCL `System.Diagnostics.ActivityListener`" always means the BCL
subscription type. It is never a **Brokered Message Receiver** (`Chatter.MessageBrokers`
`CONTEXT.md` reserves that term and marks `listener` as an alias to avoid).

## Considered Options

- **Option 1 — take the `OpenTelemetry.*` NuGet dependency.** Rejected outright by the issue and by
  this ADR: it forces a hard dependency and a version-compatibility surface onto every consuming
  application, including those that want no telemetry at all. The BCL primitives are already the
  correct abstraction — `OpenTelemetry.*` is a *consumer* of `ActivitySource`/`Meter`, not a
  prerequisite for emitting to them.
- **Option 2 — a new tenth package, `Chatter.Diagnostics`.** A clean-looking home for a
  cross-cutting concern. Rejected: it costs a csproj, a `<module>-cicd.yml`, a
  `version-check-all.yml` entry, a tag prefix, a NuGet listing, and nine new dependency edges — and
  buys nothing, because every package already reaches `Chatter.CQRS`.
- **Option 3 — per-module private static `ActivitySource`/`Meter`, no shared surface.** Rejected:
  the tag-name constants, the outcome vocabulary, and the trace-context propagation helper would be
  duplicated per module, which is exactly the shape ADR-0004 was written to eliminate for header
  translation (one semantic concept translated inconsistently at each boundary).
- **Option 4 — shared surface in `Chatter.CQRS`, per-assembly `ActivitySource`/`Meter` names
  (ACCEPTED).** One home, zero new packages, zero new dependency edges, and instrumentation scopes
  that match the package boundary consumers already reason about.
- **Option 5 — a single flat `"Chatter"` instrumentation scope** (as issue #274 proposes).
  Rejected; see D3.

## Decision

### D1 — BCL only. No `OpenTelemetry.*` dependency, ever

Instrumentation uses `System.Diagnostics.ActivitySource` (tracing) and
`System.Diagnostics.Metrics.Meter` (metrics) exclusively. **No `PackageReference` is added to any
csproj, and no `packages.lock.json` changes.** (Lock files exist only under the ten `tests/`
projects; no production csproj carries one.)

Every BCL member the design depends on ships in the `Microsoft.NETCore.App` shared framework for
**both** target frameworks. Verified against the installed reference packs — each of the following
is present in `System.Diagnostics.DiagnosticSource.xml` under **both**
`Microsoft.NETCore.App.Ref/8.0.30/ref/net8.0/` and `Microsoft.NETCore.App.Ref/10.0.8/ref/net10.0/`:

| Member | net8.0 | net10.0 |
| --- | --- | --- |
| `System.Diagnostics.ActivitySource.HasListeners` | present | present |
| `System.Diagnostics.DistributedContextPropagator` | present | present |
| `DistributedContextPropagator.PropagatorGetterCallback` | present | present |
| `DistributedContextPropagator.PropagatorSetterCallback` | present | present |
| `System.Diagnostics.Metrics.Meter` | present | present |
| `System.Diagnostics.Metrics.Instrument.Enabled` | present | present |

Consequence: the emitting code compiles and behaves identically on both TFMs with no `#if`
multi-targeting for the telemetry surface.

### D2 — The shared diagnostics surface lives in `Chatter.CQRS`, and it is public

All eight other packages reach `Chatter.CQRS` through this verified `ProjectReference` chain:

- `Chatter.MessageBrokers.csproj:36` → `Chatter.CQRS`
- `Chatter.MessageBrokers.AzureServiceBus.csproj:24` → `Chatter.MessageBrokers`
- `Chatter.MessageBrokers.AzureServiceBus.Auth.csproj:23` → `Chatter.MessageBrokers.AzureServiceBus`
- `Chatter.MessageBrokers.RabbitMQ.csproj:25` → `Chatter.MessageBrokers`
- `Chatter.MessageBrokers.Reliability.EntityFramework.csproj:29` → `Chatter.MessageBrokers`
- `Chatter.MessageBrokers.Reliability.Cosmos.csproj:27` → `Chatter.MessageBrokers`
- `Chatter.MessageBrokers.SqlServiceBroker.csproj:33` → `Chatter.MessageBrokers`
- `Chatter.SqlChangeFeed.csproj:26` → `Chatter.MessageBrokers.SqlServiceBroker`

One home therefore serves all nine packages with **zero new packages, zero new CI workflows, and
zero new dependency edges**.

**The surface must be PUBLIC.** `Chatter.CQRS` has no `Properties/AssemblyInfo.cs`; its only
`InternalsVisibleTo` declarations are inline assembly attributes naming `Chatter.CQRS.Tests` and
`DynamicProxyGenAssembly2` (`Commands/CommandDispatcher.cs:9-10`,
`Queries/QueryDispatcher.cs:8-9`). There is **no** `InternalsVisibleTo` from `Chatter.CQRS` to
`Chatter.MessageBrokers`, so an `internal` diagnostics surface would be unreachable from the broker
packages that must emit through it.

### D3 — `ActivitySource` and `Meter` are named PER EMITTING ASSEMBLY

The names are `"Chatter.CQRS"` and `"Chatter.MessageBrokers"` — the emitting assembly's own name —
not a single flat `"Chatter"`.

Applications opt in with `.AddSource("Chatter.*")` / `.AddMeter("Chatter.*")`, or by naming the two
scopes exactly.

Rationale: the instrumentation scope name is the standard per-library filter dimension
(`otel.scope.name`). Naming per assembly gives per-module sampling and filtering for free, keeps
the telemetry scope aligned with the independently-versioned package boundary applications already
reason about, and leaves a home for future adapter-native scopes (`"Chatter.MessageBrokers.RabbitMQ"`
and friends) without renaming anything. A flat `"Chatter"` scope would force a hand-rolled
`chatter.module` tag on every span to recover a dimension the scope name already carries — and that
tag would then have to be maintained at every emit site.

**DIVERGENCE FROM ISSUE #274, RECORDED DELIBERATELY.** Issue #274 proposes the flat name: *"Consumers
who want OTEL just call `.AddSource("Chatter")` / `.AddMeter("Chatter")`."* This ADR diverges for the
reason above. The ergonomic cost of the divergence is one wildcard character.

**EXTERNALLY-SOURCED CLAIM (not testable in this repository).** That `AddSource` and `AddMeter`
support a `"Prefix.*"` wildcard is a property of the OpenTelemetry .NET SDK, which this repository
may not reference (D1). It is therefore asserted on the strength of the upstream documentation
cited under [References](#references), and **cannot be pinned by a test here**. If the wildcard form
were unavailable, applications would name the two scopes exactly; the design does not depend on the
wildcard, only the ergonomics do.

### D4 — Pin ONE messaging semantic-convention version; never emit two spellings for one concept

**Pinned version: OpenTelemetry semantic conventions v1.30.0** (release tag verified to exist;
released 2025-01-24).

Broker-boundary spans emit that version's attribute spellings, verified against
`docs/messaging/messaging-spans.md` at tag `v1.30.0`:

- `messaging.system` — Required
- `messaging.operation.name` — Required
- `messaging.operation.type` — Conditionally required; allowed values `create`, `send`, `receive`,
  `process`, `settle`
- `messaging.destination.name` — Conditionally required
- `messaging.message.id` — Conditionally required (single-message operations)
- `messaging.batch.message_count` — Conditionally required (batch operations)
- `error.type` — Required only when the operation failed
- Span name convention: `{messaging.operation.name} {destination}`

This pin matches what `RabbitMQ.Client` 7.2.1 already emits. Verified by reading the user-string
heap of the shipped assembly: it contains `messaging.operation.name`, `messaging.operation.type`,
`messaging.system`, `messaging.destination.name`, `messaging.message.id`,
`messaging.message.conversation_id`, `messaging.message.body.size`,
`messaging.message.envelope.size`, `messaging.rabbitmq.delivery_tag`, and
`messaging.rabbitmq.destination.routing_key`.

`Azure.Messaging.ServiceBus` 7.20.1, by contrast, still emits the **older** `messaging.operation`
spelling (verified in its user-string heap alongside `messaging.batch.message_count` and
`messaging.system`). Chatter does not follow the ASB SDK's older spelling and does not emit both:
**one concept, one attribute name, taken from the pinned version.** A trace may therefore contain a
Chatter span carrying `messaging.operation.type` next to an ASB SDK span carrying
`messaging.operation`; that is the SDK's convention lag, not a Chatter inconsistency, and both are
valid under their respective pinned versions.

**CQRS pipeline spans use Chatter-native attribute names under a `chatter.` prefix.** No
OpenTelemetry convention covers in-process CQRS dispatch, so inventing a `messaging.*` spelling for
it would be a false claim of conformance. The concrete constant set lives in one place
(`ChatterTelemetryTags`) so the vocabulary has a single definition point, mirroring how ADR-0004
made `MessageContext` the single ground truth for header keys.

**Attribute names are NOT compile-time API.** They are emitted telemetry data, not a type surface.
The expectation recorded here is that Chatter's attribute names track the pinned semconv version and
**may change in a minor release** when the pin advances. Applications that hard-code attribute names
in dashboards or alert queries should expect to revisit them on a semconv pin bump; the pin bump is
announced in the affected package's CHANGELOG.

### D5 — W3C trace-context keys live in `TraceContextHeaders`, declared OUTSIDE `MessageContext`

`"traceparent"` and `"tracestate"` are declared in a `TraceContextHeaders` static in
`Chatter.MessageBrokers`. They are **not** added to `MessageContext`.

This is the load-bearing structural decision, and it is forced by a real gate in the RabbitMQ
adapter.

`RabbitMqHeaderMarshaller` has a static-constructor **completeness gate**
(`src/Chatter.MessageBrokers.RabbitMQ/src/Chatter.MessageBrokers.RabbitMQ/RabbitMqHeaderMarshaller.cs:120-147`).
At type initialization it reflects every `public static` `string` field on `MessageContext` and
throws `InvalidOperationException` naming any key that lacks an explicit `HeaderDisposition` in its
inbound disposition table. `MessageContext` currently declares 17 such fields
(`src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/MessageContext.cs:125-198`), each with a
disposition.

Adding `traceparent`/`tracestate` **to** `MessageContext` would therefore:

1. require a same-release `Chatter.MessageBrokers.RabbitMQ` change to add two dispositions, coupling
   two independently-versioned packages that this repository deliberately versions apart; and
2. produce a runtime `TypeInitializationException` — on first touch of the marshaller type, i.e. at
   the first send or receive — for **any** application that upgrades `Chatter.MessageBrokers`
   without also upgrading `Chatter.MessageBrokers.RabbitMQ`. That is a hard startup break, not a
   degradation.

Keys declared **outside** `MessageContext` are treated by the marshaller as **non-core** and are
preserved verbatim in both directions
(`RabbitMqHeaderMarshaller.cs:290-292`: *"NON-core keys: preserve verbatim. Force-decoding an unknown
`byte[]` would corrupt a genuine binary header"*). That is precisely the desired behavior: the trace
context rides as an ordinary application header, with no adapter change and no cross-package
release coupling.

**Recorded consequence — the extractor MUST tolerate `byte[]`.** Because a non-core key is
*preserved verbatim* rather than decoded, RabbitMQ delivers the value back as an AMQP `longstr`,
which surfaces in .NET as `byte[]` (the marshaller's own `DecodeStringTypedValue` documents
`byte[]` as *"AMQP longstr from a real broker"*, but that decode runs only for **core** keys). The
trace-context extractor therefore MUST accept `byte[]` (UTF-8) **as well as** `string`. This is not
optional defensive coding; on RabbitMQ it is the normal case.

### D6 — Receive-side parenting: extracted context is the PARENT, ambient is a LINK

The receive span parents to the **extracted** trace context from the message headers. When an
ambient `Activity.Current` exists **and differs** from the extracted parent, it is attached as a
**link**, never promoted to parent.

Rationale: a message's causal parent is its producer. Re-parenting to whatever local activity
happened to be current at delivery time would sever the distributed trace at every hop. A link
preserves the local-ambient relationship — useful when a poll loop or host activity is worth seeing —
without detaching the message from the trace that produced it.

### D7 — Span granularity: per dispatch call on send, per delivery on receive

**SEND: one span per dispatch call, not per message.** `BrokeredMessageDispatcher` constructs N
`OutboundBrokeredMessage` instances from a **single shared** `options.MessageContext` dictionary
reference — the same instance is handed to every message in the loop
(`src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Sending/BrokeredMessageDispatcher.cs:107-129`).
The shared-dictionary consequence is already visible in the codebase: `OutboundBrokeredMessage`'s
constructor stamps a fresh `CorrelationId` only when the context does not already carry one
(`Sending/OutboundBrokeredMessage.cs:31-34`), so in a batch the first message stamps it and all N
share it. A per-message `traceparent` is therefore **not representable** without changing the
dispatcher's context-sharing shape, which is out of scope and would change existing correlation
behavior. The send span is tagged with the batch message count
(`messaging.batch.message_count`, per D4).

**RECEIVE: one span per delivery, not per retry.** The recovery strategy **wraps** dispatch —
`BrokeredMessageReceiver.cs:1025-1027` invokes `DispatchReceivedMessageAsync` inside
`_recoveryStrategy.ExecuteAsync(...)`. A span opened outside that wrapper therefore spans **all**
retry attempts for one delivery. Retries are recorded as an attempt-count tag on the span plus
per-retry events emitted **only** when `Activity.IsAllDataRequested` is true, so a sampled-out span
pays nothing for event construction.

### D8 — No suppression of, and no key namespacing against, the broker SDKs' own instrumentation

Chatter spans are ordinary parents; the SDKs' spans nest inside them. Trace-id continuity holds
under same-key last-writer-wins because both Chatter and the SDK derive their context from the same
ambient `Activity` chain — whichever writes `traceparent` last writes a value from that same trace.

Two verified facts make this safe by default:

- **`Azure.Messaging.ServiceBus` 7.20.1 is OFF by default.** Its `ActivitySource` tracing is gated
  behind the AppContext switch `Azure.Experimental.EnableActivitySource` / environment variable
  `AZURE_EXPERIMENTAL_ENABLE_ACTIVITY_SOURCE`. Both literals are present in the resolved
  `Azure.Core` 1.46.2 assembly (verified in its user-string heap; the resolved version is pinned by
  `src/Chatter.MessageBrokers.AzureServiceBus/tests/packages.lock.json`).
  **NOW VERIFIED (previously recorded here as UNVERIFIED): the SDK caches the switch value in a
  static constructor**, so "set it before first touch of the SDK type" is an ESTABLISHED requirement
  rather than a documented-behavior inference. Established by decompiling the resolved package:
  `Azure.Messaging.ServiceBus` 7.20.1 compiles its own internal copy of the `Azure.Core` shared
  source, and that copy's `internal static class Azure.Core.Pipeline.ActivityExtensions` declares
  `static ActivityExtensions() => ResetFeatureSwitch();`, where `ResetFeatureSwitch` assigns
  `SupportsActivitySource` from `AppContextSwitchHelper.GetConfigValue` reading the
  `Azure.Experimental.EnableActivitySource` AppContext switch with the
  `AZURE_EXPERIMENTAL_ENABLE_ACTIVITY_SOURCE` environment variable as its fallback.
  `DiagnosticScopeFactory.GetActivitySource` then returns `null` unless that cached value is true, so
  a switch set after the type is initialized leaves the SDK with no `ActivitySource` at all and emits
  nothing. The consequence is carried in the test suite:
  `src/Chatter.MessageBrokers.AzureServiceBus/tests/Integration/AzureSdkActivitySourceSwitch.cs`
  sets the switch from a `[ModuleInitializer]` for exactly this reason and records the same
  decompilation.
- **`RabbitMQ.Client` 7.2.1 is OFF by default.** Publish-side activity creation and context
  injection are gated on `RabbitMQActivitySource.PublisherHasListeners`. Both
  `RabbitMQActivitySource` and `PublisherHasListeners` are verified present in the shipped
  assembly's metadata. **ACCURACY NOTE:** `PublisherHasListeners` is **`internal`** on
  `RabbitMQ.Client` 7.2.1. The only PUBLIC members of `RabbitMQActivitySource` are the
  `PublisherSourceName` / `SubscriberSourceName` consts and the `ContextInjector`,
  `ContextExtractor`, `UseRoutingKeyAsOperationName` and `TracingOptions` properties. The gate is
  therefore read reflectively (`BindingFlags.NonPublic`) wherever a test must assert on it, so a
  package upgrade that renames or removes the gate fails loudly instead of quietly voiding the
  premise.

**MEASURED AGAINST A REAL BROKER — RabbitMQ interop.** D8's nesting and last-writer-wins claims are
no longer reasoned only from the gate. With a .NET BCL `System.Diagnostics.ActivityListener`
attached to `"RabbitMQ.Client.Publisher"`, the SDK emits **exactly one** publisher span per dispatch
call, that span is a **direct child** of Chatter's send span, and the SDK **does overwrite** the
`traceparent` header Chatter wrote. The overwrite is harmless in precisely the way D8 predicts: the
delivered `traceparent` carries the **same trace id** and names the SDK's own descendant span, so
trace-id continuity holds under last-writer-wins. Pinned in
`src/Chatter.MessageBrokers.RabbitMQ/tests/Integration/RabbitMqTraceContextInteropTests.cs`, which
also pins the SDK-off cell where Chatter's `traceparent` arrives byte-for-byte unmodified.

**NEWLY FOUND INTEROP CONSEQUENCE — the ASB SDK's `Diagnostic-Id` stamping is SUPPRESSED, AND SO IS
ITS PER-MESSAGE SPAN.** Azure's `MessagingClientDiagnostics.InstrumentMessage` short-circuits when
the message already carries `"Diagnostic-Id"` or `"traceparent"`. Because Chatter writes
`"traceparent"` (D5), the SDK does neither of the two things that guard stands in front of.

**NOW VERIFIED (previously recorded here as UNVERIFIED): the short-circuit control flow.**
Decompiling the resolved `Azure.Messaging.ServiceBus` 7.20.1 shows the guard exactly as this ADR
described it — `if (!properties.ContainsKey("Diagnostic-Id") && !properties.ContainsKey("traceparent"))`
wrapping both the scope creation and the `Diagnostic-Id` stamping. No divergence from the earlier
description was found, and the conformance test needed no adjustment to pass.

**WIDER THAN FIRST RECORDED.** Because that guard wraps the **scope creation** and not merely the
stamp, it also suppresses the SDK's per-message span on the `ActivitySource`
`"Azure.Messaging.ServiceBus.Message"`. The SDK's send span on
`"Azure.Messaging.ServiceBus.ServiceBusSender"` is **UNAFFECTED** and nests inside Chatter's send
span, exactly as D8's opening claim requires. Both behaviors are asserted in
`src/Chatter.MessageBrokers.AzureServiceBus/tests/Integration/AzureServiceBusTraceContextInteropTests.cs`,
against a control case in which Chatter wrote no trace context and the SDK consequently both stamps
`Diagnostic-Id` and emits its per-message span.

**Mitigation, documented for applications that rely on `Diagnostic-Id`-based correlation:** set
`AZURE_EXPERIMENTAL_ENABLE_ACTIVITY_SOURCE=true` (or the equivalent AppContext switch) so the SDK
reads `traceparent` instead of stamping and reading `Diagnostic-Id`. This is a one-line environment
change and is the SDK's own forward path — but it must be in place **before the process first
touches an SDK type**, per the static-constructor caching established above.

### D9 — Sampling: a sampled-out span still propagates

When .NET BCL `System.Diagnostics.ActivityListener`s exist but the span is sampled out,
`ActivitySource.StartActivity` returns `null`. Trace context propagation still continues from the
ambient context, per R3 below. Sampling decisions must not break the distributed trace for
downstream hops that sample independently.

### D10 — AOT and trimming: safe

The instrumentation adds **no reflection and no dynamic code**. `ActivitySource`, `Meter`, and
`DistributedContextPropagator` are all statically resolvable, and the tag/attribute names are
constants.

Noted separately and explicitly **not in scope**: `QueryDispatcher` already dispatches through
`dynamic` (`src/Chatter.CQRS/src/Chatter.CQRS/Queries/QueryDispatcher.cs:36-37`), which is a
pre-existing DLR dependency and a pre-existing AOT/trimming constraint. This ADR neither introduces
nor fixes it.

## The off-guard

This section is the acceptance criterion for the whole change. Four rules, all four load-bearing.

**R1 — The outer guard at EVERY emit site is `Source.HasListeners()`** (or `Instrument.Enabled` for
metrics), evaluated **before any argument is constructed**. On the off path there must be:

- no span-name static read from a generic type — a static field on a generic type read from shared
  generic code is a runtime generic-dictionary lookup, not a constant load;
- no string interpolation;
- no tag array, no `ActivityTagsCollection`, no `KeyValuePair` allocation.

The guard is the *first* thing evaluated, and everything else is inside it.

**R2 — Trace-context injection is a pure function of an EXPLICITLY PASSED `Activity`.** The
injection helper takes the `Activity` as a parameter; a `null` activity returns immediately without
touching headers. No Chatter .NET BCL `System.Diagnostics.ActivityListener` means no Chatter
`Activity`, which means no injection, which means no header, which means **no wire change** —
regardless of how much foreign instrumentation the host application has.

**WHY R2 EXISTS, because this is the subtle part and a previous draft of this design got it wrong.**
Guarding on `Activity.Current is null` would **not** mean "Chatter tracing is off". `Activity.Current`
is non-null in any host with unrelated instrumentation — an ASP.NET Core request activity plus any
.NET BCL `System.Diagnostics.ActivityListener` at all is enough, and that is an extremely common
shape. Under it, with Chatter tracing **never opted into**, a naive `Activity.Current`-keyed guard
would still pay `DistributedContextPropagator` `Inject`, `traceparent` string construction, and
header-dictionary writes on every single message — *and* would put a `traceparent` on the wire. That
is both a measurable per-message cost and an **observable behavior change while nominally "off"**.
The guard must therefore key on **Chatter's own** .NET BCL `System.Diagnostics.ActivityListener`s,
never on ambient activity.

**R3 — `Activity.Current` may be read ONLY inside a `HasListeners()` guard.** It has exactly two
legitimate uses, both structurally unreachable when Chatter tracing is off:

- the **sampled-out fallback** — `StartActivity` returns `null` under head sampling while
  .NET BCL `System.Diagnostics.ActivityListener`s exist, so propagation continues from the ambient
  context (D9); and
- the **receive-side link** (D6).

**R4 — No hot-path shape change.** `CommandDispatcher.Dispatch` keeps its synchronous
`Task`-returning shape (`src/Chatter.CQRS/src/Chatter.CQRS/Commands/CommandDispatcher.cs:38-59`
returns `handler.Handle(...)` / `pipeline.Execute(...)` directly and is **not** `async`). Any
receive-side wrapper returns the original `Task` directly on the off path. **No async state machine
is introduced when tracing is off**, and no additional allocation is added to the non-instrumented
dispatch path.

## Propagation scope

Propagation is bounded and stated honestly. Both limitations below are **PRE-EXISTING**, affect
**ALL** headers rather than just trace context, and are **pinned by conformance tests** rather than
assumed.

**Trace context survives end-to-end for:**

- **Azure Service Bus, both directions.** The whole `MessageContext` dictionary is projected onto
  `ServiceBusMessage.ApplicationProperties` on send
  (`Sending/OutboundBrokeredMessageExtensions.cs:24` → `Extensions/MessageExtensions.cs:28-32`) and
  every application property is read back into the context on receive
  (`Receiving/InboundBrokeredMessageFactory.cs:62`).
- **RabbitMQ**, as a preserved non-core header in both directions (D5).
- **The EntityFramework outbox.** The context is persisted as a JSON string
  (`Reliability/Outbox/OutboxMessage.cs:10`) and rehydrated on replay through
  `MessageContext.MaterializePersistedContext` (`Reliability/Outbox/OutboxProcessor.cs:40,60`).
- **The Cosmos outbox.** Same shape: serialized whole on stage
  (`Reliability/CosmosOutboxDocument.cs:127`), materialized on relay
  (`Reliability/CosmosOutboxRelay.cs:158`).
- **Outbox replay generally.** The shared materialization recipe coerces a JSON string to `DateTime`
  only when `JsonElement.TryGetDateTime` succeeds, and otherwise returns the string unchanged
  (`MessageContext.cs:90-97`). A W3C `traceparent`
  (`00-<32 hex>-<16 hex>-<2 hex>`) is not ISO-8601-shaped, so it round-trips as a `string`. The
  mechanism, precisely: the `traceparent`'s two-character version prefix puts the first `-` at
  **index 2**, where an ISO-8601 date requires a digit, so `JsonElement.TryGetDateTime`'s strict
  parse declines it.
  **EXECUTED AND HOLDS — this was previously recorded as reasoned-from-code and not executed.**
  Proven independently by two conformance suites:
  `src/Chatter.MessageBrokers.SqlServiceBroker/tests/Diagnostics/WhenTraceContextCrossesTheServiceBrokerBoundary.cs`
  runs it as a `Theory` over three `traceparent` shapes (sampled, unsampled, and an all-digit trace
  id), both directly through `MessageContext.MaterializePersistedContext` and through the full
  envelope round-trip, and pairs them with a **control** proving the same recipe **does** coerce a
  genuine ISO-8601 string to a `DateTime` — so the pass is a property of the `traceparent`'s shape
  rather than of a materializer that never coerces anything; and
  `src/Chatter.MessageBrokers/tests/Diagnostics/WhenSendingWithTracingEnabled.cs`
  (`MustSurviveOutboxReplayAsAString`) proves it on a real `traceparent` produced by a real send
  span, serialized with `ChatterJson.Options`.

**Trace context does NOT survive for:**

- **`Chatter.MessageBrokers.SqlServiceBroker`'s `DefaultType` receive path.** The receiver builds a
  **fresh** header dictionary (`Receiving/SqlServiceBrokerReceiver.cs:172`) and only the Chatter
  envelope branch — taken when `MessageTypeName == ServicesMessageTypes.ChatterBrokeredMessageType`
  — replaces it with the deserialized envelope's own `MessageContext`
  (`SqlServiceBrokerReceiver.cs:178-195`). A `DEFAULT`-typed message keeps the fresh dictionary, so
  **all** upstream context is dropped. The deadletter path likewise builds a fresh dictionary literal
  (`SqlServiceBrokerReceiver.cs:347-355`). Only the envelope path — used when the sending
  application supplies `MessageTypeName == ChatterBrokeredMessageType` — round-trips context.
- **`Chatter.SqlChangeFeed`.** Its messages originate from a SQL trigger that sends
  `MESSAGE TYPE [DEFAULT]`
  (`Scripts/Triggers/CreateChangeFeedTrigger.cs:95`). There is no producer-side Chatter dispatch and
  no headers at all, so there is nothing to propagate and nothing to extract. This is inherent to
  the change-feed's origin, not a gap in the instrumentation.

## Closed-by-construction acceptance test

*What class of future finding does this design make impossible, and why?*

- **"Chatter put a `traceparent` on the wire even though I never enabled it."** Impossible by R2: the
  injection helper is a pure function of an explicitly passed `Activity`, and the only producer of a
  Chatter `Activity` is a `HasListeners()`-guarded `StartActivity`. The guard does not *check for*
  ambient activity — ambient activity is not an input to the injection decision at all. The class is
  eliminated by changing the key, not by adding a condition.
- **"A new `MessageContext` key broke the RabbitMQ adapter at type-init."** Not reached, because D5
  adds no `MessageContext` key. The existing completeness gate keeps doing its job unchanged.
- **"Chatter emits `messaging.operation` here and `messaging.operation.type` there."** Impossible by
  D4's single-pin rule plus a single constants home: there is one spelling per concept, defined once.

The one thing this design does **not** close by construction is per-emit-site guard discipline (R1):
a future emit site could be written without its `HasListeners()` guard. That is a review-and-test
obligation, and it is why a guard-cost probe is part of the implementing work rather than a nice-to-have.

## Non-goals

Explicitly out of scope for this decision and the work it governs:

- **Query dispatch instrumentation.** `QueryDispatcher` dispatches through `dynamic`
  (`QueryDispatcher.cs:36-37`) and is left untouched.
- **The unconditional `LogTrace` string interpolation in `CommandDispatcher`.** Both
  `_logger.LogTrace($"...")` calls (`CommandDispatcher.cs:47,51`) interpolate their message before
  the logging level is checked, so they allocate on every dispatch regardless of configured level.
  This is a real behavior/performance defect but it is a **separate** fix with its own risk surface;
  fixing it inside a telemetry change would conflate two concerns. **Recommended as a follow-up
  issue.**
- **Adapter-native spans** for the individual broker modules (RabbitMQ, Azure Service Bus, SQL
  Service Broker, Cosmos). D3's per-assembly naming leaves the door open; this decision does not
  walk through it.
- **Anything in the `samples/` tree.**

## Consequences

- **Zero packaging change.** No new NuGet package, no new `<module>-cicd.yml`, no new
  `version-check-all.yml` entry, no new tag prefix, no `packages.lock.json` churn. The change lands
  as source in two existing packages.
- **The diagnostics surface is public API of `Chatter.CQRS`.** It is therefore subject to SemVer:
  adding to it is a minor bump, changing or removing from it is a major bump. This is the price of
  D2's no-`InternalsVisibleTo` reality, and it is paid deliberately.
- **Telemetry attribute names are data, not API.** They may change on a semconv pin advance in a
  minor release (D4). Dashboards and alert queries that hard-code them are the application's
  responsibility to revisit; the pin bump is announced in the CHANGELOG.
- **A Chatter-instrumented trace and an ASB-SDK-instrumented trace may show two spellings of
  "operation"** until the ASB SDK advances its own convention pin (D4). Both are correct under their
  own pins.
- **Applications relying on ASB `Diagnostic-Id` correlation must set
  `AZURE_EXPERIMENTAL_ENABLE_ACTIVITY_SOURCE=true`** once Chatter tracing is enabled, and must set it
  before the process first touches an SDK type, because the SDK caches the switch in a static
  constructor (D8). The suppression is **wider than the `Diagnostic-Id` stamp alone**: the SDK's
  per-message span (`ActivitySource` `"Azure.Messaging.ServiceBus.Message"`) is suppressed with it,
  while the SDK's send span (`"Azure.Messaging.ServiceBus.ServiceBusSender"`) is unaffected and nests
  inside Chatter's. This is the single behavior change an opting-in application may notice, and it is
  documented with its one-line mitigation.
- **Two propagation gaps are documented, not silently tolerated** — the SqlServiceBroker
  `DefaultType`/deadletter paths and SqlChangeFeed. Both predate this change, both affect all
  headers, and both are pinned by conformance tests so a future change that accidentally *fixes* or
  *worsens* them is visible.
- **Off is provably off.** R1–R4 are testable properties: a guard-cost probe can assert that with no
  .NET BCL `System.Diagnostics.ActivityListener` subscribed, no header is written, no allocation
  attributable to instrumentation occurs on the dispatch path, and the wire representation is
  byte-identical to the pre-change baseline.

## References

- Issue #274 — *Optional OpenTelemetry tracing/metrics support (no hard OTEL dependency)*. Source of
  the requirement and of the flat-`"Chatter"`-name proposal this ADR diverges from in D3.
- OpenTelemetry semantic conventions **v1.30.0**, `docs/messaging/messaging-spans.md` —
  <https://github.com/open-telemetry/semantic-conventions/blob/v1.30.0/docs/messaging/messaging-spans.md>
  (the pin of D4).
- OpenTelemetry .NET — *Customizing the SDK for Tracing* (`AddSource` wildcard support) —
  <https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/docs/trace/customizing-the-sdk/README.md>
- OpenTelemetry .NET — *Customizing the SDK for Metrics* (`AddMeter` wildcard support) —
  <https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/docs/metrics/customizing-the-sdk/README.md>
- ADR-0004 — *RabbitMQ single bidirectional core↔AMQP message-translation contract*. Source of the
  completeness gate that forces D5, and the precedent for one-concept-one-spelling.
- ADR-0008 — *Document-tier participation model...*. Precedent for the closed-by-construction
  acceptance framing used above.
