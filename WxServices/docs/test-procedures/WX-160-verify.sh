#!/usr/bin/env bash
# WX-160-verify.sh - confirm WX-160 (TAF-aware significance gate) is live and working
# by reading the WxReport service log after a deployment boundary.
#
# WX-160 changed the WX-114 significance gate: it used to bypass on ANY fresh TAF
# (criterion "taf-fresh") and defer the decision to Claude; now it evaluates a
# GFS+TAF *merged* body and suppresses deterministically when nothing material
# changed. So after the deploy we expect, over time:
#   - ZERO "fired: taf-fresh" lines            (that criterion is retired)
#   - gate SUPPRESSED cycles whose message says "TAF-merged forecast shows no
#     material change"                          (the new deterministic suppression)
#   - fewer Claude calls / "not news"          (taf-fresh cycles no longer reach Claude)
#   - the gust-tick micro-update sends to stop
#   - no new ERRORs; occasional windKt-validator corrections are normal
#
# Usage:  WX-160-verify.sh [--since 'YYYY-MM-DD HH:MM:SS'] [--log PATH] [--deploy-log PATH] [-h]
#         (no positional args; no arguments at all = the normal version-pinned run)
# Shell:  bash (WSL).  Log timestamps are UTC (the HarderWare PC runs on UTC).
# Lives in the repo beside its procedure (docs/test-procedures/WX-160.md): a
# change-specific verification rides the same PR as the code it checks, so it is
# reviewed and versioned with that code (WORKFLOW.md §13). Generic cross-cutting
# workflow tools (check-ci.sh, check-cr.sh) stay in Code/tools.
#
# The deploy boundary is version-pinned: the script embeds the release VERSION it
# tests (knowable at authoring -- the version bump ships with the change) and its
# COMPONENTS, then asks the shared deploy-info.sh helper for the most recent
# deploy-history.log entry matching that version among those components. It gets
# back the boundary timestamp AND the deployed commit (for the identity check).
# Pinning on VERSION means a later release at another version doesn't move our
# boundary -- WX-160's window stays anchored to the deploy that shipped it, even
# if WxReportSvc is redeployed since. Pass --since to override manually; if the
# VERSION isn't deployed yet, the verdict is WAIT.

set -uo pipefail

LOG='/mnt/c/HarderWare/Logs/wxreport-svc.log'
DEPLOY_LOG='/mnt/c/HarderWare/Logs/deploy-history.log'
TICKET='WX-160'                                    # this script's ticket (self-identification)
VERSION='1.26.0'                                   # the release VERSION under test -- the pin. Knowable at
                                                   # authoring (the bump ships with the change), so the
                                                   # script is review-complete; later versions won't match.
COMPONENTS=('WxReportSvc')                          # the service(s) WX-160 ships in
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"   # this script's directory
DEPLOY_TOOL="$HERE/deploy-info.sh"                  # shared deploy-boundary helper, beside us
MIN_WINDOW_HOURS=24                                # a "full active day" of cycles must accrue before
                                                   # PASS is a valid call; below this the verdict is
                                                   # WAIT unless a conclusive FAIL fires (a quiet few
                                                   # hours proves nothing, but a failure still does)
SINCE_OVERRIDE=''

while [ $# -gt 0 ]; do
  case "$1" in
    --since|--log|--deploy-log)
      [ $# -ge 2 ] || { echo "missing value for $1 (try --help)" >&2; exit 2; }
      case "$1" in
        --since)      SINCE_OVERRIDE="$2";;
        --log)        LOG="$2";;
        --deploy-log) DEPLOY_LOG="$2";;
      esac
      shift 2;;
    -h|--help) awk 'NR==1{next} /^#/{sub(/^# ?/,""); print; next} {exit}' "$0"; exit 0;;
    *) echo "unknown arg: $1 (try --help)" >&2; exit 2;;
  esac
done
[ -r "$LOG" ] || { echo "cannot read service log: $LOG" >&2; exit 3; }

