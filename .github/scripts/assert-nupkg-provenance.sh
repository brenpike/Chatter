#!/usr/bin/env bash
# Fails when a packable project's NuGet package is missing supply-chain provenance metadata:
# SourceLink repository info (so a shipped binary traces back to the exact source revision
# that produced it), a companion symbol package, an embedded README, or a project URL.
#
# INVARIANT: a pack failure exits 2 while an assertion failure exits 1. A pack that never
# produced a package leaves nothing to inspect, which would otherwise read as a clean pass.
#
# See https://learn.microsoft.com/en-us/dotnet/standard/library-guidance/sourcelink
set -euo pipefail
shopt -s nullglob

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
package_dir="$(mktemp -d)"
pack_log="$(mktemp)"
trap 'rm -rf "$package_dir" "$pack_log"' EXIT

gap_count=0

# The nine packable projects, per CLAUDE.md § Versioning. Enumerated rather than globbed:
# the repository also carries per-module test projects and a shared test core, none of which
# are packable, and a glob would sweep those in along with any future non-packable project.
packable_projects=(
  src/Chatter.CQRS/src/Chatter.CQRS/Chatter.CQRS.csproj
  src/Chatter.MessageBrokers/src/Chatter.MessageBrokers/Chatter.MessageBrokers.csproj
  src/Chatter.MessageBrokers.AzureServiceBus/src/Chatter.MessageBrokers.AzureServiceBus/Chatter.MessageBrokers.AzureServiceBus.csproj
  src/Chatter.MessageBrokers.AzureServiceBus.Auth/src/Chatter.MessageBrokers.AzureServiceBus.Auth/Chatter.MessageBrokers.AzureServiceBus.Auth.csproj
  src/Chatter.MessageBrokers.Reliability.EntityFramework/src/Chatter.MessageBrokers.Reliability.EntityFramework/Chatter.MessageBrokers.Reliability.EntityFramework.csproj
  src/Chatter.MessageBrokers.SqlServiceBroker/src/Chatter.MessageBrokers.SqlServiceBroker/Chatter.MessageBrokers.SqlServiceBroker.csproj
  src/Chatter.SqlChangeFeed/src/Chatter.SqlChangeFeed/Chatter.SqlChangeFeed.csproj
  src/Chatter.MessageBrokers.RabbitMQ/src/Chatter.MessageBrokers.RabbitMQ/Chatter.MessageBrokers.RabbitMQ.csproj
  src/Chatter.MessageBrokers.Reliability.Cosmos/src/Chatter.MessageBrokers.Reliability.Cosmos/Chatter.MessageBrokers.Reliability.Cosmos.csproj
)

fail_with() {
  printf '%s\n' "$1" >&2
  exit 2
}

report_gap() {
  local package_id="$1" gap="$2"
  printf '%s: %s\n' "$package_id" "$gap" >&2
  gap_count=$((gap_count + 1))
}

# This repository commits a `packages.lock.json` for every project, so restore runs in locked
# mode: lock-file drift fails the pack rather than being silently rewritten by a gate.
pack_project() {
  local project="$1"

  if ! dotnet pack "$repo_root/$project" \
    --configuration Release \
    --output "$package_dir" \
    --nologo \
    --verbosity quiet \
    -p:RestoreLockedMode=true >"$pack_log" 2>&1; then
    cat "$pack_log" >&2
    fail_with "pack failed for $project; the provenance assertions did not run"
  fi
}

read_attribute() {
  local element="$1" attribute="$2"
  local pattern="$attribute=\"([^\"]*)\""
  [[ "$element" =~ $pattern ]] || return 0
  printf '%s' "${BASH_REMATCH[1]}"
}

read_element_text() {
  local nuspec="$1" element="$2"
  local matches
  matches="$(sed -n "s@.*<$element>\(.*\)</$element>.*@\1@p" <<<"$nuspec")"
  printf '%s' "${matches%%$'\n'*}"
}

