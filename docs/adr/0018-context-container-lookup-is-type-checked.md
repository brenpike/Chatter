---
status: accepted
date: 2026-09-12
---

# Context Container lookup is type-checked: a tri-state read, `false` from `TryGet` and loud from `Get`

`ContextContainer` is the type-keyed bag every **Message Context** carries
(`src/Chatter.CQRS/CONTEXT.md:27`), and until this change its lookup asked only whether the key was present.
`TryGet<T>(string, out T)` executed a blind `result = (T)value;` and returned `true` — the whole of its
type handling, at `bfda47a:src/Chatter.CQRS/src/Chatter.CQRS/Context/ContextContainer.cs`. So a key holding
another type threw `InvalidCastException`, and a key holding a stored `null` read as a non-nullable value
type threw `NullReferenceException`, both out of a method whose entire contract is to answer `true` or
`false`, and neither documented on it. The two failure modes are the runtime's own and were confirmed
directly: casting a `null` `object` to `Guid` throws `NullReferenceException`; casting a boxed `int` to
`string` throws `InvalidCastException`.

`Get<T>(string)` was built on that same lookup and inherited a second defect. It called `TryGet` and turned a
`false` into `KeyNotFoundException("No item found in container with key: " + key)` — so a genuine mismatch
never reached that line at all; it escaped earlier as the cast exception thrown inside `TryGet`. The loud
path was loud by accident, and the one message `Get` could produce deliberately was a message that would have
been false in the mismatch case.

The question this ADR settles is what a lookup MEANS when the key is present and the value is not assignable
to the requested type. Issue #451 asks for "a miss". That answer is right for `TryGet` and wrong for `Get`,
and the asymmetry is the substance of the decision.

This is a NEW decision, not an amendment to ADR-0011. ADR-0011 answers whether the container should be
synchronized and ends "The code is unchanged by this decision"; this one changes code and answers a different
question. What it does take from ADR-0011 — by citation, unchanged — is the stored-`null` presence rule.

## The governing invariant

**Only reads that THREW may change. Every read that SUCCEEDED still succeeds, with the same value.** Every
clause of the decision below is constrained by that sentence, and it is what makes the change a fix to a
method's own contract rather than a change to the container's behaviour. Four existing-behaviour cases are
pinned against it in `src/Chatter.CQRS/tests/Context/UsingContextContainer/WhenTryingToGet.cs`: a stored
value read as its base type or an interface it implements
(`MustReturnTrueAndContextViaOutParameterWhenStoredValueIsReadAsBaseTypeOrInterface`), a boxed `int` read as
`int?` (`MustReturnTrueAndValueOutParameterWhenStoredValueTypeIsReadAsNullableValueType`), the string-keyed
`Guid` the outbox path writes and reads
(`MustReturnTrueAndEqualValueForAStringKeyedStructWrittenByTheOutboxPath`), and the type-keyed round trips for
a reference, a value and a stored `null` (`MustRoundTripReferenceValueAndStoredNullContextUnderTheTypeKey`).

## Considered Options

- **Option 1 — Keep throwing.** A type-mismatched read is arguably a programming error, and a programming
  error should be loud at the point it happens rather than silently reported as absence. The argument is
  sound and is not dismissed: it is **rejected for `TryGet` and PRESERVED for `Get`**. A `Try`-method that
  throws `InvalidCastException` violates the one thing its signature promises, and every caller that wrote
  `if (container.TryGet<T>(key, out var v))` believing the guard was total was wrong through no fault of its
  own. Loudness does not disappear under the chosen option; it moves to the member whose contract can carry
  it.

- **Option 2 — A naive `value is T` test in place of the cast.** It is the smallest possible edit and it
  fixes the reported crashes. It is rejected because it also answers `false` for a stored `null` under a
  REFERENCE-type `T`: `null is string` is `false`. That contradicts the presence rule ADR-0011 records and
  issue #332 established — a stored `null` is a PRESENT value, returned as-is rather than treated as absent.
  Adopting it would have regressed a read that succeeds today, which the governing invariant forbids. The
  chosen option keeps `value is T` as ONE arm; what it refuses is letting that arm decide the stored-`null`
  case by itself.

- **Option 3 — A tri-state lookup: `Missing`, `Found`, `TypeMismatch` (CHOSEN).** Present-but-wrong-type stops
  being confused with absent, which is exactly what lets `TryGet` answer `false` while `Get` says something
  true about which of the two happened. Its cost is a private enum and one more `out` parameter on an
  internal helper, and the residual it accepts is stated under *Consequences*: `TryGet` now hides a genuine
  programming error at the call site that makes it.

## Decision

**A lookup answers one of three outcomes, and "found" means present AND assignable to `T`.** The core is the
private `LookupOutcome` enum and `FindTypedValue<T>(string, out T, out object storedValue)` at
`src/Chatter.CQRS/src/Chatter.CQRS/Context/ContextContainer.cs:100-133`, which recurses into the inherited
container (`:127`):

