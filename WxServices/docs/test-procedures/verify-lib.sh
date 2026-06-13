#!/usr/bin/env bash
# verify-lib.sh - shared core for the WX-NN-verify.sh post-deployment verification
# scripts (docs/test-procedures/). SOURCE this from a verify script (do not execute
# it); set the per-script config vars; then call the helpers. Factored out of
# WX-160-verify.sh / WX-166-verify.sh so each verify script is just its constants +
# metrics + verdict logic -- the same de-duplication WX-163's deploy-info.sh did for
# the deploy-boundary lookup this builds on. Lives beside the verify scripts and
# deploy-info.sh: the documented WORKFLOW.md §13 carve-out for a generic helper that
# exists to serve the in-repo verify scripts.
#
# Contract -- the caller sets these BEFORE calling vl_resolve_boundary:
#   SELF             this verify script's path  (SELF="${BASH_SOURCE[0]}")
#   TICKET           e.g. 'WX-166'              (self-identification + header)
#   VERSION          the release VERSION under test (the deploy pin)
#   COMPONENTS=(...)  the service(s) the change ships in
#   TITLE            one-line description for the header
#   LOG              service log path           (default below if unset)
#   DEPLOY_LOG       deploy-history.log path    (default below if unset)
#   RESULTS_LOG      append-only verify audit log (default below; --results-log)
#   MIN_WINDOW_MINUTES PASS-gating window in MINUTES (default 1440 = 24h). Use this for
#                    sub-hour waits (e.g. 5). MIN_WINDOW_HOURS is still honored as a
#                    fallback when MINUTES is unset, so older scripts need no change.
# and calls, in order:
#   vl_parse_args "$@"   -> sets SINCE_OVERRIDE / LOG / DEPLOY_LOG; -h prints the
#                           caller's header comment block; unknown arg -> exit 2.
#   vl_resolve_boundary  -> sets SINCE COMMIT DEPLOY_INFO BOUNDARY_SRC. Emits a WAIT
#                           verdict and exits 0 when VERSION isn't deployed yet.
#   vl_setup_window      -> sets POST LAST_TS pre_start elapsed hh mm min_window_secs.
#                           Emits WAIT + exits 0 when no log lines exist past SINCE.
# Then the caller computes its metrics (win/cnt/vl_health_delta), prints sections
# (vl_header first), and ends with vl_verdict REGRESSION_COUNT EXERCISED, which makes
# the PASS/FAIL/WAIT decision and prints the verdict section. Every run -- including the
# early WAIT exits below -- appends one line to RESULTS_LOG (an append-only audit trail),
# and vl_verdict prints a ready-to-paste Jira Test Result string for PASS/FAIL (WX-170).
#
# Shell: bash (WSL). Log timestamps are UTC (the HarderWare PC runs on UTC). The
# caller owns 'set -uo pipefail'; this library relies on it (an unset required
# config var fails loudly rather than silently widening a window).

: "${LOG:=/mnt/c/HarderWare/Logs/wxreport-svc.log}"
: "${DEPLOY_LOG:=/mnt/c/HarderWare/Logs/deploy-history.log}"
: "${RESULTS_LOG:=/mnt/c/HarderWare/Logs/verify-results.log}"
SINCE_OVERRIDE=''

# PASS-gating window, in minutes. New scripts set MIN_WINDOW_MINUTES (allows sub-hour
# waits); older scripts set MIN_WINDOW_HOURS, still honored as a fallback; default 24h.
# Resolved here at source time -- the caller sets either var BEFORE sourcing, as the
# contract requires for the rest.
if [ -z "${MIN_WINDOW_MINUTES:-}" ]; then
  if [ -n "${MIN_WINDOW_HOURS:-}" ]; then
    MIN_WINDOW_MINUTES=$(( MIN_WINDOW_HOURS * 60 ))
  else
    MIN_WINDOW_MINUTES=1440
  fi
fi
# Human label for the verdict text: whole hours read as "Nh", otherwise "Nm".
if [ "$MIN_WINDOW_MINUTES" -ge 60 ] && [ $(( MIN_WINDOW_MINUTES % 60 )) -eq 0 ]; then
  MIN_WINDOW_LABEL="$(( MIN_WINDOW_MINUTES / 60 ))h"
