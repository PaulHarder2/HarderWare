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
# one reviewed parser instead of each re-implementing it. Its first caller is
# WX-160-verify.sh.
#
# Usage:
#   deploy-info.sh <component> [<component> ...]   # boundary = max latest-deploy of the named components
#   deploy-info.sh all                             # boundary = max latest-deploy across ALL components
#   deploy-info.sh --version V <component> [...]   # version-pinned: most recent deploy whose version is V
#                                                  #   AND component is one of those listed (or any, with
#                                                  #   "all"). Prints "<timestamp>\t<commit>".
#   deploy-info.sh <...> [--deploy-log PATH]
#   deploy-info.sh -h | --help
#
# Exit:   0 ok; 2 usage error (bad/missing args, unknown option); 3 environment
#         (log unreadable, unknown component, or no [Deploy] lines); 4 a
#         --version query matched nothing (that version/component isn't deployed).
#
# Without --version it prints ONE timestamp, 19 chars "YYYY-MM-DD HH:MM:SS" (UTC;
# the HarderWare PC runs on UTC) -- the deploy boundary for the requested set, so
# the caller never sorts:
#   SINCE=$(deploy-info.sh WxReportSvc)                # single-service change
#   SINCE=$(deploy-info.sh WxReportSvc WxParserSvc)    # multi-service change (latest of the two)
#   SINCE=$(deploy-info.sh all)                        # whole-release smoke test
# Naming exactly the touched components matters: "all" keys off whichever
# component deployed last, which over-shoots the boundary for a change that only
# touched an earlier-deployed subset. (19-char timestamps sort lexically == by time.)
#
# With --version it prints "<timestamp>\t<commit>" for the most recent matching
# deploy -- the boundary AND the deployed commit (identity), in one call:
#   IFS=$'\t' read SINCE COMMIT < <(deploy-info.sh --version 1.26.0 WxReportSvc)
# A verify script pins on its release VERSION (knowable at authoring time, unlike
# the commit): later releases at other versions don't match, so it stays anchored
# to the deploy that shipped its change; a same-version redeploy moves to the
# freshest matching line (same code). Exit 4 + no output means "not deployed yet".
#
# The default (no --version) path uses the latest recorded [Deploy] line per
# component without filtering on the trailing OK (parity with the inline lookup it
# replaces). The --version path additionally requires the full well-formed shape
# (... <commit> OK), so it pins to a SUCCESSFUL deploy with a real commit.
#
# Shell: bash (WSL on the HarderWare PC; any bash with read access to the log
# works). Lives beside the verify scripts it serves (docs/test-procedures/)
# rather than in Code/tools: it is generic, but exists for the in-repo verify
# scripts and travels with them -- the documented WORKFLOW.md §13 carve-out.

set -uo pipefail

DEPLOY_LOG='/mnt/c/HarderWare/Logs/deploy-history.log'
VERSION=''
TARGETS=()
USAGE='usage: deploy-info.sh [--version V] <component|all> [<component> ...] [--deploy-log PATH]   (-h for details)'

while [ $# -gt 0 ]; do
  case "$1" in
    --version)
      [ $# -ge 2 ] || { echo "missing value for $1 (try --help)" >&2; exit 2; }
      VERSION="$2"; shift 2;;
    --deploy-log)
      [ $# -ge 2 ] || { echo "missing value for $1 (try --help)" >&2; exit 2; }
      DEPLOY_LOG="$2"; shift 2;;
    -h|--help) awk 'NR==1{next} /^#/{sub(/^# ?/,""); print; next} {exit}' "$0"; exit 0;;
    -*) echo "unknown option: $1 (try --help)" >&2; exit 2;;
    *)  TARGETS+=("$1"); shift;;
  esac
done

[ "${#TARGETS[@]}" -gt 0 ] || { echo "$USAGE" >&2; exit 2; }
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

# Most recent [Deploy] line matching VERSION and a component in the set (or any
# component when "all"), printed "<timestamp>\t<commit>" -- the boundary plus the
# deployed commit, for a verify script's identity check. Same field-equality
# match (no regex); version compared literally too.
matched_line() {  # $1 = all-flag (1=any component); $2 = space-joined component set
  awk -v want="$VERSION" -v allflag="$1" -v comps="$2" '
    BEGIN { n = split(comps, a, " "); for (i = 1; i <= n; i++) set[a[i]] = 1 }
    /\[Deploy\]/ {
      comp = ""; ver = ""; cmt = ""; status = ""
      for (i = 1; i <= NF; i++) if ($i == "[Deploy]") { comp = $(i+1); ver = $(i+2); cmt = $(i+3); status = $(i+4); break }
      if (comp == "") next
      sub(/\r$/, "", status)   # deploy-history.log is PowerShell-written (CRLF): the
                               # last field carries a trailing CR -- strip it before compare
      # Require the full well-formed shape "[Deploy] <comp> <ver> <commit> OK": the
      # trailing "OK" guarantees <commit> is a real commit (not a short line where
      # "OK" lands in $(i+3) and masquerades as one) and limits the boundary to a
      # SUCCESSFUL deploy. A short or failed line is skipped, so an earlier complete
      # line stands, or nothing matches (caller -> WAIT).
      if (ver == want && status == "OK" && (allflag == "1" || (comp in set))) {
        ts = substr($0, 1, 19)
        if (ts > maxts) { maxts = ts; maxcmt = cmt }
      }
    }
    END { if (maxts != "") printf "%s\t%s\n", maxts, maxcmt }
  ' "$DEPLOY_LOG"
}

# Is "all" among the targets? ("all" -> any component)
all_flag=0
printf '%s\n' "${TARGETS[@]}" | grep -qx 'all' && all_flag=1

# --version: the version-pinned lookup -- most recent deploy whose version is
# VERSION and whose component is one of TARGETS (or any, with "all"). Prints
# "<timestamp>\t<commit>". Exit 4 (distinct from 3) when nothing matches, so a
# caller can tell "version not deployed yet" (-> WAIT) from a real env error.
if [ -n "$VERSION" ]; then
  out="$(matched_line "$all_flag" "${TARGETS[*]}")"
  [ -n "$out" ] || { echo "no deploy of version $VERSION found among [${TARGETS[*]}] in $DEPLOY_LOG" >&2; exit 4; }
  printf '%s\n' "$out"
  exit 0
fi

# Default (no --version): the boundary timestamp only.
boundary=''
if [ "$all_flag" -eq 1 ]; then
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