assert_repository_metadata() {
  local package_id="$1" nuspec="$2"
  local repository_element repository_type repository_url repository_commit

  repository_element="$(grep -m1 '<repository' <<<"$nuspec" || true)"
  if [ -z "$repository_element" ]; then
    report_gap "$package_id" 'nuspec has no <repository> element (SourceLink metadata absent)'
    return 0
  fi

  repository_type="$(read_attribute "$repository_element" type)"
  repository_url="$(read_attribute "$repository_element" url)"
  repository_commit="$(read_attribute "$repository_element" commit)"

  [ "$repository_type" = 'git' ] || report_gap "$package_id" "<repository> type is '$repository_type', expected 'git'"
  [ -n "$repository_url" ] || report_gap "$package_id" '<repository> carries no url attribute'
  # The commit attribute is written from the repository head SourceLink resolves, so its
  # absence is the signal that SourceLink is not wired into the pack at all.
  [ -n "$repository_commit" ] || report_gap "$package_id" '<repository> carries no commit attribute (SourceLink not enabled)'
}

assert_symbol_package() {
  local package_id="$1" nupkg="$2"
  local snupkg="${nupkg%.nupkg}.snupkg"

  [ -f "$snupkg" ] || report_gap "$package_id" \
    "no symbol package alongside the package ($(basename "$snupkg") missing; needs IncludeSymbols with SymbolPackageFormat=snupkg)"
}

assert_readme() {
  local package_id="$1" nupkg="$2" nuspec="$3"
  local readme entries

  readme="$(read_element_text "$nuspec" readme)"
  if [ -z "$readme" ]; then
    report_gap "$package_id" 'nuspec declares no <readme> (PackageReadmeFile not set)'
    return 0
  fi

  entries="$(unzip -Z1 "$nupkg")"
  grep -Fxq "$readme" <<<"$entries" || report_gap "$package_id" \
    "nuspec declares <readme>$readme</readme> but that file is not inside the package"
}

assert_project_url() {
  local package_id="$1" nuspec="$2"
  local project_url

  project_url="$(read_element_text "$nuspec" projectUrl)"
  [ -n "$project_url" ] || report_gap "$package_id" 'nuspec declares no <projectUrl> (PackageProjectUrl not set)'
}

assert_package_provenance() {
  local project="$1"
  local package_id nupkg nuspec
  local -a nupkg_matches

  package_id="$(basename "$project" .csproj)"

  # A version always starts with a digit, so this pattern cannot match a longer package id
  # that merely shares this one's prefix. Multi-targeted projects still emit a single package.
  nupkg_matches=("$package_dir/$package_id".[0-9]*.nupkg)
  if [ "${#nupkg_matches[@]}" -ne 1 ]; then
    fail_with "expected exactly one .nupkg for $package_id, found ${#nupkg_matches[@]}; the provenance assertions did not run"
  fi
  nupkg="${nupkg_matches[0]}"

  if ! nuspec="$(unzip -p "$nupkg" "$package_id.nuspec")"; then
    fail_with "could not read $package_id.nuspec from $(basename "$nupkg"); the provenance assertions did not run"
  fi

  assert_repository_metadata "$package_id" "$nuspec"
  assert_symbol_package "$package_id" "$nupkg"
  assert_readme "$package_id" "$nupkg" "$nuspec"
  assert_project_url "$package_id" "$nuspec"
}

for packable_project in "${packable_projects[@]}"; do
  pack_project "$packable_project"
done

for packable_project in "${packable_projects[@]}"; do
  assert_package_provenance "$packable_project"
done

if [ "$gap_count" -gt 0 ]; then
  printf '\n%s package provenance gap(s) found across %s packable project(s). Every published package must carry SourceLink repository metadata, a symbol package, an embedded README, and a project URL.\n' \
    "$gap_count" "${#packable_projects[@]}" >&2
  exit 1
fi
