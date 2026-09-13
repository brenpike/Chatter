#!/usr/bin/env bash
# Asserts the deploy-time dependency-publication guard specified by docs/adr/0019: before a
# module's nupkg is pushed, every `Chatter.*` dependency version declared in that nupkg's nuspec
# must already exist on the nuget.org flat-container index. Publishing a package whose sibling
# dependency was never published ships something no consumer can restore, and nuget.org does not
# allow deletion.
#
# The guard is inline bash in the `deploy` job of all nine `<module>-cicd.yml`, because that job
# deliberately carries no `actions/checkout` and so has no `.github/scripts/*.sh` to load. This
# harness therefore EXTRACTS the guard body from the workflow files and executes that body — never
# a reimplementation — so what is asserted here is exactly what ships. `dotnet test`, the
# repository validation procedure, cannot execute workflow YAML; this harness is the substitute.
#
# INVARIANT: hermetic and offline. The only endpoint contacted is `fixture-feed.py` bound to
# 127.0.0.1, so this test can never flake on nuget.org reachability or CDN indexing lag.
#
# INVARIANT: the feed stub is a real HTTP server, never a `file://` URL. `curl -w '%{http_code}'`
# reports `000` for `file://`, which would collapse every status class the guard classifies on
# (200 / 404 / 5xx / transport error) into one and leave the behavioural cases asserting nothing.
#
# INVARIANT: the extracted bodies must be byte-identical across all nine workflows. That single
# assertion is the entire mitigation for shipping nine copies of one script; weakening it re-opens
# the drift the duplication was accepted on the assumption of closing.
#
# INVARIANT: an assertion failure exits 1 while a missing-tooling failure exits 2, matching
# .github/scripts/assert-nupkg-provenance.sh:6-7. A harness that could not run must not read as a
# clean pass.
#
# Usage: bash .github/scripts/tests/deploy-dependency-guard.test.sh
set -euo pipefail
shopt -s nullglob

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../../.." && pwd)"
fixture_feed="$script_dir/fixture-feed.py"
workflow_dir="$repo_root/.github/workflows"

# The one nuspec this harness does not generate: the document `dotnet pack` shipped inside
# chatter.messagebrokers.0.30.0.nupkg, extracted from the published package and checked in
# verbatim, BOM and all. Its facts are constants here so the index fixtures driving it and the
# self-check guarding it cannot drift apart from the file.
real_nuspec_fixture="$script_dir/fixtures/chatter.messagebrokers.0.30.0.nuspec"
real_nuspec_package_id='Chatter.MessageBrokers'
real_nuspec_package_version='0.30.0'
real_nuspec_sibling_id='Chatter.CQRS'
real_nuspec_sibling_version='0.16.0'

# The sentinel comments bracketing the guard body inside each workflow's `run: |` block. They are
# ordinary bash comments as well as extraction anchors, so they survive into the shipped step and
# point a reader at the decision record. `awk` between sentinels is used rather than `yq`, which is
# not guaranteed to be present on a runner image.
guard_begin_marker='# BEGIN dependency-publish guard (docs/adr/0019)'
guard_end_marker='# END dependency-publish guard (docs/adr/0019)'

# The step header above the BEGIN sentinel. Everything between this anchor and that sentinel — the
# step name, `shell:`, the comment explaining why the step precedes `Setup .NET`, and the env seam
# block — is shipped nine times just as the body is, but sits outside the sentinels and so escapes
# the byte-identical assertion unless it is compared separately.
guard_step_anchor='- name: Assert declared Chatter dependencies are published'

# Nine packable modules, nine CD workflows, per CLAUDE.md § Versioning. A tenth module CD added
# without the guard trips this count. Raise it deliberately alongside a tenth guard copy; never
# delete the assertion.
expected_cd_workflow_count=9

# Message contract the guard body must honour. Pinned here because the harness is written before
# the guard: a `200` whose index does not list the declared version and a `404` for an id that was
# never published are different operator problems with different remedies, and must not share one
# message.
version_absent_phrase='is not present at'
never_published_phrase='has never been published'
# An empty sibling-dependency set must be a stated fact, not the residue of a search that found
# nothing. A guard that cannot tell "this package declares no Chatter dependencies" from "my scan
# matched nothing" fails open on every nuspec shape it does not recognise, so the empty case is
# required to say so out loud.
confirmed_empty_phrase='declares no Chatter dependencies to verify'

# The guard reads its poll window from env with production-safe defaults (600s / 15s). These are
# operator-tunable seams, not test-only scaffolding; nothing in the `deploy` job sets them, so
# unset means production behaviour. The harness collapses the window so a timeout case costs
# seconds rather than ten minutes.
guard_timeout_seconds=2
guard_poll_seconds=1

pass_count=0
fail_count=0
skip_count=0
work_dir="$(mktemp -d)"
fixture_feed_pid=''
fixture_feed_port=''
guard_body_file=''
stub_instance=0

cd_workflows=("$workflow_dir"/*-cicd.yml)
marked_workflows=()
unmarked_workflows=()
malformed_workflows=()
body_files=()

report_pass() {
  printf 'PASS  %s\n' "$1"
  pass_count=$((pass_count + 1))
}

report_fail() {
  printf 'FAIL  %s\n' "$1" >&2
  fail_count=$((fail_count + 1))
}

report_skip() {
  printf 'SKIP  %s\n' "$1"
  skip_count=$((skip_count + 1))
}

fail_infrastructure() {
  printf '%s\n' "$1" >&2
  exit 2
}

stop_fixture_feed() {
  [ -n "$fixture_feed_pid" ] || return 0
  kill "$fixture_feed_pid" 2>/dev/null || true
  wait "$fixture_feed_pid" 2>/dev/null || true
  fixture_feed_pid=''
  fixture_feed_port=''
}

trap 'stop_fixture_feed; rm -rf "$work_dir"' EXIT

assert_tooling_present() {
  local tool
  # `curl` is the guard's own dependency, `awk` extracts the body, and `python3` both runs the feed
  # stub and parses the nuspec inside the guard. Absent any of them the harness asserts nothing.
  for tool in awk curl python3; do
    command -v "$tool" >/dev/null 2>&1 \
      || fail_infrastructure "'$tool' is not on PATH; the deploy dependency guard assertions did not run"
  done
}

# --------------------------------------------------------------------------------------------
# Structural assertions: the nine workflows carry the guard, and carry the same guard.
# --------------------------------------------------------------------------------------------

strip_uniform_indent() {
  # Removes the common leading whitespace shared by every non-blank line, so bodies nested at
  # different YAML depths still compare equal while any real difference in the body survives.
  awk '
    {
      lines[NR] = $0
      if ($0 ~ /[^[:space:]]/) {
        match($0, /^[[:space:]]*/)
        if (!indent_seen || RLENGTH < shortest_indent) {
          shortest_indent = RLENGTH
          indent_seen = 1
        }
      }
    }
    END {
      if (!indent_seen) shortest_indent = 0
      for (line_number = 1; line_number <= NR; line_number++) {
        print substr(lines[line_number], shortest_indent + 1)
      }
    }
  '
}

extract_guard_body() {
  local workflow="$1"
  # This repo runs `core.autocrlf=true` and `.gitattributes` pins only `*.sh text eol=lf`, so
  # `.github/workflows/*.yml` materializes CRLF in a developer's working tree even though it is
  # LF in git. A CRLF body is a bash syntax error, so without normalizing line endings here this
  # harness would go RED on a CRLF checkout and stay GREEN in CI (where autocrlf is off) — the
  # harness must be indifferent to checkout line-ending config rather than depend on every checkout
  # being configured correctly.
  #
  # INVARIANT: only the TERMINAL CR is removed. `tr -d '\r'` would delete CR anywhere, which makes
  # two bodies differing by an embedded CR compare byte-identical and so blunts the one assertion
  # mitigating nine copies of this script. An embedded CR is a real difference and must survive to
  # break that comparison.
  awk -v begin_marker="$guard_begin_marker" -v end_marker="$guard_end_marker" '
    index($0, begin_marker) { capturing = 1; next }
    index($0, end_marker)   { capturing = 0; next }
    capturing               { sub(/\r$/, ""); print }
  ' "$workflow"
}

extract_pre_sentinel_region() {
  local workflow="$1"
  # The step header through the BEGIN sentinel inclusive. Captured separately from the body so the
  # nine-copy duplication is covered over its whole extent rather than only between the sentinels.
  awk -v anchor="$guard_step_anchor" -v begin_marker="$guard_begin_marker" '
    index($0, anchor)       { capturing = 1 }
    capturing               { sub(/\r$/, ""); print }
    index($0, begin_marker) { if (capturing) exit }
  ' "$workflow"
}

count_marker_occurrences() {
  local workflow="$1" marker="$2"
  grep -cF -- "$marker" "$workflow" || true
}

first_marker_line() {
  local workflow="$1" marker="$2"
  grep -nF -m1 -- "$marker" "$workflow" | cut -d: -f1
}

collect_guard_bodies() {
  local workflow workflow_name begin_count end_count begin_line end_line body_file
  for workflow in ${cd_workflows[@]+"${cd_workflows[@]}"}; do
    workflow_name="$(basename "$workflow")"
    begin_count="$(count_marker_occurrences "$workflow" "$guard_begin_marker")"
    end_count="$(count_marker_occurrences "$workflow" "$guard_end_marker")"

    if [ "$begin_count" -eq 0 ] && [ "$end_count" -eq 0 ]; then
      unmarked_workflows+=("$workflow_name")
      continue
    fi
    if [ "$begin_count" -ne 1 ] || [ "$end_count" -ne 1 ]; then
      malformed_workflows+=("$workflow_name (begin marker x$begin_count, end marker x$end_count; expected exactly one of each)")
      continue
    fi

    begin_line="$(first_marker_line "$workflow" "$guard_begin_marker")"
    end_line="$(first_marker_line "$workflow" "$guard_end_marker")"
    if [ "$begin_line" -ge "$end_line" ]; then
      malformed_workflows+=("$workflow_name (end marker at line $end_line precedes begin marker at line $begin_line)")
      continue
    fi

    body_file="$work_dir/guard-body-$workflow_name.sh"
    extract_guard_body "$workflow" | strip_uniform_indent >"$body_file"
    if [ ! -s "$body_file" ]; then
      malformed_workflows+=("$workflow_name (markers present but the body between them is empty)")
      continue
    fi

    marked_workflows+=("$workflow_name")
    body_files+=("$body_file")
  done

  if [ "${#body_files[@]}" -gt 0 ]; then
    guard_body_file="${body_files[0]}"
  fi
}

