---
status: accepted
date: 2026-09-10
---

# Query invoker cache: process-lifetime retention documented rather than weakened

PR C of epic #301 closes #336 by replacing a `MakeGenericType` call plus two `dynamic` conversions per
query dispatch with a closed-generic adapter that is built once and cached. The cache is
`QueryDispatcher._invokers`, a `private static readonly ConcurrentDictionary<(Type QueryType, Type
ResultType), object>` living in the default `AssemblyLoadContext`
(`src/Chatter.CQRS/src/Chatter.CQRS/Queries/QueryDispatcher.cs:25`). Its key is derived from
`query.GetType()` — a value the caller supplies — so every entry strongly roots a caller-supplied
runtime `Type` for the lifetime of the process, with no eviction.

Local adversarial review raised that retention as a HIGH finding, and it is a real one: the
`AssemblyLoadContext` unloadability documentation lists, among the references that keep a collectible
load context alive, "regular references held from outside of the collectible `AssemblyLoadContext` that
are stored in a stack slot or a processor register …, a static variable, or a strong (pinning) GC
handle, and transitively pointing to: … a type from such an assembly." A static dictionary in the
default context, keyed by a `Type` from a collectible context, is exactly that shape.

The question this ADR settles is not whether the retention is real, but whether the answer is to weaken
the cache — and hand back the saving the change exists to make — or to state, as a fact about the type
and a requirement the type places on its callers, what the cache holds and for how long.

## Considered Options

- **Option 1 — Promote `TResult` to a generic parameter and hold a `ConditionalWeakTable<Type,
  QueryInvoker<TResult>>` in a static generic class (`static class InvokerCache<TResult>`).** This is
  the strongest alternative and it is not dismissed lightly. It dissolves the composite-key objection
  entirely: the result type stops being half of a tuple key and becomes a type parameter, so the tuple
  hashing and the `object` cast on the lookup path both disappear, and the remaining key — the query
  `Type` — is held weakly rather than strongly. It is semantically inert for the ordinary host, because
  `Type` objects in the default load context are immortal and a weak key over them never expires. It
  adds no `RequiresDynamicCode` exposure beyond what the change already carries. It is rejected NOW on
  exactly two grounds. First, it edits the hot path of the very change whose purpose is removing
  hot-path cost, and **this repository carries no benchmark harness**, so the swap cannot be shown
  neutral — `ConditionalWeakTable` lookup is a different cost profile from `ConcurrentDictionary`
  lookup, and "probably the same" is not a measurement. Second, the property it buys is
  near-untestable here: proving it would need a collectible `AssemblyLoadContext`, a satellite assembly
  defining a query type, and a GC-and-wait loop asserting the unload completes — a test shape this
  repository has nowhere else, for a property no consumer has reported needing.

- **Option 2 — Make the cache a per-instance field instead of a static.** Dominated. `QueryDispatcher`
  is registered `AddScoped`
  (`src/Chatter.CQRS/src/Chatter.CQRS/DependencyInjection/CqrsExtensions.cs:168`), so a per-instance
  cache is rebuilt on every scope — that is, on every request in a typical host. It hands #336's saving
  straight back to buy a retention property Option 1 buys without that cost.

- **Option 3 — Accept the retention and document it (CHOSEN).** Costs nothing on the dispatch path and
  adds no public API. Its weakness is honest: the requirement is met by the caller, not enforced by the
  type system, and the escape hatch does not cover every caller (see the Decision).

A public `Clear()` on the dispatcher was also considered and rejected in one step: it adds public API
surface for an obligation no caller would know to invoke, and ADR-0011 rejected new public surface on
the same grounds.

## Decision

**The invoker cache is a process-lifetime static with no eviction, and every entry strongly roots the
caller-supplied query `Type`.** Its entry count is bounded by the number of distinct `(runtime query
type, result type)` pairs ever dispatched — **not by traffic**; a million dispatches of one query type
add one entry. That is the whole of what the type guarantees.

**What that requires of a caller: do not dispatch a query type defined in an `AssemblyLoadContext` you
intend to unload through `Query<TResult>(IQuery<TResult>)`.** Dispatch it through `Query<TQuery,
TResult>`, which never touches the cache, or accept that the load context will not unload.

**The escape hatch only reaches callers that can name `TQuery` at compile time.** Plugin code compiled
against Chatter can, and for it the obligation is satisfiable by choosing an overload. A reflective
host that receives an arbitrary `IQuery<TResult>` and cannot name its concrete type cannot use the
hatch at all; for that host the obligation reduces to the second half — the load context will not
unload — and this ADR does not pretend otherwise.

What the code builds, as construction facts only:

