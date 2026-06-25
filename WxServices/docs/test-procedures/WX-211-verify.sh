#!/usr/bin/env bash
# WX-211-verify.sh - confirm template generation runs OFF the report send-path (after the sends,
# never before) and the cost-instrumentation fixes are live, from wxreport-svc.log since the 1.35.1
# deploy.
#
# THE CHANGE: generation was running at the top of RunCycleAsync (ahead of the sends), delaying
# reports by a multi-second translation. The send phase is now RunReportSendsAsync and the cycle
# runs sends -> generation -> count. Plus: the per-call token line reads "Claude tokens" (not
# "Claude reconciliation tokens"), and a generation increments wxreport.claude.calls.total.
#
# THE FINGERPRINTS (in wxreport-svc.log since the deploy boundary):
#   1. NEW-BINARY signal (deterministic, present every send cycle): ZERO "Claude reconciliation
#      tokens" lines -- that wording is gone (item C). The old binary logged it on every
#      reconciliation, so any occurrence means the old binary is still live. This is the primary,
#      always-on check; it does not need a generation to have happened.
#   2. REORDER (item A): in any cycle that has BOTH a "Report cycle complete." line and a
#      "generating templates" line, the generation MUST come after the complete. A generation before
#      the complete in a send cycle is the OLD order. (Opportunistic -- only present once a language
#      was generated post-deploy; the WX-211.md procedure enables one first.)
# Item B (the calls counter) is a metric, not a log line -- checked on Grafana per WX-211.md, not here.
#
# THE GATE: deterministic -- no time window. PASS needs >= MIN_CYCLES report cycles since the deploy
# (so the new binary is proven live) and ZERO failure-signature lines. Until then -> WAIT.
#
# Log-reading verify (like WX-210). Reuses verify-lib.sh for the version-pinned deploy boundary, the
# log window, and the PASS/FAIL/WAIT decision.
#
# Usage:  WX-211-verify.sh [--since 'YYYY-MM-DD HH:MM:SS'] [--log PATH] [--deploy-log PATH] [-h]
# Shell:  bash (WSL). The PC runs in UTC. Shared scaffold in verify-lib.sh.

set -uo pipefail

SELF="${BASH_SOURCE[0]}"
TICKET='WX-211'                                      # self-identification + header
VERSION='1.35.1'                                     # the release VERSION that shipped the change -- the deploy pin
COMPONENTS=('WxReportSvc')                            # the service WX-211 ships in
TITLE='generation off the send-path -- reorder + cost instrumentation'
MIN_CYCLES=2                                         # PASS gate: minimum report cycles since deploy (proves the new binary is live)
MIN_WINDOW_MINUTES=1                                 # satisfies verify-lib's >0 floor; the real gate is the cycle count + fingerprints (min_window_secs forced to ZERO below)
source "$(cd "$(dirname "$SELF")" && pwd)/verify-lib.sh"

vl_parse_args "$@"
vl_resolve_boundary     # SINCE / COMMIT / DEPLOY_INFO (WAIT-exits if VERSION not deployed yet)
vl_setup_window         # POST (log lines since SINCE), LAST_TS, hh, mm (WAIT-exits if none)
min_window_secs=0       # deterministic -- no wait time; the gate is the cycle count + fingerprints

cycles=$(printf '%s\n' "$POST" | cnt 'Starting report cycle\.')                # exercised: report cycles on the new binary
old_wording=$(printf '%s\n' "$POST" | cnt 'Claude reconciliation tokens')      # FAILURE SIGNATURE: the retired wording (old binary / item C not shipped)
new_wording=$(printf '%s\n' "$POST" | cnt 'Claude tokens \[')                  # positive evidence: the new per-call wording
generations=$(printf '%s\n' "$POST" | cnt 'WX-172: generating templates for')  # positive evidence: a generation ran post-deploy (the reorder is checkable when >0)

# REORDER check (item A): a violation is a cycle that has a "Report cycle complete." AND a
# "generating templates" line where the generation came BEFORE the complete (the old top-of-cycle
# order). Cycles are delimited by "Starting report cycle." lines. (No apostrophes in this awk
# program -- they would close the single-quoted block, WX-197.)
reorder_violations=$(printf '%s\n' "$POST" | awk '
  function close_cycle() { if (have_complete && have_gen && gen_before) viol++ }
  /Starting report cycle\./        { if (seen_start) close_cycle(); seen_start=1; have_complete=0; have_gen=0; gen_before=0; next }
  !seen_start                      { next }
  /Report cycle complete\./        { have_complete=1; next }
  /WX-172: generating templates for/ { have_gen=1; if (!have_complete) gen_before=1; next }
  END { if (seen_start) close_cycle(); print viol+0 }')

read -r err_before err_after err_new < <(vl_health_delta ' ERROR ')            # background health vs the equal-length pre-deploy window

regression=$(( old_wording + reorder_violations ))

vl_header
echo
echo " WX-211 FINGERPRINT  (wxreport-svc.log since the deploy boundary)"
echo "   report cycles (exercised)                   : $cycles   (PASS needs >= $MIN_CYCLES)"
echo "   'Claude reconciliation tokens' lines        : $old_wording   (expect 0 -- the retired wording = failure signature)"
echo "   'Claude tokens [' lines                     : $new_wording   (positive evidence -- new per-call wording, item C)"
echo "   generations seen ('generating templates')   : $generations   (positive evidence; the reorder is checkable when >= 1)"
echo "   reorder violations (gen BEFORE send-complete): $reorder_violations   (expect 0 -- item A; generation must run after the sends)"
echo "                                          total : $regression   (expect 0)"
echo
echo " BACKGROUND HEALTH  (equal-length pre-deploy window cancels steady noise)"
echo "   ERROR lines   before=$err_before  after=$err_after  new=$err_new"
echo
if [ "$generations" -eq 0 ]; then
  echo " NOTE: no generation has run since the deploy, so the reorder (item A) is not yet positively"
  echo "       exercised — the old-wording check still proves the new binary is live. Enable a fresh"
  echo "       language (WX-211.md §2) and re-run to confirm the reorder."
  echo
fi

precond=$(( (cycles >= MIN_CYCLES || regression > 0) ? 1 : 0 ))
vl_verdict "$regression" "$cycles" \
  "a WX-211 failure signature is present: either a retired 'Claude reconciliation tokens' line (old binary, or item C not deployed) or a generation that ran BEFORE its cycle's send-complete (the old top-of-cycle order). Inspect wxreport-svc.log and RunCycleAsync/RunReportSendsAsync." \
  "generation runs off the send-path across $cycles report cycle(s): the retired token wording is gone and no generation preceded its cycle's send-complete (item A reorder holds; $generations generation(s) observed)." \
  "$precond" "at least $MIN_CYCLES report cycles since the deploy (got $cycles)"
