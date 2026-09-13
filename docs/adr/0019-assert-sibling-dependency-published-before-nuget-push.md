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
on a byte pattern found in some bytes, and never at a document position the document itself did not uniquely
determine.** Two classes are eliminated, in two passes, and each is named as a class rather than as the list
of shapes that now happen to be handled — the second was found only because naming the first as a class made
its survival checkable.

**Shape coincidence.** Under a pattern-matching guard every obligation was keyed on *a pattern appeared in a
stream*, and the absence of a match was indistinguishable from a clean pass: `grep` found no `Chatter.*`
dependency, therefore this package declares none, therefore publish. Five instances died together, and they
are named so a future reader can check that none came back: selecting the nuspec entry inside the `.nupkg`,
reading the package `<id>`, reading the package `<version>`, collecting the `<dependency>` set, and testing
flat-container membership. All five are now `zipfile`, `xml.etree.ElementTree` and `json` reads.

**Permissive selection.** A fact is read from a document position chosen permissively — first match wins,
unmatched nodes skipped, absent nodes answer "none", archive filter looser than the consumer's — so the
guard's answer is one reading among several the document admits, and the reading that yields the fewest
dependencies is indistinguishable from a correct one. Parsing alone does not close this. A parser selects
permissively exactly as a `grep` does, and the first remediation carried that primitive across intact (see
below).

The second class is closed by interpreting the nuspec **totally**. Every lookup is `exactly_one` or
`at_most_one`, raising on any other cardinality, so no fact is read off "the first one" and no absent node
answers "none". Traversal under `<dependencies>` is exhaustive to full depth, with every element required to
be a node the reader models: an unmodelled child raises rather than being passed over, and that includes a
`<dependency>` carrying child elements, which no nuspec schema models. One namespace is in play — the root's
own — and every other name in the document is matched fully-qualified against it. The archive filter is
NuGet's own `IsManifest`. A whole-document census closes the remainder as ONE rule rather than a pair of
special cases: any element anywhere in the document spelling a name the reader models — `metadata`, `id`,
`version`, `dependencies`, `group`, `dependency` — that the modelled walk did not visit raises, which refuses
a `<dependencies>` nested under a container no schema defines and a `<dependencies>` subtree redeclaring a
foreign namespace with the same sentence. And the reader emits a `manifest` identity fact FIRST, which the
bash side requires ahead of any other fact, so a dependency set — an empty one above all — can never be read
off output that never positively identified a manifest.

An empty result is therefore a parser-reported, counted, positively-terminated fact that says *this document
declares nothing*, reached by an exhaustive read of a positively identified manifest. Every document that
admits a second reading, and every document the parser could not read at all, exits 2. Nothing reads as
success by failing to find something, and nothing reads as success by having looked in only one of the places
the document could have put it.

**The failure mode is unpublished-and-loud, chosen over published-and-permanent.** A failed guard leaves the
deploy job red, GitHub notifying, the nupkg artifact retained for 7 days
(`.github/workflows/messagebrokers-cicd.yml:92,98` — retention sized to outlive an unbounded approval wait),
and the job re-runnable. Re-running a failed run preserves the original `push` event, so every `if:` guard
re-evaluates unchanged. That is recoverable. A published package with an unresolvable dependency is not:
nuget.org allows unlist and deprecate, never delete.

### Why this shape — twice, and the second time because the first remediation did not close it

**This guard has been remediated twice, and the first remediation was NON-CLOSING. Why it was is the most
useful thing this document records.** It changed the MECHANISM and carried the PRIMITIVE across intact:
`grep` became `xml.etree.ElementTree`, and the key every obligation was decided on stayed *did I find any
dependency nodes?* — it never became *did I positively identify this document as the nuspec NuGet will
consume?* The fail-open relocated into the parser rather than being removed. Both passes are recorded below,
each with the evidence that ended it.

The first version of this guard proved all five obligations by pattern coincidence. Local adversarial review
returned a root cluster over it — findings `6059c0d0…`, `93fc852a…` and `224c08c0…` — and demonstrated the
same framing recurring in a fourth instance in the same body that no finding had reported. Same-framing
recurrence is the signal that the instances are symptoms and the primitive is the defect.

