#!/usr/bin/env bash
#
# check-version-consistency.sh
#
# Guards the invariant that Directory.Build.props and VERSIONS.md never disagree
# on the current version. WX-80 added a 1.8.0 VERSIONS.md row but never bumped
# Directory.Build.props (left at 1.7.1), so 1.8.0 binaries self-reported 1.7.1
# and nothing caught it. This script makes that invariant executable.
#
# Invariant: the <Version> in WxServices/Directory.Build.props equals the
# version in the first data row of the WxServices/VERSIONS.md table. Holds at
# every commit on master -- feature PRs bump both together, the hash-fill commit
# changes only the hash, and pure-docs PRs touch neither.
#
# Exit codes: 0 = consistent, 1 = mismatch, 2 = could not parse either file.
#
# Used by the pre-push hook (WxServices/tools/hooks/pre-push) and the CI
# "Version consistency" step. Resolves files from the repo root via git so it
# runs correctly regardless of the caller's working directory.

set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
props="$repo_root/WxServices/Directory.Build.props"
versions="$repo_root/WxServices/VERSIONS.md"

if [[ ! -f "$props" ]]; then
    echo "ERROR: not found: $props" >&2
    exit 2
fi
if [[ ! -f "$versions" ]]; then
    echo "ERROR: not found: $versions" >&2
    exit 2
fi

# <Version>1.9.0</Version> -> 1.9.0 (first occurrence wins). Accepts any
# dotted version (2+ segments) so a 4-part assembly-style version like 1.9.0.0
# is captured and compared verbatim rather than failing the parse.
props_version="$(grep -oE '<Version>[0-9]+(\.[0-9]+)+</Version>' "$props" \
    | head -n1 | grep -oE '[0-9]+(\.[0-9]+)+' || true)"

# First Markdown table row whose first cell is a semantic version, e.g.
#   | 1.9.0   | _pending_ | 2026-06-01 | ... |
# The header (| Version |) and separator (|---------|) rows have no X.Y.Z cell
# and are skipped automatically.
versions_version="$(grep -oE '^\|[[:space:]]*[0-9]+(\.[0-9]+)+[[:space:]]*\|' "$versions" \
    | head -n1 | grep -oE '[0-9]+(\.[0-9]+)+' || true)"

if [[ -z "$props_version" ]]; then
    echo "ERROR: could not parse <Version> from $props" >&2
    exit 2
fi
if [[ -z "$versions_version" ]]; then
    echo "ERROR: could not parse the top version row from $versions" >&2
    exit 2
fi

if [[ "$props_version" != "$versions_version" ]]; then
    echo "VERSION MISMATCH:" >&2
    echo "  Directory.Build.props <Version> = $props_version" >&2
    echo "  VERSIONS.md top row             = $versions_version" >&2
    echo "Bump Directory.Build.props and add a matching VERSIONS.md row (see WORKFLOW.md section 5)." >&2
    exit 1
fi

echo "Version consistency OK: $props_version"