| State | Outcome |
|---|---|
| key absent locally, no inherited container | `Missing` (`:130-132`) |
| key absent locally, inherited container present | the inherited container's outcome (`:125-128`) |
| key present, `value is T` | `Found`, result is the typed value (`:113-117`) |
| key present, value `null`, `default(T) is null` | `Found`, result is `default` (`:122`) |
| key present, anything else | `TypeMismatch` (`:122`) |

**`TryGet<T>(string, out T)` returns `true` on `Found` only, and no longer throws** — it is a single
expression, `FindTypedValue(...) == LookupOutcome.Found` (`:97-98`). On the two failing outcomes `result` is
`default(T)`. Pinned by `MustReturnFalseAndDefaultOutParameterWhenStoredValueIsNotAssignableToRequestedType`
and `MustReturnFalseAndDefaultOutParameterWhenStoredNullIsReadAsNonNullableValueType` in
`WhenTryingToGet.cs`.

**`Get<T>(string)` distinguishes the two failures** (`:50-67`). `Missing` keeps the existing
`KeyNotFoundException` and its message VERBATIM — `"No item found in container with key: " + key` (`:56`),
byte-identical to the pre-change text at `bfda47a`, and still covered by the pre-existing
`MustThrowExceptionWhenTypeDoesntExist` in `WhenGetting.cs`. `TypeMismatch` throws `InvalidCastException`
naming the key, the stored value's type — or the literal `null` when the stored value is `null` — and
`typeof(T).FullName` (`:59-64`). It must never say "No item found", and
`MustThrowInvalidCastExceptionNamingKeyAndBothTypesWhenStoredValueIsNotAssignableToRequestedType` asserts the
absence of that phrase explicitly.

**A stored `null` remains a PRESENT value for a reference or nullable `T`, and is a mismatch only for a
non-nullable value type `T`.** This rule is inherited from #332 and ADR-0011, not re-decided here; ADR-0011
records it as part of the same contract surface ("a stored `null` is a PRESENT value and is returned as-is
rather than re-running the factory"). The test is `default(T) is null`, and it sits on the stored-`null`
branch only, off the path every successful lookup takes (`:120-122`, under an `INVARIANT:` comment citing
ADR-0011). Four tests pin the two halves: a stored `null` read as `string`
(`MustReturnTrueAndNullOutParameterWhenStoredNullIsReadAsReferenceType`) and as `int?`
(`MustReturnTrueAndNullOutParameterWhenStoredNullIsReadAsNullableValueType`) is `true` with a `null` result;
read as `Guid` it is `false` from `TryGet`
(`MustReturnFalseAndDefaultOutParameterWhenStoredNullIsReadAsNonNullableValueType`) and
`InvalidCastException` — not `NullReferenceException` — from `Get`
(`MustThrowInvalidCastExceptionNamingNullWhenStoredNullIsReadAsNonNullableValueType`, which also asserts the
message names the key, `null`, and `System.Guid`).

**No `Nullable.GetUnderlyingType` arm was needed, and this was settled empirically rather than assumed.** The
plan flagged `value is T` over a boxed underlying value as load-bearing but unproven, with an explicit
instruction to add such an arm if the test disagreed. It did not: the generic `value is T typedValue` with
`T = int?` matches a boxed `int` and yields `5`, pinned by
`MustReturnTrueAndValueOutParameterWhenStoredValueTypeIsReadAsNullableValueType`. Worth knowing before
anyone "simplifies" it: the equivalent NON-generic source, `value is int? x`, is not even legal C#
(CS8116 — "It is not legal to use nullable type 'int?' in a pattern"), so the behaviour relied on here exists
only in the generic form.

### Local presence shadows the inherited container

A key present in the local dictionary is answered from the local dictionary, whatever its type.
`FindTypedValue` consults `_inheritedContext` only when `_context.TryGetValue` misses (`:109-128`), so a local
`TypeMismatch` returns `false` from `TryGet` and throws from `Get`; it does NOT fall through to a parent that
holds an assignable value under the same key. `MustReturnFalseWhenLocallyStoredValueOfAnotherTypeShadowsInheritedContext`
pins exactly that shape — the parent holds a `string` under `"Key"`, the child stores an `int` under it, and
`TryGet<string>("Key")` is `false`. The rule is a straight continuation of the existing chain semantics
(`src/Chatter.CQRS/CONTEXT.md:27` — "a lookup that misses falls through to the parent"): a present key is not
a miss. A mismatch in a PARENT, reached because the local dictionary genuinely misses, is reported as the
mismatch it is — `MustReturnFalseWhenInheritedContextValueIsNotAssignableToRequestedType`.

### The `Get`/`TryGet` asymmetry is deliberate

The two members answer the same question and are meant to disagree about what to do with the answer.
`TryGet` exists so a caller can ASK whether usable context is there, so the only honest return for a
present-but-wrong-type value is `false` — a `Try`-method that throws has no contract left. `Get` exists so a
caller can DEMAND the context, so the only honest response is to throw, and to say which failure occurred.
One member absorbs the failure, the other reports it, and a caller picks the member by whether it has
something to do when the context is absent. This is why the fix is not "make mismatches quiet": mismatches
are quiet on exactly one of the two paths, and it is the path whose name promised quiet.

### Why `InvalidCastException` rather than a new exception type

It is what `Get` already threw for a mismatch: before this change the blind cast inside `TryGet` produced
`InvalidCastException` and it propagated straight out of `Get`. Keeping it means the mismatch case gains a
correct MESSAGE without changing the exception TYPE any existing `catch` was written against. The alternative
— a dedicated exception — was foreclosed on precedent rather than taste: ADR-0011 rejected its Option 3
partly because it "introduces new public exception surface on `Chatter.CQRS` (a new exception type, or a new
throwing condition on existing members, that consumers would then depend on)". That reasoning applies
unchanged here, and this decision adds no new public type.