The decisive evidence was not the cluster. It is what two harness cases proved about that body: on a `200`
response that was **not** the expected document, it **exited 0**, which in a `deploy` job means the push
proceeds. An index whose `versions` array held only `"0.28.0"` while the rest of the payload read `"0.30.0
was cancelled"` passed. So did a CDN HTML error page that merely quoted the version in its prose. That is the
exact incident class this ADR exists to prevent — an unrestorable package reaching a feed that permits no
deletion — reachable *through* the control written to prevent it. A guard that can pass on a document it
never understood is worse than no guard, because it also supplies confidence.

The class named in that first pass — text-scan coincidence — was real, and it is gone. It was simply the
wrong boundary to have drawn around the cluster. A parser that takes the first `<dependencies>`, passes over
a child it does not recognise, answers "none" for an absent node, and filters archive entries more loosely
than the restoring client is selecting permissively, exactly as the `grep` was. The cluster was about
permissive selection, of which scanning text is one instance.

A second adversarial review found two documents on which the parsing guard still failed open. Both are
divergences from NuGet's own reader, verified against its source rather than argued from first principles,
and both were measured against the parsing body before it was replaced.

**Duplicate `<dependencies>`.** `NuspecReader.GetDependencyGroups()` resolves the metadata namespace and then
calls `MetadataNode.Elements(XName.Get(Dependencies, ns))` — plural. NuGet enumerates EVERY `<dependencies>`
element under `<metadata>` and unions the groups found across them. The parsing guard took the first. A
document carrying an empty `<dependencies />` followed by a populated one therefore emitted
`dependency-count 0` at exit 0 — measured — and published, while the restoring client read the union and
failed. What makes this the headline of the class rather than one more malformed shape on a list: **that
document has a correct `package` root, one uniform namespace and exactly one `<metadata>`**, so a
root-element check alone does NOT catch it, and nothing about it is malformed. It is a document that admits
two readings, and the guard answered from the one that publishes. That is also why the second remediation is
not another completion of a known set. The decision taken is to **refuse** such a document with exit 2 rather
than mirror NuGet's union: `dotnet pack` never emits two, and interpreting an ambiguous document is the
primitive being removed, not a behaviour to reimplement more faithfully.

**`IsRoot` is slash AND backslash.** NuGet's `PackageHelper.IsManifest` is `IsRoot(path) && IsNuspec(path)`,
and `IsRoot` is `path.IndexOfAny(Slashes) == -1` over `new char[] { '/', '\\' }`. The parsing guard tested
only `/`. An archive whose sole nuspec-ish entry is `sub\decoy.nuspec` was therefore read as the manifest and
answered — measured, at exit 0 — from a document the restoring client never sees, because `GetNuspecFile`
finds zero manifests in that archive and throws.

### The root-namespace accept-list, as shipped rather than as planned

The plan for this remediation argued that a namespace accept-list should be REJECTED, on the grounds that a
document consistently in one unrecognised namespace is read the same way by the guard as by NuGet, so
root-derived matching alone buys everything a list would. Implementation found that irreconcilable with the
harness. A document consistently in a foreign namespace — correct root local name, one uniform namespace, one
`<metadata>`, one `<dependencies>` — is read cleanly by root-derived matching and lands on exit 1 for the
absent sibling. The harness requires exit 2, because a manifest whose schema NuGet does not accept is a
document the guard did not understand, not an assertion anyone has been shown. Measured both ways on that
fixture: rc 2 with the root-namespace check, rc 0 without it.

What SHIPPED is therefore a root-only accept-list: the six namespaces `ManifestSchemaUtility` accepts —
`2010/07`, `2011/08`, `2011/10`, `2012/06`, `2013/01` and `2013/05`, each
`http://schemas.microsoft.com/packaging/<version>/nuspec.xsd` — plus the empty string a legacy nuspec
carrying no `xmlns` resolves to. It is consulted at EXACTLY ONE site, the root. Nothing below the root
consults it; every other name in the document is matched fully-qualified against the root's own namespace.

