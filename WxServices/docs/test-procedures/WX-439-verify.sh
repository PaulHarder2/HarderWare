#!/usr/bin/env bash
# WX-439 — verify the Grafana pinNavItems toggle is on master and is what the
# running container is configured from.
#
# READ-ONLY. This script asserts; it changes nothing. The container recreate is a
# human step in WX-439.md, deliberately kept out of here so a verification cannot
# have side effects.
#
# THE INFERENCE CHAIN, because no single check below proves the claim:
#
#   docker compose ALWAYS reads the WORKING-TREE file, never a committed blob.
#   So "the running container reflects the COMMITTED change" is not observable
#   directly. It follows from two checks that ARE observable:
#
#     (a) the working-tree file is identical to the blob at origin/master   [3,4,5]
#     (b) the running container's env matches what that file specifies      [6]
#
#   Neither half is sufficient alone. Check 6 on its own passes against a
#   container created from an uncommitted edit — which is precisely the state
#   this ticket exists to end, and which held on this machine from 2026-08-14.
#
# Usage:  bash WxServices/docs/test-procedures/WX-439-verify.sh
# Exit:   0 = PASS, 1 = FAIL, 2 = could not run (a check could not be evaluated)

set -uo pipefail

FILE="observability/docker-compose.yml"
TOGGLE="GF_FEATURE_TOGGLES_ENABLE=pinNavItems"
CONTAINER="observability-grafana-1"

fails=0
pass() { printf '  PASS  %s\n' "$1"; }
fail() { printf '  FAIL  %s\n' "$1"; fails=$((fails + 1)); }
cant() { printf '  ????  %s\n' "$1" >&2; exit 2; }

repo=$(git rev-parse --show-toplevel 2>/dev/null) || cant "not inside a git repository"
cd "$repo" || cant "cannot cd to repo root $repo"
printf 'WX-439 verify — repo %s\n\n' "$repo"

# 1. The path is real. Guards a typo in every path-based check below: `git status
#    --short <typo>` prints nothing and exits 0, i.e. identical to its own pass
#    condition. --error-unmatch is the form that discriminates (rc=1 on a bogus path).
if git ls-files --error-unmatch "$FILE" >/dev/null 2>&1; then
    pass "1. $FILE is tracked (path is real, not a typo)"
else
    fail "1. $FILE is NOT tracked — path wrong, or the file was never committed"
fi

# 2. Refresh the remote-tracking ref. Checks 3 and 4 read origin/master, which is a
#    LOCAL ref updated only by fetch/pull — stale, it reports a spurious FAIL right
#    after a browser merge.
if git fetch origin --quiet 2>/dev/null; then
    pass "2. fetched origin"
else
    cant "2. could not fetch origin — checks 3 and 4 would read a stale ref"
fi

# 3. On master, and level with it. A clean `git status` proves the tree matches HEAD,
#    NOT origin/master — it is equally clean while parked on the feature branch, which
#    is not what AC-2 asks.
branch=$(git rev-parse --abbrev-ref HEAD)
head=$(git rev-parse HEAD)
origin=$(git rev-parse origin/master)
if [ "$branch" = "master" ] && [ "$head" = "$origin" ]; then
    pass "3. on master and level with origin/master (${head:0:7})"
else
    fail "3. expected master == origin/master; on '$branch' at ${head:0:7}, origin/master ${origin:0:7}"
fi

# 4. AC-1 — the toggle is in the committed blob. Exactly one occurrence: two would mean
#    a duplicated env entry, which compose accepts and which would be a real defect.
n=$(git show "origin/master:$FILE" 2>/dev/null | grep -c -- "$TOGGLE")
if [ "$n" = "1" ]; then
    pass "4. AC-1: origin/master:$FILE carries the toggle exactly once"
else
    fail "4. AC-1: expected exactly 1 occurrence in origin/master:$FILE, found $n"
fi

# 5. AC-2 — the working tree is reconciled, not merely coexisting with an identical edit.
if [ -z "$(git status --porcelain -- "$FILE")" ]; then
    pass "5. AC-2: working tree clean for $FILE"
else
    fail "5. AC-2: $FILE is still modified — the local edit was never reconciled"
fi

# 6. The running container carries the toggle. Grep for the ONE variable: the full env
#    list contains GF_SECURITY_ADMIN_PASSWORD in plaintext.
if ! docker inspect "$CONTAINER" >/dev/null 2>&1; then
    cant "6. container $CONTAINER not found — is Docker Desktop running?"
fi
env_line=$(docker inspect "$CONTAINER" \
    --format '{{range .Config.Env}}{{println .}}{{end}}' 2>/dev/null \
    | grep '^GF_FEATURE_TOGGLES_ENABLE=' || true)
if [ "$env_line" = "$TOGGLE" ]; then
    pass "6. $CONTAINER carries $TOGGLE"
else
    fail "6. expected '$TOGGLE' on $CONTAINER, got '${env_line:-<absent>}'"
fi

# Informational — the ordering a reader will want, but NOT an assertion. Container
# creation time does not bear on the inference chain above, and asserting on it would
# be a check that looks meaningful and is not.
created=$(docker inspect "$CONTAINER" --format '{{.Created}}' 2>/dev/null)
printf '\n  info  container created %s (id %s)\n' "$created" \
    "$(docker inspect -f '{{slice .Id 0 12}}' "$CONTAINER" 2>/dev/null)"
printf '  info  origin/master committed %s\n\n' "$(git log -1 --format=%cI origin/master 2>/dev/null)"

if [ "$fails" -eq 0 ]; then
    printf 'PASS — %s is on master, the tree is reconciled, and %s is configured from it.\n' \
        "$FILE" "$CONTAINER"
    exit 0
fi
printf 'FAIL — %d check(s) failed. See WX-439.md; steps 4-6 there are the browser half.\n' "$fails"
exit 1