- `AddQueryHandlers` (`src/Chatter.CQRS/src/Chatter.CQRS/DependencyInjection/CqrsExtensions.cs:146-155`)
  scans at composition time and registers a CLOSED `IQueryHandler<TQuery, TResult>` service descriptor
  per discovered query type into the root `IServiceCollection`.
- Those descriptors already strongly root the same `Type`s for the lifetime of the root provider they
  are built into.
- A query type therefore cannot reach the cache without already being rooted by DI, so **the cache adds
  no retention in the documented hosting model** — one `IServiceProvider` for the life of the process.

Marginal retention exists in exactly ONE shape: a per-plugin `ServiceProvider` built over a collectible
`AssemblyLoadContext` and later disposed in order to unload it. There, the DI root goes away with the
provider and the cache entry does not.

**What this is NOT — so that this finding is not re-raised against the wrong types every review:**

- **`ChatterDiagnostics.DispatchNames<TMessage>`
  (`src/Chatter.CQRS/src/Chatter.CQRS/Diagnostics/ChatterDiagnostics.cs:166`) is a static GENERIC
  CLASS, not a dictionary.** No entry in the default load context ever names a message type: the
  storage exists only per closed instantiation, and there is no collection in the default context to
  enumerate or to hold a key. The documented lifetime rule for constructed generic types is weaker than
  a loader-allocator guarantee and is quoted here rather than paraphrased — "a constructed generic type
  … is considered to have been defined either in the assembly that contains the generic type definition
  or in an assembly that contains the definition of one of its type arguments. The exact assembly
  that's used is an implementation detail and subject to change." The load-bearing point does not
  depend on which of the two the runtime picks: a static generic class does not create the root shape
  the unloadability documentation warns about, which is a static variable in the default context
  transitively pointing at a type from the collectible one.
- **`MessageDispatcherProvider._resolvedDispatchers`
  (`src/Chatter.CQRS/src/Chatter.CQRS/MessageDispatcherProvider.cs:20`, shipped in 0.14.1) is a
  per-INSTANCE field on a scope-registered provider.** Its retention dies with the DI scope. An
  analogous "unbounded cache keyed by `Type`" objection was raised against it in that PR and rejected
  on precisely that basis — **that rejection does not transfer here**, because this cache is static.
- **`QueryDispatcher._invokers` is the FIRST process-lifetime strong root over caller-supplied runtime
  types in `Chatter.CQRS`.** The property that distinguishes it from the two above is **reachability
  direction** — an object in the default context holding a reference to a caller's type — not growth
  rate. Reviewing the next cache by its entry count will reach the wrong answer.

**Revisit trigger.** Any one of: a consumer reporting a blocked `AssemblyLoadContext` unload; Chatter
adopting trimming or AOT annotations, which forces the invoker construction path to be described
precisely anyway; or a benchmark harness landing in this repository that can price the
`ConditionalWeakTable` swap. On any of those, the route is **Option 1, NOT Option 2**.

The code is unchanged by this decision.

## Consequences

- **No public API change, no cost on the dispatch path, no test change.** The retention decision is
  documentation; the dispatch path keeps the saving #336 was opened to obtain.
- **The retention is bounded and one-time per pair.** It is proportional to the application's query
  surface, which is fixed at compile time for a statically-composed host, and it is invisible to a host
  that never unloads a load context.
- **The obligation is a convention a caller can violate, not an invariant that holds by construction.**
  Nothing in the type system prevents dispatching a plugin's query type through the
  `IQuery<TResult>` overload, and nothing detects it afterwards.
- **The escape hatch is the strongly-typed overload**, `Query<TQuery, TResult>`, which resolves
  `IQueryHandler<TQuery, TResult>` directly and writes nothing to the cache.
- **Option 1 remains on the table** under the revisit trigger above, and it is the option to reach for
  — a per-instance cache is not.

## References

- Issue #336 — *`QueryDispatcher` builds a closed generic type and makes two `dynamic` calls per
  dispatch*. The change this ADR is attached to.
- Epic #301 — *Chatter.CQRS: handler registration correctness and per-dispatch hot path*. The parent
  epic, and the reason hot-path cost is weighed as heavily as it is in Option 1.
- ADR-0011 — *Context Container: unsynchronized, documented rather than synchronized*. Source of the
  fact-plus-obligation framing used above, and the precedent for rejecting new public surface.
- *How to use and debug assembly unloadability in .NET* —
  <https://learn.microsoft.com/en-us/dotnet/standard/assembly/unloadability>. Source of the quoted list
  of references that prevent a collectible load context from unloading.
- *Collectible assemblies for dynamic type generation* —
  <https://learn.microsoft.com/en-us/dotnet/fundamentals/reflection/collectible-assemblies>. Source of
  the quoted lifetime rule for constructed generic types.
- CHANGELOG `[0.14.2]` — the release carrying this change.