# Resolve the deploy boundary + identity. --since wins (manual override, no
# identity); else ask the shared helper for the most recent deploy of our VERSION
# among our COMPONENTS, which returns "<timestamp>\t<commit>". A miss (helper
# exit 4) means VERSION isn't in deploy-history.log yet -> not deployed -> WAIT.
DEPLOY_INFO=''
COMMIT=''
if [ -n "$SINCE_OVERRIDE" ]; then
  SINCE="${SINCE_OVERRIDE:0:19}"
  BOUNDARY_SRC='--since override'
elif out="$(bash "$DEPLOY_TOOL" --version "$VERSION" "${COMPONENTS[@]}" --deploy-log "$DEPLOY_LOG")"; then
  IFS=$'\t' read -r SINCE COMMIT <<<"$out"
  [ -n "$COMMIT" ] || { echo "deploy-info.sh returned no commit for version $VERSION (malformed deploy line?)" >&2; exit 3; }
  DEPLOY_INFO="$VERSION (commit $COMMIT)"
  BOUNDARY_SRC="deploy-history.log (version $VERSION)"
else
  rc=$?
  if [ "$rc" -eq 4 ]; then
    echo " VERDICT"
    echo "   Version $VERSION is not in $DEPLOY_LOG yet -- $TICKET not deployed (or the deploy failed)."
    echo "   ====>  WAIT   version $VERSION not deployed yet; deploy, then re-run."
    exit 0
  fi
  exit "$rc"   # 2 usage / 3 environment from the helper -- propagate
fi

# SINCE must be a full 'YYYY-MM-DD HH:MM:SS'. The win() lexical compare and the
# epoch math below both assume 19 chars: a short --since (e.g. just '2026-06-11')
# would silently widen the window to the whole calendar day and skew before/after.
case "$SINCE" in
  [0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]\ [0-9][0-9]:[0-9][0-9]:[0-9][0-9]) ;;
  *) echo "boundary '$SINCE' is not 'YYYY-MM-DD HH:MM:SS' (need a full timestamp)" >&2; exit 2;;
esac

# Timestamped log lines (ignore multi-line trace continuations) in [a,b).
# Timestamps are "YYYY-MM-DD HH:MM:SS..." so the leading 19 chars sort lexically.
win() {  # a b  -> matching real log lines on stdout
  awk -v a="$1" -v b="$2" '
    /^[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}/ {
      ts = substr($0, 1, 19)
      if (ts >= a && (b == "" || ts < b)) print
    }' "$LOG"
}
cnt() { grep -cE "$1" || true; }   # count matches on stdin, never error on 0

POST="$(win "$SINCE" '')"
# No post-boundary lines yet: emit a WAIT verdict (one token, like every other
# exit) rather than dropping out silently -- this is the right-after-deploy case
# the window gate owns. If the boundary/log is actually wrong, the same re-run
# advice applies once you correct it.
[ -n "$POST" ] || {
  echo " VERDICT"
  echo "   No log lines at/after $SINCE in $LOG -- deploy too recent (or wrong boundary/log)."
  echo "   ====>  WAIT   no cycles logged since the deploy boundary; re-run once the service has logged."
  exit 0
}

LAST_TS="$(printf '%s\n' "$POST" | tail -1 | cut -c1-19)"
since_epoch=$(date -u -d "$SINCE UTC" +%s)   || { echo "could not parse boundary '$SINCE'" >&2; exit 2; }
last_epoch=$(date -u -d "$LAST_TS UTC" +%s)  || { echo "could not parse last log time '$LAST_TS'" >&2; exit 2; }
elapsed=$(( last_epoch - since_epoch )); [ "$elapsed" -gt 0 ] || elapsed=1
pre_start="$(date -u -d "@$(( since_epoch - elapsed ))" '+%Y-%m-%d %H:%M:%S')"
hh=$(( elapsed / 3600 )); mm=$(( (elapsed % 3600) / 60 ))
min_window_secs=$(( MIN_WINDOW_HOURS * 3600 ))     # PASS is gated by this window; conclusive FAIL still fires earlier

