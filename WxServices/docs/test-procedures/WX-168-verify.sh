#!/usr/bin/env bash
# WX-168-verify.sh - confirm WX-168 (per-language deterministic timing/claim validators, es plugin)
# is live and that the es lexicon does NOT false-reject legitimate es prose, by reading the WxReport
# service log after a deployment boundary.
#
# WX-168 makes the WX-149 ({q:time}<->day-part), WX-151 ({chN}-anchored) and WX-152/WX-177 (closing /
# aggregate precip-at-a-dry-time) validators language-pluggable and adds the Spanish lexicon. Offline
# tests prove they fire on genuine es errors and leave en unchanged; what only production shows is
# whether the es lexicon FALSE-REJECTS legitimate es prose (degrading a good es report).
#
# So after the deploy, over the es report cycle (es has ~1 recipient -> es reports are infrequent, so
# the window is longer / lower-signal than WX-139/177):
#   - reports flow (the new binary is live)                             -> "exercised"
#   - EXHAUSTED es timing/claim degrades: expected 0. A non-zero count is NOT automatically a failure
#     -- it is the two-sided signal WX-177's weekend-dry key is: read the surfaced lines. Claude kept
#     writing genuinely-wrong es prose across 3 retries (the net correctly caught it -> the deterministic
#     floor is WORKING, close PASS) OR the es lexicon false-fired on legitimate es prose (-> the lexicon
#     is too aggressive -> open a Bug, tighten SpanishLexicon).
#   - self-correcting es retries are HEALTHY (the prompt+retry loop working).
#
# DISCRIMINATION: the es closing degrade shares its inner string with the en family
# ("asserts precipitation/storm activity at a local time"); it is told apart ONLY by the narrative
# 'es' tag -- so every grep here is 'es'-scoped, and WX-177-verify.sh's WX-283 count is 'en'-scoped,
# so the two never conflate. The es day-part degrade ("...next to a {q:time} token that renders to...")
# is likewise the same string the en day-part check uses, so it too is 'es'-scoped here.
#
# Usage:  WX-168-verify.sh [--since 'YYYY-MM-DD HH:MM:SS'] [--log PATH] [--deploy-log PATH] [-h]
# Shell:  bash (WSL).  Log timestamps are UTC (the HarderWare PC runs on UTC).
# Lives beside its procedure (docs/test-procedures/WX-168.md); rides the same PR as the code it checks
# (WORKFLOW.md §13). The shared scaffold is verify-lib.sh.

set -uo pipefail

SELF="${BASH_SOURCE[0]}"
TICKET='WX-168'
VERSION='1.49.0'
COMPONENTS=('WxReportSvc')
TITLE='per-language timing/claim validators (es plugin) -- no false-rejects'
MIN_WINDOW_HOURS=24
source "$(cd "$(dirname "$SELF")" && pwd)/verify-lib.sh"

vl_parse_args "$@"
vl_resolve_boundary
vl_setup_window

# ---- es-scoped metrics over the post-deploy window ----------------------------
# Two es timing/claim families (both told apart from their en twins by the 'es' tag):
#   closing/aggregate precip-at-a-dry-time (WX-152/177) + day-part<->{q:time} (WX-149).
es_close_retry=$(    printf '%s\n' "$POST" | grep "narrative 'es' asserts precipitation/storm activity at a local time" | cnt 'retrying')
es_close_exhausted=$(printf '%s\n' "$POST" | grep "narrative 'es' asserts precipitation/storm activity at a local time" | cnt 'could not make')
es_part_retry=$(     printf '%s\n' "$POST" | grep "narrative 'es'" | grep 'next to a {q:time} token that renders to' | cnt 'retrying')
es_part_exhausted=$( printf '%s\n' "$POST" | grep "narrative 'es'" | grep 'next to a {q:time} token that renders to' | cnt 'could not make')
es_retry=$(( es_close_retry + es_part_retry ))
es_exhausted=$(( es_close_exhausted + es_part_exhausted ))

sent=$(      printf '%s\n' "$POST" | grep 'report sent' | cnt 'DeliverWeatherReportAsync')
scheduled=$( printf '%s\n' "$POST" | cnt 'generating scheduled report')

vl_header
echo
echo    " ES FALSE-REJECT MONITOR (the inspect signal -- NOT an auto-fail)"
printf  '   %-54s %s\n' 'Reports flowed since deploy (sent + scheduled):' "$(( sent + scheduled ))   $([ $(( sent + scheduled )) -gt 0 ] && echo '[binary live, producing reports]' || echo '[none yet -- WAIT]')"
printf  '   %-54s %s\n' 'Self-correcting es timing/claim retries (healthy):' "$es_retry   $([ "$es_retry" -eq 0 ] && echo '[es prose never tripped the net]' || echo '[nudged, then complied]')"
printf  '   %-54s %s\n' 'EXHAUSTED es timing/claim degrades:'             "$es_exhausted   $([ "$es_exhausted" -eq 0 ] && echo '[expected 0 -- no false-reject observed]' || echo '[!! read the lines -- genuine catch vs false-reject]')"
[ "$es_exhausted" -gt 0 ] && { echo "   --- EXHAUSTED es lines (genuine es error -> net working, close PASS; legitimate es prose -> tighten SpanishLexicon, open a Bug) ---";
                               printf '%s\n' "$POST" | grep "narrative 'es'" | grep 'could not make' | tail -5 | sed 's/^/     /'; }
echo
# Verdict keys on es_exhausted -- but the message makes clear it is a READ-THE-LINES signal, not an
# automatic failure: a genuine es error caught is the deterministic floor working as designed.
vl_verdict "$es_exhausted" "$(( sent + scheduled ))" \
  "an EXHAUSTED es timing/claim degrade means either the es net correctly caught a genuinely-wrong es report (the deterministic floor WORKING -- close PASS) OR the SpanishLexicon false-rejected legitimate es prose (too aggressive -> open a Bug and tighten it). READ the surfaced lines to tell which -- this is not an automatic failure." \
  "reports flowed with ZERO exhausted es timing/claim degrades -- the deployed es net is live and did NOT false-reject legitimate es prose over the window (es is infrequent, so treat a quiet window as trustworthy-absence, not proof the net fired -- the offline suite proves that)."
