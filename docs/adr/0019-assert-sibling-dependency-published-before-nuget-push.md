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
  (`.github/workflows/messagebrokers-cicd.yml:52-68`, consumed at `:116`) skips legitimately, because "no
  version bump on this push" is a normal outcome for a workflow that also triggers on shared packaging
  configuration. An unpublished dependency is not a normal outcome. It is a release that must not proceed.

- **Option 5 — Assert in the `deploy` job and FAIL it (CHOSEN).** The `deploy` job runs only after the
  `production-<module>` environment approval, so the dependency's publish either already happened or is
  minutes away, and a short bounded poll suffices. Publication is monotonic — unlisting is not deletion — so
  an observation made here cannot be invalidated between the check and the push that follows it.

## Decision

**Every `<module>-cicd.yml` `deploy` job carries one step, `Assert declared Chatter dependencies are
published`, placed between `Download package artifact` and `NuGet login (OIDC)`. It parses the packed nuspec,
collects every `<dependency>` whose id begins with `chatter.` case-insensitively, and asserts each declared
version is a member of the `versions` array in the nuget.org flat-container index for that id. It exits
non-zero on failure and never sets `should_publish=false`.**

Placement before `NuGet login (OIDC)` is load-bearing: a failed guard means no publish credential was ever
minted in that run.

**The dependency set is read from the packed nuspec, mechanically — never from csproj `ProjectReference`
parsing, `dotnet list reference`, or a hardcoded table.** The nuspec is the artifact actually being pushed.
Anything else reimplements `dotnet pack`'s resolution and can diverge from it through `PrivateAssets`,
suppressed references, or framework-specific groups. Multi-TFM packages repeat the same dependency in the
`net8.0` and `net10.0` groups; the set is deduped before querying.

**Every obligation the guard discharges is decided on a node or a value read out of a parsed document, never
on a byte pattern found in some bytes.** This is the class the guard's shape eliminates, and it is worth
naming as a class rather than as the list of shapes that now happen to be handled. Under a pattern-matching
guard every obligation was keyed on *a pattern appeared in a stream*, and the absence of a match was
indistinguishable from a clean pass: `grep` found no `Chatter.*` dependency, therefore this package declares
none, therefore publish. There is no third outcome available now. Each obligation is keyed on a node or a
value read from a parsed document; an empty result is a parser-reported, counted, positively-terminated fact
that says *this document declares nothing*, and every document the parser could not read exits 2. Nothing
reads as success by failing to find something.

Five instances of that primitive died together, and they are named so a future reader can check that none
came back: selecting the nuspec entry inside the `.nupkg`, reading the package `<id>`, reading the package
`<version>`, collecting the `<dependency>` set, and testing flat-container membership. All five are now
`zipfile`, `xml.etree.ElementTree` and `json` reads.

**The failure mode is unpublished-and-loud, chosen over published-and-permanent.** A failed guard leaves the
deploy job red, GitHub notifying, the nupkg artifact retained for 7 days
(`.github/workflows/messagebrokers-cicd.yml:92,98` — retention sized to outlive an unbounded approval wait),
and the job re-runnable. Re-running a failed run preserves the original `push` event, so every `if:` guard
re-evaluates unchanged. That is recoverable. A published package with an unresolvable dependency is not:
nuget.org allows unlist and deprecate, never delete.

### Why this shape — the guard could already have shipped the incident it exists to prevent

The first version of this guard proved all five obligations by pattern coincidence. Local adversarial review
returned a root cluster over it — findings `6059c0d0…`, `93fc852a…` and `224c08c0…` — and demonstrated the
same framing recurring in a fourth instance in the same body that no finding had reported. Same-framing
recurrence is the signal that the instances are symptoms and the primitive is the defect.

The decisive evidence is not the cluster, though. It is what two harness cases proved about the old body: on
a `200` response that was **not** the expected document, that body **exited 0**, which in a `deploy` job means
the push proceeds. An index whose `versions` array held only `"0.28.0"` while the rest of the payload read
`"0.30.0 was cancelled"` passed. So did a CDN HTML error page that merely quoted the version in its prose.
That is the exact incident class this ADR exists to prevent — an unrestorable package reaching a feed that
permits no deletion — reachable *through* the control written to prevent it. A guard that can pass on a
document it never understood is worse than no guard, because it also supplies confidence. That, and not the
finding count, is why the primitive was replaced rather than the instances patched.

