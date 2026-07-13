#!/usr/bin/env bash
# WX-293-verify.sh - confirm WX-293 (Closing "storms"-for-a-non-severe-window prompt fix) is
# live and that live Claude complies, by reading the WxReport service log after a deployment
# boundary.
#
# WX-293 is a PROMPT fix (plus a sharpened retry-feedback message): the reconciler prompt now
# carries an absolute, phase-aware storm-word gate and a closing-scoped multi-window reminder so
# the model stops rendering "storms" for a non-severe day-part of the Closing. The deterministic
# backstop (CheckSevereStormVocabulary, WX-284) is UNCHANGED and still fails closed through the
# WX-189 retry; the fix aims to keep the model from tripping it in the first place, so the Closing
# section is no longer dropped.
# So after the deploy, over an active day:
#   - ZERO EXHAUSTED "storm wording ... no severe block" degrades  -> the PASS signal (live Claude
#     complies within the retry budget; the Closing is not dropped for this reason)
#   - self-correcting storm-wording retries are HEALTHY (the strengthened prompt + sharpened retry
#     feedback nudging the model onto "rain", then it complying)
#
# Signature is told apart by its INNER string, never the shared WX-189 "could not make ... prose
# self-consistent" wrapper: WX-293 = "uses storm wording" (... "for a time the final_snapshot
# carries no severe block"); WX-177 = "calls the weekend dry"; the precip-at-a-dry-time family =
# "asserts precipitation/storm activity at a local time"; WX-139's synoptic degrade = "attributes
# a synoptic mechanism" -- none are conflated here.
#
# Usage:  WX-293-verify.sh [--since 'YYYY-MM-DD HH:MM:SS'] [--log PATH] [--deploy-log PATH] [-h]
# Shell:  bash (WSL).  Log timestamps are UTC (the HarderWare PC runs on UTC).
# Lives in the repo beside its procedure (docs/test-procedures/WX-293.md); rides the same PR as
# the code it checks (WORKFLOW.md §13). The shared scaffold (arg parsing, version-pinned deploy
# boundary via deploy-info.sh, before/after window, header, verdict) is verify-lib.sh.

set -uo pipefail

SELF="${BASH_SOURCE[0]}"
TICKET='WX-293'                                    # self-identification + header
VERSION='1.50.1'                                   # the release VERSION under test -- the pin. Knowable at
                                                   # authoring (the bump ships with the change).
COMPONENTS=('WxReportSvc')                         # the service WX-293 ships in
TITLE='Closing storm-wording for a non-severe window (prompt fix)'
MIN_WINDOW_HOURS=24                                # a "full active day" of cycles before PASS is valid;
                                                   # below this the verdict is WAIT unless a conclusive
                                                   # FAIL (an exhausted storm-wording degrade) fires.
source "$(cd "$(dirname "$SELF")" && pwd)/verify-lib.sh"

vl_parse_args "$@"
vl_resolve_boundary     # sets SINCE/COMMIT/DEPLOY_INFO/BOUNDARY_SRC (WAIT-exits if undeployed)
vl_setup_window         # sets POST/LAST_TS/pre_start/elapsed/hh/mm/min_window_secs (WAIT-exits if no lines)

# ---- metrics over the post-deploy window --------------------------------------
# WX-293 fingerprint: the "uses storm wording" rejection message. A self-correcting retry logs the
# WARN ("failed validation: ... uses storm wording ...; retrying with feedback") but no ERROR; an
# EXHAUSTED degrade logs the "could not make ... prose self-consistent" ERROR carrying that inner
# string -- the non-compliance signal (the PASS/FAIL key).
storm_retry=$(    printf '%s\n' "$POST" | grep 'uses storm wording' | cnt 'retrying')
storm_exhausted=$(printf '%s\n' "$POST" | grep 'uses storm wording' | cnt 'could not make')

# Exercised = reports actually flowed post-deploy (the new binary is live and producing reports).
sent=$(      printf '%s\n' "$POST" | grep 'report sent' | cnt 'DeliverWeatherReportAsync')
scheduled=$( printf '%s\n' "$POST" | cnt 'generating scheduled report')

vl_header
echo
echo    " WX-293 -- storm-wording for a non-severe window (the PASS/FAIL signal; section named per line)"
printf  '   %-52s %s\n' 'Reports flowed since deploy (sent + scheduled):' "$(( sent + scheduled ))   $([ $(( sent + scheduled )) -gt 0 ] && echo '[fix is live, producing reports]' || echo '[none yet -- WAIT]')"
printf  '   %-52s %s\n' 'Self-correcting storm-wording retries (healthy):' "$storm_retry   $([ "$storm_retry" -eq 0 ] && echo '[Claude never reached for "storms"]' || echo '[nudged, then complied]')"
printf  '   %-52s %s\n' 'EXHAUSTED storm-wording degrades:'               "$storm_exhausted   $([ "$storm_exhausted" -eq 0 ] && echo '[expected 0 -- Claude complies]' || echo '[!! read the lines below]')"
[ "$storm_exhausted" -gt 0 ] && { echo "   --- EXHAUSTED storm-wording lines (Claude kept 'storms' for a non-severe window across 3 retries -> strengthen the prompt further; a genuinely-severe window mis-flagged -> a validator Bug -- read to tell which) ---";
                                  printf '%s\n' "$POST" | grep 'uses storm wording' | grep 'could not make' | tail -5 | sed 's/^/     /'; }
echo
# Verdict keys ONLY on WX-293's change-specific failure signature: an EXHAUSTED storm-wording
# degrade (Claude kept "storms" for a non-severe window across 3 retries -> did not comply, so the
# Closing was dropped). Self-correcting retries never fail it -- they are the prompt+retry loop
# working. Exercised = reports flowed.
vl_verdict "$storm_exhausted" "$(( sent + scheduled ))" \
  "an EXHAUSTED storm-wording degrade means live Claude kept 'storms' for a non-severe window across 3 retries (the offending prose section -- Closing or ChangeSummary, named in the line -- was still dropped), OR CheckSevereStormVocabulary mis-flagged a genuinely-severe window -- read the lines above: persistent non-compliance -> strengthen the prompt gate further; a real severe window rejected -> a validator Bug. Either way, open a Bug." \
  "reports flowed with ZERO exhausted storm-wording degrades -- live Claude complies with the storm-word gate (any self-correcting retries above are the strengthened prompt + sharpened retry feedback working). Send to Done."