The argument against accept-lists is recorded too, because it is not wrong, only not decisive: a hand-written
list is likely wrong on arrival and stale on the next schema. `2011/10` is absent from most published
summaries of the nuspec schemas and is in the guard deliberately, because the six were read off
`ManifestSchemaUtility` rather than off those summaries. The residual is stated plainly: a seventh NuGet
schema exits 2 until the list is updated. That is FAIL-CLOSED — a red deploy with the artifact retained 7
days — never a bad publish, which is the only direction this guard is permitted to fail in.

### The harness could not have caught any of this, and its fixture builders are the reason

The previous harness had 49 green assertions over the parsing guard, and not one of them could have failed on
either divergence, because no fixture it was able to build expressed one. `write_nuspec` and
`shape_nuspec_document` BOTH hardcoded
`<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">` over one `<metadata>` holding
one `<dependencies>`, and parameterized only the dependency block. Root element, namespace and element
cardinality were not arguments they took. The blind spot was enforced by the fixture builders' SIGNATURES
rather than by the choice of cases, so extending the case list alone would not have helped. Both builders had
to be rebased onto a shared `emit_nuspec_document` taking `--root`, `--namespace`, `--child-namespace`,
`--metadata-copies`, `--id-copies`, `--version-copies`, `--dependencies-copies` and `--extra-metadata-block`
before any of the new cases could be written at all.

The general lesson is the one worth keeping: **a harness authored against an implementation inherits that
implementation's blind spots unless its fixture builders can express documents the implementation does not
handle.** A case list is auditable at a glance; the fixture builders' parameter list is what decides which
cases that list is even able to contain.

### Why a third strike of this shape is not reachable under today's NuGet schema

The census earns less than "structurally unreachable", and saying exactly what it does earn is the point of
this section. `assert_declarations_are_all_modelled` (`.github/workflows/messagebrokers-cicd.yml:351-363`) is
a RELOCATED-MODELLED-NAME rule, not an unmodelled-element rule: an element the modelled walk never visited
raises only if it spells one of the six `MODELLED_LOCAL_NAMES` (`:212-213`) — `metadata`, `id`, `version`,
`dependencies`, `group`, `dependency`. An unvisited element spelling anything else is passed over in silence.
Measured against the shipped reader, three documents exit **0** while carrying a `Chatter.*` dependency it
never reports: an unmodelled container holding only unmodelled-named children;
`<packageDependencies><requires package="Chatter.CQRS" atLeast="0.16.0"/></packageDependencies>`; and an
attribute-borne `<frameworkReferences dependsOn="Chatter.CQRS/0.16.0"/>`. Even `<dependencyGroups>` — the
shape the census comment names — is refused through its `<group>` and `<dependency>` descendants and NOT
because the container itself is unknown: the same element carrying only unmodelled-named children exits 0.

What is earned is worth stating positively, because it is strong. **The guard is SOUND against today's NuGet
schema.** Restore reads dependencies from exactly one position, `metadata/dependencies`, and that subtree is
traversed exhaustively with every child required to be a node the reader models. A document carrying a
dependency in any of the positions above therefore declares nothing NuGet would restore, so the bypass
construction — a package publishing while a sibling it really depends on is absent from the feed — is not
satisfiable. Every position a `dotnet pack` output can put a dependency in is read or refused.

The residual is FORWARD-LOOKING: a future NuGet schema introducing a dependency-bearing construct spelled with
a seventh name would be skipped silently rather than raising. Closing it means replacing the six-name census
with a positive allowlist of modelled positions, deferred to
<https://github.com/brenpike/Chatter/issues/476> and pinned in the harness by
`case_unmodelled_container_with_only_unmodelled_children`, whose exit-0 assertion is written to flip to exit 2
when that rework lands.

