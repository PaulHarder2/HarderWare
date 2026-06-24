#!/usr/bin/env bash
# WX-210-verify.sh - confirm the METAR within-response dedup fix is live: no
# UX_Metars_Station_Time_Type duplicate-key violations in wxparser-svc.log since the 1.34.4 deploy.
#
# THE BUG: MetarFetcher.FetchUrlAndInsertAsync deduped parsed observations only against rows ALREADY
# in the DB, never against duplicates WITHIN one fetch response. AWC re-serves some stations byte-for-
# byte (KJXI comes back ~3x every cycle), so the repeats both passed the not-in-DB check, both were
# Add-ed, and SaveChanges violated the unique index UX_Metars_Station_Time_Type. The batch is one
# transaction, so that single duplicate rolled back EVERY co-batched station's inserts for the cycle.
# It fired ~56 times / 12h, all KJXI.
#
# THE FIX: the insert is reconciled by correction rank. CollapseByKey collapses same-key duplicates
# within a response to one survivor (a COR beats a non-COR regardless of feed order); Reconcile decides
# insert/overwrite/skip against the stored row; a per-row fallback keeps one bad row from discarding the
# batch. The COR-overwrite half is UNIT-tested (production issues no corrections on demand), so this
# functional check covers the dedup / no-crash invariant the log can express.
#
# THE INVARIANT (post-fix): ZERO UX_Metars_Station_Time_Type violations in wxparser-svc.log since the
# deploy. ANY such line is the failure signature (the within-response collapse is not deduping the
# repeat, or the old binary is live). That FAILs at any horizon.
#
# THE GATE: a deterministic code fix -- no time window. PASS is gated on >= MIN_CYCLES METAR fetch
# cycles since the deploy (a "METAR fetch done" line marks one). KJXI re-serves its duplicate on every
# ~10-minute cycle, so a handful of clean cycles is strong evidence the collapse holds.
#
# LOG-reading verify against wxparser-svc.log. Reuses verify-lib.sh for the version-pinned deploy
# boundary and the PASS/FAIL/WAIT decision.
#
# Usage:  WX-210-verify.sh [--since 'YYYY-MM-DD HH:MM:SS'] [--log PATH] [--deploy-log PATH] [-h]
# Shell:  bash (WSL). The PC runs in UTC. Shared scaffold in verify-lib.sh.

set -uo pipefail

SELF="${BASH_SOURCE[0]}"
TICKET='WX-210'                                      # self-identification + header
VERSION='1.34.4'                                     # the release that shipped the dedup fix -- the deploy pin
COMPONENTS=('WxParserSvc')                           # the service WX-210 ships in
TITLE='METAR within-response dedup (UX_Metars duplicate-key fix)'
LOG='/mnt/c/HarderWare/Logs/wxparser-svc.log'        # this fix ships in WxParser.Svc, not the verify-lib default wxreport log
MIN_CYCLES=6                                         # PASS gate: minimum METAR fetch cycles since deploy
MIN_WINDOW_MINUTES=1                                 # satisfies verify-lib's >0 floor; the real gate is the cycle count (min_window_secs forced to ZERO below)
source "$(cd "$(dirname "$SELF")" && pwd)/verify-lib.sh"

vl_parse_args "$@"
vl_resolve_boundary     # SINCE / COMMIT / DEPLOY_INFO (WAIT-exits if VERSION not deployed)
vl_setup_window         # POST (log lines since SINCE) (WAIT-exits if none)
min_window_secs=0       # deterministic fix -- no wait time; the gate is the fetch-cycle count

cycles=$(printf '%s\n' "$POST" | cnt 'METAR fetch done')              # exercised: a METAR fetch cycle ran
dupkey=$(printf '%s\n' "$POST" | cnt 'UX_Metars_Station_Time_Type')   # FAILURE SIGNATURE: a unique-index duplicate-key violation
fallback=$(printf '%s\n' "$POST" | cnt 'METAR insert failed for')     # per-row fallback firings (non-gating context; expected 0)
read -r err_before err_after err_new < <(vl_health_delta ' ERROR ')   # background health: ERRORs new vs the equal-length pre-deploy window

VIOLATIONS="$(printf '%s\n' "$POST" | grep -E 'UX_Metars_Station_Time_Type' | sed -E 's/(.{170}).*/\1.../')"

vl_header
echo
echo " WX-210 FINGERPRINT  (wxparser-svc.log since the deploy boundary)"
echo "   METAR fetch cycles (exercised)           : $cycles   (PASS needs >= $MIN_CYCLES)"
echo "   UX_Metars duplicate-key violations       : $dupkey   (expect 0 -- the failure signature)"
echo "   per-row insert-fallback firings          : $fallback   (non-gating; expected 0 in normal operation)"
echo
echo " BACKGROUND HEALTH  (equal-length pre-deploy window cancels steady noise)"
echo "   ERROR lines   before=$err_before  after=$err_after  new=$err_new"
if [ "$dupkey" -gt 0 ]; then
  echo
  echo " FAILURE-SIGNATURE LINES"
  printf '%s\n' "$VIOLATIONS"
fi
echo

regression=$dupkey
precond=$(( (cycles >= MIN_CYCLES || regression > 0) ? 1 : 0 ))
vl_verdict "$regression" "$cycles" \
  "a UX_Metars_Station_Time_Type duplicate-key violation still fires post-deploy (shown above) -- the within-response collapse is not deduping the repeat, or the old binary is live; inspect MetarFetcher.CollapseByKey and FetchUrlAndInsertAsync." \
  "no duplicate-key violation across $cycles METAR fetch cycle(s) -- the within-response dedup is live (AWC's repeated KJXI lines collapse to a single insert)." \
  "$precond" "at least $MIN_CYCLES METAR fetch cycles since the deploy (got $cycles)"
