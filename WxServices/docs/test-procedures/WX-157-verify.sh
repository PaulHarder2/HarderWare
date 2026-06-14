#!/usr/bin/env bash
# WX-157-verify.sh - confirm WX-157 (pre-scheduled quiet window + late-scheduled fix)
# is live and working by reading the WxReport service log after a deployment boundary.
#
# WX-157 added a day-banded, severity-aware *pre-scheduled quiet window* on
# *unscheduled* updates: a significant but non-severe-onset change is suppressed when
# the next scheduled slot falls within the change's day-band quiet window (schedule
# "1:90,3:180" -> a near-term change is quieted in the 90 min before the slot, a day-3+
# change in the 180 min before it) and its content rides the upcoming scheduled report.
# A not-severe->severe onset punches through, and the service-wide 90-minute MinGap
# remains a hard floor. It also reorders ShouldSend so a due scheduled slot fires on
# time, never deferred by a preceding unscheduled send (the late-scheduled bug).
#
# So after the deploy we expect, over time:
#   - "WX-157 quiet window suppressed" lines appear  (the fix's code path runs / EXERCISED)
#   - ZERO "invalid PreScheduledQuietSchedule" warnings (the schedule parses; fail-closed
#     would disable the quiet window -> REGRESSION signature)
#   - scheduled reports still go out on cadence       (quiet window is unscheduled-only)
#   - a not-severe->severe onset still sends          (punch-through; manual/severe-driven)
#   - daily Claude cost trends down from the post-WX-181 baseline (confirm on Grafana;
#     the log fingerprint here is the deterministic proxy)
#
# Usage:  WX-157-verify.sh [--since 'YYYY-MM-DD HH:MM:SS'] [--log PATH] [--deploy-log PATH] [-h]
#         (no positional args; no arguments = the normal version-pinned run)
# Shell:  bash (WSL).  Log timestamps are UTC (the HarderWare PC runs on UTC).
# Shared scaffold in verify-lib.sh.

# No -e by design: verify-lib's cnt() returns 0 on no-match (`grep -cE ... || true`),
# and intermediate grep stages under pipefail legitimately exit non-zero on a metric
# miss -- those are treated as a valid zero count, so -e would exit prematurely.
set -uo pipefail

SELF="${BASH_SOURCE[0]}"
TICKET='WX-157'                                    # self-identification + header
VERSION='1.29.0'                                   # the release VERSION that shipped the feature -- the deploy pin
COMPONENTS=('WxReportSvc')                          # the service WX-157 ships in
TITLE='pre-scheduled quiet window'                 # header description
MIN_WINDOW_HOURS=24                                # a full active day of cycles must accrue before PASS is valid
source "$(cd "$(dirname "$SELF")" && pwd)/verify-lib.sh"

vl_parse_args "$@"
vl_resolve_boundary     # sets SINCE/COMMIT/DEPLOY_INFO/BOUNDARY_SRC (WAIT-exits if undeployed)
vl_setup_window         # sets POST/LAST_TS/pre_start/elapsed/hh/mm/min_window_secs (WAIT-exits if no lines)

# ---- metrics over the post-deploy window --------------------------------------
suppressed=$( printf '%s\n' "$POST" | cnt 'WX-157 quiet window suppressed')        # enforce: Claude call skipped
would_suppr=$(printf '%s\n' "$POST" | cnt 'WX-157 quiet window .* WOULD suppress') # shadow: would-suppress, Claude still called
exercised=$(( suppressed + would_suppr ))                                          # fix path ran either way
bad_sched=$( printf '%s\n' "$POST" | cnt 'invalid PreScheduledQuietSchedule')      # fail-closed disable -> REGRESSION
scheduled=$( printf '%s\n' "$POST" | cnt 'generating scheduled report')            # cadence still flows (context)

# The fix is broken iff the schedule failed to parse (quiet window silently disabled).
regression=$bad_sched

vl_header
echo
echo " WX-157 FINGERPRINT"
echo "   quiet window suppressed (enforce, fix ran) : $suppressed"
echo "   quiet window WOULD suppress (shadow)       : $would_suppr"
echo "   quiet window exercised (either mode)       : $exercised   (expect > 0 once a change lands near a slot)"
echo "   invalid PreScheduledQuietSchedule          : $bad_sched   (expect 0 -- fail-closed would disable the window)"
echo "   --- context (not gating) ---"
echo "   scheduled reports generated                : $scheduled   (quiet window is unscheduled-only; cadence must persist)"

if [ "$regression" -gt 0 ]; then
  echo
  echo " FAILURE-SIGNATURE LINES"
  printf '%s\n' "$POST" | grep 'invalid PreScheduledQuietSchedule' || true
fi

echo
echo " BACKGROUND HEALTH (new errors vs the equal-length pre-deploy window)"
read -r err_before err_after err_new < <(vl_health_delta ' ERROR ')
echo "   ERROR lines: pre=$err_before  post=$err_after  new=$err_new"

vl_verdict "$regression" "$exercised" \
  "PreScheduledQuietSchedule failed to parse post-deploy -- the quiet window is disabled (fail-closed); check the config string and ReportConfig binding." \
  "the pre-scheduled quiet window is exercising and the schedule parses; now confirm scheduled reports arrived on their slot (not deferred), a severe onset punched through, and the Grafana \$-rate is trending down."