That is a claim about the named class under the stated assumptions — direct dependencies only, today's schema,
versions published before the guard landed uncovered, peer identity trusted. It is not a claim that no
fail-open of any kind is possible.

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
and exits 2. Neither string in that membership test is compared as it arrived: both pass through one
normalizer, for the reasons recorded in the next section.

### One normalizer on both sides, and why that divergence class has exactly one member

`normalize_version` (`.github/workflows/messagebrokers-cicd.yml:411`) is `value.strip().lower()`, and the
membership test (`:445`) applies it to the declared version AND to every entry of the index's `versions`
array. ONE function, BOTH sides. A normalizer on one side alone is not a weaker version of this; it is a
different and wrong guard, green on a nuspec whose label is uppercase against a lowercase index and red on
the mirror document. The harness drives both directions for exactly that reason.

It folds the WHOLE string rather than the prerelease label alone. Numeric parts have no case, so lowering
them is identity, while splitting on `-` to reach the label would be hand-parsing a version this guard
otherwise never parses — which is the exact primitive the guard exists to remove. Case-folding an identifier
is already the rule on either side of this reader: the `chatter.` prefix test lowers a dependency id
(`:310`), and the bash side lowers that same id to build the flat-container path (`:540`).

**The ORDERING is load-bearing.** Normalization happens AFTER the list-of-strings validation of `versions`,
never before it. Normalizing first would raise `AttributeError` on a non-string entry and leave the reader
through exit 1 — silently reclassifying *this is not a document the guard could read* as *this version is
absent from the feed*, which inverts the 1-versus-2 split on exactly the document that split exists for. The
constraint is stated as an `INVARIANT:` comment at the site (`:439-441`) rather than left to reading order.

**The class closes at exactly one member: prerelease-label case.** `dotnet pack`, writing a
`ProjectReference` sibling's `<Version>` into the nuspec dependency entry, was measured to normalize:

| declared `<Version>` | emitted `<dependency version=...>` |
| --- | --- |
| `1.02.3` | `1.2.3` — leading zeros stripped |
| `1.2.3.0` | `1.2.3` — trailing-zero fourth component stripped |
| `1.2.3+build7` | `1.2.3` — build metadata stripped |
| `1.2.3.4` | `1.2.3.4` — a genuine fourth component preserved verbatim |
| `1.02.3-RC.1` | `1.2.3-RC.1` — number normalized, LABEL CASE PRESERVED |

The flat-container index is always lowercase: zero non-lowercase entries across 685 versions of
`Newtonsoft.Json` and `Serilog`. So every NUMERIC spelling is already identical on both sides before the
comparison exists — pack strips what the index strips — and a genuine four-part version is preserved verbatim
on both sides and matches, which the feed confirms: `castle.core` lists `3.0.0.2001` and
`microsoft.data.sqlclient` lists `1.0.19239.1`. Prerelease label case is the only spelling divergence the
toolchain can deliver, so it is the only one the normalizer folds.

That is the paragraph that should stop a future reader reopening this as complete-the-known-set. The claim is
scoped: the class closes at one member FOR VERSION-SPELLING DIVERGENCE, under today's toolchain. It is not a
claim that the guard has no residual — issue #476 is open and forward-looking.

The divergence was latent rather than live. Chatter has never shipped a prerelease: 211 versions across the
nine package indexes, none carrying a prerelease label, and no prerelease git tag. The guard would have met
this on the first one.

One inverse risk follows from the same reading, and it fails CLOSED. A ranged declaration —
`version="[0.16.0, )"` — carries a space, so `UNSAFE_FIELD_CHARACTERS` (`:192`) matches it inside
`require_field` (`:264-271`), reached from `read_dependency` (`:316-317`), and the `ValueError` leaves through
`report_anomaly` at exit 2. Measured against the shipped reader. A space-free range — `version="[0.16.0,)"` —
passes `require_field` instead and falls through to the membership test as a version string the index does not
list, so it exits 1: a different code, the same refusal to publish. Neither is reachable while every sibling
reference is a `ProjectReference`, which holds across all nine today; the condition that makes them reachable
is converting a sibling `ProjectReference` to a ranged or floating `PackageReference`.

