#!/usr/bin/env bash
# WX-139-verify.sh - confirm WX-139 (synoptic-mechanism grounding) is live and that
# live Claude COMPLIES with it, by reading the WxReport service log after a deployment
# boundary.
#
# WX-139 adds two layers so reader-facing prose never attributes a synoptic MECHANISM
# ("...as a front pushes through") it cannot evidence from single-point data:
#   1. a prompt rule (ReconcilerPrompts) telling Claude to state the effect, never a cause;
#   2. a deterministic validator (CheckProse) that fails closed through the WX-189 retry.
# So after the deploy we expect, over an active day:
#   - reports flow normally (the fix is live)                         -> "exercised"
#   - ZERO EXHAUSTED synoptic-mechanism degrades                      -> the PASS signal
#     (an exhausted degrade = Claude kept a mechanism through 3 retries = did NOT comply,
#      OR the regex false-fired on legit prose; either way, read the surfaced lines)
#   - self-correcting retries (Claude drops the mechanism on a nudge) are HEALTHY and
#     shown for context; they mean the prompt+retry loop is working, not a failure.
#
# NOT this ticket, shown only to keep the two apart: the pre-existing "Closing prose
# self-consistent ... asserts precipitation/storm activity at a local time the snapshot
# leaves dry" degrades (ValidateClosingClaims / WX-177 territory) share the SAME
# WX-189 "could not make ... prose self-consistent" wrapper. This script keys on the
# INNER string "synoptic mechanism", never the wrapper, so the two are never conflated.
#
# Usage:  WX-139-verify.sh [--since 'YYYY-MM-DD HH:MM:SS'] [--log PATH] [--deploy-log PATH] [-h]
#         (no positional args; no arguments at all = the normal version-pinned run)
# Shell:  bash (WSL).  Log timestamps are UTC (the HarderWare PC runs on UTC).
# Lives in the repo beside its procedure (docs/test-procedures/WX-139.md): a
# change-specific verification rides the same PR as the code it checks (WORKFLOW.md §13).
#
# The shared scaffold -- arg parsing, the version-pinned deploy boundary (deploy-info.sh),
# the before/after window, the header, and the verdict -- lives in verify-lib.sh, sourced
# below; only WX-139's metrics + verdict logic are here.

set -uo pipefail

SELF="${BASH_SOURCE[0]}"
TICKET='WX-139'                                    # self-identification + header
VERSION='1.47.1'                                   # the release VERSION under test -- the pin. Knowable at
                                                   # authoring (the bump ships with the change), so the
                                                   # script is review-complete; later versions won't match.
COMPONENTS=('WxReportSvc')                         # the service WX-139 ships in
TITLE='synoptic-mechanism grounding (state the effect, never an invented cause)'
MIN_WINDOW_HOURS=24                                # a "full active day" of cycles must accrue before PASS
                                                   # is a valid call; below this the verdict is WAIT unless
                                                   # a conclusive FAIL (an exhausted mechanism degrade) fires.
source "$(cd "$(dirname "$SELF")" && pwd)/verify-lib.sh"

vl_parse_args "$@"
vl_resolve_boundary     # sets SINCE/COMMIT/DEPLOY_INFO/BOUNDARY_SRC (WAIT-exits if undeployed)
vl_setup_window         # sets POST/LAST_TS/pre_start/elapsed/hh/mm/min_window_secs (WAIT-exits if no lines)

# ---- metrics over the post-deploy window --------------------------------------
# WX-139's fingerprint is the validator message "attributes a synoptic mechanism".
# A self-correcting retry logs the WARN (attempt N/3; retrying) but no ERROR; an
# EXHAUSTED degrade logs the ERROR "could not make ... prose self-consistent" carrying
# that same inner string -- that is the non-compliance / false-positive signal.
mech_retry=$(    printf '%s\n' "$POST" | grep 'attributes a synoptic mechanism' | cnt 'retrying')
mech_exhausted=$(printf '%s\n' "$POST" | grep 'synoptic mechanism'              | cnt 'could not make')

