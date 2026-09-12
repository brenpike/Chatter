---
status: accepted
date: 2026-09-11
---

# Inbound header trust: ground truth stamped over wire values, and no trust boundary

Every reserved `Chatter.*` key in a Message Context arrives from the wire, and nothing on the receive path
asks who wrote it. Epic #299 states the gap accurately: there is no integrity check, no authenticity check,
no ingress allowlist, and no boundary anywhere between "a header a peer Chatter service legitimately stamped"
and "a header any sender wrote". Two reviewers found it independently from opposite ends of the pipeline.

The epic is right about the facts and its four children are each real. What it then proposes — an ingress
allowlist that drops or re-derives route-critical and control-plane keys, flipped slip precedence, and a
destination-validation seam — is a TRUST BOUNDARY, and the question this ADR settles is whether Chatter
should have one. It should not, and the reason is not that the risk is tolerable. It is that a trust boundary
needs something to anchor trust TO, and at this layer there is nothing: no broker Chatter sits on hands it a
per-message sender identity it could check. An allowlist without an authenticated sender does not separate a
peer's header from an attacker's; it separates keys Chatter happened to name from keys it did not, which is a
different property wearing the same word.

What Chatter does instead, and has done in every realization since before the epic was filed, is refuse to
need the answer: wherever a receiver holds ground truth for a value, it stamps that ground truth OVER
whatever the wire said. That is not an allowlist and it is not authenticity. It is a narrower and more
honest claim — that the values Chatter's own machinery depends on are sourced from the receiver, not from
the sender — and it is what this ADR records as the trust doctrine, alongside the decision not to build the
boundary the epic asked for.

## Considered Options

- **Option 1 — Build the strict trust boundary the epic describes.** An ingress allowlist over `Chatter.*`
  keys, with `RoutingSlip`, `IsError`, `FailureDetails`, `ReplyTo*`, `ReceiveAttempts`,
  `ScheduledEnqueueTimeUtc` and `PartitionKey` dropped or re-derived on receive; a container-provided slip
  beating an inbound header; a destination validator for honoured slip steps. It is the shape a security
  reviewer expects, and it is not a foolish shape. It is rejected on the reachability and cost evidence
  below, and on the observation that it would buy an ASSERTION of authenticity that the layer cannot supply.

- **Option 2 — Record the ground-truth-over-wire doctrine as the trust model, harden the type-confusion
  sites, and build no boundary (CHOSEN).** No change to which headers survive a hop, so no wire-observable
  behaviour change and no major bump. Its weaknesses are real and are stated in full under *Accepted
  residuals*: a hostile slip is still honoured by an application that opts into slip routing, and an
  application that reads a `Chatter.*` header as an assertion about its sender is still wrong to.

## Why authenticity is unobtainable at this layer

A trust boundary that distinguishes a peer from an attacker needs a per-message sender identity the receiver
can verify. No broker Chatter realizes supplies one.

- **Azure Service Bus.** `InboundBrokeredMessageFactory.CreateContext`
  (`src/Chatter.MessageBrokers.AzureServiceBus/src/Chatter.MessageBrokers.AzureServiceBus/Receiving/InboundBrokeredMessageFactory.cs`)
  is handed a `ServiceBusReceivedMessage` and reads its `ApplicationProperties` plus its top-level
  `TimeToLive`, `ExpiresAt`, `DeliveryCount`, `SessionId`, `MessageId`, `ContentType` and `Body`. There is no
  sender principal on the received message to read. Azure Service Bus authorizes a SEND at the entity, and
  what it authorizes is the act, not the content: the broker does not stamp the sending principal onto the
  message, so a message from a principal with send rights and a message from any other principal with send
  rights are indistinguishable once delivered.