The dependency set is read the same way, and exhaustively: `<dependencies>` holds either `<group>`
elements — the form `dotnet pack` emits for a multi-targeted package — or bare `<dependency>` elements, the
flat form a single-targeted one emits, both are read, and every child of both is required to be one of those
two nodes in this document's own namespace. It is NOT a walk at any depth that collects whatever it
recognises: an element it does not model raises rather than being passed over, because a construct nobody
modelled is where an unreported dependency would sit. Element and attribute order, namespace prefixes, and
self-closing versus open/close form remain invisible to a parser that reads nodes. The `Chatter.` prefix
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
entry, or one whose sole `.nuspec` entry is not at the archive root under NuGet's own `IsManifest` rule, or
one holding two entries under a single root `.nuspec` name; a nuspec that is not well-formed XML, whose root
is not `<package>`, or whose root sits in a namespace no nuspec schema defines; one carrying no `<metadata>`
or declaring no `<id>` or `<version>`; one carrying more than one of `<metadata>`, `<id>`, `<version>` or
`<dependencies>`, because which of them states the fact is then undecidable; one spelling a modelled element
at a position no schema models, including inside a subtree that redeclares a foreign namespace; a
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
- **A blocked deploy can idle roughly 645 seconds of runner time before failing — once, not once per
  sibling.** The deadline is a total across the whole step and the guard exits at the first absent sibling, so
  the worst case is one budget however many `Chatter.*` dependencies a package declares. The number is not
  600: the deadline is tested AFTER the fetch (`.github/workflows/messagebrokers-cicd.yml:576`, the fetch
  itself at `:546` carrying `curl --max-time 30`) and BEFORE an unconditional
  `sleep "$DEPENDENCY_GUARD_POLL_SECONDS"` (`:577`), so a
  check passing an instant inside the deadline still buys one poll interval and one fetch timeout before the
  next one breaks — budget plus 15 plus 30. The harness does not bound that number and must not be read as
  doing so: `case_poll_budget_bounds_total_wall_time` allows 1.5x the budget, a tolerance sized to separate
  one budget from two on a loaded machine, and 1.5x never separates 600 from 645. Accepted, and retunable
  through the documented env seam.
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
- **Total parsing closes shape coincidence and selection ambiguity, not peer identity.** The guard now
  understands the documents it reads and refuses the ones that admit more than one reading, but it still
  trusts that the endpoint it reached is the endpoint it meant — DNS and TLS — and still trusts nuget.org's
  own semantics for what membership of a flat-container `versions` array means. A response from
  the wrong peer, correctly shaped, is still believed. That residual is out of this guard's reach and is
  recorded rather than claimed closed.
- **The same primitive survives in `.github/scripts/assert-nupkg-provenance.sh`** at `:107`, `:115`,
  `:150-151`, `:173` and `:197` — `sed`/`grep` over nuspec text and `unzip -Z1` output where a parse belongs.
  Issue #475 names that class *string coincidence*; on the evidence above the name should widen to *permissive
  selection*, because a parse alone would not close it. The bounded-impact judgement is UNCHANGED: it is
  deliberately NOT fixed here, and it is tracked as
  <https://github.com/brenpike/Chatter/issues/475> so the deferral cannot be silently dropped. The fix-now /
  defer split is bounded impact, not convenience: this guard body runs in the ONLY job holding
  `id-token: write`, and its fail-open terminates in `dotnet nuget push` of an unrestorable package to a
  registry that permits no deletion. The provenance script runs with a checkout in `supply-chain-gates.yml`,
  holds no publish credential, and its fail-open ships a package with poorer supply-chain metadata —
  permanent too, but materially less severe, and not reached through a credential.
