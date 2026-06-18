#!/usr/bin/env bash
# WX-180-verify.sh - confirm WX-180 (windKt clamp + degrade cost fix) is live and
# working by reading the WxReport service log after a deployment boundary.
#
# WX-180 changed the reconciler: a folded gust in windKt.max is now CLAMPED to the
# sustained ceiling instead of reject -> retry -> degrade (the ~$45/day cost
# incident, where a gusty forecast degraded every cycle and re-burned reconciliations).
# So after the deploy we expect, over time:
#   - "clamping windKt.max ... (WX-180)" lines appear   (the fix's code path runs)
#   - ZERO degradations citing windKt                    (windKt no longer degrades)
#   - ZERO old-reject lines "windKt.max is ... SUSTAINED" (the reject path is gone)
#   - the Watonga every-cycle scheduled retry-storm stops
#   - daily Claude cost falls back to baseline (confirm on the Grafana panel; the log
#     fingerprint here is the deterministic proxy)
#
# Usage:  WX-180-verify.sh [--since 'YYYY-MM-DD HH:MM:SS'] [--log PATH] [--deploy-log PATH] [-h]
#         (no positional args; no arguments = the normal version-pinned run)
# Shell:  bash (WSL).  Log timestamps are UTC (the HarderWare PC runs on UTC).
# Rides the PR alongside WX-181 as documented out-of-scope work: the WX-180 cost
# hotfix merged before its WORKFLOW.md §13 artifact existed, so the script + procedure
# were added on the next branch. Shared scaffold in verify-lib.sh.

# No -e by design: verify-lib's cnt() returns 0 on no-match (`grep -cE ... || true`),
# and intermediate grep stages under pipefail legitimately exit non-zero on a metric
# miss -- those are treated as a valid zero count, so -e would exit prematurely.
set -uo pipefail

SELF="${BASH_SOURCE[0]}"
TICKET='WX-180'                                    # self-identification + header
VERSION='1.27.1'                                   # the release VERSION that shipped the fix -- the deploy pin
COMPONENTS=('WxReportSvc')                          # the service WX-180 ships in
TITLE='windKt clamp + degrade cost fix'            # header description
MIN_WINDOW_HOURS=24                                # a full active day of cycles must accrue before PASS is valid
source "$(cd "$(dirname "$SELF")" && pwd)/verify-lib.sh"

vl_parse_args "$@"
vl_resolve_boundary     # sets SINCE/COMMIT/DEPLOY_INFO/BOUNDARY_SRC (WAIT-exits if undeployed)
vl_setup_window         # sets POST/LAST_TS/pre_start/elapsed/hh/mm/min_window_secs (WAIT-exits if no lines)

# ---- metrics over the post-deploy window --------------------------------------
clamps=$(    printf '%s\n' "$POST" | cnt 'clamping windKt.max')
old_reject=$(printf '%s\n' "$POST" | cnt 'windKt\.max is .* SUSTAINED')
windkt_degr=$(printf '%s\n' "$POST" | grep 'could not produce a self-consistent report' | cnt 'windKt')
degr_total=$( printf '%s\n' "$POST" | cnt 'could not produce a self-consistent report')
watonga_gen=$(printf '%s\n' "$POST" | grep -i 'watonga' | cnt 'generating .* report')

# The fix is broken iff windKt still degrades, or the old reject string reappears.
regression=$(( old_reject + windkt_degr ))

vl_header
echo
echo " WX-180 FINGERPRINT"
echo "   clamping windKt.max (fix path ran)   : $clamps"
echo "   old reject 'windKt.max is SUSTAINED' : $old_reject   (expect 0)"
echo "   degradations citing windKt           : $windkt_degr   (expect 0)"
echo "   --- context (not gating) ---"
echo "   degradations, any cause              : $degr_total   (residual = WX-165 time-word, separate ticket)"
echo "   Watonga scheduled generations        : $watonga_gen   (was ~32 / 6h pre-fix; the loop should be gone)"

if [ "$regression" -gt 0 ]; then
  echo
  echo " FAILURE-SIGNATURE LINES"
  printf '%s\n' "$POST" | grep -E 'windKt\.max is .* SUSTAINED' || true
  printf '%s\n' "$POST" | grep 'could not produce a self-consistent report' | grep 'windKt' || true
fi

echo
echo " BACKGROUND HEALTH (new errors vs the equal-length pre-deploy window)"
read -r err_before err_after err_new < <(vl_health_delta ' ERROR ')
echo "   ERROR lines: pre=$err_before  post=$err_after  new=$err_new"

vl_verdict "$regression" "$clamps" \
  "windKt still degrades/rejects post-deploy -- the clamp is not taking; check NormalizeWindKtSustained." \
  "windKt folds are clamped, not degraded; now confirm the Grafana \$-rate is back to baseline."
