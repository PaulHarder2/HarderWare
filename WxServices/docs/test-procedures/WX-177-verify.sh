#!/usr/bin/env bash
# WX-177-verify.sh - confirm WX-177 (narrative self-consistency, round 2) is live and that
# live Claude complies, by reading the WxReport service log after a deployment boundary.
#
# WX-177 adds a deterministic net + prompt rules so the closing/change-band prose does not
# contradict the snapshot:
#   - Defect A: a "the weekend stays dry" claim while a weekend day carries precip (new
#     CheckAggregateDryClaim, fail-closed through the WX-189 retry) + a prompt rule;
#   - Defect B: prompt rules only (cite the day's grid-headline figure; don't over-state a
#     window the grid renders dry). The deterministic weekday/multi-window catch was DEFERRED
#     to WX-283, gated on THIS verify's evidence.
# So after the deploy, over an active day:
#   - ZERO EXHAUSTED "calls the weekend dry" degrades   -> the PASS signal (Defect A: Claude
#     complies, or the new check false-fired -- read the surfaced lines to tell which)
#   - self-correcting weekend-dry retries are HEALTHY (the prompt+retry loop working)
#   - the pre-existing "asserts precipitation/storm activity at a local time" degrades
#     (ValidateClosingClaims -- the category/precip-at-a-dry-time family) should FALL vs the
#     equal-length pre-deploy window: this is the WX-283 EVIDENCE signal (informational --
#     it does NOT drive the verdict; a residual count is WX-283's call, not a WX-177 failure).
#
# Signatures are told apart by their INNER string, never the shared WX-189 "could not make ...
# prose self-consistent" wrapper: Defect A = "calls the weekend dry"; the category family =
# "asserts precipitation/storm activity at a local time"; WX-139's synoptic degrade (a third
# user of the same wrapper) = "attributes a synoptic mechanism" -- none are conflated here.
#
# Usage:  WX-177-verify.sh [--since 'YYYY-MM-DD HH:MM:SS'] [--log PATH] [--deploy-log PATH] [-h]
# Shell:  bash (WSL).  Log timestamps are UTC (the HarderWare PC runs on UTC).
# Lives in the repo beside its procedure (docs/test-procedures/WX-177.md); rides the same PR
# as the code it checks (WORKFLOW.md §13). The shared scaffold (arg parsing, version-pinned
# deploy boundary via deploy-info.sh, before/after window, header, verdict) is verify-lib.sh.

set -uo pipefail

SELF="${BASH_SOURCE[0]}"
TICKET='WX-177'                                    # self-identification + header
VERSION='1.47.2'                                   # the release VERSION under test -- the pin. Knowable at
                                                   # authoring (the bump ships with the change).
COMPONENTS=('WxReportSvc')                         # the service WX-177 ships in
TITLE='narrative self-consistency (aggregate-period dry claims)'
MIN_WINDOW_HOURS=24                                # a "full active day" of cycles before PASS is valid;
                                                   # below this the verdict is WAIT unless a conclusive
                                                   # FAIL (an exhausted weekend-dry degrade) fires.
source "$(cd "$(dirname "$SELF")" && pwd)/verify-lib.sh"

vl_parse_args "$@"
vl_resolve_boundary     # sets SINCE/COMMIT/DEPLOY_INFO/BOUNDARY_SRC (WAIT-exits if undeployed)
vl_setup_window         # sets POST/LAST_TS/pre_start/elapsed/hh/mm/min_window_secs (WAIT-exits if no lines)

# ---- metrics over the post-deploy window --------------------------------------
# Defect A fingerprint: the new "calls the weekend dry" validator message. A self-correcting
# retry logs the WARN but no ERROR; an EXHAUSTED degrade logs the "could not make ..." ERROR
# carrying that inner string -- the non-compliance / false-positive signal (the PASS/FAIL key).
wknd_retry=$(    printf '%s\n' "$POST" | grep 'calls the weekend dry' | cnt 'retrying')
wknd_exhausted=$(printf '%s\n' "$POST" | grep 'calls the weekend dry' | cnt 'could not make')