- **The whole-document census enumerates six element names; it does not interpret the manifest against NuGet's
  accepted element set.** `assert_declarations_are_all_modelled`
  (`.github/workflows/messagebrokers-cicd.yml:351-363`) raises only where an unvisited element spells one of
  `MODELLED_LOCAL_NAMES` (`:212-213`), so an unmodelled container holding only unmodelled-named children, a
  `<packageDependencies><requires package="Chatter.CQRS" atLeast="0.16.0"/></packageDependencies>` block, and
  an attribute-borne `<frameworkReferences dependsOn="Chatter.CQRS/0.16.0"/>` each exit 0 with their
  `Chatter.*` dependency unreported — all three measured against the shipped reader. The root cause is the
  permissive selection this guard exists to remove, surviving as a negative rule over six names where a
  positive allowlist of modelled POSITIONS belongs. It is deliberately NOT fixed here and is tracked as
  <https://github.com/brenpike/Chatter/issues/476> so the deferral cannot be silently dropped. The
  bounded-impact judgement: none of the three is reachable as a bad publish under today's NuGet schema, because
  restore reads dependencies only from `metadata/dependencies`, so a document shaped like any of them declares
  nothing NuGet would restore. The exposure is forward-looking — a future schema gaining a seventh
  dependency-bearing name — and it is pinned in the harness by
  `case_unmodelled_container_with_only_unmodelled_children`, whose exit-0 assertion is written to invert when
  the rework lands rather than to be deleted.
- **The guard is deliberately stricter than NuGet on an ambiguous manifest.** Two `<dependencies>` elements
  are a union to `NuspecReader`, and exit 2 here. `dotnet pack` never emits them, so the cost is zero on
  every document this repository produces, and the alternative is reimplementing NuGet's resolution closely
  enough to be trusted — which is the primitive this guard exists to remove.
