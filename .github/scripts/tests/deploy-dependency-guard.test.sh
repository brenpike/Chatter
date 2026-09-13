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

# The sentinel comments bracketing the guard body inside each workflow's `run: |` block. They are
# ordinary bash comments as well as extraction anchors, so they survive into the shipped step and
# point a reader at the decision record. `awk` between sentinels is used rather than `yq`, which is
# not guaranteed to be present on a runner image.
guard_begin_marker='# BEGIN dependency-publish guard (docs/adr/0019)'
guard_end_marker='# END dependency-publish guard (docs/adr/0019)'

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
  # `unzip` and `curl` are the guard's own dependencies, `awk` extracts the body and `python3`
  # runs the feed stub. Absent any of them the harness asserts nothing.
  for tool in awk curl python3 unzip; do
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
  awk -v begin_marker="$guard_begin_marker" -v end_marker="$guard_end_marker" '
    index($0, begin_marker) { capturing = 1; next }
    index($0, end_marker)   { capturing = 0; next }
    capturing               { print }
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

# --------------------------------------------------------------------------------------------
# Fixtures: nupkgs built at test time, and flat-container index documents for the stub feed.
# --------------------------------------------------------------------------------------------

write_nuspec() {
  # Reproduces the shape `dotnet pack` emits for a multi-targeted Chatter module: one <group> per
  # target framework, each repeating the identical dependency set. The real
  # Chatter.MessageBrokers.SqlServiceBroker 0.14.2 nuspec declares Chatter.MessageBrokers 0.29.0 in
  # both its net8.0 and net10.0 groups, so a guard that does not dedupe queries the feed twice.
  local nuspec_path="$1" package_id="$2" package_version="$3"
  shift 3
  local dependencies=("$@")
  local target_framework dependency dependency_id dependency_version

  {
    printf '<?xml version="1.0" encoding="utf-8"?>\n'
    printf '<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">\n'
    printf '  <metadata>\n'
    printf '    <id>%s</id>\n' "$package_id"
    printf '    <version>%s</version>\n' "$package_version"
    printf '    <description>deploy-dependency-guard fixture</description>\n'
    printf '    <dependencies>\n'
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
    printf '    </dependencies>\n'
    printf '  </metadata>\n'
    printf '</package>\n'
  } >"$nuspec_path"
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
  if grep -qF -- "$needle" "$output_file"; then
    report_fail "$case_name: message must not contain \"$needle\"; the never-published and version-absent failures are different operator problems and must read differently"
    print_guard_output "$output_file"
    return 0
  fi
  report_pass "$case_name: message is distinct from the version-absent failure"
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
}

assert_tooling_present
[ -f "$fixture_feed" ] || fail_infrastructure "fixture feed stub not found at $fixture_feed; the deploy dependency guard assertions did not run"

printf 'Structural assertions over %s:\n' "$workflow_dir"
collect_guard_bodies
assert_cd_workflow_set
assert_marker_coverage
assert_bodies_byte_identical

run_behavioural_cases

printf '\n%s passed, %s failed, %s skipped.\n' "$pass_count" "$fail_count" "$skip_count"
if [ "$fail_count" -gt 0 ]; then
  printf 'The deploy-time dependency-publication guard specified by docs/adr/0019 is not satisfied.\n' >&2
  exit 1
fi