else
  MIN_WINDOW_LABEL="${MIN_WINDOW_MINUTES}m"
fi

# Timestamped log lines (ignoring multi-line trace continuations) in [a,b).
# Timestamps are "YYYY-MM-DD HH:MM:SS..." so the leading 19 chars sort lexically.
win() {  # a b  -> matching real log lines on stdout
  awk -v a="$1" -v b="$2" '
    /^[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}/ {
      ts = substr($0, 1, 19)
      if (ts >= a && (b == "" || ts < b)) print
    }' "$LOG"
}
cnt() { grep -cE "$1" || true; }   # count matches on stdin, never error on 0

# Standard arg parsing: --since (manual boundary override), --log, --deploy-log,
# --results-log, -h.
vl_parse_args() {  # "$@"
  while [ $# -gt 0 ]; do
    case "$1" in
      --since|--log|--deploy-log|--results-log)
        [ $# -ge 2 ] || { echo "missing value for $1 (try --help)" >&2; exit 2; }
        case "$1" in
          --since)       SINCE_OVERRIDE="$2";;
          --log)         LOG="$2";;
          --deploy-log)  DEPLOY_LOG="$2";;
          --results-log) RESULTS_LOG="$2";;
        esac
        shift 2;;
      -h|--help) awk 'NR==1{next} /^#/{sub(/^# ?/,""); print; next} {exit}' "$SELF"; exit 0;;
      *) echo "unknown arg: $1 (try --help)" >&2; exit 2;;
    esac
  done
}

# Resolve the deploy boundary + identity. --since wins (manual override, no identity);
# else ask the shared deploy-info.sh (beside us) for the most recent deploy of our
# VERSION among our COMPONENTS, returned as "<timestamp>\t<commit>". A miss (helper
# exit 4) means VERSION isn't in deploy-history.log yet -> not deployed -> WAIT.
#
# A --since override must be >= the REAL deploy time: an earlier boundary pulls the OLD
# binary's normal output into the window, where it can match the change's failure
# signature (e.g. WX-160's retired 'fired: taf-fresh', which the old binary logged by
# design) and produce a false FAIL. The version-pinned path (no --since) always lands
# on the actual deploy, so prefer it; use --since only to re-anchor deliberately.
vl_resolve_boundary() {
  [ -r "$LOG" ] || { echo "cannot read service log: $LOG" >&2; exit 3; }
  local here tool out rc
  here="$(cd "$(dirname "$SELF")" && pwd)"
  tool="$here/deploy-info.sh"
  DEPLOY_INFO=''
  COMMIT=''
  if [ -n "$SINCE_OVERRIDE" ]; then
    SINCE="${SINCE_OVERRIDE:0:19}"
    BOUNDARY_SRC='--since override'
  elif out="$(bash "$tool" --version "$VERSION" "${COMPONENTS[@]}" --deploy-log "$DEPLOY_LOG")"; then
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
      vl_log WAIT
      exit 0
    fi
    exit "$rc"   # 2 usage / 3 environment from the helper -- propagate
  fi
  # SINCE must be a full 'YYYY-MM-DD HH:MM:SS' (19 chars): the win() lexical compare
  # and the epoch math both assume it; a short --since would silently widen the window.
  case "$SINCE" in
    [0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]\ [0-9][0-9]:[0-9][0-9]:[0-9][0-9]) ;;
    *) echo "boundary '$SINCE' is not 'YYYY-MM-DD HH:MM:SS' (need a full timestamp)" >&2; exit 2;;
  esac
}

# Compute the post-deploy window and its equal-length pre-deploy mirror.
vl_setup_window() {
  POST="$(win "$SINCE" '')"
  [ -n "$POST" ] || {
    echo " VERDICT"
    echo "   No log lines at/after $SINCE in $LOG -- deploy too recent (or wrong boundary/log)."
    echo "   ====>  WAIT   no cycles logged since the deploy boundary; re-run once the service has logged."
    vl_log WAIT
    exit 0
  }
  LAST_TS="$(printf '%s\n' "$POST" | tail -1 | cut -c1-19)"
  local since_epoch last_epoch
  since_epoch=$(date -u -d "$SINCE UTC" +%s)   || { echo "could not parse boundary '$SINCE'" >&2; exit 2; }
  last_epoch=$(date -u -d "$LAST_TS UTC" +%s)  || { echo "could not parse last log time '$LAST_TS'" >&2; exit 2; }
  elapsed=$(( last_epoch - since_epoch )); [ "$elapsed" -gt 0 ] || elapsed=1
  pre_start="$(date -u -d "@$(( since_epoch - elapsed ))" '+%Y-%m-%d %H:%M:%S')"
  hh=$(( elapsed / 3600 )); mm=$(( (elapsed % 3600) / 60 ))
  min_window_secs=$(( MIN_WINDOW_MINUTES * 60 ))
}