- **A nuspec in a seventh NuGet schema namespace would block every release of that module.** The root
  namespace is checked against the six `ManifestSchemaUtility` URIs plus the empty legacy one, and an
  unrecognised seventh exits 2. Retuning is a one-line list edit in nine files; the failure direction is a red
  deploy with the artifact retained, never a publish.
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
  `:100-111`, and the guard step `Assert declared Chatter dependencies are published` at `:130-599` (sentinel
  comments at `:136` and `:599`), sitting between `Download package artifact` (`:125`) and the `deploy` job's
  own `Setup .NET` (`:600` — not the `package` job's at `:43`), and therefore ahead of `NuGet login (OIDC)`
  (`:608`) and `Push NuGet packages` (`:613`).
- `.github/scripts/assert-nupkg-provenance.sh:6-7` — the existing exit-1-versus-exit-2 invariant this guard's
  exit codes mirror. The same file's `:107`, `:115`, `:150-151`, `:173` and `:197` still carry the primitive
  this guard removed, deferred to issue #475.
- Issue #475 — *`assert-nupkg-provenance.sh` proves its obligations by string coincidence, not by parsing*.
  The tracked home for the deferred half of the same class, with the bounded-impact reasoning recorded in the
  Consequences above. Its title names the narrower class; the work it tracks is permissive selection.
- Issue #476 — *Dependency guard enumerates dependency-bearing positions instead of interpreting the manifest
  against NuGet's accepted element set*. The tracked home for the six-name census's forward-looking gap, with
  the bounded-impact reasoning recorded in the Consequences above, and the gap pinned in the harness by
  `case_unmodelled_container_with_only_unmodelled_children`.
- `.github/scripts/tests/deploy-dependency-guard.test.sh` and `.github/scripts/tests/fixture-feed.py` — the
  hermetic offline harness, **84 assertions, all green**. Nine are structural: extraction strips only a
  terminal CR, the nine-workflow CD set, marker coverage, the byte-identical assertion, the identical
  pre-sentinel regions, `bash -n` over every extracted body, and three over the checked-in packed nuspec
  fixture. Seventy-five are behavioural across 40 cases driving the real extracted body. Feed-shaped: all
  dependencies published; declared version absent; dependency never published; index `5xx`; endpoint
  unreachable; a `200` with a non-JSON body; a `200` mentioning the version outside the `versions` array; a
  `200` with no `versions` key; a `versions` that is not a list of strings; a version substring collision; a
  declared prerelease label whose case differs from the published entry's, driven in BOTH directions so that a
  normalizer applied to one side alone fails one of them; a declared prerelease differing from the published
  one by more than case, the negative control that keeps folding case from becoming folding the label away;
  and the poll budget bounding total wall time to one budget rather than one per dependency. Document-shaped:
  zero `Chatter.*` dependencies; dependency attributes reversed; attributes wrapped across lines;
  namespace-prefixed elements; a commented-out `<id>` that must not become the package identity;
  dependencies without target-framework groups; a dependency in open/close rather than self-closing form; a
  `Chatter.*` dependency with no version; a truncated nuspec; a `.nupkg` holding two root `.nuspec` entries;
  one holding none; a root element that is not `<package>`; duplicated `<metadata>`, `<id>`, `<version>` and
  `<dependencies>`; a foreign document namespace; a `<dependencies>` subtree redeclaring a foreign namespace;
  a modelled element name recurring at a position the walk never visits — `<group>` and `<dependency>` under
  `<dependencyGroups>`, refused for the names they spell and NOT because their container is unknown; the same
  container holding only unmodelled-named children, exiting 0 as the pinned known gap tracked by issue #476; a sole
  `.nuspec` entry behind a backslash-separated path; two archive entries sharing one root `.nuspec` name; a nuspec
  carrying no `xmlns` at all; every one of the six `ManifestSchemaUtility` namespaces read in turn; and the real
  shipped nuspec driven both ways, its
  sibling present and absent. Four of these ran RED against a body already written, every one of them letting
  the publish proceed: a `200` whose body is not JSON and a `200` mentioning the version outside the
  `versions` array exited 0 under the pattern-matching body, and two `<dependencies>` elements and the
  backslash-separated entry exited 0 under the parsing body that replaced it. Two more ran RED in the opposite
  direction: both directional prerelease-label cases exited 1 under the body that compared the two version
  strings as they arrived, blocking a release whose sibling was in fact published. It uses a real local HTTP
  stub rather than `file://`, because `curl -w '%{http_code}'` reports `000` for `file://` and would defeat
  the status classifier the guard is built on.
- `.github/scripts/tests/fixtures/chatter.messagebrokers.0.30.0.nuspec` — the one document in the harness
  `dotnet pack` actually wrote, checked in verbatim out of `chatter.messagebrokers.0.30.0.nupkg`: 1642 bytes,
  opening with a UTF-8 BOM and declaring `Chatter.CQRS 0.16.0` in both target-framework groups.
  `.gitattributes` pins `*.nuspec -text`, so no checkout, line-ending normalization or editor rewrite can
  touch those bytes, and
  three structural assertions re-check the BOM, both groups and the declared sibling on every run. The BOM is
  the point: `ElementTree.fromstring` consumes it when handed BYTES, so a refactor that decoded to `str` first
  would break on every shipped package, and this fixture is the only thing in the harness that would notice.
- NuGet.Client `dev`, read for the two divergences recorded above:
  `src/NuGet.Core/NuGet.Packaging/NuspecReader.cs` — `GetDependencyGroups()` calling
  `MetadataNode.Elements(XName.Get(Dependencies, ns))` and unioning across every match;
  `src/NuGet.Core/NuGet.Packaging/PackageExtraction/PackageHelper.cs` — `IsManifest = IsRoot && IsNuspec`
  with `Slashes = { '/', '\\' }`; and
  `src/NuGet.Core/NuGet.Packaging/PackageCreation/Authoring/ManifestSchemaUtility.cs` — the six schema
  namespaces the root accept-list carries.
- `.github/workflows/supply-chain-gates.yml` — where the harness runs, unfiltered by path so a workflows-only
  change still exercises it.
- Commits `1d3b8e7` (PR #466, the `0.29.0` bump whose CD run `34671797737` was cancelled) and `bfda47a`
  (PR #467, the push that shipped `SqlServiceBroker 0.14.2` and `SqlChangeFeed 0.14.2`) — the incident.
- ADR-0015 — *Inbound header trust: ground truth stamped over wire values, and no trust boundary*. Source of
  the delete-the-unearned-claim doctrine applied to the documentation consequence above.
