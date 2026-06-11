#!/usr/bin/env bash
# deploy-info.sh - report the deploy boundary (a single UTC timestamp) for one or
# more services/applications from deploy-history.log, for the post-deploy
# verification scripts in this directory.
#
# deploy-history.log is the authoritative deploy record: each line is
#   <YYYY-MM-DD HH:MM:SS.fff> INFO  [Deploy] <Component> <Version> <Commit> OK
# appended chronologically as a release deploys its components (at staggered
# times). Every WX-NN-verify.sh needs the same first fact -- its before/after
# boundary -- which is the LATEST deploy time of the component(s) the change
# touched. This helper is that lookup, factored out so verify scripts can share
# one reviewed parser instead of each re-implementing it. (Its first caller,
# WX-160-verify.sh, still inlines its own boundary lookup; it migrates to this
# helper in a follow-up -- this ticket ships the helper standalone.)
#
# Usage:
#   deploy-info.sh <component> [<component> ...]   # boundary = max latest-deploy of the named components
#   deploy-info.sh all                             # boundary = max latest-deploy across ALL components
#   deploy-info.sh <...> [--deploy-log PATH]
#   deploy-info.sh -h | --help
#
# Exit:   0 ok (one timestamp printed); 2 usage error (bad/missing args, unknown
#         option); 3 environment (log unreadable, unknown component, or no
#         [Deploy] lines).
#
# It always prints ONE timestamp: 19 chars "YYYY-MM-DD HH:MM:SS" (UTC; the
# HarderWare PC runs on UTC) -- the deploy boundary for the requested set, so the
# caller never sorts:
#   SINCE=$(deploy-info.sh WxReportSvc)                # single-service change
#   SINCE=$(deploy-info.sh WxReportSvc WxParserSvc)    # multi-service change (latest of the two)
#   SINCE=$(deploy-info.sh all)                        # whole-release smoke test
# Naming exactly the touched components matters: "all" keys off whichever
# component deployed last, which over-shoots the boundary for a change that only
# touched an earlier-deployed subset. (19-char timestamps sort lexically == by time.)
#
# It uses the latest recorded [Deploy] line per component (parity with the inline
# lookup it replaces); it does not currently filter on the trailing OK.
#
# Shell: bash (WSL on the HarderWare PC; any bash with read access to the log
# works). Lives beside the verify scripts it serves (docs/test-procedures/)
# rather than in Code/tools: it is generic, but exists for the in-repo verify
# scripts and travels with them -- the documented WORKFLOW.md §13 carve-out.

set -uo pipefail

DEPLOY_LOG='/mnt/c/HarderWare/Logs/deploy-history.log'
TARGETS=()

while [ $# -gt 0 ]; do
  case "$1" in
    --deploy-log)
      [ $# -ge 2 ] || { echo "missing value for $1 (try --help)" >&2; exit 2; }
      DEPLOY_LOG="$2"; shift 2;;
    -h|--help) awk 'NR==1{next} /^#/{sub(/^# ?/,""); print; next} {exit}' "$0"; exit 0;;
    -*) echo "unknown option: $1 (try --help)" >&2; exit 2;;
    *)  TARGETS+=("$1"); shift;;
  esac
done

[ "${#TARGETS[@]}" -gt 0 ] || { echo "usage: deploy-info.sh <component|all> [<component> ...] [--deploy-log PATH]" >&2; exit 2; }
[ -r "$DEPLOY_LOG" ]       || { echo "cannot read deploy log: $DEPLOY_LOG" >&2; exit 3; }

# Latest [Deploy] timestamp for one component, or across ALL components when the
# argument is empty. The component is matched by awk FIELD EQUALITY (the token
# after "[Deploy]"), never a regex -- so the name is compared literally (no
# metacharacter can match the wrong line, and a prefix like WxVis never matches a
# longer WxVisSvc). 19-char timestamps sort lexically == chronologically, so a
# string max is the latest deploy.
max_ts() {  # $1 = component name, or "" for all components
  awk -v c="$1" '
    /\[Deploy\]/ {
      comp = ""
      for (i = 1; i <= NF; i++) if ($i == "[Deploy]") { comp = $(i + 1); break }
      if (comp == "") next
      if (c == "" || comp == c) { ts = substr($0, 1, 19); if (ts > max) max = ts }
    }
    END { if (max != "") print max }
  ' "$DEPLOY_LOG"
}

boundary=''
if printf '%s\n' "${TARGETS[@]}" | grep -qx 'all'; then
  boundary="$(max_ts '')"          # whole-release boundary: latest deploy of any component
  [ -n "$boundary" ] || { echo "no [Deploy] lines found in $DEPLOY_LOG" >&2; exit 3; }
else
  # Boundary for the named set = the latest among their individual latest deploys.
  for comp in "${TARGETS[@]}"; do
    ts="$(max_ts "$comp")"
    [ -n "$ts" ] || { echo "no deploy found for '$comp' in $DEPLOY_LOG (try 'all')" >&2; exit 3; }
    [[ "$ts" > "$boundary" ]] && boundary="$ts"
  done
fi
printf '%s\n' "$boundary"
