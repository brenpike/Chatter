---
status: accepted
date: 2026-09-12
---

# T-SQL identifier quoting: a round-trip parse that accepts an already-quoted name verbatim

Two Dialog Commands in this module put a configured name into a T-SQL IDENTIFIER position: the `RECEIVE`
reads `FROM <queue>`, and `BEGIN DIALOG` names `FROM SERVICE <service>`. An identifier position cannot be
parameterized — a parameter there is a value expression, not a name — so whatever is configured reaches the
server as written, and ESCAPING is the only defence available. Issue #355 is the report that neither value
was escaped. The same command's `TO SERVICE` and `ON CONTRACT` clauses take string expressions, and those
already travel as `SqlParameter`s; they are not part of this problem and not part of this decision.

The obvious fix — bracket-wrap the value and double every interior `]` — is correct and total, and it would
have broken a shape this module has been feeding its own Service Broker Receiver since the integration
harness was written: the configured queue path arrives ALREADY bracketed.
`ServiceBrokerProvisioning.ObjectSet.TargetQueuePathBracketed`
(`src/Chatter.MessageBrokers.SqlServiceBroker/tests/Integration/ServiceBrokerProvisioning.cs:51`) is
`"[" + TargetQueueName + "]"`, and eight integration test classes configure the receiver's queue path with
it across nine call sites. That is not an eccentric habit: before this change the `RECEIVE` interpolated the
configured path verbatim, so bracketing it in configuration was the only way to express a name that needed
quoting at all. Escaping such a value as raw turns `[q]` into `[[q]]]`, which names no object.

The rule therefore has to be total AND has to leave an already-quoted name alone. A ROUND-TRIP PARSE gives
both: parse the value back to its raw form first, then quote the raw form.

## Considered Options

- **Option 1 — Escape every value as raw, always.** Total, one rule, nothing to reason about. Rejected
  because it silently breaks every application that brackets its queue path in configuration: the escaped
  name simply does not exist, so the failure arrives at runtime as a missing object on what is shipped as a
  security patch.

- **Option 2 — Round-trip parse: accept a well-formed quoted identifier verbatim, escape everything else
  (CHOSEN).** Total in the same sense as Option 1 — no input reaches the server unescaped unless it is
  already correctly escaped — and idempotent, so a bracketed configuration value survives unchanged. Its
  weaknesses are stated in full under *Accepted residuals*.

- **Option 3 — Throw when the value is already bracketed.** Loud instead of silent, which is the honest
  version of Option 1, but still a hard behavioural break: every application using the established bracketed
  shape would have to edit configuration to take a patch upgrade.

- **Option 4 — Validate against an allowlist of permitted identifier characters.** Stricter-looking, and
  rejected on the grounds that it buys no additional safety once escaping is total, while risking rejection
  of an exotic but perfectly valid Service Broker name. Service Broker names are routinely URL-shaped; a
  character allowlist is a guess about which of those shapes someone will use.

## Decision

**A value destined for an identifier position is parsed back to its raw form and then quoted.** Concretely:

- A value that is ALREADY a well-formed quoted identifier — it starts with `[`, ends with `]`, and every
  interior `]` is doubled — is emitted VERBATIM.
- Anything else is treated as a raw identifier: every `]` in it is doubled to `]]`, and the result is wrapped
  in `[` and `]`.
- A value that is null, empty or whitespace once parsed back to raw form THROWS. That is a well-formedness
  guard on a name that cannot address anything, not an allowlist of permitted characters.

Two properties follow, and they are the point of the rule.

**It is idempotent.** Quoting an already-quoted name returns it unchanged, so a configuration value and the
string this module builds from it converge on the same text. `[dbo].[My]]Queue]` — already quoted, with its
interior bracket already doubled — comes back out exactly as written.

**An injection attempt degrades to a nonexistent object name rather than escaping the identifier position.**
`[x]; DROP TABLE t; --` is not well-formed as a quoted identifier — it does not end with `]` — so it is
escaped whole and becomes `[[x]]; DROP TABLE t; --]`, which the server reads as one object name that happens
to contain punctuation. An undoubled interior `]` fails well-formedness the same way: a payload that opens
and closes with brackets but breaks out in the middle is escaped whole, not trusted. Both shapes are pinned
in `src/Chatter.MessageBrokers.SqlServiceBroker/tests/Scripts/UsingSqlIdentifier/WhenQuotingIdentifiers.cs`.

The rule is implemented once, in
`src/Chatter.MessageBrokers.SqlServiceBroker/src/Chatter.MessageBrokers.SqlServiceBroker/Scripts/SqlIdentifier.cs`,
and its parse-then-quote invariant is stated there. Nothing else in this module is entitled to build an
identifier by concatenation.