# Health helper: count PATTERN over the post window and the equal-length pre-deploy
# window, echoing "before after new" (new = max(after - before, 0)). A steady
# background of unrelated lines appears in both windows and cancels, so only what the
# deploy INTRODUCED shows up as "new". read three vars from it:
#   read before after new < <(vl_health_delta ' ERROR ')
vl_health_delta() {  # PATTERN -> "before after new"
  local pat="$1" before after new
  before=$(win "$pre_start" "$SINCE" | cnt "$pat")
  after=$(printf '%s\n' "$POST" | cnt "$pat")
  new=$(( after - before )); [ "$new" -gt 0 ] || new=0
  echo "$before $after $new"
}

# The standard header block (divider, title, boundary, deployed identity, window, log).
vl_header() {
  echo "============================================================"
  echo " $TICKET post-deploy verification ($TITLE)"
  echo "============================================================"
  echo " Deploy boundary : $SINCE UTC   ($BOUNDARY_SRC)"
  [ -n "$DEPLOY_INFO" ] && echo " Deployed        : ${COMPONENTS[*]} $DEPLOY_INFO"
  echo " Window analysed : $SINCE  ->  $LAST_TS   (${hh}h ${mm}m of cycles)"
  echo " Service log     : $LOG"
}

# WX-170: append one line per verification run to the append-only RESULTS_LOG -- when it
# ran (UTC), the ticket, the version under test, the deployed commit, the verdict, and
# the window analysed. Called at EVERY exit (the two early WAITs and vl_verdict) so each
# run leaves a durable, timestamped record and the Jira Test Result becomes a copy, not a
# recollection. set -u-safe: fields not yet known at an early exit fall back to '-'.
# Never fails the run -- an unwritable log warns to stderr and is skipped.
vl_log() {  # VERDICT
  local verdict="$1" now commit since last window line
  now="$(date -u '+%Y-%m-%d %H:%M:%S')"
  commit="${COMMIT:-}"; [ -n "$commit" ] || commit='-'
  since="${SINCE:-}"; last="${LAST_TS:-}"
  if [ -n "$since" ]; then
    window="${since} -> ${last:-?} (${hh:-0}h ${mm:-0}m)"
  else
    window='(boundary not resolved)'
  fi
  line="$now  [Verify] ${TICKET}  ${VERSION}  ${commit}  ${verdict}  window ${window}"
  # Brace group so a failed log-open (unwritable path) is suppressed too, not just
  # printf's stderr; the run never fails on a logging problem -- it only warns.
  { printf '%s\n' "$line" >> "$RESULTS_LOG"; } 2>/dev/null \
    || echo "  (note: could not write results log $RESULTS_LOG)" >&2
}