- **RabbitMQ.** `RabbitMqMessageTranslator`'s one declarative `_fieldMap` is the sole declaration of which
  native AMQP frame fields carry a core concept: `MessageId` (which the receiver carries on the
  `MessageBrokerContext` itself, with no core key sink), `ContentType`, the dual-home `CorrelationId`, and a
  dedicated arm lifting the native `Expiration` into `MessageContext.TimeToLive`. Everything else with a
  native home is DECISION-B carry-only — `ContentEncoding`, `Type`, `AppId`, `Priority`, `Timestamp` — held
  on the republish-carry facts and never surfaced into the core context. `UserId` is not among either set: it
  does not appear anywhere in the module's source. Even if it were surfaced it would not help, because AMQP's
  `user-id` is optional and publisher-set; a broker that validates it validates it against the publishing
  connection's own credential, which is a statement about who connected, not about the itinerary they wrote.

- **SQL Service Broker.** `SqlServiceBrokerReceiver` reads what the `RECEIVE` returns
  (`src/Chatter.MessageBrokers.SqlServiceBroker/src/Chatter.MessageBrokers.SqlServiceBroker/Scripts/ReceiveMessageFromQueueCommand.cs`):
  conversation group handle, conversation handle, message sequence number, `service_name`,
  `service_contract_name`, message type name. `service_name` is the LOCAL service the conversation lands on —
  the receiving side's own name — not the far end's identity, and the receiver stamps it from the delivery
  under `SSBMessageContext.ServiceName` accordingly.

## The practice that already holds

Every realization copies the inbound headers and then stamps its own ground truth ON TOP. The pattern is
identical across all three, and it predates the epic.

- **Azure Service Bus** copies every `ApplicationProperties` entry into a fresh mutable dictionary and then
  writes `MessageContext.TimeToLive` from the message's `TimeToLive`, `MessageContext.ExpiryTimeUtc` from
  `ExpiresAt.UtcDateTime`, `MessageContext.InfrastructureType` from `ASBMessageContext.InfrastructureType`,
  and `MessageContext.ReceiveAttempts` from `DeliveryCount` — four stamps that a wire copy of any of those
  four keys cannot survive. `MessageContext.GroupId` is stamped from the broker's `SessionId` when the
  delivery has one.

- **RabbitMQ** declares the disposition of every core key up front.
  `RabbitMqHeaderMarshaller._dispositions` gives `MessageContext.TimeToLive`,
  `MessageContext.ReceiveAttempts`, `MessageContext.IsError` and `MessageContext.ChatterBaseHeader` the
  `Drop` disposition, so those wire values are omitted from the core context entirely rather than decoded.
  The table is not advisory: the type initializer reflects over `MessageContext`'s public static string
  fields and throws when any of them lacks a declared disposition, so the failure surfaces as a
  `TypeInitializationException` on first use and a NEW core key cannot be added without someone deciding, in
  writing, whether an inbound copy of it is trusted.

- **RabbitMQ's receiver strips its own outbound control keys at receive.** After translation,
  `RabbitMqReceiver` removes `RabbitMqMessageContext.TargetExchange` and `RabbitMqMessageContext.RoutingKey`
  from the header dictionary, and removes both delivery-count keys. Its `INVARIANT:` comment states the
  reason exactly, and the reason is the laundering mechanism the epic describes: *"the core seeds an outbound
  send's options from the inbound context, so preserving an inbound copy of these would silently re-route
  every receive-then-send follow-up back toward the inbound queue."* That is a receiver electing to be the
  authority for its own control plane, by hand, on the same reasoning an ingress allowlist would use — and
  it already ships.

- **SQL Service Broker** takes the Chatter envelope's `MessageContext` wholesale off the wire and then
  overwrites the conversation group handle, conversation handle, message sequence number, service name,
  service contract name, message type name, `MessageContext.InfrastructureType` and
  `MessageContext.ReceiveAttempts`. The attempt count comes from `_localReceiverDeliveryAttempts`, a
  `ConcurrentDictionary<Guid, int>` owned by the receiver instance and keyed by conversation handle: it is
  counted locally, so no wire value influences it at all.

The doctrine those four share is one sentence: **where a receiver has ground truth, the wire does not get a
vote.** Nothing in the epic's allowlist would change any of them.

## Decision

