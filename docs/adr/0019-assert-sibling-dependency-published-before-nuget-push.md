---
status: accepted
date: 2026-09-13
---

# Assert every declared `Chatter.*` dependency is already published before `dotnet nuget push`

The nine packages in this repository reference each other by `ProjectReference`, and `dotnet pack` turns each
one into a nuspec `<dependency>` pinned at the referenced project's `<Version>` as it stood at pack time. Pack
reads the sibling's csproj. It does not ask whether that sibling version exists on nuget.org. Nothing between
pack and `dotnet nuget push` asked either, and nuget.org permits no deletion, so the first time the two
diverged the divergence shipped permanently.

It has already happened. `1d3b8e7` (PR #466) raised `Chatter.MessageBrokers` to `0.29.0`. The CD run for that
head SHA — run `34671797737` of *Chatter.MessageBrokers CD* — concluded **`cancelled`**; the `deploy` job was
never approved and never ran. There is no `messagebrokers/v0.29.0` tag: the tags jump `v0.28.0` to `v0.30.0`,
and the nuget.org flat-container index for `chatter.messagebrokers` jumps `"0.28.0"` to `"0.30.0"`. That
version does not exist and never will. The next push, `bfda47a`, released
`Chatter.MessageBrokers.SqlServiceBroker 0.14.2`, and its deploy *was* approved. The nupkg that reached
nuget.org declares `<dependency id="Chatter.MessageBrokers" version="0.29.0" ... />` in **both** the `net8.0`
and the `net10.0` group, stamped `<repository ... commit="bfda47a503409370880b00778b6e121b7d580e86" />`. The
same push shipped `Chatter.SqlChangeFeed 0.14.2` declaring `Chatter.MessageBrokers.SqlServiceBroker 0.14.2` —
a dependency on the broken package, which only resolves today because `0.14.3` was published to replace it.
The cleanup cost was paid in the only currency nuget.org accepts: `0.14.2` unlisted, deprecated, superseded.

Issue #390 rates itself `severity: low`, `confidence: medium`, "reachability is low". **That rating is
falsified by the incident above, and this ADR is the correction of the record.** The reachability is not low;
the path was taken, by the ordinary release procedure, without anyone doing anything wrong. This is not
hardening against a hypothetical. It is the control that would have stopped an unrestorable publish that
actually occurred.

One precision the guard's design turns on: a nuspec `version="X"` is the min-inclusive range `[X, )`. Between
the `0.14.2` push and the later `0.30.0` push, restoring SqlServiceBroker `0.14.2` failed hard. After `0.30.0`
landed, the same restore silently floats up to `0.30.0` — a package compiled against `0.29.0` sources
resolving a different assembly. Both outcomes are wrong, and only one of them is loud.

## Considered Options

- **Option 1 — Accept the exposure; rely on operators releasing in dependency order.** Rejected, because that
  is the status quo that produced the incident. The operator who approved `bfda47a` had no signal that
  `0.29.0` had been cancelled rather than published; the two facts live in different workflow runs. A control
  whose only enforcement is a human remembering a fact they cannot see is not a control.

- **Option 2 — Order the nine CD workflows so a dependent cannot deploy before its dependency.** Rejected on
  two independent grounds, either of which is sufficient. First, it is **not expressible**: `needs:` is
  intra-workflow only, so ordering nine separate files requires either `workflow_run` chaining or collapsing
  all nine into one workflow, which destroys the independent path-filtered pipelines. Second, and this is the
  one that kills it, ordering is **not sufficient**: the MessageBrokers deploy was *cancelled*, not failed. A
  `workflow_run` trigger keyed on `completed` lets the dependent straight through — `cancelled` is a
  completion — and a trigger keyed on `success` blocks the dependent forever, because that run will never
  succeed. Ordering guarantees *sequence*; it never observes *publication*. Only an assertion against the feed
  does. Ordering is also unnecessary for the common case: each CD is path-filtered on its own `src/**`, so a
  MessageBrokers-only change never fires the SqlServiceBroker CD and there is nothing to order.

- **Option 3 — Assert in the `package` job, at pack time.** Rejected, and not as the weaker placement — as
  unworkable. `bfda47a` released SqlServiceBroker `0.14.2` **and** SqlChangeFeed `0.14.2` in a single push,
  and SqlChangeFeed depends on SqlServiceBroker. Both pack jobs run concurrently at T+0, long before either
  environment approval. A pack-time assertion fails SqlChangeFeed on a completely legitimate paired release.
  The only sound wait at pack time is "until a human approves the sibling", which a runner cannot poll for.

- **Option 4 — Assert in the `deploy` job, but skip the publish when the assertion fails.** Rejected. Setting
  `should_publish=false` would make the failure silent and green. The existing duplicate-release guard
  (`.github/workflows/messagebrokers-cicd.yml:52-68`, consumed at `:111`) skips legitimately, because "no
  version bump on this push" is a normal outcome for a workflow that also triggers on shared packaging
  configuration. An unpublished dependency is not a normal outcome. It is a release that must not proceed.

- **Option 5 — Assert in the `deploy` job and FAIL it (CHOSEN).** The `deploy` job runs only after the
  `production-<module>` environment approval, so the dependency's publish either already happened or is
  minutes away, and a short bounded poll suffices. Publication is monotonic — unlisting is not deletion — so
  an observation made here cannot be invalidated between the check and the push that follows it.

## Decision

**Every `<module>-cicd.yml` `deploy` job carries one step, `Assert declared Chatter dependencies are
published`, placed between `Download package artifact` and `NuGet login (OIDC)`. It reads the packed nuspec,
extracts every `<dependency>` whose id matches `^Chatter\.`, and asserts each declared version is present in
the nuget.org flat-container index for that id. It exits non-zero on failure and never sets
`should_publish=false`.**

Placement before `NuGet login (OIDC)` is load-bearing: a failed guard means no publish credential was ever
minted in that run.

**The dependency set is read from the packed nuspec, mechanically — never from csproj `ProjectReference`
parsing, `dotnet list reference`, or a hardcoded table.** The nuspec is the artifact actually being pushed.
Anything else reimplements `dotnet pack`'s resolution and can diverge from it through `PrivateAssets`,
suppressed references, or framework-specific groups. Multi-TFM packages repeat the same dependency in the
`net8.0` and `net10.0` groups; the set is deduped before querying.

**The failure mode is unpublished-and-loud, chosen over published-and-permanent.** A failed guard leaves the
deploy job red, GitHub notifying, the nupkg artifact retained for 7 days
(`.github/workflows/messagebrokers-cicd.yml:92,98` — retention sized to outlive an unbounded approval wait),
and the job re-runnable. Re-running a failed run preserves the original `push` event, so every `if:` guard
re-evaluates unchanged. That is recoverable. A published package with an unresolvable dependency is not:
nuget.org allows unlist and deprecate, never delete.

### Why the body is inline YAML and not `.github/scripts/*.sh`

The `deploy` job carries a hard INVARIANT, identical in all nine files
(`.github/workflows/messagebrokers-cicd.yml:100-106`): it has no `actions/checkout`, and that absence is the
closure mechanism. It is the only job granted `id-token: write`, so with no working tree there is no repo
source, no test assemblies, no test-only dependency graph, and no `.github/scripts/*.sh` for anything to load
alongside the publish credential. Calling a script from this job requires restoring the checkout the invariant
exists to forbid. A local composite action requires the same checkout. Smuggling the script in through the
uploaded artifact honours the letter of the invariant and violates its intent.

Two job-topology alternatives were considered and rejected. **A second job holding the same environment, doing
the checkout and running the script, with `deploy` gaining `needs:` on it** — a second job on the same
environment creates a second pending deployment, which is a second manual approval per release, times nine
modules. Moving `environment:` off `deploy` to avoid that breaks `vars.NUGET_URL` and changes the OIDC subject
claim. **Probing by `dotnet restore`ing the packed nupkg from a temporary project** — this pulls the entire
third-party transitive graph into the `id-token: write` job, directly against that job's stated invariant, and
it is the wrong assertion besides (see below).

The accepted cost is **nine byte-identical copies of the guard body**. The nine-file CD set is already
duplicated this way by construction. The duplication is bought back by a hermetic offline harness
(`.github/scripts/tests/deploy-dependency-guard.test.sh`) that extracts the body from each workflow between
sentinel markers, asserts exactly nine workflows carry them, and asserts all nine bodies are byte-identical
after stripping the uniform YAML indent. The same assertion fails when a tenth module CD is added without the
guard. Weakening that assertion re-opens the drift the duplication would otherwise cause.

### The assertion is "this exact version is on the feed", not "restore succeeds"

Restore-success is too weak to catch the incident it exists to catch. Because a nuspec `version="X"` is
`[X, )`, a restore probe passes whenever *some* version at or above `X` exists, which is exactly the state
SqlServiceBroker `0.14.2` was in after `0.30.0` shipped: restore green, package built against `0.29.0`
sources, resolving `0.30.0`. The guard queries
`https://api.nuget.org/v3-flatcontainer/<id-lowercased>/index.json` and matches the declared version as a
quoted exact string. Lowercasing the id is mandatory — mixed case returns 404. Quoting the match is mandatory
— an unquoted `0.3.0` substring-matches `10.3.0`. Flat-container normalizes versions (leading zeros stripped,
a fourth zero segment dropped); all nine modules use three-part SemVer, so exact string matching is sound, and
that assumption is pinned in a comment next to the match rather than left implicit.

### Unlisted versions count as present — a boundary, not an oversight

The guard asserts *existence on the feed*, not listing state. Unlisted versions remain in flat-container: the
`chatter.messagebrokers.sqlservicebroker` index still lists `"0.14.2"` although that version is unlisted and
deprecated. Asserting the stronger "listed" property via the registration API was considered and rejected on
two grounds: NuGet resolves an exact lower bound against an unlisted version, so an unlisted dependency is
still restorable by the consumers this guard protects; and listing state changes after publish, which would
make the guard's verdict a function of something that can move underneath it. This is a stated limit of what
the guard proves, recorded here so a future reader does not read it as a bug.

### The tolerance window, and why the env seams are safe in a credential-minting job

The guard polls rather than single-shots, with `DEPENDENCY_GUARD_TIMEOUT_SECONDS` defaulting to **600** and
`DEPENDENCY_GUARD_POLL_SECONDS` to **15**. The window covers flat-container CDN indexing lag, which is
observably minutes, plus a sibling module's deploy finishing a few minutes later within the same push. It is
deliberately NOT sized to cover an unbounded wait on a human approval; that case must fail loudly, because the
recovery is an operator action and not the passage of time. The sizing rationale is blunt: a guard that fails
spuriously on indexing lag gets disabled by the first person it blocks, and a disabled guard protects nothing.

The timeout, the poll interval, `FLATCONTAINER_BASE_URL` and `PACKAGE_DIR` are read from the environment with
production-safe defaults. These are operator seams, not test-only scaffolding, and their presence in a job
holding `id-token: write` does not widen that job's exposure. Nothing in any `deploy` job sets them, so unset
means production behaviour; setting them requires write access to the workflow file, which is already write
access to the publish step itself; and none of them can cause a publish that would not otherwise happen —
the worst an operator can do through them is point the guard at a feed that makes it fail, or shorten its
patience. They exist because the guard's behaviour has to be executable offline by the harness, and a seam
that only a test uses is a seam that drifts from the thing it claims to test.

### Exit 1 is an assertion failure; exit 2 is an infrastructure failure

A `200` with the version present passes immediately. A `200` with the version absent keeps polling and, on
timeout, exits **1**, naming the package, the declared dependency and the index URL. A `404` — the id was
never published at all — keeps polling and, on timeout, exits **1** with a distinct message naming the id and
its casing, because "wrong package id" and "version not yet published" have different remedies. A transport
error, `5xx`, `429`, or a curl failure keeps polling and, on timeout, exits **2**: nuget.org being unreachable
is not evidence that the dependency is missing, and conflating the two would teach operators to re-run through
a real assertion failure. The 1-versus-2 split mirrors the invariant already stated in
`.github/scripts/assert-nupkg-provenance.sh:6-7`.

### Direct dependencies only

`dotnet pack` emits direct references only, and the nuspec is what the guard reads, so the guard verifies each
package's direct `Chatter.*` dependencies and nothing deeper. Transitive correctness follows by induction:
`Chatter.MessageBrokers.AzureServiceBus.Auth` depends on `Chatter.MessageBrokers.AzureServiceBus`, which
depends on `Chatter.MessageBrokers`, which depends on `Chatter.CQRS`; if every package's direct dependencies
are verified present at its own publish time, then at the moment `Auth` publishes, `AzureServiceBus` is
published, and it in turn published only when `MessageBrokers` was — so the whole chain is on the feed. The
induction has a base case that this ADR does not supply: **versions published before this guard landed were
never checked**, and the guard makes no claim about them. `Chatter.CQRS` has no sibling dependencies at all;
its loop is empty and must pass cleanly under `set -euo pipefail`, and it still carries the step for
uniformity, for the nine-copy assertion, and against a future dependency.

### The operator rule

**Approve the dependency's `production-*` environment before the dependent's.** The guard's usability depends
on this: it waits 600 seconds, not for a human. The rule is recorded here and not only in the guard's failure
message, because a rule that exists only in the text of an error is a rule nobody reads until it has already
fired.

## Consequences

- **A release whose declared sibling dependency is not on the feed now fails instead of shipping.** The failure
  is a red `deploy` job with a message naming the package, the missing dependency, the index URL queried, and
  the recovery: approve and complete the dependency's `production-*` deployment, then re-run the failed job.
  Past the 7-day artifact retention, recovery requires a fresh push touching the module's `src/**`.
- **The already-shipped `Chatter.MessageBrokers.SqlServiceBroker 0.14.2` is unaffected.** Nothing here makes
  that package restorable; nuget.org has no deletion and the guard runs before a push, not after one. `0.14.2`
  stays unlisted and deprecated, and `0.14.3` remains its replacement. The guard prevents the next one.
- **No new PR-time network gate exists.** The guard runs only inside `deploy`, which itself runs only when
  `needs.package.outputs.should_publish == 'true'` (`.github/workflows/messagebrokers-cicd.yml:111`), so a
  push with no version bump never reaches it. The only always-on CI addition is the hermetic offline harness,
  which never touches nuget.org and therefore cannot flake on it.
- **A blocked deploy can idle up to 600 seconds of runner time before failing.** Accepted, and retunable
  through the documented env seam.
- **Nine copies of one bash body must stay identical.** The byte-identical assertion in the harness is the
  mechanism; a tenth CD workflow added later trips the "exactly nine" count, which must be updated
  deliberately rather than removed.
- **The guard proves publication, not correctness.** It does not verify that the declared version is the
  version the dependent was compiled against, only that it exists. A package pinned at a real-but-wrong
  version still passes.
- **No `src/**` file changes, so no package version moves and no CHANGELOG entry is due.** The nine
  `CHANGELOG.md` files live inside `src/<Module>/src/<Module>/` and sit under the CD path filters, which is a
  second reason not to touch them for a workflow-only change.
- **Documentation must not describe this guard as preventing unrestorable packages generally.** It prevents
  publishing a package whose *direct `Chatter.*`* dependency versions are absent from the feed at deploy time.
  Any broader claim — that releases are ordered, that transitive closure is verified, that already-published
  versions are covered — is false and must be deleted rather than softened (ADR-0015 doctrine).
- **Manual `workflow_dispatch` recovery is not addressed here.** Every CD declares the trigger while every job
  additionally requires `github.event_name == 'push'`; that gap is out of scope for this decision.

## References

- Issue #390 — *Dependent package can publish declaring an unpublished sibling version*. The report this ADR
  answers, and whose `severity: low` / low-reachability self-rating this ADR records as falsified. Closing it
  closes its parent epic #309 (CI/CD supply chain), of which it is the last open child.
- `.github/workflows/messagebrokers-cicd.yml` — reference structure for all nine: the duplicate-release guard
  at `:52-68` consumed at `:111`, the 7-day artifact retention at `:92,98`, the `deploy` job INVARIANT at
  `:100-106`, and the guard's insertion point between `Download package artifact` (`:120`) and
  `NuGet login (OIDC)` (`:133`).
- `.github/scripts/assert-nupkg-provenance.sh:6-7` — the existing exit-1-versus-exit-2 invariant this guard's
  exit codes mirror.
- `.github/scripts/tests/deploy-dependency-guard.test.sh` and `.github/scripts/tests/fixture-feed.py` — the
  hermetic offline harness: the nine-marker count, the byte-identical assertion, and the behavioural cases
  (all present, version absent, unknown id, `5xx`, unreachable host, zero `Chatter.*` dependencies). It uses a
  real local HTTP stub rather than `file://`, because `curl -w '%{http_code}'` reports `000` for `file://` and
  would defeat the status classifier the guard is built on.
- `.github/workflows/supply-chain-gates.yml` — where the harness runs, unfiltered by path so a workflows-only
  change still exercises it.
- Commits `1d3b8e7` (PR #466, the `0.29.0` bump whose CD run `34671797737` was cancelled) and `bfda47a`
  (PR #467, the push that shipped `SqlServiceBroker 0.14.2` and `SqlChangeFeed 0.14.2`) — the incident.
- ADR-0015 — *Inbound header trust: ground truth stamped over wire values, and no trust boundary*. Source of
  the delete-the-unearned-claim doctrine applied to the documentation consequence above.