### What the type-keyed overloads can and cannot hit

`Get<T>()` and `TryGet<T>(out T)` delegate to the string-keyed overloads with `typeof(T).FullName`
(`:38-39`, `:82-83`), and `Include<T>(T t)` writes under the same key (`:140-141`). That key is a NAME, not
a type IDENTITY. `Type.FullName` is not injective: two distinct types declared in different assemblies under
the same `Namespace.TypeName` produce the same string and so share one slot in the
`IDictionary<string, object>` backing store (`:20`). A mismatch is therefore reachable from type-keyed WRITES
ALONE — `Include<X.A>(a1)` followed by `GetOrAdd<Y.A>(() => a2)` reads the slot `X.A` wrote — and the
create-if-absent overwrite recorded under *Consequences* needs no string-keyed write to reach it.

This decision changed the DIRECTION of that failure rather than introducing it. At `bfda47a` the blind
`(T)value` cast threw `InvalidCastException` on the collision: broken, but LOUD. At HEAD the type-checked
lookup reports the colliding value ABSENT, so `GetOrAdd`'s factory runs and `Include(createdValue)`
overwrites it: broken, and SILENT. The collision predates this change and never served a caller correctly.
It is carried as a tracked deferral under *Consequences*.

A mismatch is separately reachable on a type-keyed READ with no collision at all, because
`Include<T>(string, T)` (`:149-150`) will write any value under any string, including a string equal to some
`typeof(T).FullName`. That is the situation
`MustInvokeFactoryMethodAndOverwriteWhenAValueOfAnotherTypeIsStoredUnderTheTypeKey` constructs.

## Consequences

- **`GetOrAdd<T>` over a foreign-typed value under the type key now RUNS the factory and OVERWRITES it**,
  where it previously threw out of the blind cast. `GetOrAdd` gates on `TryGet<T>` (`:177-187`), a mismatch is
  now `false`, and `Include(createdValue)` writes to the same key the foreign value occupies — so the foreign
  value is gone afterwards. This is the only shape in which the fix overwrites stored data rather than
  merely reporting differently, and it is stated rather than hidden;
  `MustInvokeFactoryMethodAndOverwriteWhenAValueOfAnotherTypeIsStoredUnderTheTypeKey` asserts the factory ran
  exactly once and that the container holds the new value afterwards. `GetOrDefault<T>()` inherits it through
  `GetOrAdd` (`:161-162`) and `GetOrNew<T>()` inherits it through its own `TryGet<T>` gate (`:200-210`).
  Nothing in this repository writes a foreign type under a `typeof(T).FullName` key outside that test.
- **`TryGet` returning `false` now HIDES a genuine programming error at the call site that makes it.** A
  handler that reads the wrong `T` for a key gets `false` and a `default`, and will most likely take its
  "not present" branch rather than fail. That is the accepted cost of Option 3, and the offset is `Get`: the
  loud path still exists, still throws, and now says which of "absent" and "present but another type"
  actually happened, with both type names in the message.
- **No public API is added or removed.** `LookupOutcome` and `FindTypedValue` are private
  (`:100-133`); the four `Get`/`TryGet` signatures are unchanged. What changes is which exceptions can leave
  them, and the direction is one-way: `TryGet` can now throw strictly less than before, and `Get` throws no
  type it could not throw before — the undocumented `NullReferenceException` over a stored `null` under a
  non-nullable value type `T` is the one it loses, to `InvalidCastException`.
- **The XML documentation now states the contract on the members themselves.** Both `TryGet` overloads say
  that "found" means present AND assignable to `T` and spell out the stored-`null` rule (`:75-81`, `:92-96`);
  both `Get` overloads carry `<exception cref="InvalidCastException">` (`:35`, `:46`). No other claim was
  added to them.