**Chatter does not authenticate inbound `Chatter.*` headers, and does not strip control-plane headers at a
trust boundary.** A `Chatter.*` key arriving on a delivery is DATA — a proposal, carrying no assertion
whatever about who wrote it. It is not evidence that a peer Chatter service produced the message.

**Authority for anything a header proposes comes from the receiving application's own registration, never
from the header.** A routing slip on the wire is honoured only because the application registered a behavior
that honours routing slips. A destination is reached only because the application's own broker credential can
reach it. A handler runs only because the application registered it for that message type. In every case the
header selects among capabilities the application already granted; it never grants one.

**Where a receiver holds ground truth for a value, it stamps that ground truth over the wire value**, as the
four realizations above already do. This is the whole of the mechanical guarantee, and it is deliberately
narrower than authenticity: it says the values Chatter's own machinery depends on come from the receiver, and
it says nothing at all about the values it does not.

### Why the strict boundary was rejected

**Every mechanism in the epic requires the attacker to have send rights on a queue the application receives
from.** An attacker holding that already has a far larger capability than any header confers: they can send
any payload of any registered message type and invoke any registered handler with content of their choosing.
Header trust is not the boundary being crossed in that scenario; it was crossed at the point send rights were
granted. Hardening the headers of a message from a principal who can already choose the message's entire body
buys very little.

**The one genuinely new capability is confused-deputy injection into a THIRD queue — and it is gated behind
an entry point with zero callers.** `RoutingSlipBehavior<TMessage>` is the only place in the library that
honours a slip automatically: it reads the slip from the context, lets the pipeline run, and then re-sends
the handled command to the slip's first `DestinationPath` using the application's own credential. The only
registration path for it is `WithRoutingSlipBehavior`, and that name's SOLE occurrence in the repository is
its own declaration at
`src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Routing/Slips/CommandPipelineBuilderExtensions.cs:8`.
No test, no sample and no module calls it. The deputy exists, it is reachable by anyone who registers the
behavior, and today nobody in this repository does.

**The strict fix would break behaviour the project documents as by-design.** Inheriting the inbound Message
Context onto an outbound message is not an oversight to be corrected;
`src/Chatter.MessageBrokers.AzureServiceBus/CONTEXT.md:36` records it as a decision: *"A handler sending or
publishing through `IMessageHandlerContext` inherits the inbound Group Id (and the rest of the inbound
message context) onto the outbound message by design, not incidentally."* That is the same copy #323 calls
laundering. `BrokeredMessageDispatcher.MergeSendOptionsWithMessageContext` — and its publish twin — build the
outbound options from `GetInboundBrokeredMessage()?.MessageContextImpl` and then merge the caller's own
options over the top, and that inheritance is what makes correlation, Group Id and Trace Context propagate
across a hop without a handler restating them.

**The cost is a major version and three-realization conformance work.** Changing which headers survive a hop
is observable wire behaviour, so the epic's own versioning note is right that it is breaking. It would take
`Chatter.MessageBrokers.AzureServiceBus` from `2.4.0` to `3.0.0`, and the ingress rule would have to be
specified once and then conformance-tested against every implementation of `IMessagingInfrastructureReceiver`
— `ServiceBusReceiver`, `RabbitMqReceiver` and `SqlServiceBrokerReceiver` — on the same terms ADR-0010's D7
obligation already imposes for settlement. Paying that for a deputy with no callers, against an attacker who
by construction already holds send rights on a queue the application receives from, is the wrong trade.

### Accepted residuals

These are the costs of Option 2. They are stated as they are, not softened, because an ADR whose residuals
are hedged cannot stop the epic being reopened on the same evidence.

**An allowlist would have bounded deputy authority, but never itinerary integrity.** A destination allowlist
constrains WHICH hops a slip may name; it cannot make the itinerary itself trustworthy. An attacker able to
write the slip could still truncate it — dropping the compensation or audit step at the end — or reorder the
steps within the permitted set, and every resulting hop would pass the allowlist. Integrity of a multi-step
business process is a property of durable state, not of a header riding with the message: **saga correctness
belongs in the saga store, never in headers.** That is the reason a destination validator was not adopted as
a partial measure — it would look like itinerary protection while supplying none.