### Why the body is inline YAML and not `.github/scripts/*.sh`

The `deploy` job carries a hard INVARIANT, identical in all nine files
(`.github/workflows/messagebrokers-cicd.yml:100-111`): it has no `actions/checkout`, and that absence is the
closure mechanism. It is the only job granted `id-token: write`, so with no working tree there is no repo
source, no test assemblies, no test-only dependency graph, and no `.github/scripts/*.sh` for anything to load
alongside the publish credential. Calling a script from this job requires restoring the checkout the invariant
exists to forbid. A local composite action requires the same checkout. Smuggling the script in through the
uploaded artifact honours the letter of the invariant and violates its intent.

That same invariant decides the parser. Reading a `.nupkg` as a zip archive, its nuspec as XML and the
flat-container response as JSON needs a real parser, and the only parsers reachable from a job with no working
tree are the ones already on the runner image: `python3` is preinstalled on `ubuntu-latest`, and its standard
library carries `zipfile`, `xml.etree.ElementTree` and `json` — no `pip install`, no marketplace action, no
`setup-python`, and nothing to check out. It is the only closure available under the no-checkout INVARIANT,
which is why the guard hard-requires it: `command -v python3` failing is an exit 2, not a pass. The guard's
whole runtime dependency set is `curl` for the fetch and `python3` for both parses; no archive utility is
invoked, so `unzip` is not among its requirements.

The reader is materialized at run time by a quoted heredoc — `cat >"$guard_reader" <<'PY'` into an `mktemp`
file, removed by a `trap ... EXIT` — and that is deliberately **not** a checkout and **not** a
`.github/scripts/*.sh`. The program's bytes are part of the inline step body the workflow file already carries
and the harness already extracts and compares across all nine copies; materializing them to a temporary path
only gives `python3` a file to execute. No repo source is fetched and nothing outside the step body becomes
loadable, so the invariant's closure is intact. The heredoc delimiter is quoted, so the shell interpolates
nothing into the program and every input it reads arrives as `argv`.

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
`https://api.nuget.org/v3-flatcontainer/<id-lowercased>/index.json`, parses the response as JSON, and asserts
the declared version is a member of the document's `versions` array. Lowercasing the id is mandatory — mixed
case returns 404. *Published* means membership of that one list: the same characters occurring anywhere else
in the response — a note, another array, an object key — are not a publication, which is precisely what a
text search over the response bytes could not tell apart. A body that is not JSON, is not an object, carries
no `versions` key, or whose `versions` is not a list of strings is a document the guard did not understand
and exits 2. Flat-container normalizes versions (leading zeros stripped, a fourth zero segment dropped); all
nine modules use three-part SemVer, so the nuspec string and the index entry are the same characters and
compare directly, and that assumption is pinned in a comment next to the membership test rather than left
implicit.

The dependency set is read the same way. Every `<dependency>` under `<dependencies>` at any depth is
collected, so the grouped form `dotnet pack` emits for a multi-targeted package and the flat form a
single-targeted one emits are read identically, and element or attribute order, namespace prefixes, and
self-closing versus open/close form are all invisible to a parser that reads nodes. The `Chatter.` prefix
test is **case-insensitive**, a deliberate widening over the case-sensitive match it replaces: an id is
matched on what NuGet considers the same package, not on how the nuspec happened to capitalize it.

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

**The 600 seconds are a TOTAL budget for the whole step, not a budget per sibling.** One deadline is computed
once, before the first sibling, and never reset inside the loop; a per-sibling deadline would silently
multiply a ten-minute wait by the number of declared siblings. The guard is additionally **fail-fast**: the
first sibling resolving to anything other than *present* exits inside that sibling's own branch, so a later
sibling is never reached. Blocking a publish takes one unpublished sibling, not all of them, and the two
properties together are what keep the worst case at one budget rather than N of them.

