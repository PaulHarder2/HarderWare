#!/usr/bin/env bash
# WX-204-verify.sh - confirm the reconciliation-degrade + wasted-Claude-call fixes
# (WX-204 / WX-205 / WX-206, shipped together in 1.34.1) are live, by inspecting the service log
# since the deploy.
#
# THE CHANGE: a post-1.34.0 analysis found recurring reconciliation degrades dominated by a
# phantom "Strengthening" -- DeterministicChangeDetector classifies a change PER BLOCK (and groups
# consecutive blocks into one window) while ValidateChangeSnapshotConsistency checked the WINDOW
# AGGREGATE, so a real interior-block rise whose window max/severe looked flat was false-rejected
# as "not a real change", degrading the report and wasting 3 Claude calls (the retries can't fix a
# change Claude didn't author).
#   WX-204: the consistency check now backs a change PER BLOCK -> the phantom rejection stops.
#   WX-205: a ChangeConsistencyException now degrades on attempt 1 (no 2nd/3rd Claude call).
#   WX-206: the closing-claim guard names the offending sentence in its retry feedback.
#
# THE INVARIANT (post-fix): the phantom "Strengthening" rejection no longer fires. Since the
# changes[] set is COMPUTED (WX-189) and the consistency check is its inverse, a "structured_report
# change ... is not a real change versus prior_snapshot" line should now be essentially ZERO -- the
# detector and validator agree. ANY such line since the deploy is the failure signature: either the
# per-block fix is incomplete or the detector has another disagreement. That FAILs at any horizon.
#
# THE GATE (Paul, WX-204 design pass): a deterministic code fix -- nothing to settle over wall-clock
# time, so there is NO time window. PASS is gated on a minimum number of RECONCILIATIONS since the
# deploy (default 3), so the change path is exercised across more than one real cycle. A "reconciled"
# token-log line marks one reconciliation.
#
# Non-gating evidence surfaced for the WX-205.md manual review:
#   - WX-205 immediate-degrade lines ("degrading immediately without retry"): if a computed-change
#     fault DID occur post-deploy, it should appear here AND not be paired with retry attempts.
#   - WX-206 named-sentence closings ("sentence: \"...\""): a closing-claim fault now names the
#     sentence in its message.
#
# This is a LOG-reading verify (the fixes change the reconciler's control flow / log lines, which
# leave a service-log fingerprint -- unlike the WX-171/188/190 rendered-output checks). It reuses
# verify-lib.sh for the version-pinned deploy boundary, the log window, and the PASS/FAIL/WAIT call.
#
# Usage:  WX-204-verify.sh [--since 'YYYY-MM-DD HH:MM:SS'] [--log PATH] [--deploy-log PATH] [-h]
# Shell:  bash (WSL). The PC runs in UTC. Shared scaffold in verify-lib.sh.

set -uo pipefail

SELF="${BASH_SOURCE[0]}"
TICKET='WX-204'                                     # self-identification + header
VERSION='1.34.1'                                    # the release that shipped WX-204/205/206 -- the deploy pin
COMPONENTS=('WxReportSvc')                            # the service the fixes ship in
TITLE='reconciliation degrade fixes -- per-block consistency + non-retryable computed faults'
MIN_CYCLES=3                                        # PASS gate: minimum reconciliations since deploy
MIN_WINDOW_MINUTES=1                                # satisfies verify-lib's >0 floor; the real gate is the reconcile count, NOT a wait (min_window_secs forced to ZERO below)
source "$(cd "$(dirname "$SELF")" && pwd)/verify-lib.sh"

vl_parse_args "$@"
vl_resolve_boundary     # SINCE / COMMIT / DEPLOY_INFO (WAIT-exits if VERSION not deployed)
vl_setup_window         # POST (log lines since SINCE) / elapsed / hh / mm (WAIT-exits if none)
min_window_secs=0       # deterministic fix -- no wait time; the lib's >0 floor is bypassed here

# Fingerprints over the post-deploy window.
reconciled=$(printf '%s\n' "$POST" | cnt 'reconciled')                                  # exercised: a reconciliation ran
phantom=$(printf '%s\n' "$POST" | cnt 'is not a real change versus prior_snapshot')      # FAILURE SIGNATURE: the phantom rejection
wx205=$(printf '%s\n' "$POST" | cnt 'degrading immediately without retry')               # WX-205 fired (non-gating evidence)
named=$(printf '%s\n' "$POST" | cnt 'sentence: \\"')                                      # WX-206 named-sentence closing fault (non-gating)
retries=$(printf '%s\n' "$POST" | cnt 'tool_use input failed validation')                # any validation-retry (context)

# Matched failure-signature lines (shown above the verdict so the count and evidence are the same).
VIOLATIONS="$(printf '%s\n' "$POST" | grep -E 'is not a real change versus prior_snapshot' | sed -E 's/(.{160}).*/\1.../')"

vl_header
echo
echo " WX-204 FINGERPRINT  (service log since the deploy boundary)"
echo "   reconciliations (exercised)              : $reconciled   (PASS needs >= $MIN_CYCLES)"
echo "   phantom 'not a real change' rejections   : $phantom   (expect 0 -- the failure signature)"
echo "   validation-retries (any reason, context) : $retries"
echo "   WX-205 immediate computed-fault degrades : $wx205   (non-gating; should NOT be paired with retries)"
echo "   WX-206 named-sentence closing faults     : $named   (non-gating)"
if [ "$phantom" -gt 0 ]; then
  echo
  echo " FAILURE-SIGNATURE LINES"
  printf '%s\n' "$VIOLATIONS"
fi
echo

regression=$phantom
# Fail-fast on a real signature regardless of cycle count; else PASS gated on >= MIN_CYCLES reconciliations.
precond=$(( (reconciled >= MIN_CYCLES || regression > 0) ? 1 : 0 ))
vl_verdict "$regression" "$reconciled" \
  "the phantom-'Strengthening' rejection still fires post-deploy (shown above) -- the per-block consistency fix (WX-204) is not eliminating it; inspect the matched lines and the DeterministicChangeDetector/ValidateChangeSnapshotConsistency pair." \
  "no phantom-'Strengthening' rejection fired across $reconciled reconciliation(s); any computed-change fault degraded immediately (WX-205: $wx205 line(s), no wasted retries). Review the WX-205/206 non-gating evidence above per WX-204.md." \
  "$precond" "at least $MIN_CYCLES reconciliations since the deploy (got $reconciled)"