**RabbitMQ's delivery count stays publisher-influenced, and is bounded only to self-shrink.**
`ResolveReceiveAttempts` reads the delivery-count header off the delivery, and its own comment names the
value untrusted: a hostile publisher can stamp it. The bound is that the value is saturated into
`[0, int.MaxValue]` before being stamped as `MessageContext.ReceiveAttempts`, so a negative or oversized
header can never produce a negative or wrapped attempt count. What that leaves is asymmetric in the safe
direction for the broker and the unsafe direction for the application: a publisher can UNDERSTATE the count
and win itself extra retries, but cannot overstate it into a value that dodges dead-lettering, because the
core dead-letters when the count reaches `MaxReceiveAttempts`. The floor of `0` is what bounds the abuse:
the best a publisher can buy itself is ONE fresh full budget, after which the count climbs on every
redelivery, so the budget only ever shrinks from there. The residual is wasted retries, not a poison message
that never dead-letters.

**`GroupId` is inherited by design and maps to a broker-meaningful field.** On Azure Service Bus the Group Id
IS the `SessionId` — the FIFO lane. A sender who sets `Chatter.GroupId` therefore chooses which lane the
receiving application's own outbound traffic serializes into, and `CONTEXT.md:36` records that inheritance as
intended. This is accepted rather than fixed: removing it would break the propagation the term exists for.

**An untyped `Chatter.*` header can still fault a SEND.** The tolerance work below covers the RECEIVE path.
`OutboundBrokeredMessageExtensions.GetScheduledEnqueueTimeUtc` still hard-casts
`(DateTime?)GetMessageContextByKey(ASBMessageContext.ScheduledEnqueueTimeUtc)`, and
`OutboundBrokeredMessage.RefreshTimeToLive` hard-casts `(DateTime?)` on `MessageContext.ExpiryTimeUtc`, so a
wire value of the wrong type that is inherited outward throws on dispatch. That is tracked by the still-open
#464 and is not closed by this ADR.

**An application that reads a `Chatter.*` header as an assertion about its sender is wrong to, and Chatter
will not stop it.** `Via`, `ReplyToAddress`, `ReplyToGroupId`, `FailureDetails`, `FailureDescription` and
`Subject` are all sender-written and survive a hop. They are useful for correlation and diagnostics and are
worthless as an authorization input.

### The #324 and #325 changes are robustness, not a trust boundary

The two fixes landing in the release that carries this ADR remove ways a malformed header can damage the
receive path. They change nothing about trust.

`InboundBrokeredMessage.GetMessageContextByKey<T>` now type-TESTS rather than casts, so a wire value of an
unexpected type reads as ABSENT instead of faulting the receive, and the default `MessageDeliveryCountAsync`
no longer leaves a message unsettled when the attempt-count header is missing or foreign-typed — the case
that produced an infinite redelivery loop with no dead-letter escape, because the probe that would have
detected max-receives was the thing failing. `TryGetRoutingSlip` no longer swallows every exception as "no
slip present": only a MALFORMED slip value — a non-string, a null or whitespace string, unparsable JSON, or
JSON that parses to null — returns false, with an optional warning naming the message id, and every other
exception propagates.

Both make a hostile message harmless to the RECEIVER rather than making it unauthoritative. A well-formed
slip from a hostile sender is still honoured exactly as before by an application that registered the
behavior, and the wire header is still consulted BEFORE the container, so an inbound slip still takes
precedence over one the application added itself. Nobody should read these fixes as having closed #299.

## Consequences

- **No production behaviour changes because of this ADR.** It records a doctrine the code already follows and
  a decision not to add a mechanism. No header's fate on a hop changes, so no module takes a major bump for
  it.
- **#322, #326 and #464 stay open as known, accepted exposure**, not as work queued behind this ADR. They
  describe real properties of the system; this decision is that the properties are not worth the fix at the
  current evidence, and closing them requires the trigger below rather than a fresh reading of the same
  facts.