# ---- metrics over the post-deploy window --------------------------------------
new_wording=$(printf '%s\n' "$POST" | cnt 'TAF-merged forecast shows no material change')
taf_fresh=$(  printf '%s\n' "$POST" | cnt 'fired: taf-fresh')
prefilter=$(  printf '%s\n' "$POST" | cnt 'no input changed since last Claude call')
suppressed=$( printf '%s\n' "$POST" | cnt 'significance gate suppressed')
passed=$(     printf '%s\n' "$POST" | cnt 'significance gate passed')
not_news=$(   printf '%s\n' "$POST" | cnt 'judged the .* not news')
sent=$(       printf '%s\n' "$POST" | grep 'report sent' | cnt 'DeliverWeatherReportAsync')
scheduled=$(  printf '%s\n' "$POST" | cnt 'generating scheduled report')
windkt=$(     printf '%s\n' "$POST" | cnt 'windKt carries sustained wind only')
errors=$(     printf '%s\n' "$POST" | cnt ' ERROR ')
degraded=$(   printf '%s\n' "$POST" | cnt 'reconciliation degraded|could not produce a self-consistent')

taf_fresh_before=$(win "$pre_start" "$SINCE" | cnt 'fired: taf-fresh')

# Health is scoped to NEW problems: count ERROR / degraded lines over the equal-
# length pre-deploy window too, and fail only on an INCREASE. A steady background
# of unrelated reconciler degradations (e.g. WX-165 time-of-day contradictions)
# appears in both windows and cancels out, so it no longer FAILs WX-160's gate
# test; only errors this deploy actually introduced push the after-count up.
errors_before=$(  win "$pre_start" "$SINCE" | cnt ' ERROR ')
degraded_before=$(win "$pre_start" "$SINCE" | cnt 'reconciliation degraded|could not produce a self-consistent')
new_errors=$((   errors   - errors_before ));   [ "$new_errors"   -gt 0 ] || new_errors=0
new_degraded=$(( degraded - degraded_before )); [ "$new_degraded" -gt 0 ] || new_degraded=0

echo    "============================================================"
echo    " WX-160 post-deploy verification (TAF-aware significance gate)"
echo    "============================================================"
echo    " Deploy boundary : $SINCE UTC   ($BOUNDARY_SRC)"
[ -n "$DEPLOY_INFO" ] && echo " Deployed        : ${COMPONENTS[*]} $DEPLOY_INFO"
echo    " Window analysed : $SINCE  ->  $LAST_TS   (${hh}h ${mm}m of cycles)"
echo    " Service log     : $LOG"
echo
echo    " NEW CODE LIVE?"
printf  '   %-46s %s\n' '"TAF-merged" suppression wording present:' "$([ "$new_wording" -gt 0 ] && echo "YES ($new_wording)  <- 1.26.0 gate is running" || echo 'not yet (no suppressions so far)')"
printf  '   %-46s %s\n' '"fired: taf-fresh" lines after deploy:'    "$taf_fresh   $([ "$taf_fresh" -eq 0 ] && echo '[expected 0 - criterion retired]' || echo '[!! UNEXPECTED - old binary still running?]')"
echo
echo    " GATE BEHAVIOR SINCE DEPLOY"
printf  '   %-46s %s\n' 'Input unchanged (cheap pre-filter skip):'  "$prefilter"
printf  '   %-46s %s\n' 'Gate SUPPRESSED (Claude NOT called):'      "$suppressed   <- new deterministic TAF-merge suppression"
printf  '   %-46s %s\n' 'Gate passed -> Claude called:'             "$passed"
printf  '   %-46s %s\n' 'Claude "not news" (called, no send):'      "$not_news"
printf  '   %-46s %s\n' 'Scheduled reports generated:'              "$scheduled"
printf  '   %-46s %s\n' 'Weather reports sent (per recipient):'     "$sent"
echo
echo    " TAF-FRESH COLLAPSE (equal-length windows, ${hh}h ${mm}m each)"
printf  '   %-46s %s\n' "Before deploy ($pre_start ->):"            "$taf_fresh_before  taf-fresh gate admits"
printf  '   %-46s %s\n' "After  deploy ($SINCE ->):"                "$taf_fresh  taf-fresh gate admits"
echo
echo    " WINDKT VALIDATOR (new fail-closed guard)"
printf  '   %-46s %s\n' 'Folded-gust rejections (retry-w/-feedback):' "$windkt"
echo
echo    " HEALTH (failing signal = NEW vs the equal-length pre-deploy window)"
printf  '   %-46s %s\n' 'ERROR lines (before -> after):'            "$errors_before -> $errors   (new: $new_errors)"
printf  '   %-46s %s\n' 'Reconciliation degraded (before -> after):' "$degraded_before -> $degraded   (new: $new_degraded)"
[ "$errors"   -gt 0 ] && { echo "   --- ERROR lines in window (may include pre-existing background; see before->after) ---"; printf '%s\n' "$POST" | grep ' ERROR ' | sed 's/^/     /'; }
[ "$passed"   -gt 0 ] && { echo; echo "   gate-passed 'fired:' reasons (should be REAL criteria, never taf-fresh):";
                           printf '%s\n' "$POST" | grep -oE 'fired: .*' | sort | uniq -c | sort -rn | sed 's/^/     /'; }