- **The outbox path is unaffected and is pinned that way.** `UnitOfWork` writes
  `Include("CurrentTransactionId", transaction.TransactionId)`
  (`src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/UnitOfWork.cs:35`)
  and `OutboxProcessingBehavior` reads `TryGet<Guid>("CurrentTransactionId", out var persistanceTransactionId)`
  (`src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Reliability/Outbox/OutboxProcessingBehavior.cs:28`).
  `IDbContextTransaction.TransactionId` is a `Guid`, so the pair agrees and the read is `Found` on the typed
  branch — a string-keyed struct round trip, which the invariant above forbids regressing and
  `MustReturnTrueAndEqualValueForAStringKeyedStructWrittenByTheOutboxPath` pins inside `Chatter.CQRS`'s own
  tests.
- **Deferred (tracked) — the type key is a NAME, not a type identity** (issue
  [#469](https://github.com/brenpike/Chatter/issues/469)). Every type-keyed member derives its key from
  `typeof(T).FullName`, which is not injective, so two distinct types declared in different assemblies under
  the same `Namespace.TypeName` collide on one slot and the overwrite above is reachable from type-keyed
  writes alone. Triggering it needs a caller-owned duplicate `Namespace.TypeName` across two loaded
  assemblies, with BOTH types used as context keys in the SAME per-dispatch container; every create-if-absent
  site in this repository keys on a `Chatter.*` type — `IExternalDispatcher` and `IMessageDispatcher`
  (`src/Chatter.CQRS/src/Chatter.CQRS/MessageDispatcher.cs:29-30`), `TransactionContext`
  (`src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Reliability/UnitOfWorkBehavior.cs:19`,
  `src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Receiving/BrokeredMessageReceiver.cs:1062`) and
  `IPersistanceTransaction`
  (`src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/BrokeredMessageOutbox.cs:110`)
  — so the path is unreachable unless a caller declares types inside Chatter's own namespaces. After an
  overwrite the victim's reads are still loud: `Get<T>()` throws `InvalidCastException` and `TryGet<T>`
  returns `false`. The one end-to-end-silent shape is two `GetOrNew<T>()` callers thrashing, each handed a
  fresh instance and losing the other's state, bounded to one dispatch with nothing persisted and nothing
  leaked across messages. Closure is deferred because coexistence of the two types requires two slots, two
  slots require two keys, and the type-keyed key is OBSERVABLE through `Include<T>(string, T)` and
  `Get<T>(string)` — so re-keying on `Type` identity or the assembly-qualified name changes reads that
  SUCCEED today, which the governing invariant forbids and which would be a MAJOR. The alternative, a
  first-wins `FullName` slot with an assembly-qualified fallback slot, is a lookup-semantics redesign
  spanning inherited-chain shadowing, interop with the string-keyed overloads, the stored-`null` rule and
  first-write ordering; it needs its own ADR and test matrix. Making `GetOrAdd` throw on a mismatch was
  costed and declined: it does not close the class, and it contradicts the decision recorded above.
- **A future contributor reaching for "just make `TryGet` throw again" is reaching for Option 1**, which was
  costed and declined here. The route to loudness is `Get<T>()`, and it is the route the container already
  offers.

## References

- Issue #451 — *`ContextContainer.TryGet` casts blindly — a type-mismatched or null-valued key throws instead
  of reporting a miss*. The issue this ADR answers.
- Issue #332 — *`ContextContainer.GetOrAdd` gates on `is null` — value-type context silently returns
  `default(T)`*. Source of the stored-`null`-is-present rule this decision inherits rather than re-decides.
- Issue #333 — *`ContextContainer` is an unsynchronized `Dictionary` mutated per dispatch — concurrent
  dispatches sharing a context race*. The other defect on this same type, answered by ADR-0011; nothing here
  changes its answer, and the tri-state lookup adds no synchronization.
- Issue #469 — *`ContextContainer` keys type identity on `typeof(T).FullName`, which is a name, not an
  identity*. The deferred finding carried as a tracked consequence above.
- Epic #301 — *Chatter.CQRS: handler registration correctness and per-dispatch hot path*. The parent epic, and
  the reason the nullability test is confined to the stored-`null` branch instead of running on every lookup.
- ADR-0011 — *Context Container: unsynchronized, documented rather than synchronized*. Cited, not amended:
  source of the stored-`null` presence rule and of the rejection of new public exception surface on this
  module.
- ADR-0015 — *Inbound header trust: ground truth stamped over wire values, and no trust boundary*. Precedent
  for the robustness shape adopted here — a lookup that type-TESTS rather than casts, so a foreign-typed
  value degrades to a reported absence instead of faulting the caller.
