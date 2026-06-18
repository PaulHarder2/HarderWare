#!/usr/bin/env bash
# WX-182-verify.sh - confirm WX-182 (degrade circuit-breaker) is live and working by
# reading the WxReport service log after a deployment boundary.
#
# WX-182 stops a locality that DEGRADES-TO-NO-SEND on a given input from re-paying a
# (deterministically failing) Claude reconcile every cycle: it records the degraded
# input on LocalityState.LastDegradedInputHash ("WX-182 breaker armed ..."), and when a
# due scheduled/first slot recurs on that SAME input it re-issues the last good report
# from cache with NO Claude call ("re-issuing the ... report from the last good
# snapshot"), or skips when there is no usable cached snapshot (self-heal on the next
# input change).
#
# This is a SAFETY NET. WX-180 prong A already removed the main non-convergence cause,
# so the breaker may legitimately not fire at all in a given window. The verdict is
# therefore PRECONDITIONED on a degrade-to-no-send having occurred (the breaker armed);
# with no degrade in the window there is nothing to verify -> WAIT (re-run during a
# non-convergence episode, or watch the Grafana cost stay flat through one).
#
# So when a non-convergent locality recurs we expect, over time:
#   - "WX-182 breaker armed" lines                   (a degrade-to-no-send -> PRECONDITION)
#   - "WX-182 degrade circuit-breaker" lines         (the breaker path runs / EXERCISED:
#                                                      re-issue-from-cache and/or skip)
#   - ZERO "failed to render or send cached scheduled report" (cached-send path errors
#                                                      -> REGRESSION signature)
#   - daily Claude cost stays flat even while a locality is stuck (confirm on Grafana;
#     the log fingerprint here is the deterministic proxy)
#
# Usage:  WX-182-verify.sh [--since 'YYYY-MM-DD HH:MM:SS'] [--log PATH] [--deploy-log PATH] [-h]
#         (no positional args; no arguments = the normal version-pinned run)
# Shell:  bash (WSL).  Log timestamps are UTC (the HarderWare PC runs on UTC).
# Shared scaffold in verify-lib.sh.

# No -e by design: verify-lib's cnt() returns 0 on no-match (`grep -cE ... || true`),
# and intermediate grep stages under pipefail legitimately exit non-zero on a metric
# miss -- those are treated as a valid zero count, so -e would exit prematurely.
set -uo pipefail

SELF="${BASH_SOURCE[0]}"
TICKET='WX-182'                                    # self-identification + header
VERSION='1.30.0'                                   # the release VERSION that shipped the feature -- the deploy pin
COMPONENTS=('WxReportSvc')                          # the service WX-182 ships in
TITLE='degrade circuit-breaker'                    # header description
MIN_WINDOW_HOURS=24                                # a full active day of cycles must accrue before PASS is valid
source "$(cd "$(dirname "$SELF")" && pwd)/verify-lib.sh"

vl_parse_args "$@"
vl_resolve_boundary     # sets SINCE/COMMIT/DEPLOY_INFO/BOUNDARY_SRC (WAIT-exits if undeployed)
vl_setup_window         # sets POST/LAST_TS/pre_start/elapsed/hh/mm/min_window_secs (WAIT-exits if no lines)

# ---- metrics over the post-deploy window --------------------------------------
armed=$(    printf '%s\n' "$POST" | cnt 'WX-182 breaker armed')                              # precondition: a degrade-to-no-send happened
exercised=$(printf '%s\n' "$POST" | cnt 'WX-182 degrade circuit-breaker')                    # breaker path ran (re-issue and/or skip)
cached=$(   printf '%s\n' "$POST" | cnt 'WX-182 degrade circuit-breaker .* re-issuing')      # served a scheduled report from cache (no Claude)
errors=$(   printf '%s\n' "$POST" | cnt 'failed to render or send cached scheduled report')  # cached-send path errors -> REGRESSION

# The fix is broken iff the cached-send path threw while delivering.
regression=$errors

vl_header
echo
echo " WX-182 FINGERPRINT"
echo "   breaker armed (degrade-to-no-send)         : $armed   (PRECONDITION -- 0 = no non-convergence to test against)"
echo "   breaker exercised (re-issue and/or skip)   : $exercised"
echo "   scheduled reports re-issued from cache      : $cached   (no Claude call)"
echo "   cached-send path errors                    : $errors   (expect 0 -- the failure signature)"

if [ "$regression" -gt 0 ]; then
  echo
  echo " FAILURE-SIGNATURE LINES"
  printf '%s\n' "$POST" | grep 'failed to render or send cached scheduled report' || true
fi

echo
echo " BACKGROUND HEALTH (new errors vs the equal-length pre-deploy window)"
read -r err_before err_after err_new < <(vl_health_delta ' ERROR ')
echo "   ERROR lines: pre=$err_before  post=$err_after  new=$err_new"

# Preconditioned verdict: with no degrade-to-no-send in the window (armed = 0) there is
# nothing to verify -> WAIT, not a false PASS (the breaker is a safety net).
vl_verdict "$regression" "$exercised" \
  "the cached send-from-snapshot path threw while delivering -- check the cached snapshot/report load and the renderer." \
  "the degrade circuit-breaker is engaging (re-issuing scheduled reports from cache and/or skipping) with no cached-send errors; now confirm the Grafana \$-rate stayed flat through the non-convergence episode." \
  "$armed" "a degrade-to-no-send (a 'WX-182 breaker armed' line) in the window"