The harness pins this as bounded total wall time, and the limit of that pin is worth stating honestly: because
the guard is fail-fast, a second sibling is never reached at all, so no test can observe a per-sibling
deadline on its own. What the wall-time case actually catches is the *compound* regression — a per-sibling
deadline **plus** the loss of fail-fast — and it cannot isolate which half caused it. Counting feed queries
instead would be unobservable by construction for the same reason. Keeping fail-fast is the accepted design;
redesigning the failure shape so a test could count a second query would be the tail wagging the dog.

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

Parsing extends exit 2 to every document the guard could not read, and that extension follows from the same
invariant: a guard that could not discharge its obligation must not read as a pass. So exit 2 covers a
`.nupkg` that is not a readable archive; an archive holding anything other than exactly one root `.nuspec`
entry; a nuspec that is not well-formed XML, carries no `<metadata>`, or declares no `<id>` or `<version>`; a
`Chatter.*` `<dependency>` declaring no version, because whether *that* sibling is published cannot be
determined at all; reader output that is not positively terminated or whose record count disagrees with the
count it declared; and a `200` that is not a flat-container index. None of these is an assertion failure —
nobody has been shown an unpublished dependency — and none of them is a pass.

### Direct dependencies only

`dotnet pack` emits direct references only, and the nuspec is what the guard reads, so the guard verifies each
package's direct `Chatter.*` dependencies and nothing deeper. Transitive correctness follows by induction:
`Chatter.MessageBrokers.AzureServiceBus.Auth` depends on `Chatter.MessageBrokers.AzureServiceBus`, which
depends on `Chatter.MessageBrokers`, which depends on `Chatter.CQRS`; if every package's direct dependencies
are verified present at its own publish time, then at the moment `Auth` publishes, `AzureServiceBus` is
published, and it in turn published only when `MessageBrokers` was — so the whole chain is on the feed. The
induction has a base case that this ADR does not supply: **versions published before this guard landed were
never checked**, and the guard makes no claim about them.

`Chatter.CQRS` has no sibling dependencies at all, and its empty set is now a **stated result rather than the
residue of a failed search**. The reader emits the package identity, a record per `Chatter.*` dependency, an
explicit `dependency-count`, and a terminator sentinel; the guard refuses to proceed unless the sentinel is
the last line and the count matches the records it read. Only then does zero mean *this package declares
nothing to verify*, printed as such and exited 0. Under the previous shape, zero records was whatever came
back from a search — the same output a truncated read, an unparsed manifest, or a reader that died mid-write
would have produced, and every one of those would have published. CQRS still carries the step for uniformity,
for the nine-copy assertion, and against a future dependency.

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
  `needs.package.outputs.should_publish == 'true'` (`.github/workflows/messagebrokers-cicd.yml:116`), so a
  push with no version bump never reaches it. The only always-on CI addition is the hermetic offline harness,
  which never touches nuget.org and therefore cannot flake on it.
- **A blocked deploy can idle up to 600 seconds of runner time before failing — once, not once per sibling.**
  The deadline is a total across the whole step and the guard exits at the first absent sibling, so the worst
  case is one budget however many `Chatter.*` dependencies a package declares. Accepted, and retunable through
  the documented env seam.
- **Nine copies of one guard body, bash plus its embedded `python3` reader, must stay identical.** The
  byte-identical assertion in the harness is the mechanism and it covers the reader too, because the reader is
  part of the body; a tenth CD workflow added later trips the "exactly nine" count, which must be updated
  deliberately rather than removed.
- **The `deploy` job now depends on `python3` being on the runner image.** It is preinstalled on
  `ubuntu-latest`. Its absence is an exit 2 with a message saying the nuspec was never read, never a pass —
  but a runner image that dropped it would block every release until the image or the guard changed.
- **The guard proves publication, not correctness.** It does not verify that the declared version is the
  version the dependent was compiled against, only that it exists. A package pinned at a real-but-wrong
  version still passes.
- **Parsing closes shape-coincidence, not peer identity.** The guard now understands the documents it reads,
  but it still trusts that the endpoint it reached is the endpoint it meant — DNS and TLS — and still trusts
  nuget.org's own semantics for what membership of a flat-container `versions` array means. A response from
  the wrong peer, correctly shaped, is still believed. That residual is out of this guard's reach and is
  recorded rather than claimed closed.
