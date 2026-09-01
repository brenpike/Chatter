#!/usr/bin/env bash
# Fails when any package in the solution graph — direct or transitive — carries a known
# NuGet advisory.
#
# INVARIANT: `dotnet list package --vulnerable` exits 0 even when it reports advisories,
# so its exit status carries no signal. This gate parses the reported findings and derives
# its own exit status from them; checking `$?` alone would be a silent no-op.
#
# Usage: assert-no-vulnerable-packages.sh [severity-threshold]
#   severity-threshold: low|moderate|high|critical (default: low — fail on ANY severity)
#
# See https://learn.microsoft.com/en-us/nuget/concepts/auditing-packages
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
solution_path="$repo_root/Chatter.sln"
severity_threshold="${1:-low}"

findings_file="$(mktemp)"
report_file="$(mktemp)"
trap 'rm -f "$findings_file" "$report_file"' EXIT

fail_with() {
  printf '%s\n' "$1" >&2
  exit 2
}

# NuGet advisory severities, ordered. An unrecognized severity ranks above `critical` so a
# future or malformed severity value is always reported, never silently filtered out.
rank_severity() {
  case "$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]')" in
    low) printf '1' ;;
    moderate) printf '2' ;;
    high) printf '3' ;;
    critical) printf '4' ;;
    *) printf '99' ;;
  esac
}

assert_threshold_valid() {
  case "$(printf '%s' "$severity_threshold" | tr '[:upper:]' '[:lower:]')" in
    low|moderate|high|critical) return 0 ;;
  esac
  fail_with "unknown severity threshold '$severity_threshold'; expected one of low, moderate, high, critical"
}

# `dotnet list package` reads the restore assets, so the solution must be restored first.
# This repository restores in locked mode against its committed `packages.lock.json` files:
# a drifted lock file fails the restore rather than being rewritten.
restore_solution() {
  if ! dotnet restore "$solution_path" --locked-mode >/dev/null; then
    fail_with "restore failed for $solution_path; the vulnerability scan did not run"
  fi
}

collect_findings() {
  # `--output-version 1` pins the machine-readable schema this parser was written against
  # (verified against SDK 10.0.300). Asserting the version keeps a future schema change a
  # loud failure instead of a silent zero-finding pass.
  if ! dotnet list "$solution_path" package --vulnerable --include-transitive --format json --output-version 1 --no-restore >"$report_file"; then
    fail_with "'dotnet list package --vulnerable' failed; the vulnerability scan did not run"
  fi

  if ! jq -e '.version == 1 and (.projects | type == "array")' "$report_file" >/dev/null 2>&1; then
    fail_with "'dotnet list package --vulnerable' did not emit a version 1 JSON report; the vulnerability scan did not run"
  fi

  jq -r --arg root "$repo_root/" '
    .projects[]
    | ((.path // "unknown") | if startswith($root) then ltrimstr($root) else . end) as $project
    | (.frameworks // [])[]
    | .framework as $framework
    | ((.topLevelPackages // []) + (.transitivePackages // []))[]
    | .id as $package
    | (.resolvedVersion // "unknown") as $version
    | (.vulnerabilities // [])[]
    | [$package, $version, (.severity // "Unknown"), (.advisoryurl // "unknown"), $framework, $project]
    | @tsv
  ' "$report_file" | LC_ALL=C sort >"$findings_file"
}

# Multi-targeting reports the same advisory once per target framework and once per project.
# The advisory header is printed once per unique package/version/advisory; every affected
# project and framework is still listed beneath it, so nothing is suppressed.
report_findings() {
  local package version severity advisory framework project
  local threshold_rank previous_key='' key
  local advisory_count=0

  threshold_rank="$(rank_severity "$severity_threshold")"

  while IFS=$'\t' read -r package version severity advisory framework project; do
    [ "$(rank_severity "$severity")" -ge "$threshold_rank" ] || continue

    key="$package|$version|$severity|$advisory"
    if [ "$key" != "$previous_key" ]; then
      printf '\n%s %s: %s severity: %s\n' "$package" "$version" "$severity" "$advisory" >&2
      previous_key="$key"
      advisory_count=$((advisory_count + 1))
    fi
    printf '    %s (%s)\n' "$project" "$framework" >&2
  done <"$findings_file"

  printf '%s' "$advisory_count"
}

assert_threshold_valid
restore_solution
collect_findings

advisory_count="$(report_findings)"

if [ "$advisory_count" -gt 0 ]; then
  printf '\n%s vulnerable package(s) found at or above `%s` severity. Update each package, or a package that pulls it in transitively, to a version without a known advisory.\n' \
    "$advisory_count" "$severity_threshold" >&2
  exit 1
fi