assert_cd_workflow_set() {
  local discovered="${#cd_workflows[@]}"
  if [ "$discovered" -eq "$expected_cd_workflow_count" ]; then
    report_pass "$discovered '*-cicd.yml' CD workflows discovered, as expected"
    return 0
  fi
  report_fail "expected $expected_cd_workflow_count '*-cicd.yml' CD workflows, found $discovered; a new module CD must carry the guard and raise expected_cd_workflow_count"
}

assert_marker_coverage() {
  local discovered="${#cd_workflows[@]}"
  local marked="${#marked_workflows[@]}"
  local entry

  if [ "$marked" -eq "$discovered" ] && [ "${#malformed_workflows[@]}" -eq 0 ]; then
    report_pass "$marked of $discovered workflows carry the guard markers"
    return 0
  fi

  report_fail "$marked of $discovered workflows carry the guard markers"
  for entry in ${unmarked_workflows[@]+"${unmarked_workflows[@]}"}; do
    printf '        missing the guard markers: %s\n' "$entry" >&2
  done
  for entry in ${malformed_workflows[@]+"${malformed_workflows[@]}"}; do
    printf '        malformed guard markers: %s\n' "$entry" >&2
  done
}

assert_bodies_byte_identical() {
  local reference other divergent=()

  if [ "${#body_files[@]}" -lt 2 ]; then
    report_skip "guard bodies byte-identical: only ${#body_files[@]} body/bodies extracted, nothing to compare"
    return 0
  fi

  reference="${body_files[0]}"
  for other in "${body_files[@]:1}"; do
    cmp -s "$reference" "$other" || divergent+=("$(basename "$other")")
  done

  if [ "${#divergent[@]}" -eq 0 ]; then
    report_pass "all ${#body_files[@]} extracted guard bodies are byte-identical"
    return 0
  fi

  report_fail "${#divergent[@]} extracted guard body/bodies differ from $(basename "$reference"): ${divergent[*]}"
  diff -u "$reference" "${body_files[1]}" >&2 || true
}

assert_extraction_strips_only_terminal_cr() {
  # A self-test of the extractor the byte-identical assertion above is built on. If extraction
  # deleted every CR, two bodies that genuinely differ by an embedded CR would compare equal and
  # that assertion would pass over real drift.
  local case_name='guard body extraction strips the terminal CR and preserves an embedded one'
  local fixture_dir="$work_dir/cr-extraction"
  local fixture="$fixture_dir/synthetic-cicd.yml"
  local expected="$fixture_dir/expected.txt" actual="$fixture_dir/actual.txt"

  mkdir -p "$fixture_dir"
  {
    printf '      run: |\n'
    printf '        %s\r\n' "$guard_begin_marker"
    printf '        terminal-cr-line\r\n'
    printf '        embedded\rcr-line\n'
    printf '        %s\r\n' "$guard_end_marker"
  } >"$fixture"
  printf '        terminal-cr-line\n' >"$expected"
  printf '        embedded\rcr-line\n' >>"$expected"

  extract_guard_body "$fixture" >"$actual"
  if cmp -s "$expected" "$actual"; then
    report_pass "$case_name"
    return 0
  fi
  report_fail "$case_name"
  diff -u <(od -c "$expected") <(od -c "$actual") >&2 || true
}

assert_pre_sentinel_regions_identical() {
  local workflow workflow_name region_file reference='' divergent=() missing=()

  for workflow in ${cd_workflows[@]+"${cd_workflows[@]}"}; do
    workflow_name="$(basename "$workflow")"
    region_file="$work_dir/guard-header-$workflow_name.txt"
    extract_pre_sentinel_region "$workflow" | strip_uniform_indent >"$region_file"
    if [ ! -s "$region_file" ] || ! grep -qF -- "$guard_begin_marker" "$region_file"; then
      missing+=("$workflow_name")
      continue
    fi
    if [ -z "$reference" ]; then
      reference="$region_file"
      continue
    fi
    cmp -s "$reference" "$region_file" || divergent+=("$workflow_name")
  done

  if [ "${#missing[@]}" -eq 0 ] && [ "${#divergent[@]}" -eq 0 ] && [ -n "$reference" ]; then
    report_pass 'the guard step header through the BEGIN sentinel is byte-identical across all CD workflows'
    return 0
  fi

  report_fail "the guard step header diverges: ${#missing[@]} workflow(s) missing the region (${missing[*]-none}), ${#divergent[@]} divergent (${divergent[*]-none})"
  if [ -n "$reference" ] && [ "${#divergent[@]}" -gt 0 ]; then
    diff -u "$reference" "$work_dir/guard-header-${divergent[0]}.txt" >&2 || true
  fi
}

assert_guard_bodies_parse() {
  # `bash -n` catches genuine syntax errors in a body lifted out of a YAML block scalar — a CRLF
  # body, an unbalanced quote, an unclosed `if` — before any of them reach a deploy job holding a
  # publish credential.
  #
  # It does NOT catch a mis-indented heredoc terminator. Measured on bash 5.0.17: indenting the
  # `PY` terminator prints `warning: here-document ... delimited by end-of-file (wanted 'PY')` and
  # exits 0, so the exit code this assertion reads is clean. The behavioural cases below are what
  # catch that one, because the whole guard is swallowed into the heredoc and every case goes red.
  local body_file parse_log invalid=()

  if [ "${#body_files[@]}" -eq 0 ]; then
    report_skip 'guard bodies parse under `bash -n`: no body was extracted, nothing to parse'
    return 0
  fi

  parse_log="$work_dir/guard-body-parse.log"
  : >"$parse_log"
  for body_file in "${body_files[@]}"; do
    bash -n "$body_file" 2>>"$parse_log" || invalid+=("$(basename "$body_file")")
  done

  if [ "${#invalid[@]}" -eq 0 ]; then
    report_pass "all ${#body_files[@]} extracted guard bodies parse under \`bash -n\`"
    return 0
  fi

  report_fail "${#invalid[@]} extracted guard body/bodies are not valid bash: ${invalid[*]}"
  sed 's/^/        /' "$parse_log" >&2
}

# --------------------------------------------------------------------------------------------
# Fixtures: nupkgs built at test time, and flat-container index documents for the stub feed.
# --------------------------------------------------------------------------------------------

# The namespace `dotnet pack` writes, and the identity a decoy element carries. A decoy is given a
# recognisable id and an out-of-range version so a guard that reads one reports something no
# operator could mistake for the package being published.
default_nuspec_namespace='http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd'
decoy_package_id='DECOY.Package'
decoy_package_version='9.9.9'

emit_nuspec_document() {
  # The well-formed surround every generated nuspec fixture shares. Root element name, namespace
  # URI and element cardinality are PARAMETERS rather than literals: the fail-opens this harness
  # must be able to see all live in exactly those three places. A reader that never checks the root,
  # never checks the namespace, or takes the first of a repeated element is choosing one of several
  # readings the document admits, and the reading that yields the fewest dependencies is the one
  # that publishes. A builder that can only emit `<package xmlns="…2013/05…">` with one of each
  # child cannot express a document in those shapes, so the blind spot would be enforced by this
  # function's signature.
  #
  # Leading `--option value` pairs, then the package id, version, and dependency block:
  #   --root NAME               root element local name (default `package`)
  #   --namespace URI           default xmlns on the root; the empty string emits no xmlns at all
  #   --child-namespace URI     xmlns redeclared on <dependencies>, so that subtree sits in a
  #                             different namespace from the root while local names still match
  #   --metadata-copies N       N>1 emits a decoy <metadata> ahead of the real one
  #   --id-copies N             N>1 emits a decoy <id> ahead of the real one
  #   --version-copies N        N>1 emits a decoy <version> ahead of the real one
  #   --dependencies-copies N   N>1 emits an empty <dependencies /> ahead of the populated one
  #   --extra-metadata-block X  literal XML inserted into <metadata> before the dependencies
  local root_element='package'
  local namespace_uri="$default_nuspec_namespace"
  local child_namespace_uri=''
  local metadata_copies=1 id_copies=1 version_copies=1 dependencies_copies=1
  local extra_metadata_block=''
  local root_attribute='' dependencies_attribute=''

  while [ "$#" -gt 0 ]; do
    case "$1" in
      --root)                 root_element="$2"; shift 2 ;;
      --namespace)            namespace_uri="$2"; shift 2 ;;
      --child-namespace)      child_namespace_uri="$2"; shift 2 ;;
      --metadata-copies)      metadata_copies="$2"; shift 2 ;;
      --id-copies)            id_copies="$2"; shift 2 ;;
      --version-copies)       version_copies="$2"; shift 2 ;;
      --dependencies-copies)  dependencies_copies="$2"; shift 2 ;;
      --extra-metadata-block) extra_metadata_block="$2"; shift 2 ;;
      --*) fail_infrastructure "unknown nuspec shape option '$1'; the deploy dependency guard assertions did not run" ;;
      *) break ;;
    esac
  done

  local package_id="$1" package_version="$2" dependency_block="$3"

  [ -z "$namespace_uri" ] || root_attribute=" xmlns=\"$namespace_uri\""
  [ -z "$child_namespace_uri" ] || dependencies_attribute=" xmlns=\"$child_namespace_uri\""

  printf '<?xml version="1.0" encoding="utf-8"?>\n'
  printf '<%s%s>\n' "$root_element" "$root_attribute"
  if [ "$metadata_copies" -gt 1 ]; then
    printf '  <metadata>\n'
    printf '    <id>%s</id>\n' "$decoy_package_id"
    printf '    <version>%s</version>\n' "$decoy_package_version"
    printf '    <description>decoy metadata</description>\n'
    printf '    <dependencies />\n'
    printf '  </metadata>\n'
  fi
  printf '  <metadata>\n'
  [ "$id_copies" -le 1 ] || printf '    <id>%s</id>\n' "$decoy_package_id"
  printf '    <id>%s</id>\n' "$package_id"
  [ "$version_copies" -le 1 ] || printf '    <version>%s</version>\n' "$decoy_package_version"
  printf '    <version>%s</version>\n' "$package_version"
  printf '    <description>deploy-dependency-guard fixture</description>\n'
  [ -z "$extra_metadata_block" ] || printf '%s\n' "$extra_metadata_block"
  [ "$dependencies_copies" -le 1 ] || printf '    <dependencies />\n'
  printf '    <dependencies%s>\n' "$dependencies_attribute"
  [ -z "$dependency_block" ] || printf '%s\n' "$dependency_block"
  printf '    </dependencies>\n'
  printf '  </metadata>\n'
  printf '</%s>\n' "$root_element"
}