- **The identical pattern-coincidence primitive survives in `.github/scripts/assert-nupkg-provenance.sh`** at
  `:107`, `:115`, `:150-151`, `:173` and `:197` — `sed`/`grep` over nuspec text and `unzip -Z1` output where
  a parse belongs. It is deliberately NOT fixed here, and it is tracked as
  <https://github.com/brenpike/Chatter/issues/475> so the deferral cannot be silently dropped. The fix-now /
  defer split is bounded impact, not convenience: this guard body runs in the ONLY job holding
  `id-token: write`, and its fail-open terminates in `dotnet nuget push` of an unrestorable package to a
  registry that permits no deletion. The provenance script runs with a checkout in `supply-chain-gates.yml`,
  holds no publish credential, and its fail-open ships a package with poorer supply-chain metadata —
  permanent too, but materially less severe, and not reached through a credential.
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
  at `:52-68` consumed at `:116`, the 7-day artifact retention at `:92,98`, the `deploy` job INVARIANT at
  `:100-111`, and the guard step `Assert declared Chatter dependencies are published` at `:130-451` (sentinel
  comments at `:136` and `:451`), sitting between `Download package artifact` (`:125`) and `Setup .NET`
  (`:452`), and therefore ahead of `NuGet login (OIDC)` (`:460`) and `Push NuGet packages` (`:465`).
- `.github/scripts/assert-nupkg-provenance.sh:6-7` — the existing exit-1-versus-exit-2 invariant this guard's
  exit codes mirror. The same file's `:107`, `:115`, `:150-151`, `:173` and `:197` still carry the
  pattern-coincidence primitive this guard removed, deferred to issue #475.
- Issue #475 — *`assert-nupkg-provenance.sh` proves its obligations by string coincidence, not by parsing*.
  The tracked home for the deferred half of the same class, with the bounded-impact reasoning recorded in the
  Consequences above.
- `.github/scripts/tests/deploy-dependency-guard.test.sh` and `.github/scripts/tests/fixture-feed.py` — the
  hermetic offline harness, **49 assertions, all green**. Seven are structural: tooling present, extraction
  strips only a terminal CR, the nine-workflow CD set, marker coverage, the byte-identical assertion, the
  identical pre-sentinel regions, and `bash -n` over every extracted body. Twenty-two are behavioural, driving
  the real extracted body: all dependencies published; declared version absent; dependency never published;
  index `5xx`; endpoint unreachable; zero `Chatter.*` dependencies; version substring collision; dependency
  attributes reversed; attributes wrapped across lines; namespace-prefixed elements; a commented-out `<id>`
  that must not become the package identity; dependencies without target-framework groups; a dependency in
  open/close rather than self-closing form; a `Chatter.*` dependency with no version; a truncated nuspec; a
  `.nupkg` holding two root `.nuspec` entries; one holding none; a `200` with a non-JSON body; a `200`
  mentioning the version outside the `versions` array; a `200` with no `versions` key; a `versions` that is
  not a list of strings; and the poll budget bounding total wall time to one budget rather than one per
  dependency. Two of those — a `200` whose body is not JSON at all and a `200` mentioning the version outside
  the `versions` array — are the cases that ran RED against the previous pattern-matching body by exiting 0
  and letting the publish proceed. It uses a real local HTTP
  stub rather than `file://`, because `curl -w '%{http_code}'` reports `000` for `file://` and would defeat
  the status classifier the guard is built on.
- `.github/workflows/supply-chain-gates.yml` — where the harness runs, unfiltered by path so a workflows-only
  change still exercises it.
- Commits `1d3b8e7` (PR #466, the `0.29.0` bump whose CD run `34671797737` was cancelled) and `bfda47a`
  (PR #467, the push that shipped `SqlServiceBroker 0.14.2` and `SqlChangeFeed 0.14.2`) — the incident.
- ADR-0015 — *Inbound header trust: ground truth stamped over wire values, and no trust boundary*. Source of
  the delete-the-unearned-claim doctrine applied to the documentation consequence above.
