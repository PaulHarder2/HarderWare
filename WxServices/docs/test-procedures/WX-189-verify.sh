#!/usr/bin/env bash
# WX-189-verify.sh - confirm WX-189 (deterministic change detection) is live by reading
# the WxReport service log after a deployment boundary.
#
# WX-189 moves "What's changed" DETECTION out of the LLM: Claude no longer authors the
# structural changes[] array; DeterministicChangeDetector computes it from
# (prior committed snapshot, reconciled final_snapshot) after the call, and the reconciler
# injects it. A structural phantom is therefore impossible BY CONSTRUCTION -- the
# WX-148/151 consistency validator now runs only as defense-in-depth on the COMPUTED set,
# where it is tautologically green.
#
# THE GATING SIGNATURE (deterministic, attributable): a reconciler degrade whose reason is
# a CHANGE-CONSISTENCY message. Before WX-189 these were the dominant degrade cause -- a
# Claude-authored phantom the validator rejected. After WX-189 they can ONLY occur if the
# DETECTOR ITSELF produced a change its own inverse validator rejects (a detector bug), so
# their post-deploy count must be ZERO. The three ChangeConsistencyException fragments are
# distinctive in the degrade log line:
#   - "is not a real change versus prior_snapshot"        (prior-aware precip/severe reject)
#   - "is not aligned to a snapshot block boundary"       (off-grid window)
#   - "carries a safety-grade signal"                     (safety-tier over-escalation)
# A deploy restarts the service, which logs exactly one startup (diagnostic) report -- the
# deterministic precondition that the new path ran at least once.
#
# THE BROADER GOAL (reported, not gated): with the dominant (structural) degrade cause
# eliminated, the GENERAL reconciler degrade rate should drop materially from the
# ~16.5/day baseline (~33 events / 2 days, the WX-165 measure). Prose-fault degrades
# (NarrativeProseException) can still occur, so the rate is not zero; "materially below"
# is a human judgment over a noisy background, so the script REPORTS the pre/post counts
# and per-day rate for WX-189.md to interpret and does not fail the verdict on them.
#
# Usage:  WX-189-verify.sh [--since 'YYYY-MM-DD HH:MM:SS'] [--log PATH] [--deploy-log PATH] [-h]
#         (no positional args; no arguments = the normal version-pinned run)
# Shell:  bash (WSL).  Log timestamps are UTC (the HarderWare PC runs on UTC).
# Shared scaffold in verify-lib.sh.

# No -e by design: verify-lib's cnt() returns 0 on no-match (`grep -cE ... || true`),
# and intermediate grep stages under pipefail legitimately exit non-zero on a metric
# miss -- those are treated as a valid zero count, so -e would exit prematurely.
set -uo pipefail

SELF="${BASH_SOURCE[0]}"
TICKET='WX-189'                                    # self-identification + header
VERSION='1.32.0'                                   # the release VERSION that shipped the change -- the deploy pin
COMPONENTS=('WxReportSvc')                          # the service WX-189 ships in
TITLE='deterministic change detection'             # header description
MIN_WINDOW_HOURS=24                                # a full active day of cycles must accrue before PASS is valid
source "$(cd "$(dirname "$SELF")" && pwd)/verify-lib.sh"

vl_parse_args "$@"
vl_resolve_boundary     # sets SINCE/COMMIT/DEPLOY_INFO/BOUNDARY_SRC (WAIT-exits if undeployed)
vl_setup_window         # sets POST/LAST_TS/pre_start/elapsed/hh/mm/min_window_secs (WAIT-exits if no lines)

# ---- gating metric over the post-deploy window --------------------------------
# A structural change-consistency degrade can now only come from a detector bug. The
# degrade log line carries the rejection reason; match the three ChangeConsistency
# fragments. Expect 0.
CC_REASONS='is not a real change versus prior_snapshot|is not aligned to a snapshot block boundary|carries a safety-grade signal'
struct_degrades=$(printf '%s\n' "$POST" \
  | grep 'degrading to the parsed snapshot' \
  | grep -cE "$CC_REASONS" || true)

# Precondition: the deploy's startup diagnostic ran, so the new detection path executed.
startup_ran=$(printf '%s\n' "$POST" | cnt 'sending startup \(diagnostic\) report')

regression=$struct_degrades

# ---- reported (non-gating) degrade-rate read ----------------------------------
# General reconciler degrades (any cause, any locality), pre vs post. A DROP shows up as
# post < pre. vl_health_delta's "new" flags increases only, so read the raw before/after.
read -r deg_before deg_after _deg_new < <(vl_health_delta 'degrading to the parsed snapshot')
deg_rate=$(awk -v c="$deg_after" -v e="$elapsed" 'BEGIN { printf (e > 0 ? "%.1f" : "n/a"), (e > 0 ? c * 86400 / e : 0) }')

vl_header
echo
echo " WX-189 FINGERPRINT  (gating: no STRUCTURAL change-consistency degrade)"
echo "   startup diagnostic ran (a deploy logs one)  : $startup_ran   (PRECONDITION + EXERCISED -- 0 = none yet)"
echo "   structural change-consistency degrades       : $struct_degrades   (expect 0 -- now impossible unless the detector is buggy)"

if [ "$regression" -gt 0 ]; then
  echo
  echo " FAILURE-SIGNATURE LINES"
  printf '%s\n' "$POST" | grep 'degrading to the parsed snapshot' | grep -E "$CC_REASONS" || true
fi

echo
echo " REGRESSION-RATE READ  (reported, not gated -- judge in WX-189.md)"
echo "   reconciler degrades: pre=$deg_before  post=$deg_after  (equal-length windows)"
echo "   post degrade rate  : ~${deg_rate}/day   (pre-fix baseline ~16.5/day = the 33 events / 2 days)"
echo "   With the structural cause eliminated, the residual is prose-fault degrades only;"
echo "   a flat or higher rate means prose faults dominate -- investigate even on a PASS."

echo
echo " BACKGROUND HEALTH (new errors vs the equal-length pre-deploy window)"
read -r err_before err_after err_new < <(vl_health_delta ' ERROR ')
echo "   ERROR lines: pre=$err_before  post=$err_after  new=$err_new"

# Preconditioned verdict: with no startup diagnostic in the window there is nothing to
# confirm the new path ran -> WAIT, not a false PASS. A structural degrade -> FAIL at any
# horizon. PASS is withheld until the 24h window AND an exercised diagnostic.
vl_verdict "$regression" "$startup_ran" \
  "a structural change-consistency degrade occurred post-deploy -- the deterministic detector emitted a change its own inverse validator rejects (a detector bug); dump the degrade line above and the (prior, final_snapshot) it reconciled." \
  "no structural change-consistency degrade occurred; now confirm in WX-189.md that the general reconciler degrade rate dropped materially from the ~16.5/day baseline (the detection-moved-deterministic payoff)." \
  "$startup_ran" "a startup diagnostic report (a 'sending startup (diagnostic) report' line) in the window"
