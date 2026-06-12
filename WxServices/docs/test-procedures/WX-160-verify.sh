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
# The shared scaffold -- arg parsing, the version-pinned deploy boundary (via the
# shared deploy-info.sh helper), the before/after window, and the header -- lives in
# verify-lib.sh, sourced below; only WX-160's metrics + verdict logic are here. The
# boundary is version-pinned: this script embeds the release VERSION it tests
# (knowable at authoring -- the bump ships with the change) and its COMPONENTS, and
# verify-lib asks deploy-info.sh for the most recent matching deploy. Pinning on
# VERSION means a later release at another version doesn't move our boundary --
# WX-160's window stays anchored to the deploy that shipped it. Pass --since to
# override manually; if the VERSION isn't deployed yet, the verdict is WAIT.

set -uo pipefail

SELF="${BASH_SOURCE[0]}"
TICKET='WX-160'                                    # self-identification + header
VERSION='1.26.0'                                   # the release VERSION under test -- the pin. Knowable at
                                                   # authoring (the bump ships with the change), so the
                                                   # script is review-complete; later versions won't match.
COMPONENTS=('WxReportSvc')                          # the service(s) WX-160 ships in
TITLE='TAF-aware significance gate'                # header description
MIN_WINDOW_HOURS=24                                # a "full active day" of cycles must accrue before PASS
                                                   # is a valid call; below this the verdict is WAIT unless
                                                   # a conclusive FAIL fires (a quiet few hours proves
                                                   # nothing, but a failure still does)
source "$(cd "$(dirname "$SELF")" && pwd)/verify-lib.sh"

vl_parse_args "$@"
vl_resolve_boundary     # sets SINCE/COMMIT/DEPLOY_INFO/BOUNDARY_SRC (WAIT-exits if undeployed)
vl_setup_window         # sets POST/LAST_TS/pre_start/elapsed/hh/mm/min_window_secs (WAIT-exits if no lines)

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

taf_fresh_before=$(win "$pre_start" "$SINCE" | cnt 'fired: taf-fresh')

# Health is scoped to NEW problems via the equal-length pre-deploy window: a steady
# background of unrelated reconciler degradations (e.g. WX-165 time-of-day
# contradictions) appears in both windows and cancels, so only what THIS deploy
# introduced pushes the after-count up.
read errors_before errors new_errors < <(vl_health_delta ' ERROR ')
read degraded_before degraded new_degraded < <(vl_health_delta 'reconciliation degraded|could not produce a self-consistent')

# WX-160's failure signature: taf-fresh firing (the retired criterion -> gate not live)
# OR an exception in WX-160's own components (a crash in the TAF merge or the merged-body
# gate), scoped to ERROR/FATAL so the reconciler's prose-validator degrades never match.
# Absence of taf-fresh alone wouldn't catch a crash; this pairs the two.
wx160_crash=$(printf '%s\n' "$POST" | grep -E ' (ERROR|FATAL) ' | cnt 'TafBlockProjector|SignificanceGate')
regressions=$(( taf_fresh + wx160_crash ))

vl_header
echo
echo    " NEW CODE LIVE?"
printf  '   %-46s %s\n' '"TAF-merged" suppression wording present:' "$([ "$new_wording" -gt 0 ] && echo "YES ($new_wording)  <- 1.26.0 gate is running" || echo 'not yet (no suppressions so far)')"
printf  '   %-46s %s\n' '"fired: taf-fresh" lines after deploy:'    "$taf_fresh   $([ "$taf_fresh" -eq 0 ] && echo '[expected 0 - criterion retired]' || echo '[!! UNEXPECTED - old binary still running?]')"
printf  '   %-46s %s\n' 'WX-160 component errors (merge/gate):'     "$wx160_crash   $([ "$wx160_crash" -eq 0 ] && echo '[expected 0]' || echo '[!! crash in WX-160 code]')"
[ "$wx160_crash" -gt 0 ] && { echo "   --- WX-160 component error lines ---"; printf '%s\n' "$POST" | grep -E ' (ERROR|FATAL) ' | grep -E 'TafBlockProjector|SignificanceGate' | tail -5 | sed 's/^/     /'; }
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
echo    " BACKGROUND HEALTH (context only -- does NOT drive the verdict; the verdict keys on taf-fresh)"
printf  '   %-46s %s\n' 'ERROR lines (before -> after):'            "$errors_before -> $errors   (new: $new_errors)"
printf  '   %-46s %s\n' 'Reconciliation degraded (before -> after):' "$degraded_before -> $degraded   (new: $new_degraded)"
[ "$errors"   -gt 0 ] && { echo "   --- ERROR lines in window (may include pre-existing background; see before->after) ---"; printf '%s\n' "$POST" | grep ' ERROR ' | sed 's/^/     /'; }
[ "$passed"   -gt 0 ] && { echo; echo "   gate-passed 'fired:' reasons (should be REAL criteria, never taf-fresh):";
                           printf '%s\n' "$POST" | grep -oE 'fired: .*' | sort | uniq -c | sort -rn | sed 's/^/     /'; }
[ "$suppressed" -gt 0 ] && { echo; echo "   sample suppressions:";
                           printf '%s\n' "$POST" | grep 'significance gate suppressed' | tail -3 | sed -E 's/\[[^]]*\]//; s/^/     /'; }
echo
# Verdict keys on WX-160's CHANGE-SPECIFIC failure signature -- 'regressions' = taf-fresh
# firing (the retired criterion -> gate not live) + any WX-160 component crash, both shown
# in NEW CODE LIVE above. If 0, WX-160 works regardless of unrelated reconciler/background
# noise (which the BACKGROUND HEALTH section shows for context but does NOT fail on).
# Exercised = the gate actually made a decision (suppressed or passed) -- WX-160 made the
# gate evaluate the GFS+TAF merged body on BOTH paths, so either exercises the new code.
vl_verdict "$regressions" "$(( suppressed + passed ))" \
  "taf-fresh firing or a TafBlockProjector/gate error means WX-160 isn't live/working -> confirm the $VERSION deploy/restart and check the lines above." \
  "taf-fresh retired (0), no WX-160 component error; the cost win shows as 'gate SUPPRESSED' replacing taf-fresh Claude calls. Confirm the gate-passed reasons above are real criteria, then send to Done."