[ "$suppressed" -gt 0 ] && { echo; echo "   sample suppressions:";
                           printf '%s\n' "$POST" | grep 'significance gate suppressed' | tail -3 | sed -E 's/\[[^]]*\]//; s/^/     /'; }
echo
echo    " VERDICT"
# The section ALWAYS ends with exactly one explicit token -- PASS / FAIL / WAIT --
# so the In Test call is unambiguous (and greppable for automation). Order matters:
# a conclusive FAIL is reported at ANY elapsed time, but PASS is withheld until a
# full active day of cycles has accrued (short of that the verdict is WAIT, even
# when the gate already looks healthy -- a quiet few hours proves nothing).
#   FAIL : taf-fresh still firing (old binary), or NEW ERROR / reconciliation-
#          degraded lines vs the equal-length pre-deploy window. Conclusive ->
#          fail-closed at any horizon. (Background errors that also occur BEFORE
#          the deploy cancel in the before/after comparison and do NOT fail --
#          this deploy's verdict is about what IT introduced, not pre-existing
#          noise like the WX-165 time-of-day degradations.)
#   WAIT : < MIN_WINDOW_HOURS of cycles since deploy (not yet a valid test), OR no
#          TAF/GFS-driven cycle has reached the gate yet (nothing to judge).
#   PASS : a full active day in, taf-fresh retired (0), the gate is making real
#          decisions, no NEW errors.
if [ "$taf_fresh" -gt 0 ]; then
  verdict='FAIL'
  echo  "   'taf-fresh' still firing -> the old binary may still be running. Confirm the 1.26.0 deploy/restart."
elif [ "$new_errors" -gt 0 ] || [ "$new_degraded" -gt 0 ]; then
  verdict='FAIL'
  echo  "   $new_errors new ERROR / $new_degraded new degraded line(s) since deploy, above the pre-deploy"
  echo  "   baseline (see HEALTH above) -- investigate before passing."
elif [ "$elapsed" -lt "$min_window_secs" ]; then
  verdict='WAIT'
  echo  "   Only ${hh}h ${mm}m of cycles since deploy -- a valid test needs a full active day"
  echo  "   (>= ${MIN_WINDOW_HOURS}h). No conclusive failure so far; re-run once the window fills."
elif [ "$suppressed" -eq 0 ] && [ "$passed" -eq 0 ]; then
  verdict='WAIT'
  echo  "   A full active day elapsed, but no TAF/GFS-driven cycle has reached the gate (only quiet"
  echo  "   pre-filter skips) -- nothing to judge yet. Re-run after the next active weather period."
else
  verdict='PASS'
  echo  "   taf-fresh retired (0), the TAF-merge gate is making real decisions, and no new errors."
  echo  "   The cost win shows as 'gate SUPPRESSED' replacing what used to be taf-fresh Claude calls."
fi
echo
case "$verdict" in
  PASS) echo "   ====>  PASS   taf-fresh retired + health clean. Confirm the gate-passed";
        echo "                 reasons above are real criteria (never taf-fresh), then send to Done.";;
  FAIL) echo "   ====>  FAIL   open a Bug linked to WX-160; this ticket still completes (fix-forward).";;
  WAIT) echo "   ====>  WAIT   insufficient time elapsed for a valid test; give it a full active day, then re-run.";;
esac
echo    "============================================================"