write_nuspec() {
  # Reproduces the shape `dotnet pack` emits for a multi-targeted Chatter module: one <group> per
  # target framework, each repeating the identical dependency set. The real
  # Chatter.MessageBrokers.SqlServiceBroker 0.14.2 nuspec declares Chatter.MessageBrokers 0.29.0 in
  # both its net8.0 and net10.0 groups, so a guard that does not dedupe queries the feed twice.
  #
  # Leading `--option value` pairs are document-shape options, forwarded verbatim to
  # emit_nuspec_document, so an archive-level fixture can also vary root, namespace or cardinality.
  local shape_options=()
  while [ "$#" -gt 0 ]; do
    case "$1" in
      --*) shape_options+=("$1" "$2"); shift 2 ;;
      *) break ;;
    esac
  done

  local nuspec_path="$1" package_id="$2" package_version="$3"
  shift 3
  local dependencies=("$@")
  local target_framework dependency dependency_id dependency_version dependency_block

  dependency_block="$(
    for target_framework in net8.0 net10.0; do
      printf '      <group targetFramework="%s">\n' "$target_framework"
      # Every fixture carries a non-Chatter dependency, so the `Chatter.` prefix filter has
      # something it must exclude rather than trivially matching everything in the nuspec.
      printf '        <dependency id="Microsoft.Extensions.Logging.Abstractions" version="8.0.3" exclude="Build,Analyzers" />\n'
      for dependency in ${dependencies[@]+"${dependencies[@]}"}; do
        dependency_id="${dependency%%:*}"
        dependency_version="${dependency#*:}"
        printf '        <dependency id="%s" version="%s" exclude="Build,Analyzers" />\n' \
          "$dependency_id" "$dependency_version"
      done
      printf '      </group>\n'
    done
  )"

  emit_nuspec_document ${shape_options[@]+"${shape_options[@]}"} \
    "$package_id" "$package_version" "$dependency_block" >"$nuspec_path"
}

create_nupkg() {
  # `zip` is not certain to be installed on every runner image, while `python3` is (the feed stub
  # needs it regardless), so the zipfile fallback is specified rather than discovered in CI.
  local package_dir="$1" package_id="$2" package_version="$3"
  shift 3
  local stage_dir nuspec_name nupkg_path

  stage_dir="$work_dir/stage-$package_id-$package_version"
  nuspec_name="$package_id.nuspec"
  nupkg_path="$package_dir/$package_id.$package_version.nupkg"
  mkdir -p "$stage_dir" "$package_dir"
  write_nuspec "$stage_dir/$nuspec_name" "$package_id" "$package_version" "$@"

  if command -v zip >/dev/null 2>&1; then
    ( cd "$stage_dir" && zip -q "$nupkg_path" "$nuspec_name" )
  else
    python3 -c 'import sys, zipfile
with zipfile.ZipFile(sys.argv[1], "w") as archive:
    archive.write(sys.argv[2], sys.argv[3])' "$nupkg_path" "$stage_dir/$nuspec_name" "$nuspec_name"
  fi
  printf '%s' "$nupkg_path"
}

pack_nupkg_entries() {
  # Packs an arbitrary entry set into the archive at $1; the remaining arguments are (entry name,
  # source path) pairs. `zip` names an entry after its source file, and these fixtures need
  # archives holding zero or two root `.nuspec` entries, so python3's zipfile is used here
  # unconditionally rather than as a fallback.
  local nupkg_path="$1"
  shift
  mkdir -p "$(dirname "$nupkg_path")"
  python3 -c 'import sys, zipfile
with zipfile.ZipFile(sys.argv[1], "w") as archive:
    entries = sys.argv[2:]
    for position in range(0, len(entries), 2):
        archive.write(entries[position + 1], entries[position])' "$nupkg_path" "$@"
}

create_nupkg_from_nuspec_text() {
  # Packs one nupkg whose nuspec is the literal bytes given, so a fixture can carry a nuspec shape
  # `write_nuspec` never emits — reordered attributes, a namespace prefix, a truncated document.
  # `write_nuspec` stays the well-formed path every other case is built on.
  local package_dir="$1" fixture_slug="$2" package_id="$3" package_version="$4" nuspec_text="$5"
  local stage_dir nuspec_name nupkg_path

  stage_dir="$work_dir/stage-$fixture_slug"
  nuspec_name="$package_id.nuspec"
  nupkg_path="$package_dir/$package_id.$package_version.nupkg"
  mkdir -p "$stage_dir" "$package_dir"
  printf '%s' "$nuspec_text" >"$stage_dir/$nuspec_name"
  pack_nupkg_entries "$nupkg_path" "$nuspec_name" "$stage_dir/$nuspec_name"
  printf '%s' "$nupkg_path"
}

create_nupkg_from_nuspec_file() {
  # Packs one nupkg whose nuspec is the given file's bytes, copied rather than routed through a
  # shell variable. `$(cat …)` strips trailing newlines and a shell variable is a poor carrier for
  # a byte-exact document, and byte-exactness is the entire point of the checked-in real nuspec:
  # its UTF-8 BOM and its two target-framework groups are the fixture.
  local package_dir="$1" fixture_slug="$2" package_id="$3" package_version="$4" nuspec_source="$5"
  local stage_dir nuspec_name nupkg_path

  stage_dir="$work_dir/stage-$fixture_slug"
  nuspec_name="$package_id.nuspec"
  nupkg_path="$package_dir/$package_id.$package_version.nupkg"
  mkdir -p "$stage_dir" "$package_dir"
  cp "$nuspec_source" "$stage_dir/$nuspec_name"
  pack_nupkg_entries "$nupkg_path" "$nuspec_name" "$stage_dir/$nuspec_name"
  printf '%s' "$nupkg_path"
}

shape_nuspec_document() {
  # The well-formed surround the nuspec-shape fixtures share: only the sibling dependency
  # declaration varies, so a failure names the shape rather than an unrelated difference. Every
  # shape declares Chatter.MessageBrokers 0.29.0, the version the shipped incident referenced and
  # the one the shape fixtures' index deliberately omits.
  #
  # Leading `--option value` pairs are document-shape options forwarded to emit_nuspec_document,
  # so a shape fixture can vary the root element, the namespace, or how many times an element the
  # reader looks for occurs. An empty dependency block emits the group carrying only the non-Chatter
  # dependency, which is what a document hiding its siblings somewhere the reader does not look
  # needs.
  local shape_options=()
  while [ "$#" -gt 0 ]; do
    case "$1" in
      --*) shape_options+=("$1" "$2"); shift 2 ;;
      *) break ;;
    esac
  done

  local dependency_block="$1"
  local group_block='        <dependency id="Microsoft.Extensions.Logging.Abstractions" version="8.0.3" exclude="Build,Analyzers" />'

  [ -z "$dependency_block" ] || group_block="$group_block
$dependency_block"

  emit_nuspec_document ${shape_options[@]+"${shape_options[@]}"} \
    Chatter.MessageBrokers.SqlServiceBroker 0.14.2 \
    "$(printf '      <group targetFramework="net8.0">\n%s\n      </group>' "$group_block")"
}

add_symbol_package_sibling() {
  # ./dist/nuget holds the .snupkg beside the .nupkg. A guard globbing anything looser than
  # `*.nupkg` would find two packages where one was packed.
  local nupkg_path="$1"
  cp "$nupkg_path" "${nupkg_path%.nupkg}.snupkg"
}