### Queue names are split on the dot; service names never are

This asymmetry is deliberate, and it is the part of the decision most likely to be "tidied" into symmetry by
a later reader. It must not be.

**A QUEUE name is split on top-level dots and each part is quoted separately.** Service Broker queues are
schema-scoped objects, and `dbo.MyQueue` is ordinary configuration that has to become `[dbo].[MyQueue]`.
A dot INSIDE a bracketed run is not a separator — the scan stays inside the brackets — so a one-part name
that legitimately contains a dot is expressed by bracketing it in configuration, `[my.queue]`, and the
round-trip rule then passes it through unsplit.

**A SERVICE name is NEVER split.** Service Broker services are not schema-scoped, so there is no
schema-qualified form to decompose, and the naming convention for them is URL-shaped names carrying both
dots and slashes — this module's own `//Chatter` contract and `//Chatter/BrokeredMessage` message type
(`src/Chatter.MessageBrokers.SqlServiceBroker/src/Chatter.MessageBrokers.SqlServiceBroker/ServicesMessageTypes.cs:34-35`)
are that shape. Splitting `//company.com/service` on its dot would produce `[//company].[com/service]`: two
identifiers, addressing nothing. The whole name is quoted as one part instead.

### Accepted residuals

**An empty part is now a hard failure where the value previously travelled to the server as written.**
`dbo.`, `.q` and `a..b` each contain a part that is empty once parsed, and each now throws when the command
is built. Before this change the configured path was interpolated verbatim, so the misconfiguration left the
application and became the server's problem. This is a better failure and it is still a NEW one: a
deployment carrying such a value fails earlier and more loudly than it did.

**A target service name is parameterized and now travels intact.** `TO SERVICE` takes a string expression,
so the destination service name is bound as a parameter rather than quoted; what changes is that it is no
longer stripped of its brackets first. An application that was relying on the old strip — configuring
`[my]service` and reaching `myservice` — now addresses the name it actually wrote.

**Well-formedness is the only thing checked.** A value that is a well-formed quoted identifier is emitted
verbatim regardless of what is inside it, because inside a correctly escaped identifier nothing is
executable. Anyone who reads this rule as input VALIDATION will be disappointed; it is escaping, and its
guarantee is that the identifier position cannot be escaped, not that the name is sensible.

## Consequences

- **Bracketed configuration keeps working.** A queue path configured as `[MyQueue]` or `[dbo].[MyQueue]`
  produces exactly the same T-SQL it did before, so applications using the established shape see no change
  and the integration harness needs no edit.
- **Unbracketed configuration is now safe rather than merely conventional.** A path configured as
  `dbo.MyQueue` or a service configured as `//Chatter/Target` is quoted on the way out instead of being
  concatenated raw.
- **This is a patch-level behavioural fix, not a surface change.** No public type or option changes; what
  changes is the text of the T-SQL the Dialog Commands build.
- **A new identifier position in this module inherits the rule by using the quoter**, and must choose
  explicitly between the single-part form for a service-like name and the multi-part form for a
  schema-scoped one. Choosing the multi-part form for a service name is the specific mistake this ADR exists
  to prevent.
- **An allowlist is settled as declined, here.** A future contributor reaching for a character allowlist
  should know it was considered and rejected because escaping is already total, not because it was hard.

## References

- Issue #355 — *SQL Service Broker builds identifiers by string concatenation*. The report this ADR answers.
- `src/Chatter.MessageBrokers.SqlServiceBroker/src/Chatter.MessageBrokers.SqlServiceBroker/Scripts/SqlIdentifier.cs`
  — the single implementation of the rule, with its parse-then-quote invariant;
  `src/Chatter.MessageBrokers.SqlServiceBroker/tests/Scripts/UsingSqlIdentifier/WhenQuotingIdentifiers.cs` —
  the pinned behaviour, including verbatim pass-through, escape-whole for a malformed payload, the
  URL-shaped service name, and the empty-part throws.
- `src/Chatter.MessageBrokers.SqlServiceBroker/CONTEXT.md` — *Queue*, *Conversation*, *Dialog Command*. This
  module provisions nothing, which is why a queue or service name is always configuration supplied from
  outside and never a name this module minted.
- ADR-0015 — *Inbound header trust: ground truth stamped over wire values, and no trust boundary*. Its
  reasoning is the opposite case and worth contrasting: there the layer had nothing to anchor trust to, so no
  boundary was built; here the value reaches a position where escaping fully closes the hole, so it is
  closed.
