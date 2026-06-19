#!/usr/bin/env bash
# WX-207-verify.sh - confirm the tier-'Safety' over-escalation fix is live, by inspecting the
# service log since the 1.34.3 deploy.
#
# THE BUG: DeterministicChangeDetector.PrecipTier tiered a precip change **Safety** when EITHER the
# final OR the prior block carried a safety-grade signal -- but it applied the prior-block check for
# EVERY direction. So an APPEARING/STRENGTHENING change inherited Safety from a prior hazard the
# final no longer carries (a block going Snow->Rain: the appearing rain is not itself a safety
# event). ValidateChangeSnapshotConsistency (correctly) requires the FINAL block to carry the signal
# for an Appearing/Strengthening Safety change, so it rejected the over-tiered change -> the report
# degraded ("... is tier 'Safety' (Rain) but no final_snapshot block ... carries a safety-grade
# signal ... the tier is over-escalated"). This is the sibling of WX-204 on the TIER axis; WX-205
# degraded these immediately. In prod it recurred EVERY cycle on a Snow->Rain locality (Watonga, OK).
#
# THE FIX: PrecipTier inherits Safety from the prior block ONLY for a WEAKENING/CLEARING change (a
# removed hazard is safety news, and the validator does not require final backing for those). For
# APPEARING/STRENGTHENING, only the FINAL block's hazard qualifies -- matching the validator exactly.
#
# THE INVARIANT (post-fix): no reconciliation degrades on tier over-escalation. Since changes[] is
# COMPUTED (WX-189) and the consistency check is its inverse, a "the tier is over-escalated" rejection
# should now be ZERO. ANY such line since the deploy is the failure signature (the fix is incomplete
# or the old binary is live). That FAILs at any horizon.
#
# THE GATE (same as the WX-204/205/206 verify): a deterministic code fix -- no time window. PASS is
# gated on >= 3 reconciliations since the deploy (a "Claude reconciliation tokens" log line marks one
# -- it fires on success AND degrade, unlike the old "reconciled" success line which missed degrades).
#
# LOG-reading verify (the fix changes the reconciler's control flow / log lines). Reuses verify-lib.sh
# for the version-pinned deploy boundary and the PASS/FAIL/WAIT decision.
#
# Usage:  WX-207-verify.sh [--since 'YYYY-MM-DD HH:MM:SS'] [--log PATH] [--deploy-log PATH] [-h]
# Shell:  bash (WSL). The PC runs in UTC. Shared scaffold in verify-lib.sh.

set -uo pipefail

SELF="${BASH_SOURCE[0]}"
TICKET='WX-207'                                     # self-identification + header
VERSION='1.34.3'                                    # the release that shipped the tier fix -- the deploy pin
COMPONENTS=('WxReportSvc')                            # the service WX-207 ships in
TITLE='tier-Safety over-escalation fix (detector tier axis)'
MIN_CYCLES=3                                        # PASS gate: minimum reconciliations since deploy
MIN_WINDOW_MINUTES=1                                # satisfies verify-lib's >0 floor; the real gate is the reconcile count, NOT a wait (min_window_secs forced to ZERO below)
source "$(cd "$(dirname "$SELF")" && pwd)/verify-lib.sh"

vl_parse_args "$@"
vl_resolve_boundary     # SINCE / COMMIT / DEPLOY_INFO (WAIT-exits if VERSION not deployed)
vl_setup_window         # POST (log lines since SINCE) (WAIT-exits if none)
min_window_secs=0       # deterministic fix -- no wait time; the gate is the reconcile count

reconciled=$(printf '%s\n' "$POST" | cnt 'Claude reconciliation tokens')                 # exercised: a reconciliation ran (success or degrade)
overesc=$(printf '%s\n' "$POST" | cnt 'tier is over-escalated')                           # FAILURE SIGNATURE: a tier over-escalation rejection
wx205=$(printf '%s\n' "$POST" | cnt 'degrading immediately without retry')                # WX-205 immediate degrades (non-gating context)

VIOLATIONS="$(printf '%s\n' "$POST" | grep -E 'tier is over-escalated' | sed -E 's/(.{170}).*/\1.../')"

vl_header
echo
echo " WX-207 FINGERPRINT  (service log since the deploy boundary)"
echo "   reconciliations (exercised)              : $reconciled   (PASS needs >= $MIN_CYCLES)"
echo "   tier 'over-escalated' rejections         : $overesc   (expect 0 -- the failure signature)"
echo "   WX-205 immediate computed-fault degrades : $wx205   (non-gating context; should trend to 0 as detector bugs are fixed)"
if [ "$overesc" -gt 0 ]; then
  echo
  echo " FAILURE-SIGNATURE LINES"
  printf '%s\n' "$VIOLATIONS"
fi
echo

regression=$overesc
precond=$(( (reconciled >= MIN_CYCLES || regression > 0) ? 1 : 0 ))
vl_verdict "$regression" "$reconciled" \
  "a tier over-escalation rejection still fires post-deploy (shown above) -- PrecipTier still inherits Safety from the prior block on an appearing/strengthening change, or the old binary is live; inspect DeterministicChangeDetector.PrecipTier and ValidateChangeSnapshotConsistency's safety-backing check." \
  "no tier over-escalation rejection fired across $reconciled reconciliation(s) -- the PrecipTier fix is live (the prior-block hazard no longer over-tiers an appearing/strengthening precip change)." \
  "$precond" "at least $MIN_CYCLES reconciliations since the deploy (got $reconciled)"