write_flat_container_index() {
  # The real document is a JSON object with one "versions" array of quoted version strings. The
  # quotes matter: the guard must match `"0.3.0"` and not the bare substring, or `10.3.0` matches.
  local index_path="$1"
  shift
  local versions=("$@") index position

  mkdir -p "$(dirname "$index_path")"
  {
    printf '{\n  "versions": [\n'
    for position in "${!versions[@]}"; do
      index="${versions[$position]}"
      if [ "$position" -eq $(( ${#versions[@]} - 1 )) ]; then
        printf '    "%s"\n' "$index"
      else
        printf '    "%s",\n' "$index"
      fi
    done
    printf '  ]\n}\n'
  } >"$index_path"
}

write_raw_feed_body() {
  # Writes a feed fixture that the stub returns verbatim: `<id>.json` for a literal index document
  # the array writer above cannot express, `<id>.body` for a 200 under a non-JSON content type.
  # A 200 from the real feed is a CDN response, not a promise of well-formed JSON.
  local fixture_path="$1" document_text="$2"

  mkdir -p "$(dirname "$fixture_path")"
  printf '%s' "$document_text" >"$fixture_path"
}

start_fixture_feed() {
  local fixture_dir="$1" fail_id="${2:-}" log_file="${3:-}"
  local port_file arguments waited

  stub_instance=$((stub_instance + 1))
  port_file="$work_dir/feed-port-$stub_instance"
  arguments=(--fixture-dir "$fixture_dir")
  if [ -n "$fail_id" ]; then
    arguments+=(--fail-id "$fail_id")
  fi
  if [ -n "$log_file" ]; then
    arguments+=(--log-file "$log_file")
  fi

  : >"$port_file"
  python3 "$fixture_feed" "${arguments[@]}" >"$port_file" &
  fixture_feed_pid=$!

  # The stub prints its ephemeral port once bound; reading it is also the readiness signal.
  waited=0
  while [ ! -s "$port_file" ]; do
    if [ "$waited" -ge 100 ]; then
      fail_infrastructure 'the fixture feed did not report a port within 10s; the deploy dependency guard assertions did not run'
    fi
    sleep 0.1
    waited=$((waited + 1))
  done
  fixture_feed_port="$(head -n1 "$port_file" | tr -d '\r')"
}

# --------------------------------------------------------------------------------------------
# Behavioural assertions: run the extracted guard body against the stub feed.
# --------------------------------------------------------------------------------------------

run_guard_body() {
  # Runs the extracted body with only the four documented env seams set, under `-euo pipefail`:
  # the `deploy` job has no working tree, so the guard may assume nothing beyond these.
  local package_dir="$1" base_url="$2" output_file="$3"
  local exit_code=0

  PACKAGE_DIR="$package_dir" \
  FLATCONTAINER_BASE_URL="$base_url" \
  DEPENDENCY_GUARD_TIMEOUT_SECONDS="$guard_timeout_seconds" \
  DEPENDENCY_GUARD_POLL_SECONDS="$guard_poll_seconds" \
    bash -euo pipefail "$guard_body_file" >"$output_file" 2>&1 || exit_code=$?

  printf '%s' "$exit_code"
}

print_guard_output() {
  printf '        --- guard output ---\n' >&2
  sed 's/^/        /' "$1" >&2
  printf '        --- end guard output ---\n' >&2
}

assert_guard_exit() {
  local case_name="$1" expected_exit="$2" actual_exit="$3" output_file="$4"
  if [ "$actual_exit" -eq "$expected_exit" ]; then
    report_pass "$case_name: exited $actual_exit"
    return 0
  fi
  report_fail "$case_name: expected exit $expected_exit, got $actual_exit"
  print_guard_output "$output_file"
  return 1
}

assert_output_contains() {
  local case_name="$1" needle="$2" output_file="$3"
  if grep -qF -- "$needle" "$output_file"; then
    report_pass "$case_name: message contains \"$needle\""
    return 0
  fi
  report_fail "$case_name: message does not contain \"$needle\""
  print_guard_output "$output_file"
}

assert_output_lacks() {
  local case_name="$1" needle="$2" output_file="$3"
  local reason="${4:-the never-published and version-absent failures are different operator problems and must read differently}"
  if grep -qF -- "$needle" "$output_file"; then
    report_fail "$case_name: message must not contain \"$needle\"; $reason"
    print_guard_output "$output_file"
    return 0
  fi
  report_pass "$case_name: message does not contain \"$needle\""
}

# Set by run_nuspec_shape_case and run_index_document_case. Those drivers start and stop the
# fixture feed, so they cannot be invoked in a command substitution the way run_guard_body is: the
# stub's pid would be recorded in a subshell and the EXIT trap would never reap it.
driven_case_exit=''

run_nuspec_shape_case() {
  # Packs a nupkg whose nuspec is the given literal text, against an index listing 0.28.0 and
  # 0.30.0 but never the 0.29.0 every shape fixture declares. A guard that reads the nuspec as an
  # XML document sees that dependency in every shape and fails; a guard that scans raw bytes with
  # a line-oriented text tool sees it in only some of them and publishes the rest.
  local fixture_slug="$1" nuspec_text="$2" output_file="$3"
  local case_dir="$work_dir/$fixture_slug"
  local package_dir="$case_dir/packages" fixture_dir="$case_dir/feed"

  mkdir -p "$case_dir" "$package_dir" "$fixture_dir"
  create_nupkg_from_nuspec_text "$package_dir" "$fixture_slug" \
    Chatter.MessageBrokers.SqlServiceBroker 0.14.2 "$nuspec_text" >/dev/null
  write_flat_container_index "$fixture_dir/chatter.messagebrokers.json" 0.28.0 0.30.0

  start_fixture_feed "$fixture_dir"
  driven_case_exit="$(run_guard_body "$package_dir" "http://127.0.0.1:$fixture_feed_port" "$output_file")"
  stop_fixture_feed
}

run_index_document_case() {
  # Packs a well-formed nupkg declaring Chatter.MessageBrokers 0.30.0 and answers its index query
  # with the given document. The package is never the problem in these cases: what the feed
  # returned is.
  local fixture_slug="$1" fixture_name="$2" document_text="$3" output_file="$4"
  local case_dir="$work_dir/$fixture_slug"
  local package_dir="$case_dir/packages" fixture_dir="$case_dir/feed"

  mkdir -p "$case_dir" "$package_dir" "$fixture_dir"
  create_nupkg "$package_dir" Chatter.MessageBrokers.SqlServiceBroker 0.14.3 Chatter.MessageBrokers:0.30.0 >/dev/null
  write_raw_feed_body "$fixture_dir/$fixture_name" "$document_text"

  start_fixture_feed "$fixture_dir"
  driven_case_exit="$(run_guard_body "$package_dir" "http://127.0.0.1:$fixture_feed_port" "$output_file")"
  stop_fixture_feed
}

case_all_dependencies_published() {
  local case_name='all declared dependencies published'
  local package_dir="$work_dir/case1/packages" fixture_dir="$work_dir/case1/feed"
  local request_log="$work_dir/case1/requests.log" output_file="$work_dir/case1/output.txt"
  local nupkg guard_exit query_count

  mkdir -p "$package_dir" "$fixture_dir" "$work_dir/case1"
  : >"$request_log"
  nupkg="$(create_nupkg "$package_dir" Chatter.MessageBrokers.SqlServiceBroker 0.14.3 Chatter.MessageBrokers:0.30.0)"
  add_symbol_package_sibling "$nupkg"
  write_flat_container_index "$fixture_dir/chatter.messagebrokers.json" 0.28.0 0.30.0

  start_fixture_feed "$fixture_dir" '' "$request_log"
  guard_exit="$(run_guard_body "$package_dir" "http://127.0.0.1:$fixture_feed_port" "$output_file")"
  stop_fixture_feed

  assert_guard_exit "$case_name" 0 "$guard_exit" "$output_file" || return 0

  # One query for a dependency declared in both the net8.0 and net10.0 groups: the dedupe path.
  query_count="$(grep -cFx '/chatter.messagebrokers/index.json' "$request_log" || true)"
  if [ "$query_count" -eq 1 ]; then
    report_pass "$case_name: the twice-declared dependency was queried once (deduped)"
  else
    report_fail "$case_name: expected 1 flat-container query for chatter.messagebrokers, saw $query_count; dependencies repeated across target framework groups must be deduped"
  fi
}

case_declared_version_absent() {
  local case_name='declared version absent from the index'
  local package_dir="$work_dir/case2/packages" fixture_dir="$work_dir/case2/feed"
  local output_file="$work_dir/case2/output.txt" guard_exit

  mkdir -p "$package_dir" "$fixture_dir" "$work_dir/case2"
  # The shipped incident: SqlServiceBroker 0.14.2 declared Chatter.MessageBrokers 0.29.0, whose
  # deploy was cancelled. The real index jumps "0.28.0","0.30.0" — 0.29.0 never existed.
  create_nupkg "$package_dir" Chatter.MessageBrokers.SqlServiceBroker 0.14.2 Chatter.MessageBrokers:0.29.0 >/dev/null
  write_flat_container_index "$fixture_dir/chatter.messagebrokers.json" 0.28.0 0.30.0

  start_fixture_feed "$fixture_dir"
  guard_exit="$(run_guard_body "$package_dir" "http://127.0.0.1:$fixture_feed_port" "$output_file")"
  stop_fixture_feed

  assert_guard_exit "$case_name" 1 "$guard_exit" "$output_file" || return 0
  assert_output_contains "$case_name" 'Chatter.MessageBrokers' "$output_file"
  assert_output_contains "$case_name" '0.29.0' "$output_file"
  assert_output_contains "$case_name" "$version_absent_phrase" "$output_file"
}

case_dependency_never_published() {
  local case_name='dependency id was never published'
  local package_dir="$work_dir/case3/packages" fixture_dir="$work_dir/case3/feed"
  local output_file="$work_dir/case3/output.txt" guard_exit

  mkdir -p "$package_dir" "$fixture_dir" "$work_dir/case3"
  # No fixture index for chatter.messagebrokers.sqlservicebroker, so the feed answers 404 the way
  # nuget.org does for an id that has never been published under any version.
  create_nupkg "$package_dir" Chatter.SqlChangeFeed 0.14.2 Chatter.MessageBrokers.SqlServiceBroker:0.14.2 >/dev/null
  write_flat_container_index "$fixture_dir/chatter.messagebrokers.json" 0.28.0 0.30.0

  start_fixture_feed "$fixture_dir"
  guard_exit="$(run_guard_body "$package_dir" "http://127.0.0.1:$fixture_feed_port" "$output_file")"
  stop_fixture_feed

  assert_guard_exit "$case_name" 1 "$guard_exit" "$output_file" || return 0
  assert_output_contains "$case_name" 'chatter.messagebrokers.sqlservicebroker' "$output_file"
  assert_output_contains "$case_name" "$never_published_phrase" "$output_file"
  assert_output_lacks "$case_name" "$version_absent_phrase" "$output_file"
}

case_index_returns_server_error() {
  local case_name='flat-container index returns 500'
  local package_dir="$work_dir/case4/packages" fixture_dir="$work_dir/case4/feed"
  local output_file="$work_dir/case4/output.txt" guard_exit

  mkdir -p "$package_dir" "$fixture_dir" "$work_dir/case4"
  create_nupkg "$package_dir" Chatter.MessageBrokers.SqlServiceBroker 0.14.3 Chatter.MessageBrokers:0.30.0 >/dev/null
  write_flat_container_index "$fixture_dir/chatter.messagebrokers.json" 0.28.0 0.30.0

  # The dependency IS published here. Only the feed is broken, so this must classify as
  # infrastructure (exit 2), not as an assertion failure (exit 1).
  start_fixture_feed "$fixture_dir" chatter.messagebrokers
  guard_exit="$(run_guard_body "$package_dir" "http://127.0.0.1:$fixture_feed_port" "$output_file")"
  stop_fixture_feed

  assert_guard_exit "$case_name" 2 "$guard_exit" "$output_file" || return 0
}

case_endpoint_unreachable() {
  local case_name='flat-container endpoint unreachable'
  local package_dir="$work_dir/case5/packages" output_file="$work_dir/case5/output.txt"
  local guard_exit

  mkdir -p "$package_dir" "$work_dir/case5"
  create_nupkg "$package_dir" Chatter.MessageBrokers.SqlServiceBroker 0.14.3 Chatter.MessageBrokers:0.30.0 >/dev/null

  # Port 1 is privileged and never bound by this harness: curl fails at transport, which is an
  # infrastructure outcome and must not be reported as an unpublished dependency.
  guard_exit="$(run_guard_body "$package_dir" 'http://127.0.0.1:1' "$output_file")"

  assert_guard_exit "$case_name" 2 "$guard_exit" "$output_file" || return 0
}

case_no_chatter_dependencies() {
  local case_name='nuspec declares no Chatter dependencies'
  local package_dir="$work_dir/case6/packages" output_file="$work_dir/case6/output.txt"
  local guard_exit

  mkdir -p "$package_dir" "$work_dir/case6"
  # The Chatter.CQRS shape: sibling dependency set empty, third-party dependencies present. The
  # loop must pass cleanly under `set -euo pipefail`. Pointing at an unreachable endpoint proves
  # it makes no query at all rather than passing a query by luck.
  create_nupkg "$package_dir" Chatter.CQRS 0.16.0 >/dev/null

  guard_exit="$(run_guard_body "$package_dir" 'http://127.0.0.1:1' "$output_file")"

  assert_guard_exit "$case_name" 0 "$guard_exit" "$output_file" || return 0
  # An empty sibling set must be reported as a confirmed reading of the nuspec. Silence here is
  # indistinguishable from a scan that failed to understand the document, which is precisely how a
  # guard ships an unrestorable package while printing nothing alarming.
  assert_output_contains "$case_name" "$confirmed_empty_phrase" "$output_file"
}

case_version_substring_collision() {
  local case_name='declared version is only a substring of a published version'
  local package_dir="$work_dir/case7/packages" fixture_dir="$work_dir/case7/feed"
  local output_file="$work_dir/case7/output.txt" guard_exit

  mkdir -p "$package_dir" "$fixture_dir" "$work_dir/case7"
  # `0.3.0` is a real published Chatter.MessageBrokers version, so this collision class is live.
  # An index listing 10.3.0 but not 0.3.0 passes an unquoted substring match and must fail here.
  create_nupkg "$package_dir" Chatter.MessageBrokers.SqlServiceBroker 0.14.3 Chatter.MessageBrokers:0.3.0 >/dev/null
  write_flat_container_index "$fixture_dir/chatter.messagebrokers.json" 0.28.0 0.30.0 10.3.0

  start_fixture_feed "$fixture_dir"
  guard_exit="$(run_guard_body "$package_dir" "http://127.0.0.1:$fixture_feed_port" "$output_file")"
  stop_fixture_feed

  assert_guard_exit "$case_name" 1 "$guard_exit" "$output_file" || return 0
  assert_output_contains "$case_name" '0.3.0' "$output_file"
}

# --------------------------------------------------------------------------------------------
# Nuspec-shape cases. A nuspec is an XML document, and `dotnet pack` is not the only producer of
# one: every shape below is legal XML that a line-oriented byte scan reads differently from the
# way a restoring NuGet client reads it. Each declares Chatter.MessageBrokers 0.29.0, which the
# index does not list, so any shape the guard fails to understand publishes an unrestorable
# package while exiting 0.
# --------------------------------------------------------------------------------------------

case_dependency_attributes_reversed() {
  local case_name='dependency declares version before id'
  local output_file="$work_dir/shape-reversed/output.txt" nuspec_text

  mkdir -p "$work_dir/shape-reversed"
  # Attribute order carries no meaning in XML; a pattern that requires id before version invents
  # an ordering rule the format does not have.
  nuspec_text="$(shape_nuspec_document \
    '        <dependency version="0.29.0" id="Chatter.MessageBrokers" exclude="Build,Analyzers" />')"

  run_nuspec_shape_case shape-reversed "$nuspec_text" "$output_file"

  assert_guard_exit "$case_name" 1 "$driven_case_exit" "$output_file" || return 0
  assert_output_contains "$case_name" '0.29.0' "$output_file"
}

case_dependency_attributes_wrapped_across_lines() {
  local case_name='dependency attributes wrap across lines'
  local output_file="$work_dir/shape-wrapped/output.txt" nuspec_text

  mkdir -p "$work_dir/shape-wrapped"
  # Line breaks inside a tag are insignificant whitespace. A scan that reads one element per line
  # is asserting a formatting convention, not reading the document.
  nuspec_text="$(shape_nuspec_document \
"        <dependency
          id=\"Chatter.MessageBrokers\"
          version=\"0.29.0\"
          exclude=\"Build,Analyzers\" />")"

  run_nuspec_shape_case shape-wrapped "$nuspec_text" "$output_file"

  assert_guard_exit "$case_name" 1 "$driven_case_exit" "$output_file" || return 0
  assert_output_contains "$case_name" '0.29.0' "$output_file"
}

case_namespace_prefixed_elements() {
  local case_name='nuspec uses a prefixed namespace'
  local output_file="$work_dir/shape-prefixed/output.txt" nuspec_text

  mkdir -p "$work_dir/shape-prefixed"
  # `<n:dependency>` and `<dependency>` are the same element in the same namespace. So are
  # `<n:id>` and `<id>`, which is why the failure must still name the package being published.
  nuspec_text="$(printf '%s\n' \
    '<?xml version="1.0" encoding="utf-8"?>' \
    '<n:package xmlns:n="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">' \
    '  <n:metadata>' \
    '    <n:id>Chatter.MessageBrokers.SqlServiceBroker</n:id>' \
    '    <n:version>0.14.2</n:version>' \
    '    <n:description>deploy-dependency-guard fixture</n:description>' \
    '    <n:dependencies>' \
    '      <n:group targetFramework="net8.0">' \
    '        <n:dependency id="Chatter.MessageBrokers" version="0.29.0" exclude="Build,Analyzers" />' \
    '      </n:group>' \
    '    </n:dependencies>' \
    '  </n:metadata>' \
    '</n:package>')"

  run_nuspec_shape_case shape-prefixed "$nuspec_text" "$output_file"

  assert_guard_exit "$case_name" 1 "$driven_case_exit" "$output_file" || return 0
  assert_output_contains "$case_name" 'Chatter.MessageBrokers.SqlServiceBroker' "$output_file"
  assert_output_contains "$case_name" '0.29.0' "$output_file"
}

case_commented_out_id_is_not_the_package_id() {
  local case_name='an XML comment above the metadata carries a decoy id'
  local output_file="$work_dir/shape-decoy/output.txt" nuspec_text

  mkdir -p "$work_dir/shape-decoy"
  # Comment content is not document content. A first-match byte scan reports the failure against a
  # package that is not being published, sending the operator to the wrong pipeline.
  nuspec_text="$(printf '%s\n' \
    '<?xml version="1.0" encoding="utf-8"?>' \
    '<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">' \
    '  <!-- <id>DECOY</id> -->' \
    '  <metadata>' \
    '    <id>Chatter.MessageBrokers.SqlServiceBroker</id>' \
    '    <version>0.14.2</version>' \
    '    <description>deploy-dependency-guard fixture</description>' \
    '    <dependencies>' \
    '      <group targetFramework="net8.0">' \
    '        <dependency id="Chatter.MessageBrokers" version="0.29.0" exclude="Build,Analyzers" />' \
    '      </group>' \
    '    </dependencies>' \
    '  </metadata>' \
    '</package>')"

  run_nuspec_shape_case shape-decoy "$nuspec_text" "$output_file"

  assert_guard_exit "$case_name" 1 "$driven_case_exit" "$output_file" || return 0
  assert_output_contains "$case_name" 'Chatter.MessageBrokers.SqlServiceBroker' "$output_file"
  assert_output_lacks "$case_name" 'DECOY' "$output_file" \
    'a commented-out element is not the package identity and must never be reported as it'
}

case_dependencies_without_groups() {
  local case_name='dependencies are declared without target framework groups'
  local output_file="$work_dir/shape-ungrouped/output.txt" nuspec_text

  mkdir -p "$work_dir/shape-ungrouped"
  # The flat form a single-targeted package packs. Passing today; pinned so the parse replacing
  # the byte scan does not narrow to the grouped form alone.
  nuspec_text="$(printf '%s\n' \
    '<?xml version="1.0" encoding="utf-8"?>' \
    '<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">' \
    '  <metadata>' \
    '    <id>Chatter.MessageBrokers.SqlServiceBroker</id>' \
    '    <version>0.14.2</version>' \
    '    <description>deploy-dependency-guard fixture</description>' \
    '    <dependencies>' \
    '      <dependency id="Microsoft.Extensions.Logging.Abstractions" version="8.0.3" />' \
    '      <dependency id="Chatter.MessageBrokers" version="0.29.0" />' \
    '    </dependencies>' \
    '  </metadata>' \
    '</package>')"

  run_nuspec_shape_case shape-ungrouped "$nuspec_text" "$output_file"

  assert_guard_exit "$case_name" 1 "$driven_case_exit" "$output_file" || return 0
  assert_output_contains "$case_name" '0.29.0' "$output_file"
}

case_dependency_in_open_close_form() {
  local case_name='dependency is written in open/close rather than self-closing form'
  local output_file="$work_dir/shape-openclose/output.txt" nuspec_text

  mkdir -p "$work_dir/shape-openclose"
  # Equivalent to the self-closing form. Passing today; pinned as a regression.
  nuspec_text="$(shape_nuspec_document \
    '        <dependency id="Chatter.MessageBrokers" version="0.29.0"></dependency>')"

  run_nuspec_shape_case shape-openclose "$nuspec_text" "$output_file"

  assert_guard_exit "$case_name" 1 "$driven_case_exit" "$output_file" || return 0
  assert_output_contains "$case_name" '0.29.0' "$output_file"
}

case_chatter_dependency_without_version() {
  local case_name='a Chatter dependency declares no version'
  local output_file="$work_dir/shape-noversion/output.txt" nuspec_text

  mkdir -p "$work_dir/shape-noversion"
  # A sibling dependency with no version range cannot be checked for publication, so its status is
  # unknown — an infrastructure outcome. Dropping it silently is the fail-open this guard exists
  # to prevent.
  nuspec_text="$(shape_nuspec_document \
    '        <dependency id="Chatter.MessageBrokers" exclude="Build,Analyzers" />')"

  run_nuspec_shape_case shape-noversion "$nuspec_text" "$output_file"

  assert_guard_exit "$case_name" 2 "$driven_case_exit" "$output_file" || return 0
  assert_output_contains "$case_name" 'Chatter.MessageBrokers' "$output_file"
}

case_nuspec_is_truncated() {
  local case_name='the packed nuspec is truncated'
  local output_file="$work_dir/shape-truncated/output.txt" nuspec_text

  mkdir -p "$work_dir/shape-truncated"
  # A nuspec that will not parse says nothing about the dependency set. Treating "I could not read
  # it" as "it declares nothing" is the same fail-open with a different cause.
  nuspec_text="$(printf '%s\n' \
    '<?xml version="1.0" encoding="utf-8"?>' \
    '<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">' \
    '  <metadata>' \
    '    <id>Chatter.MessageBrokers.SqlServiceBroker</id>' \
    '    <version>0.14.2</version>' \
    '    <dependencies>' \
    '      <group targetFramework="net8.0">' \
    '        <dependency id="Chatter.Mess')"

  run_nuspec_shape_case shape-truncated "$nuspec_text" "$output_file"

  assert_guard_exit "$case_name" 2 "$driven_case_exit" "$output_file" || return 0
}

case_nupkg_holds_two_nuspec_entries() {
  local case_name='the nupkg holds two root .nuspec entries'
  local case_dir="$work_dir/shape-two-nuspecs"
  local package_dir="$case_dir/packages" fixture_dir="$case_dir/feed"
  local stage_dir="$case_dir/stage" output_file="$case_dir/output.txt"
  local nupkg_path guard_exit

  mkdir -p "$package_dir" "$fixture_dir" "$stage_dir"
  # Which of the two is the manifest is undecidable, so the archive is ambiguous and must be
  # refused. Both declare a published version, so a guard that concatenates them reads one clean
  # dependency set and publishes.
  write_nuspec "$stage_dir/Chatter.MessageBrokers.SqlServiceBroker.nuspec" \
    Chatter.MessageBrokers.SqlServiceBroker 0.14.3 Chatter.MessageBrokers:0.30.0
  write_nuspec "$stage_dir/Chatter.MessageBrokers.SqlServiceBroker.Extra.nuspec" \
    Chatter.MessageBrokers.SqlServiceBroker 0.14.3 Chatter.MessageBrokers:0.30.0
  nupkg_path="$package_dir/Chatter.MessageBrokers.SqlServiceBroker.0.14.3.nupkg"
  pack_nupkg_entries "$nupkg_path" \
    'Chatter.MessageBrokers.SqlServiceBroker.nuspec' "$stage_dir/Chatter.MessageBrokers.SqlServiceBroker.nuspec" \
    'Chatter.MessageBrokers.SqlServiceBroker.Extra.nuspec' "$stage_dir/Chatter.MessageBrokers.SqlServiceBroker.Extra.nuspec"
  write_flat_container_index "$fixture_dir/chatter.messagebrokers.json" 0.28.0 0.30.0

  start_fixture_feed "$fixture_dir"
  guard_exit="$(run_guard_body "$package_dir" "http://127.0.0.1:$fixture_feed_port" "$output_file")"
  stop_fixture_feed

  assert_guard_exit "$case_name" 2 "$guard_exit" "$output_file" || return 0
}

case_nupkg_holds_no_nuspec_entry() {
  local case_name='the nupkg holds no .nuspec entry'
  local case_dir="$work_dir/shape-no-nuspec"
  local package_dir="$case_dir/packages" stage_dir="$case_dir/stage"
  local output_file="$case_dir/output.txt" nupkg_path guard_exit

  mkdir -p "$package_dir" "$stage_dir"
  # A nupkg with no manifest is not a package. The endpoint is unreachable so a guard that reaches
  # the feed at all fails this case on that ground too; the point is that it must refuse before
  # ever getting there, with its own diagnosis rather than an archiver's exit code.
  printf '%s\n' '<?xml version="1.0" encoding="utf-8"?><Types />' >"$stage_dir/content-types.xml"
  nupkg_path="$package_dir/Chatter.MessageBrokers.SqlServiceBroker.0.14.3.nupkg"
  pack_nupkg_entries "$nupkg_path" '[Content_Types].xml' "$stage_dir/content-types.xml"

  guard_exit="$(run_guard_body "$package_dir" 'http://127.0.0.1:1' "$output_file")"

  assert_guard_exit "$case_name" 2 "$guard_exit" "$output_file" || return 0
}

# --------------------------------------------------------------------------------------------
# Index-document cases. A 200 from the flat-container is a CDN response, not a guarantee of a JSON
# document listing versions. Deciding publication by searching the raw response for a quoted
# version string answers "does this text appear anywhere in whatever came back", which is a
# different question from "is this version published".
# --------------------------------------------------------------------------------------------

case_index_returns_non_json_body() {
  local case_name='index answers 200 with a non-JSON body'
  local output_file="$work_dir/index-html/output.txt" document_text

  mkdir -p "$work_dir/index-html"
  # An HTML error page that happens to quote the version. Publication status is unknown here, and
  # unknown is an infrastructure outcome — never a green light.
  document_text="$(printf '%s\n' \
    '<!DOCTYPE html>' \
    '<html><head><title>503 Service Unavailable</title></head>' \
    '<body><h1>Origin unavailable</h1>' \
    '<p>The request for version "0.30.0" could not be served.</p>' \
    '</body></html>')"

  run_index_document_case index-html chatter.messagebrokers.body "$document_text" "$output_file"

  assert_guard_exit "$case_name" 2 "$driven_case_exit" "$output_file" || return 0
}

case_index_mentions_version_outside_versions_array() {
  local case_name='index mentions the version outside the versions array'
  local output_file="$work_dir/index-note/output.txt" document_text

  mkdir -p "$work_dir/index-note"
  # The version appears in the document, quoted, twice — and in neither place is it a member of
  # "versions". Only membership of that one list means published. The quoted occurrence inside
  # "cancelled" is what a raw text search finds, and it is the opposite of published.
  document_text='{"versions":["0.28.0"],"note":"0.30.0 was cancelled","cancelled":["0.30.0"]}'

  run_index_document_case index-note chatter.messagebrokers.json "$document_text" "$output_file"

  assert_guard_exit "$case_name" 1 "$driven_case_exit" "$output_file" || return 0
  assert_output_contains "$case_name" '0.30.0' "$output_file"
  assert_output_contains "$case_name" "$version_absent_phrase" "$output_file"
}

case_index_lacks_versions_key() {
  local case_name='index answers 200 with JSON carrying no versions key'
  local output_file="$work_dir/index-nokey/output.txt" document_text

  mkdir -p "$work_dir/index-nokey"
  # Valid JSON, wrong document. Nothing about publication can be concluded, so this is
  # infrastructure, not a verdict that the version is absent.
  document_text='{"note":"this is not a flat-container index"}'

  run_index_document_case index-nokey chatter.messagebrokers.json "$document_text" "$output_file"

  assert_guard_exit "$case_name" 2 "$driven_case_exit" "$output_file" || return 0
}

case_index_versions_is_not_a_string_list() {
  local case_name='index versions is not a list of strings'
  local output_file="$work_dir/index-shape/output.txt" document_text

  mkdir -p "$work_dir/index-shape"
  # The key is present and the version appears as an object key rather than a list entry. A raw
  # text search cannot tell the two apart and reads this as published.
  document_text='{"versions":{"0.30.0":true}}'

  run_index_document_case index-shape chatter.messagebrokers.json "$document_text" "$output_file"

  assert_guard_exit "$case_name" 2 "$driven_case_exit" "$output_file" || return 0
}

case_poll_budget_bounds_total_wall_time() {
  local case_name='the poll budget bounds total wall time to one budget, not one per dependency'
  local case_dir="$work_dir/budget"
  local package_dir="$case_dir/packages" fixture_dir="$case_dir/feed"
  local output_file="$case_dir/output.txt"
  local guard_exit start_ns end_ns elapsed_ms budget_ms tolerance_ms

  mkdir -p "$package_dir" "$fixture_dir" "$case_dir"
  # Two absent siblings and a budget of two seconds.
  #
  # The guard is deliberately FAIL-FAST: on the first absent (or never-published, or unreachable)
  # sibling it exits inside that dependency's `case` statement, so a later sibling is never
  # reached and its query count cannot be observed — counting queries against a fail-fast guard is
  # unobservable by construction, which is why this case is asserted on wall time instead. Staying
  # fail-fast is the accepted design (docs/adr/0019): it blocks the publish exactly as well as
  # evaluating every sibling would, every module today declares exactly one `Chatter.*` sibling,
  # and redesigning the failure shape to let a test count a second query would be the tail wagging
  # the dog.
  #
  # The property genuinely worth pinning is that the poll deadline is computed once for the whole
  # run, not reset per dependency, so the worst case this guard can ever spend is bounded by ONE
  # budget. A future regression that recomputed the deadline per dependency — combined with the
  # guard ceasing to be fail-fast, since fail-fast alone can never reach a second dependency at
  # all — would silently turn that worst case into N times the budget, which is a wall-clock
  # symptom this case would catch even though it cannot isolate which half of the regression
  # caused it.
  create_nupkg "$package_dir" Chatter.SqlChangeFeed 0.15.0 \
    Chatter.CQRS:0.29.0 Chatter.MessageBrokers:0.29.0 >/dev/null
  write_flat_container_index "$fixture_dir/chatter.cqrs.json" 0.16.0
  write_flat_container_index "$fixture_dir/chatter.messagebrokers.json" 0.28.0 0.30.0

  start_fixture_feed "$fixture_dir"
  start_ns="$(date +%s%N)"
  guard_exit="$(run_guard_body "$package_dir" "http://127.0.0.1:$fixture_feed_port" "$output_file")"
  end_ns="$(date +%s%N)"
  stop_fixture_feed

  assert_guard_exit "$case_name" 1 "$guard_exit" "$output_file" || return 0

  elapsed_ms=$(( (end_ns - start_ns) / 1000000 ))
  budget_ms=$(( guard_timeout_seconds * 1000 ))
  # 1.5x the budget: generous enough that poll-interval granularity and a loaded machine cannot
  # flake it (a passing run sits close to 1x the budget), yet tight enough that a per-dependency
  # deadline — which approaches 2x the budget in this two-dependency fixture — still trips it with
  # a wide margin on both sides.
  tolerance_ms=$(( budget_ms + budget_ms / 2 ))
  if [ "$elapsed_ms" -le "$tolerance_ms" ]; then
    report_pass "$case_name: elapsed ${elapsed_ms}ms stayed within one budget (${budget_ms}ms) plus tolerance"
  else
    report_fail "$case_name: elapsed ${elapsed_ms}ms exceeded one budget (${budget_ms}ms) plus tolerance (${tolerance_ms}ms); a per-dependency deadline would approach 2x the budget instead of sharing one"
  fi
}

# --------------------------------------------------------------------------------------------
# Permissive-selection cases. Each document below is well-formed XML that admits more than one
# reading, and each fact the guard decides on is read from a position it chose permissively: a root
# element it never checked, a namespace it never checked, or the FIRST of an element that occurs
# more than once. When a document admits several readings, the one yielding the fewest dependencies
# is indistinguishable from a correct one — and that reading is the one that publishes. None of
# these documents can be answered honestly, so every one of them must be refused (exit 2) rather
# than answered from a guess.
# --------------------------------------------------------------------------------------------

case_root_element_is_not_package() {
  local case_name='the nuspec root element is not <package>'
  local output_file="$work_dir/shape-foreign-root/output.txt" nuspec_text

  mkdir -p "$work_dir/shape-foreign-root"
  # A document whose root is not <package> is not a manifest, whatever its children are named. A
  # reader that descends straight to <metadata> never asks what document it is holding, so any file
  # carrying the right child names is read as one.
  nuspec_text="$(shape_nuspec_document --root notpackage \
    '        <dependency id="Chatter.MessageBrokers" version="0.29.0" exclude="Build,Analyzers" />')"

  run_nuspec_shape_case shape-foreign-root "$nuspec_text" "$output_file"

  assert_guard_exit "$case_name" 2 "$driven_case_exit" "$output_file" || return 0
}

case_metadata_is_duplicated() {
  local case_name='the nuspec carries two <metadata> elements, a decoy first'
  local output_file="$work_dir/shape-two-metadata/output.txt" nuspec_text

  mkdir -p "$work_dir/shape-two-metadata"
  # Which <metadata> is the package's is undecidable, so the document is ambiguous. Taking the first
  # is a choice, not a reading: the decoy declares no dependencies, so the whole sibling set of the
  # second one disappears and the guard reports a package identity that is not being published.
  nuspec_text="$(shape_nuspec_document --metadata-copies 2 \
    '        <dependency id="Chatter.MessageBrokers" version="0.29.0" exclude="Build,Analyzers" />')"

  run_nuspec_shape_case shape-two-metadata "$nuspec_text" "$output_file"

  assert_guard_exit "$case_name" 2 "$driven_case_exit" "$output_file" || return 0
}

case_dependencies_is_duplicated() {
  local case_name='the nuspec carries two <dependencies> elements, an empty one first'
  local output_file="$work_dir/shape-two-dependencies/output.txt" nuspec_text

  mkdir -p "$work_dir/shape-two-dependencies"
  # The headline of this class, and the one a root-element check alone does not catch: this document
  # has the correct root, one uniform namespace and exactly one <metadata>. NuGet's own
  # NuspecReader.GetDependencyGroups() enumerates EVERY <dependencies> under the metadata node and
  # unions the groups, so a restoring client sees Chatter.MessageBrokers 0.29.0 here. A reader that
  # takes the first sees an empty element, concludes the package declares nothing, and publishes.
  nuspec_text="$(shape_nuspec_document --dependencies-copies 2 \
    '        <dependency id="Chatter.MessageBrokers" version="0.29.0" exclude="Build,Analyzers" />')"

  run_nuspec_shape_case shape-two-dependencies "$nuspec_text" "$output_file"

  assert_guard_exit "$case_name" 2 "$driven_case_exit" "$output_file" || return 0
}

case_id_is_duplicated() {
  local case_name='the nuspec carries two <id> elements, a decoy first'
  local output_file="$work_dir/shape-two-ids/output.txt" nuspec_text

  mkdir -p "$work_dir/shape-two-ids"
  # The identity the guard prints is the identity an operator acts on. Read from the first of two,
  # it names a package that is not being published and sends them to the wrong pipeline.
  nuspec_text="$(shape_nuspec_document --id-copies 2 \
    '        <dependency id="Chatter.MessageBrokers" version="0.29.0" exclude="Build,Analyzers" />')"

  run_nuspec_shape_case shape-two-ids "$nuspec_text" "$output_file"

  assert_guard_exit "$case_name" 2 "$driven_case_exit" "$output_file" || return 0
}

case_version_is_duplicated() {
  local case_name='the nuspec carries two <version> elements, a decoy first'
  local output_file="$work_dir/shape-two-versions/output.txt" nuspec_text

  mkdir -p "$work_dir/shape-two-versions"
  # Same ambiguity on the other half of the identity: the version reported is the one the operator
  # goes looking for in the feed afterwards.
  nuspec_text="$(shape_nuspec_document --version-copies 2 \
    '        <dependency id="Chatter.MessageBrokers" version="0.29.0" exclude="Build,Analyzers" />')"

  run_nuspec_shape_case shape-two-versions "$nuspec_text" "$output_file"

  assert_guard_exit "$case_name" 2 "$driven_case_exit" "$output_file" || return 0
}

case_document_namespace_is_foreign() {
  local case_name='every element sits in a namespace that is not a nuspec schema'
  local output_file="$work_dir/shape-foreign-namespace/output.txt" nuspec_text

  mkdir -p "$work_dir/shape-foreign-namespace"
  # Matching on local names alone makes the namespace URI decorative, so `{urn:something-else}id`
  # and `{…/2013/05…}id` are read as the same element. They are not: a namespace is part of an
  # element's name, and a document in a foreign namespace is a different document that happens to
  # spell its elements the same way.
  nuspec_text="$(shape_nuspec_document --namespace 'http://example.invalid/not-a-nuspec-schema' \
    '        <dependency id="Chatter.MessageBrokers" version="0.29.0" exclude="Build,Analyzers" />')"

  run_nuspec_shape_case shape-foreign-namespace "$nuspec_text" "$output_file"

  assert_guard_exit "$case_name" 2 "$driven_case_exit" "$output_file" || return 0
}

case_dependencies_namespace_is_foreign() {
  local case_name='the <dependencies> subtree redeclares a foreign namespace'
  local output_file="$work_dir/shape-mixed-namespace/output.txt" nuspec_text

  mkdir -p "$work_dir/shape-mixed-namespace"
  # A correct root with one subtree moved into another namespace. NuGet resolves <dependencies>
  # against the ROOT's namespace and would find none here, while a local-name reader finds the
  # sibling — the two disagree about what this package declares, which is precisely a document that
  # cannot be answered honestly. This is also the distinction the prefixed-namespace fixture must
  # not blur: there, `n:package` EXPANDS to the same 2013/05 URI as every child, and the document is
  # uniform; here the spelling looks uniform and the expanded URIs differ.
  nuspec_text="$(shape_nuspec_document --child-namespace 'http://example.invalid/not-a-nuspec-schema' \
    '        <dependency id="Chatter.MessageBrokers" version="0.29.0" exclude="Build,Analyzers" />')"

  run_nuspec_shape_case shape-mixed-namespace "$nuspec_text" "$output_file"

  assert_guard_exit "$case_name" 2 "$driven_case_exit" "$output_file" || return 0
}

case_dependencies_under_unmodelled_element() {
  local case_name='a modelled element name recurs at a position the reader never visits'
  local output_file="$work_dir/shape-dependency-groups/output.txt"
  local nuspec_text unmodelled_block

  mkdir -p "$work_dir/shape-dependency-groups"
  # This is not a probe of <dependencyGroups> itself - the reader never asks whether that name
  # is known. Its <group> and <dependency> descendants are the modelled names the census watches
  # for; because the reader's walk never visits them, the census meets them unmodelled and raises.
  # An unmodelled container has no bearing on the outcome here - only its modelled-named children
  # do, at a position the walk didn't reach.
  unmodelled_block="$(printf '%s\n' \
    '    <dependencyGroups>' \
    '      <group targetFramework="net8.0">' \
    '        <dependency id="Chatter.MessageBrokers" version="0.29.0" exclude="Build,Analyzers" />' \
    '      </group>' \
    '    </dependencyGroups>')"
  nuspec_text="$(shape_nuspec_document --extra-metadata-block "$unmodelled_block" '')"

  run_nuspec_shape_case shape-dependency-groups "$nuspec_text" "$output_file"

  assert_guard_exit "$case_name" 2 "$driven_case_exit" "$output_file" || return 0
}

case_unmodelled_container_with_only_unmodelled_children() {
  local case_name='an unmodelled container holding only unmodelled-named children exits 0'
  local output_file="$work_dir/shape-unmodelled-only/output.txt"
  local nuspec_text unmodelled_block

  mkdir -p "$work_dir/shape-unmodelled-only"
  # PINNED KNOWN GAP, tracked as https://github.com/brenpike/Chatter/issues/476. The census
  # (assert_declarations_are_all_modelled) is a relocated-modelled-name rule, not an
  # unmodelled-element rule: it only raises when an unvisited element spells one of the six
  # MODELLED_LOCAL_NAMES. <packageDependencies>/<requires> spell neither, so the walk skips them
  # silently and the guard exits 0. This is not reachable under today's NuGet schema - restore reads
  # dependencies only from <metadata><dependencies>, so a document shaped like this declares
  # nothing NuGet would restore - which is why the gap is accepted rather than fixed now. When the
  # deferred allowlist rework lands, this assertion is expected to FLIP to exit 2; invert it then,
  # never delete it.
  unmodelled_block='    <packageDependencies><requires package="Chatter.CQRS" atLeast="0.16.0" /></packageDependencies>'
  nuspec_text="$(shape_nuspec_document --extra-metadata-block "$unmodelled_block" '')"

  run_nuspec_shape_case shape-unmodelled-only "$nuspec_text" "$output_file"

  assert_guard_exit "$case_name" 0 "$driven_case_exit" "$output_file" || return 0
}

case_sole_nuspec_entry_is_not_at_the_archive_root() {
  local case_name='the only .nuspec entry sits under a backslash-separated path'
  local case_dir="$work_dir/shape-backslash-entry"
  local package_dir="$case_dir/packages" fixture_dir="$case_dir/feed"
  local stage_dir="$case_dir/stage" output_file="$case_dir/output.txt"
  local nupkg_path guard_exit

  mkdir -p "$package_dir" "$fixture_dir" "$stage_dir"
  # NuGet's PackageHelper.IsManifest is `IsRoot && IsNuspec`, and IsRoot rejects a `\` separator as
  # well as a `/` one, because a zip entry name may carry either. An archive filter that excludes
  # only `/` therefore admits an entry NuGet would never read as the manifest, and this archive has
  # no manifest at its root at all. The decoy declares a published version, so today this archive
  # is answered from a document the restoring client will never see, and it publishes.
  #
  # The entry name reaches the archive verbatim because this harness runs on a POSIX runner, where
  # `zipfile` only rewrites os.sep: on Windows the same call would store `sub/decoy.nuspec` and the
  # case would silently degrade into the `/` one the guard already refuses.
  write_nuspec "$stage_dir/decoy.nuspec" \
    Chatter.MessageBrokers.SqlServiceBroker 0.14.3 Chatter.MessageBrokers:0.30.0
  nupkg_path="$package_dir/Chatter.MessageBrokers.SqlServiceBroker.0.14.3.nupkg"
  pack_nupkg_entries "$nupkg_path" 'sub\decoy.nuspec' "$stage_dir/decoy.nuspec"
  write_flat_container_index "$fixture_dir/chatter.messagebrokers.json" 0.28.0 0.30.0

  start_fixture_feed "$fixture_dir"
  guard_exit="$(run_guard_body "$package_dir" "http://127.0.0.1:$fixture_feed_port" "$output_file")"
  stop_fixture_feed

  assert_guard_exit "$case_name" 2 "$guard_exit" "$output_file" || return 0
}

case_nupkg_holds_two_entries_of_one_nuspec_name() {
  local case_name='the nupkg holds two entries under one root .nuspec name'
  local case_dir="$work_dir/shape-duplicate-entry-name"
  local package_dir="$case_dir/packages" fixture_dir="$case_dir/feed"
  local stage_dir="$case_dir/stage" output_file="$case_dir/output.txt"
  local nupkg_path guard_exit

  mkdir -p "$package_dir" "$fixture_dir" "$stage_dir"
  # A zip archive may carry the same entry name twice: `namelist()` returns both, while `read(name)`
  # hands back whichever the central directory resolves to. The two copies here declare different
  # sibling versions — one published, one not — so the verdict genuinely depends on which copy is
  # read, and an archive whose manifest is decided that way must be refused rather than answered.
  write_nuspec "$stage_dir/published.nuspec" \
    Chatter.MessageBrokers.SqlServiceBroker 0.14.3 Chatter.MessageBrokers:0.30.0
  write_nuspec "$stage_dir/absent.nuspec" \
    Chatter.MessageBrokers.SqlServiceBroker 0.14.3 Chatter.MessageBrokers:0.29.0
  nupkg_path="$package_dir/Chatter.MessageBrokers.SqlServiceBroker.0.14.3.nupkg"
  # zipfile warns on a duplicate entry name. The duplicate IS this fixture, so the warning is
  # silenced for this one pack rather than left to read as harness noise.
  (
    export PYTHONWARNINGS='ignore::UserWarning'
    pack_nupkg_entries "$nupkg_path" \
      'Chatter.MessageBrokers.SqlServiceBroker.nuspec' "$stage_dir/published.nuspec" \
      'Chatter.MessageBrokers.SqlServiceBroker.nuspec' "$stage_dir/absent.nuspec"
  )
  write_flat_container_index "$fixture_dir/chatter.messagebrokers.json" 0.28.0 0.30.0

  start_fixture_feed "$fixture_dir"
  guard_exit="$(run_guard_body "$package_dir" "http://127.0.0.1:$fixture_feed_port" "$output_file")"
  stop_fixture_feed

  assert_guard_exit "$case_name" 2 "$guard_exit" "$output_file" || return 0
}

# --------------------------------------------------------------------------------------------
# Shapes a real nuspec is allowed to take. These pass today and must keep passing: refusing an
# ambiguous document is only worth anything if the documents NuGet actually produces are still read.
# --------------------------------------------------------------------------------------------

case_nuspec_carries_no_namespace() {
  local case_name='the nuspec declares no namespace at all'
  local output_file="$work_dir/shape-no-namespace/output.txt" nuspec_text

  mkdir -p "$work_dir/shape-no-namespace"
  # Legacy nuspecs carry no xmlns, and NuGet reads them. A namespace check that demands a schema URI
  # rather than accepting the documented set plus none would reject packages that restore today.
  nuspec_text="$(shape_nuspec_document --namespace '' \
    '        <dependency id="Chatter.MessageBrokers" version="0.29.0" exclude="Build,Analyzers" />')"

  run_nuspec_shape_case shape-no-namespace "$nuspec_text" "$output_file"

  assert_guard_exit "$case_name" 1 "$driven_case_exit" "$output_file" || return 0
  assert_output_contains "$case_name" '0.29.0' "$output_file"
}

case_every_manifest_schema_namespace_is_read() {
  local schema_version namespace_uri case_name fixture_slug output_file nuspec_text

  # NuGet's ManifestSchemaUtility accepts SIX schema namespaces, and a package keeps whichever one
  # was current when it was packed. `2011/10` is missing from most commonly-cited lists and is
  # included here deliberately: a namespace check built from a four- or five-entry list would reject
  # a document NuGet restores.
  for schema_version in 2010/07 2011/08 2011/10 2012/06 2013/01 2013/05; do
    namespace_uri="http://schemas.microsoft.com/packaging/$schema_version/nuspec.xsd"
    case_name="nuspec declares the $schema_version schema namespace"
    fixture_slug="shape-ns-${schema_version//\//-}"
    output_file="$work_dir/$fixture_slug/output.txt"

    mkdir -p "$work_dir/$fixture_slug"
    nuspec_text="$(shape_nuspec_document --namespace "$namespace_uri" \
      '        <dependency id="Chatter.MessageBrokers" version="0.29.0" exclude="Build,Analyzers" />')"

    run_nuspec_shape_case "$fixture_slug" "$nuspec_text" "$output_file"

    assert_guard_exit "$case_name" 1 "$driven_case_exit" "$output_file" || continue
  done
}

case_real_packed_nuspec_sibling_absent() {
  local case_name='the real packed nuspec declares a sibling the index does not list'
  local case_dir="$work_dir/real-nuspec-absent"
  local package_dir="$case_dir/packages" fixture_dir="$case_dir/feed"
  local output_file="$case_dir/output.txt" guard_exit

  mkdir -p "$package_dir" "$fixture_dir"
  # The document that actually shipped inside chatter.messagebrokers.0.30.0.nupkg: a UTF-8 BOM, the
  # <license>/<readme>/<repository> elements no generated fixture carries, and two target-framework
  # groups each declaring Chatter.CQRS 0.16.0. Every other nuspec here is a forgery written to probe
  # one seam; this one is the evidence that the guard still reads what `dotnet pack` emits.
  create_nupkg_from_nuspec_file "$package_dir" real-nuspec-absent \
    "$real_nuspec_package_id" "$real_nuspec_package_version" "$real_nuspec_fixture" >/dev/null
  write_flat_container_index "$fixture_dir/chatter.cqrs.json" 0.15.0 0.17.0

  start_fixture_feed "$fixture_dir"
  guard_exit="$(run_guard_body "$package_dir" "http://127.0.0.1:$fixture_feed_port" "$output_file")"
  stop_fixture_feed

  assert_guard_exit "$case_name" 1 "$guard_exit" "$output_file" || return 0
  assert_output_contains "$case_name" "$real_nuspec_sibling_id" "$output_file"
  assert_output_contains "$case_name" "$real_nuspec_sibling_version" "$output_file"
  assert_output_contains "$case_name" "$version_absent_phrase" "$output_file"
}

case_real_packed_nuspec_sibling_published() {
  local case_name='the real packed nuspec declares a sibling the index lists'
  local case_dir="$work_dir/real-nuspec-published"
  local package_dir="$case_dir/packages" fixture_dir="$case_dir/feed"
  local request_log="$case_dir/requests.log" output_file="$case_dir/output.txt"
  local guard_exit query_count

  mkdir -p "$package_dir" "$fixture_dir"
  : >"$request_log"
  create_nupkg_from_nuspec_file "$package_dir" real-nuspec-published \
    "$real_nuspec_package_id" "$real_nuspec_package_version" "$real_nuspec_fixture" >/dev/null
  write_flat_container_index "$fixture_dir/chatter.cqrs.json" 0.15.0 0.16.0 0.17.0

  start_fixture_feed "$fixture_dir" '' "$request_log"
  guard_exit="$(run_guard_body "$package_dir" "http://127.0.0.1:$fixture_feed_port" "$output_file")"
  stop_fixture_feed

  assert_guard_exit "$case_name" 0 "$guard_exit" "$output_file" || return 0
  # The shipped document declares Chatter.CQRS 0.16.0 in BOTH target-framework groups, so the dedupe
  # this harness asserts on generated fixtures is asserted here against a real one.
  query_count="$(grep -cFx '/chatter.cqrs/index.json' "$request_log" || true)"
  if [ "$query_count" -eq 1 ]; then
    report_pass "$case_name: the sibling declared in both real groups was queried once (deduped)"
  else
    report_fail "$case_name: expected 1 flat-container query for chatter.cqrs, saw $query_count; the shipped nuspec declares it in both target-framework groups and must be queried once"
  fi
}

assert_checked_in_nuspec_fixture_is_intact() {
  # A self-check of the one nuspec this harness does not generate: the document `dotnet pack`
  # shipped inside chatter.messagebrokers.0.30.0.nupkg, checked in verbatim. The three properties
  # pinned here are the ones an editor silently drops, and each is load-bearing.
  #
  # The UTF-8 BOM: `ElementTree.fromstring` handles a BOM when it is handed BYTES, and every real
  # nuspec `dotnet pack` writes carries one. A refactor that decoded to `str` first would break on
  # every shipped package, and this fixture is the only thing that would notice.
  local case_name='the checked-in packed nuspec fixture is intact'
  local leading_bytes group_count

  if [ ! -f "$real_nuspec_fixture" ]; then
    report_fail "$case_name: $real_nuspec_fixture is missing, so the shipped-document case asserts nothing"
    return 0
  fi

  leading_bytes="$(head -c 3 "$real_nuspec_fixture" | od -An -tx1 | tr -d '[:space:]')"
  if [ "$leading_bytes" = 'efbbbf' ]; then
    report_pass "$case_name: it still begins with the UTF-8 BOM"
  else
    report_fail "$case_name: it begins $leading_bytes, not the UTF-8 BOM efbbbf; the shipped bytes have been rewritten"
  fi

  group_count="$(grep -cF '<group targetFramework=' "$real_nuspec_fixture" || true)"
  if [ "$group_count" -eq 2 ]; then
    report_pass "$case_name: it still carries both target-framework groups"
  else
    report_fail "$case_name: it carries $group_count target-framework group(s), expected 2; the dedupe assertion needs the sibling declared twice"
  fi

  if grep -qF "id=\"$real_nuspec_sibling_id\" version=\"$real_nuspec_sibling_version\"" "$real_nuspec_fixture"; then
    report_pass "$case_name: it still declares $real_nuspec_sibling_id $real_nuspec_sibling_version"
  else
    report_fail "$case_name: it no longer declares $real_nuspec_sibling_id $real_nuspec_sibling_version, so the index fixtures driving it are aimed at the wrong sibling"
  fi
}

run_behavioural_cases() {
  if [ -z "$guard_body_file" ]; then
    report_skip 'behavioural cases: no guard body could be extracted from any CD workflow, so there is nothing to execute (the marker assertions above are the failing signal)'
    return 0
  fi

  printf '\nBehavioural cases, running the body extracted from %s:\n' "${marked_workflows[0]}"
  case_all_dependencies_published
  case_declared_version_absent
  case_dependency_never_published
  case_index_returns_server_error
  case_endpoint_unreachable
  case_no_chatter_dependencies
  case_version_substring_collision
  case_dependency_attributes_reversed
  case_dependency_attributes_wrapped_across_lines
  case_namespace_prefixed_elements
  case_commented_out_id_is_not_the_package_id
  case_dependencies_without_groups
  case_dependency_in_open_close_form
  case_chatter_dependency_without_version
  case_nuspec_is_truncated
  case_nupkg_holds_two_nuspec_entries
  case_nupkg_holds_no_nuspec_entry
  case_root_element_is_not_package
  case_metadata_is_duplicated
  case_dependencies_is_duplicated
  case_id_is_duplicated
  case_version_is_duplicated
  case_document_namespace_is_foreign
  case_dependencies_namespace_is_foreign
  case_dependencies_under_unmodelled_element
  case_unmodelled_container_with_only_unmodelled_children
  case_sole_nuspec_entry_is_not_at_the_archive_root
  case_nupkg_holds_two_entries_of_one_nuspec_name
  case_nuspec_carries_no_namespace
  case_every_manifest_schema_namespace_is_read
  case_real_packed_nuspec_sibling_absent
  case_real_packed_nuspec_sibling_published
  case_index_returns_non_json_body
  case_index_mentions_version_outside_versions_array
  case_index_lacks_versions_key
  case_index_versions_is_not_a_string_list
  case_poll_budget_bounds_total_wall_time
}

assert_tooling_present
[ -f "$fixture_feed" ] || fail_infrastructure "fixture feed stub not found at $fixture_feed; the deploy dependency guard assertions did not run"

printf 'Structural assertions over %s:\n' "$workflow_dir"
assert_extraction_strips_only_terminal_cr
collect_guard_bodies
assert_cd_workflow_set
assert_marker_coverage
assert_bodies_byte_identical
assert_pre_sentinel_regions_identical
assert_guard_bodies_parse
assert_checked_in_nuspec_fixture_is_intact

run_behavioural_cases

printf '\n%s passed, %s failed, %s skipped.\n' "$pass_count" "$fail_count" "$skip_count"
if [ "$fail_count" -gt 0 ]; then
  printf 'The deploy-time dependency-publication guard specified by docs/adr/0019 is not satisfied.\n' >&2
  exit 1
fi
