#!/usr/bin/env bash
# WX-165-verify.sh - confirm WX-165 (reconciler invention reduction) is live by reading
# the WxReport service log after a deployment boundary.
#
# WX-165 attacks invented "What's changed" items at the source, so the reconciler stops
# authoring phantom changes the validators then reject (which degrades the send). Three
# coordinated fixes: (1) the sampling temperature is pinned low (0.25) so retries
# converge instead of rolling a fresh phantom each attempt; (2) the Diagnostic report
# kind now gets the WX-178 near-term-severe-onset band rule (it had fallen through to an
# EMPTY instruction, the one report kind never told "an empty changes array is correct",
# so it filled the band against a stale prior and the phantom degraded the send); (3) a
# change-consistency rejection now replays PRESCRIPTIVE feedback naming the offending
# change and saying correct-or-remove, don't invent a new one.
#
# THE GATING SIGNATURE (deterministic, attributable): a startup diagnostic that DEGRADES.
# A deploy restarts the service, which logs exactly one startup (diagnostic) report. The
# 2026-06-15 incident was that startup diagnostic degrading three times on invented
# changes and never sending (a diagnostic degrade is a hard abort -- no email). With the
# fix the diagnostic suppresses the non-severe band and sends. So:
#   - "sending startup (diagnostic) report"               (the diagnostic path ran -> PRECONDITION + EXERCISED)
#   - ZERO "reconciliation failed for startup send: degraded"  (a startup degrade -> REGRESSION signature)
#   - "startup (diagnostic) report sent"                  (the diagnostic delivered -> confirmation)
#
# THE BROADER GOAL (reported, not gated): the temperature + retry-feedback fixes should
# also cut the general reconciler degrade rate (the ~33 events / 2 days = ~16.5/day
# baseline). "Materially fewer" is a human judgment over a noisy background, so this
# script REPORTS the pre/post degrade counts and the per-day rate for the WX-165.md
# procedure to interpret; it does not fail the automated verdict on them. The gate stays
# on the deterministic startup-degrade signature (verify-lib's design: gate on the
# change's own failure fingerprint, lean on reported sections for the statistical reads).
#
# Usage:  WX-165-verify.sh [--since 'YYYY-MM-DD HH:MM:SS'] [--log PATH] [--deploy-log PATH] [-h]
#         (no positional args; no arguments = the normal version-pinned run)
# Shell:  bash (WSL).  Log timestamps are UTC (the HarderWare PC runs on UTC).
# Shared scaffold in verify-lib.sh.

# No -e by design: verify-lib's cnt() returns 0 on no-match (`grep -cE ... || true`),
# and intermediate grep stages under pipefail legitimately exit non-zero on a metric
# miss -- those are treated as a valid zero count, so -e would exit prematurely.
set -uo pipefail

SELF="${BASH_SOURCE[0]}"
TICKET='WX-165'                                    # self-identification + header
VERSION='1.31.1'                                   # the release VERSION that shipped the fix -- the deploy pin
COMPONENTS=('WxReportSvc')                          # the service WX-165 ships in
TITLE='reconciler invention reduction'             # header description
MIN_WINDOW_HOURS=24                                # a full active day of cycles must accrue before PASS is valid
source "$(cd "$(dirname "$SELF")" && pwd)/verify-lib.sh"

vl_parse_args "$@"
vl_resolve_boundary     # sets SINCE/COMMIT/DEPLOY_INFO/BOUNDARY_SRC (WAIT-exits if undeployed)
vl_setup_window         # sets POST/LAST_TS/pre_start/elapsed/hh/mm/min_window_secs (WAIT-exits if no lines)

# ---- gating metrics over the post-deploy window -------------------------------
startup_ran=$(     printf '%s\n' "$POST" | cnt 'sending startup \(diagnostic\) report')          # precondition + exercised
startup_degraded=$(printf '%s\n' "$POST" | cnt 'reconciliation failed for startup send: degraded') # REGRESSION signature
startup_sent=$(    printf '%s\n' "$POST" | cnt 'startup \(diagnostic\) report sent')              # the diagnostic delivered

# The fix is broken iff a startup diagnostic degraded post-deploy (the very abort WX-165
# is meant to prevent for the phantom-band case).
regression=$startup_degraded

# ---- reported (non-gating) degrade-rate read ----------------------------------
# General reconciler degrades (any locality), pre vs post. A DROP shows up as
# post < pre (vl_health_delta's "new" stays 0 on a drop -- it only flags increases --
# so we read the raw before/after, not "new").
read -r deg_before deg_after _deg_new < <(vl_health_delta 'degrading to the parsed snapshot')
# Per-active-day post rate, for comparison against the ~16.5/day pre-fix baseline.
deg_rate=$(awk -v c="$deg_after" -v e="$elapsed" 'BEGIN { printf (e > 0 ? "%.1f" : "n/a"), (e > 0 ? c * 86400 / e : 0) }')

vl_header
echo
echo " WX-165 FINGERPRINT  (gating: the startup diagnostic must not degrade)"
echo "   startup diagnostic ran (a deploy logs one)  : $startup_ran   (PRECONDITION + EXERCISED -- 0 = none yet)"
echo "   startup diagnostic degraded                 : $startup_degraded   (expect 0 -- the failure signature)"
echo "   startup diagnostic SENT                      : $startup_sent   (the deploy verification delivered)"

if [ "$regression" -gt 0 ]; then
  echo
  echo " FAILURE-SIGNATURE LINES"
  printf '%s\n' "$POST" | grep 'reconciliation failed for startup send: degraded' || true
fi

echo
echo " REGRESSION-RATE READ  (reported, not gated -- judge in WX-165.md)"
echo "   reconciler degrades: pre=$deg_before  post=$deg_after  (equal-length windows)"
echo "   post degrade rate  : ~${deg_rate}/day   (pre-fix baseline ~16.5/day = the 33 events / 2 days)"
echo "   A material drop here is the temperature + retry-feedback payoff; a flat or"
echo "   higher rate means the generation-side fixes are not converging -- investigate"
echo "   even when the startup-degrade gate passes."

echo
echo " BACKGROUND HEALTH (new errors vs the equal-length pre-deploy window)"
read -r err_before err_after err_new < <(vl_health_delta ' ERROR ')
echo "   ERROR lines: pre=$err_before  post=$err_after  new=$err_new"

# Preconditioned verdict: with no startup diagnostic in the window there is nothing to
# gate on -> WAIT, not a false PASS. A startup degrade -> FAIL at any horizon. PASS is
# withheld until the 24h window AND a clean exercised diagnostic.
vl_verdict "$regression" "$startup_ran" \
  "a startup diagnostic degraded post-deploy -- the band suppression / temperature / retry-feedback did not prevent the phantom; dump the SendStartupReportAsync ERROR and the three reconcile attempts above it." \
  "the startup diagnostic sent without degrading; now confirm in WX-165.md that the general reconciler degrade rate dropped materially from the ~16.5/day baseline (the temperature + retry payoff)." \
  "$startup_ran" "a startup diagnostic report (a 'sending startup (diagnostic) report' line) in the window"