- **The allowlist question is settled ONCE, here, as the epic asked — with the answer "no allowlist".** A
  future contributor who reaches for a per-key ingress rule should know the rule was specified, costed and
  declined, and that the reason is the missing sender identity rather than the difficulty of the rule.
- **The ground-truth-over-wire practice is now a stated obligation on a NEW realization**, not an accident
  each existing one arrived at separately. A broker adapter added later must stamp `InfrastructureType` and
  `ReceiveAttempts` from its own knowledge and must not let a wire copy of either survive.
- **RabbitMQ's completeness gate is the only enforced part of the doctrine, and only for that module.** A new
  `MessageContext` key fails `RabbitMqHeaderMarshaller`'s type initialization until it is dispositioned, so
  the RabbitMQ realization cannot silently inherit a new core key from the wire. Azure Service Bus and SQL
  Service Broker have no equivalent gate: a new key added there is trusted by default until someone stamps
  over it.
- **Operational advice replaces the missing boundary.** A queue an application receives from should be
  writable only by principals the application already trusts to invoke its handlers, because that is what
  send rights amount to. This ADR is a statement that Chatter relies on that grant being correct; it does not
  supply a second line of defence behind it.
- **The revisit trigger is a caller or a credential, not a re-reading of this evidence.** Two things reopen
  the epic. FIRST, a real user of routing slips — the moment `WithRoutingSlipBehavior` has a caller, or a
  handler calls the public `TryGetRoutingSlip`/`Forward`/`Send(message, slip)` extensions on an inbound slip,
  the confused deputy is live and a destination allowlist stops being theoretical. SECOND, a broker
  realization that gains an authenticated per-message sender identity — at that point a trust boundary has
  something to anchor to, and the argument in this ADR no longer holds.

## References

- Epic #299 — *No trust boundary on inbound `Chatter.*` control-plane headers*. The epic this ADR answers,
  and the source of the allowlist proposal it declines.
- Issue #322 — *Inbound routing slip steers outbound forwarding with no validation (confused deputy)*;
  issue #326 — *`FailureContext.ToString` embeds exception message and stack trace*; issue #464 — *Outbound
  header reads still hard-cast, so a mistyped inherited header throws on dispatch*. All three remain open as
  accepted exposure. Issue #323 — *Handler send/publish copies the entire inbound context, laundering
  control-plane headers through trusted hops* — is CLOSED with this decision: the context inheritance it
  calls laundering is by design, as recorded above, and its one remaining finding, the outbound hard casts,
  was split out into #464.
- Issue #324 — *Unvalidated casts of broker-supplied headers; failed delivery-count probe leaves the message
  permanently unsettled*; issue #325 — *`TryGetRoutingSlip` catch-all masks tampering and deserialization
  faults as 'no slip present'*. The robustness fixes shipping alongside this ADR.
- ADR-0004 — *RabbitMQ message translation contract*. Source of the one declarative field map, the
  carry-only DECISION-B natives, and the marshaller's per-key disposition table and completeness gate that
  this ADR reads as the enforced half of the doctrine.
- ADR-0010 — *Optional BCL-only telemetry: per-assembly sources and the off-guard*. Source of the D7
  obligation that every implementation of `IMessagingInfrastructureReceiver` is contract-tested, which is the
  conformance cost a strict ingress rule would have incurred three times over.
- ADR-0012 — *Event fan-out: abort on the first failing handler, documented rather than aggregated*, and
  ADR-0014 — *In-process session concurrency: one session-mode receiver multiplexing N single-session
  receivers*. Precedent for stating a decision as a type-local fact plus an obligation the caller carries.
- `src/Chatter.MessageBrokers/CONTEXT.md` — *Routing Slip*, *Trace Context*, *Trace Context Header*;
  `src/Chatter.MessageBrokers.AzureServiceBus/CONTEXT.md` — *Group Id ↔ SessionId realization*, the
  by-design context inheritance this ADR declines to break.
