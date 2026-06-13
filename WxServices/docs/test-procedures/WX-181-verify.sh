#!/usr/bin/env bash
# WX-181-verify.sh - confirm WX-181 (day-banded update debounce) is live and working
# by reading the WxReport service log after a deployment boundary.
#
# WX-181 added a day-banded, severity-aware debounce on *unscheduled* updates: a
# significant but non-severe-onset change is suppressed when its day-band's minimum
# gap (schedule "1:6,3:12" -> days 1-2 need 6h, day 3+ need 12h since the last
# unscheduled send) has not elapsed. A not-severe->severe onset punches through, and
# the service-wide 90-minute MinGap remains a hard floor. This trims the residual
# update-churn cost left after WX-180.
#
# So after the deploy we expect, over time:
#   - "WX-181 debounce suppressed" lines appear      (the fix's code path runs / EXERCISED)
#   - ZERO "invalid UpdateDebounceSchedule" warnings (the schedule parses; fail-closed
#     would disable the debounce -> REGRESSION signature)
#   - scheduled reports still go out on cadence       (debounce keys on unscheduled only)
#   - a not-severe->severe onset still sends          (punch-through; manual/severe-driven)
#   - daily Claude cost trends down from the post-WX-180 baseline (confirm on Grafana;
#     the log fingerprint here is the deterministic proxy)
#
# Usage:  WX-181-verify.sh [--since 'YYYY-MM-DD HH:MM:SS'] [--log PATH] [--deploy-log PATH] [-h]
#         (no positional args; no arguments = the normal version-pinned run)
# Shell:  bash (WSL).  Log timestamps are UTC (the HarderWare PC runs on UTC).
# Shared scaffold in verify-lib.sh.

# No -e by design: verify-lib's cnt() returns 0 on no-match (`grep -cE ... || true`),
# and intermediate grep stages under pipefail legitimately exit non-zero on a metric
# miss -- those are treated as a valid zero count, so -e would exit prematurely.
set -uo pipefail

SELF="${BASH_SOURCE[0]}"
TICKET='WX-181'                                    # self-identification + header
VERSION='1.28.0'                                   # the release VERSION that shipped the feature -- the deploy pin
COMPONENTS=('WxReportSvc')                          # the service WX-181 ships in
TITLE='day-banded update debounce'                 # header description
MIN_WINDOW_HOURS=24                                # a full active day of cycles must accrue before PASS is valid
source "$(cd "$(dirname "$SELF")" && pwd)/verify-lib.sh"

vl_parse_args "$@"
vl_resolve_boundary     # sets SINCE/COMMIT/DEPLOY_INFO/BOUNDARY_SRC (WAIT-exits if undeployed)
vl_setup_window         # sets POST/LAST_TS/pre_start/elapsed/hh/mm/min_window_secs (WAIT-exits if no lines)

# ---- metrics over the post-deploy window --------------------------------------
suppressed=$( printf '%s\n' "$POST" | cnt 'WX-181 debounce suppressed')        # enforce: Claude call skipped
would_suppr=$(printf '%s\n' "$POST" | cnt 'WX-181 debounce .* WOULD suppress') # shadow: would-suppress, Claude still called
exercised=$(( suppressed + would_suppr ))                                       # fix path ran either way
bad_sched=$( printf '%s\n' "$POST" | cnt 'invalid UpdateDebounceSchedule')      # fail-closed disable -> REGRESSION
scheduled=$( printf '%s\n' "$POST" | cnt 'generating scheduled report')         # cadence still flows (context)

# The fix is broken iff the schedule failed to parse (debounce silently disabled).
regression=$bad_sched

vl_header
echo
echo " WX-181 FINGERPRINT"
echo "   debounce suppressed (enforce, fix ran) : $suppressed"
echo "   debounce WOULD suppress (shadow)       : $would_suppr"
echo "   debounce exercised (either mode)       : $exercised   (expect > 0 once a churny forecast recurs)"
echo "   invalid UpdateDebounceSchedule         : $bad_sched   (expect 0 -- fail-closed would disable debounce)"
echo "   --- context (not gating) ---"
echo "   scheduled reports generated            : $scheduled   (debounce is unscheduled-only; cadence must persist)"

if [ "$regression" -gt 0 ]; then
  echo
  echo " FAILURE-SIGNATURE LINES"
  printf '%s\n' "$POST" | grep 'invalid UpdateDebounceSchedule' || true
fi

echo
echo " BACKGROUND HEALTH (new errors vs the equal-length pre-deploy window)"
read -r err_before err_after err_new < <(vl_health_delta ' ERROR ')
echo "   ERROR lines: pre=$err_before  post=$err_after  new=$err_new"

vl_verdict "$regression" "$exercised" \
  "UpdateDebounceSchedule failed to parse post-deploy -- debounce is disabled (fail-closed); check the config string and ReportConfig binding." \
  "the day-banded debounce is exercising and the schedule parses; now confirm scheduled cadence held, a severe onset punched through, and the Grafana \$-rate is trending down."