# Unrelated, shown ONLY to keep it distinct (ValidateClosingClaims / WX-177): the
# precip-at-a-dry-time degrade. Same WX-189 wrapper, different inner string -- never
# folded into the WX-139 counts above.
cc_exhausted=$(  printf '%s\n' "$POST" | cnt 'asserts precipitation/storm activity at a local time')

# Exercised = reports actually flowed post-deploy (the new binary is live and producing
# reports, which are now mechanism-free by construction). Either path proves liveness.
sent=$(      printf '%s\n' "$POST" | grep 'report sent' | cnt 'DeliverWeatherReportAsync')
scheduled=$( printf '%s\n' "$POST" | cnt 'generating scheduled report')

# Background health (context only -- does NOT drive the verdict; the verdict keys on the
# WX-139 exhausted-mechanism signal). Equal-length pre-deploy window cancels steady noise.
read errors_before errors new_errors < <(vl_health_delta ' ERROR ')

vl_header
echo
echo    " NEW RULE LIVE + CLAUDE COMPLYING?"
printf  '   %-52s %s\n' 'Reports flowed since deploy (sent + scheduled):' "$(( sent + scheduled ))   $([ $(( sent + scheduled )) -gt 0 ] && echo '[fix is live, producing reports]' || echo '[none yet -- WAIT]')"
printf  '   %-52s %s\n' 'Self-correcting mechanism retries (healthy):'    "$mech_retry   $([ "$mech_retry" -eq 0 ] && echo '[Claude never even tried a cause]' || echo '[nudged, then complied -- prompt+retry working]')"
printf  '   %-52s %s\n' 'EXHAUSTED synoptic-mechanism degrades:'          "$mech_exhausted   $([ "$mech_exhausted" -eq 0 ] && echo '[expected 0 -- Claude complies]' || echo '[!! read the lines below]')"
[ "$mech_exhausted" -gt 0 ] && { echo "   --- EXHAUSTED synoptic-mechanism lines (genuine -> strengthen prompt; false-positive -> tighten regex; either way a Bug) ---";
                                 printf '%s\n' "$POST" | grep 'synoptic mechanism' | grep 'could not make' | tail -5 | sed 's/^/     /'; }
[ "$mech_retry" -gt 0 ] && { echo "   --- self-corrected mechanism retries (context; the loop worked) ---";
                             printf '%s\n' "$POST" | grep 'attributes a synoptic mechanism' | grep 'retrying' | tail -3 | sed 's/^/     /'; }
echo
echo    " NOT WX-139 -- do NOT conflate (ValidateClosingClaims / WX-177 territory)"
printf  '   %-52s %s\n' 'precip-at-a-dry-time Closing degrades:'          "$cc_exhausted   [same WX-189 wrapper, different defect -- separate ticket]"
echo
echo    " BACKGROUND HEALTH (context only -- does NOT drive the verdict)"
printf  '   %-52s %s\n' 'ERROR lines (before -> after):'                  "$errors_before -> $errors   (new: $new_errors)"
echo
# Verdict keys on WX-139's change-specific failure signature: an EXHAUSTED synoptic-
# mechanism degrade (Claude kept a mechanism through 3 retries -> did not comply, OR the
# regex false-fired). Self-correcting retries and the unrelated precip-at-dry-time
# degrades never fail it. Exercised = reports flowed (fix is live and producing them).
vl_verdict "$mech_exhausted" "$(( sent + scheduled ))" \
  "an EXHAUSTED synoptic-mechanism degrade means live Claude did NOT comply across 3 retries, or the regex false-fired -- read the lines above: if the flagged prose names a genuine mechanism, strengthen the prompt rule; if it is legitimate prose, tighten the regex. Either way, open a Bug." \
  "reports flowed with ZERO exhausted synoptic-mechanism degrades -- live Claude complies with the mechanism-grounding rule (any self-correcting retries above are the prompt+retry loop working). Confirm the precip-at-dry-time count is NOT conflated (it is WX-177's, not this), then send to Done."
