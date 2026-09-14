---
status: rejected
date: 2026-09-13
---

# A published package declared a dependency version that was never published

The nine packages in this repository reference each other by `ProjectReference`, and `dotnet pack` turns each
one into a nuspec `<dependency>` pinned at the referenced project's `<Version>` as it stood at pack time. Pack
reads the sibling's csproj; it never asks whether that sibling version exists on nuget.org. Nothing between
pack and `dotnet nuget push` asked either, so the first time the two diverged the divergence shipped
permanently — nuget.org permits unlisting and deprecation, never deletion.

This ADR records that incident, and records the decision NOT to build a control for it.

## The incident

`1d3b8e7` (PR #466) raised `Chatter.MessageBrokers` to `0.29.0`. The CD run for that head SHA — run
`34671797737` of *Chatter.MessageBrokers CD* — concluded **`cancelled`**; the `deploy` job was never approved
and never ran. There is no `messagebrokers/v0.29.0` tag: the tags jump `v0.28.0` to `v0.30.0`, and the
nuget.org flat-container index for `chatter.messagebrokers` jumps `"0.28.0"` to `"0.30.0"`. That version does
not exist and never will.

The next push, `bfda47a`, released `Chatter.MessageBrokers.SqlServiceBroker 0.14.2`, and its deploy *was*
approved. The nupkg that reached nuget.org declares `<dependency id="Chatter.MessageBrokers" version="0.29.0"
... />` in **both** the `net8.0` and the `net10.0` group, stamped
`<repository ... commit="bfda47a503409370880b00778b6e121b7d580e86" />`. The same push shipped
`Chatter.SqlChangeFeed 0.14.2` declaring `Chatter.MessageBrokers.SqlServiceBroker 0.14.2` — a dependency on
the broken package.

The cleanup was paid in the only currency nuget.org accepts: `0.14.2` unlisted, deprecated, and superseded by
`0.14.3`. An unrestorable package is on nuget.org permanently.

Issue #390 rates itself `severity: low`, `confidence: medium`, "reachability is low". **That rating is
falsified by the incident above.** The path was taken, by the ordinary release procedure, without anyone doing
anything wrong.

## Why deploy ordering would not have prevented it

Ordering the nine CD workflows so a dependent cannot deploy before its dependency is **not expressible** and
would **not be sufficient**. `needs:` is intra-workflow only, so ordering nine separate files requires either
`workflow_run` chaining or collapsing all nine into one workflow, which destroys the independent
path-filtered pipelines. And the MessageBrokers deploy was *cancelled*, not failed: a `workflow_run` trigger
keyed on `completed` lets the dependent straight through — `cancelled` is a completion — while one keyed on
`success` blocks the dependent forever, because that run will never succeed. Ordering guarantees *sequence*;
it never observes *publication*.

## A guard was built, and then dropped

A deploy-time guard asserting every declared `Chatter.*` dependency was already on the feed was implemented on
this branch and is not shipping. Recorded here so the next person does not rebuild it blind:

- It was an inline nuspec parser replicated **byte-identically across all nine `<module>-cicd.yml` deploy
  jobs**. That shape was forced: the `deploy` job carries an INVARIANT that it has no `actions/checkout`,
  because it is the only job granted the publish credential, so no checked-in script file is loadable from
  it and the guard body had to live inline in each workflow.
- That construction produced a **new defect in each of five review rounds**, including a false-reject of a
  legitimate package: `group` is reused by the nuspec schema under `<frameworkReferences>` and `<references>`,
  and the guard refused such a package with exit 2.
- The cost of the shape exceeded its value for a failure that has occurred **once in 211 releases**.

A note for a future reader rather than a tracked recommendation: if this is ever rebuilt, the extraction
belongs in the `package` job, which has a checkout. That job can pass a simple `id version` manifest alongside
the `.nupkg`, so the credentialed `deploy` job parses nothing.

## Decision

**No automated control is added. The exposure is accepted, and the operator rule below stands in its place.**

**Approve the dependency's `production-*` environment before the dependent's.** It costs nothing, requires no
code, and is the whole of the control. It is recorded here because the operator who approved `bfda47a` had no
signal that `0.29.0` had been cancelled rather than published — the two facts live in different workflow runs.

## Consequences

- **The exposure remains open.** A dependent module can still publish declaring a sibling version that was
  never pushed, and the result is permanent. The mitigation is procedural and depends on operators releasing
  in dependency order.
- **Nothing here repairs the already-shipped `Chatter.MessageBrokers.SqlServiceBroker 0.14.2`.** nuget.org has
  no deletion. `0.14.2` stays unlisted and deprecated, and `0.14.3` remains its replacement.
- **No workflow, script, or CI job changes.** No `src/**` file changes either, so no package version moves and
  no CHANGELOG entry is due.

## References

- Issue #390 — *Dependent package can publish declaring an unpublished sibling version*. The report this ADR
  answers, and whose `severity: low` / low-reachability self-rating this ADR records as falsified.
- Commits `1d3b8e7` (PR #466, the `0.29.0` bump whose CD run `34671797737` was cancelled) and `bfda47a`
  (PR #467, the push that shipped `SqlServiceBroker 0.14.2` and `SqlChangeFeed 0.14.2`) — the incident.