# vl_verdict REGRESSION_COUNT EXERCISED [FAIL_HINT] [PASS_NOTE] [PRECONDITION] [PRECOND_DESC]
# -- the reusable PASS/FAIL/WAIT decision, keyed on a CHANGE-SPECIFIC failure signature
# rather than a blanket ERROR count. This is the crux: a verify script attests to ITS
# change, so it fails only on lines that appear IFF the change is broken -- unrelated
# background noise (a coincident reconciler-degradation spike, an unrelated outage) must
# not fail it and mis-point at this ticket.
#   REGRESSION_COUNT : how many of this change's failure-signature lines are in the
#                      window. The CALLER computes this ONCE -- scoped exactly as its
#                      signature needs (e.g. WX-166 only within ERROR/FATAL lines) -- and
#                      normally displays the same count + the matched lines in a section
#                      ABOVE the verdict, so the verdict and the shown evidence are
#                      provably the SAME number (no second grep here that could diverge
#                      from, or over-match relative to, what the operator was shown).
#                      >0 => the change is not working => FAIL, at any horizon.
#   EXERCISED        : count of times the change's code path is known to have RUN in the
#                      window (so a clean PASS means something, not just silence). 0 =>
#                      the path hasn't run yet => WAIT (nothing to judge).
#   FAIL_HINT        : optional remediation line appended to a FAIL.
#   PASS_NOTE        : optional confirmation/next-step line appended to a PASS.
#   PRECONDITION     : optional count of REQUIRED-precondition lines the caller found in
#                      the window -- log lines that MUST be present before the failure
#                      signature is even meaningful (e.g. "a reconciliation actually
#                      ran"). When given and <= 0, the verdict is WAIT regardless of the
#                      signature: the test is not yet applicable. Omit (or leave empty)
#                      and there is no precondition gate (existing scripts unaffected).
#   PRECOND_DESC     : optional human phrase naming that precondition, for the WAIT text.
# Prints the " VERDICT" section + the boxed PASS|FAIL|WAIT token and sets VERDICT. Order:
# a missing precondition -> WAIT first (test not applicable); then a conclusive FAIL at
# any elapsed time; then PASS, withheld until the MIN_WINDOW_MINUTES window AND >=1
# exercised line. Callers print their own metric/context sections (the matched failure-
# signature lines, any precondition count, the background-error health) BEFORE calling.
#
# NOTE on the signature: a clean (no-signature) result means "this change's KNOWN
# failure modes didn't fire", not "the change is proven correct" -- a crash in a path
# the signature doesn't name, or a silent-but-wrong output, won't show. Keep the
# signature broad enough to cover the change's own exceptions, lean on the BACKGROUND
# HEALTH section for unexpected spikes, and on the manual WX-NN.md procedure for the
# deep checks the log can't express.
vl_verdict() {
  local n="$1" exercised="$2" fail_hint="${3:-}" pass_note="${4:-}"
  local precond="${5:-}" precond_desc="${6:-required precondition lines}"
  echo " VERDICT"
  if [ -n "$precond" ] && [ "$precond" -le 0 ]; then
    VERDICT='WAIT'
    echo "   The precondition for this test is not present yet ($precond_desc) -- the"
    echo "   failure-signature check is not applicable until it appears. Re-run once it does."
  elif [ "$n" -gt 0 ]; then
    VERDICT='FAIL'
    echo "   $n line(s) match this change's failure signature since the deploy (shown in the"
    echo "   section above) -- the change is not working as intended."
    [ -n "$fail_hint" ] && echo "   $fail_hint"
  elif [ "$elapsed" -lt "$min_window_secs" ]; then
    VERDICT='WAIT'
    echo "   Only ${hh}h ${mm}m of cycles since deploy -- a valid test needs at least"
    echo "   ${MIN_WINDOW_LABEL}. No failure signature so far; re-run once the window fills."
  elif [ "$exercised" -le 0 ]; then
    VERDICT='WAIT'
    echo "   The ${MIN_WINDOW_LABEL} window elapsed, but the change's code path has not run"
    echo "   yet (nothing to judge). Re-run after the next active cycle."
  else
    VERDICT='PASS'
    echo "   Past the ${MIN_WINDOW_LABEL} window, the change's code path ran $exercised time(s),"
    echo "   and no failure-signature line appeared. The change is live and behaving."
    [ -n "$pass_note" ] && echo "   $pass_note"
  fi
  echo
  case "$VERDICT" in
    PASS) echo "   ====>  PASS   $TICKET verified live: no failure signature, change exercised.";;
    FAIL) echo "   ====>  FAIL   open a Bug linked to $TICKET; this ticket still completes (fix-forward).";;
    WAIT) echo "   ====>  WAIT   insufficient evidence for a valid test yet; re-run once the condition above is met.";;
  esac
  # WX-170: record the run, and hand back a ready-to-paste Jira Test Result. WAIT is
  # logged (an inconclusive check is useful history) but has no Test Result to paste --
  # the field per WORKFLOW §7a is PASS/FAIL only.
  vl_log "$VERDICT"
  if [ "$VERDICT" != 'WAIT' ]; then
    echo
    echo "   Test Result (paste into Jira): $VERDICT $(date -u '+%Y-%m-%d') @ $VERSION"
  fi
  echo "============================================================"
}