# WX-283 evidence: the pre-existing precip-at-a-dry-time degrades (ValidateClosingClaims). Did
# the prompt rules reduce them? Equal-length pre-deploy window cancels steady background, so the
# before -> after delta is the signal. Informational ONLY -- does not drive the verdict.
# SCOPED to narrative 'en': WX-168 (v1.49.0) gives the SAME closing check es coverage, so es prose
# now emits the identical inner string with an 'es' tag -- counting those here would inflate this
# en trend. The es family is measured separately by WX-168-verify.sh. (Change rides the WX-168 PR.)
read precip_before precip_after precip_new < <(vl_health_delta "narrative 'en' asserts precipitation/storm activity at a local time")

# Exercised = reports actually flowed post-deploy (the new binary is live and producing reports).
sent=$(      printf '%s\n' "$POST" | grep 'report sent' | cnt 'DeliverWeatherReportAsync')
scheduled=$( printf '%s\n' "$POST" | cnt 'generating scheduled report')

vl_header
echo
echo    " DEFECT A -- aggregate-period dry claim (the PASS/FAIL signal)"
printf  '   %-52s %s\n' 'Reports flowed since deploy (sent + scheduled):' "$(( sent + scheduled ))   $([ $(( sent + scheduled )) -gt 0 ] && echo '[fix is live, producing reports]' || echo '[none yet -- WAIT]')"
printf  '   %-52s %s\n' 'Self-correcting weekend-dry retries (healthy):'  "$wknd_retry   $([ "$wknd_retry" -eq 0 ] && echo '[Claude never mis-scoped the weekend]' || echo '[nudged, then complied]')"
printf  '   %-52s %s\n' 'EXHAUSTED weekend-dry degrades:'                 "$wknd_exhausted   $([ "$wknd_exhausted" -eq 0 ] && echo '[expected 0 -- Claude complies]' || echo '[!! read the lines below]')"
[ "$wknd_exhausted" -gt 0 ] && { echo "   --- EXHAUSTED weekend-dry lines (genuine -> strengthen prompt; false-positive -> tighten AggregateDryWords; either way a Bug) ---";
                                 printf '%s\n' "$POST" | grep 'calls the weekend dry' | grep 'could not make' | tail -5 | sed 's/^/     /'; }
echo
echo    " WX-283 EVIDENCE -- precip-at-a-dry-time degrades (ValidateClosingClaims); informational, not the verdict"
printf  '   %-52s %s\n' "Before deploy ($pre_start ->):"                  "$precip_before"
printf  '   %-52s %s\n' "After  deploy ($SINCE ->):"                      "$precip_after   (new above baseline: $precip_new)"
printf  '   %-52s %s\n' 'Read:'                                           "$([ "$precip_after" -lt "$precip_before" ] && echo 'FELL -> prompt is helping; WX-283 likely unneeded' || echo 'did NOT fall -> WX-283 evidence; consider the deterministic resolver')"
echo
# Verdict keys ONLY on Defect A's change-specific failure signature: an EXHAUSTED weekend-dry
# degrade (Claude kept the mis-scoped aggregate claim across 3 retries -> did not comply, OR the
# check false-fired). Self-correcting retries and the (informational) precip-at-dry-time trend
# never fail it. Exercised = reports flowed.
vl_verdict "$wknd_exhausted" "$(( sent + scheduled ))" \
  "an EXHAUSTED weekend-dry degrade means live Claude did NOT comply with the aggregate-period rule across 3 retries, or CheckAggregateDryClaim false-fired -- read the lines above: genuine mis-scope -> strengthen the prompt rule; legitimate prose -> tighten AggregateDryWords. Either way, open a Bug." \
  "reports flowed with ZERO exhausted weekend-dry degrades -- live Claude complies with the aggregate-period rule (any self-correcting retries above are the prompt+retry loop working). Note the WX-283 evidence line, then send to Done."
