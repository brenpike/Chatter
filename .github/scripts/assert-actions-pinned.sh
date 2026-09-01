#!/usr/bin/env bash
# Fails when any GitHub Action reference is not pinned to an immutable commit SHA.
# Mutable tag refs (`@v5`) let an upstream tag move under us; a full 40-character
# commit SHA cannot be repointed.
# See https://docs.github.com/en/actions/security-for-github-actions/security-guides/security-hardening-for-github-actions#using-third-party-actions
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
violation_count=0

report_violation() {
  local file="$1" line_number="$2" ref="$3" reason="$4"
  printf '%s:%s: %s: %s\n' "${file#"$repo_root"/}" "$line_number" "$reason" "$ref" >&2
  violation_count=$((violation_count + 1))
}

assert_ref_pinned() {
  local file="$1" line_number="$2" ref="$3"

  # Local reusable workflows and local composite actions resolve inside this
  # repository at the checked-out commit, so they carry no external tag to pin.
  case "$ref" in
    ./*) return 0 ;;
  esac

  # Container actions are immutable only when addressed by image digest.
  case "$ref" in
    docker://*)
      if [[ "${ref##*@}" =~ ^sha256:[0-9a-f]{64}$ ]]; then
        return 0
      fi
      report_violation "$file" "$line_number" "$ref" 'container action not pinned to an image digest'
      return 0
      ;;
  esac

  if [[ "${ref##*@}" =~ ^[0-9a-f]{40}$ ]] && [[ "$ref" == *@* ]]; then
    return 0
  fi
  report_violation "$file" "$line_number" "$ref" 'action not pinned to a full commit SHA'
}

scan_file() {
  local file="$1"
  local line_number=0 raw ref

  while IFS= read -r raw || [ -n "$raw" ]; do
    line_number=$((line_number + 1))
    # INVARIANT: only a `uses:` in YAML key position is a step reference. Anchoring
    # to line start (optionally through a sequence dash) excludes `uses:` occurring
    # inside a comment or a `run: |` block scalar.
    [[ "$raw" =~ ^[[:space:]]*(-[[:space:]]+)?uses:[[:space:]]+ ]] || continue

    # First whitespace-delimited token after the key drops any trailing `# v5` comment.
    read -r ref _ <<<"${raw#*uses:}"
    ref="${ref%\"}"
    ref="${ref#\"}"
    ref="${ref%\'}"
    ref="${ref#\'}"

    assert_ref_pinned "$file" "$line_number" "$ref"
  done < "$file"
}

shopt -s nullglob globstar
for workflow_file in \
  "$repo_root"/.github/workflows/*.yml \
  "$repo_root"/.github/workflows/*.yaml \
  "$repo_root"/.github/actions/**/action.yml \
  "$repo_root"/.github/actions/**/action.yaml; do
  scan_file "$workflow_file"
done

if [ "$violation_count" -gt 0 ]; then
  printf '\n%s unpinned action reference(s) found. Pin each to a full 40-character commit SHA with a trailing `# <tag>` comment.\n' "$violation_count" >&2
  exit 1
fi
