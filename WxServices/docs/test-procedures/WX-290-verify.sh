#!/usr/bin/env bash
# WX-290-verify.sh - confirm WX-290 (canonical service token) resolved the WX-106 heartbeat blind
# spot, by reading the WxMonitor service log after a deployment boundary.
#
# Before WX-290 the monitor derived a service's heartbeat filename as "{Name}.Replace('.','-')" =
# e.g. "wxparser-svc", so it sought "wxparser-svc-heartbeat.txt" while WxParser WROTE
# "wxparser-heartbeat.txt" (token "wxparser"). The monitor never found the file and logged, EVERY
# cycle, for both WxParser and WxReport:
#     HeartbeatWatcher.cs:30  "<Svc>: heartbeat file not found at '...-svc-heartbeat.txt'."
# WX-290 routes writer and monitor through one canonical token (WxServiceToken), so the monitor now
# reads exactly the files the writers stamp. Post-deploy, that WARN must be GONE.
# So after the deploy, over an active window:
#   - ZERO "heartbeat file not found" WARNs   -> the PASS signal (WX-106 resolved; the monitor sees
#     the WxParser/WxReport heartbeats). Pre-deploy this fired ~2x per 5-min cycle.
#   - the monitor is exercised (cycles completing), so the absence is trustworthy, not just idle.
# Note this is a fingerprint that fired EVERY cycle pre-fix, so a short window is already conclusive
# (a full active day is not needed for absence to be trustworthy) -> MIN_WINDOW_HOURS=1.
#
# A genuinely stale heartbeat still alerts (HeartbeatWatcher.cs:37, "heartbeat is N minute(s) old")
# -- that path is unchanged and is NOT what this counts; the blind-spot fingerprint is the distinct
# "file not found" string.
#
# Usage:  WX-290-verify.sh [--since 'YYYY-MM-DD HH:MM:SS'] [--log PATH] [--deploy-log PATH] [-h]
# Shell:  bash (WSL).  Log timestamps are UTC (the HarderWare PC runs on UTC).
# Lives in the repo beside its procedure (docs/test-procedures/WX-290.md); rides the same PR as the
# code it checks (WORKFLOW.md §13). The shared scaffold (arg parsing, version-pinned deploy boundary
# via deploy-info.sh, before/after window, header, verdict) is verify-lib.sh.

set -uo pipefail

SELF="${BASH_SOURCE[0]}"
TICKET='WX-290'                                    # self-identification + header
VERSION='1.50.2'                                   # the release VERSION under test -- the pin
COMPONENTS=('WxMonitorSvc')                         # WX-290's fix is observed in the MONITOR's own log
TITLE='canonical service token resolves the WX-106 heartbeat blind spot'
MIN_WINDOW_HOURS=1                                 # the fingerprint fired every cycle pre-fix, so ~1h
                                                   # (≈12 cycles) of clean cycles is already conclusive
# Read the MONITOR log, not the default WxReport log (set before sourcing: verify-lib uses := default).
LOG='/mnt/c/HarderWare/Logs/wxmonitor-svc.log'
source "$(cd "$(dirname "$SELF")" && pwd)/verify-lib.sh"

vl_parse_args "$@"
vl_resolve_boundary     # sets SINCE/COMMIT/DEPLOY_INFO/BOUNDARY_SRC (WAIT-exits if undeployed)
vl_setup_window         # sets POST/LAST_TS/pre_start/elapsed/hh/mm/min_window_secs (WAIT-exits if no lines)

# ---- metrics over the post-deploy window --------------------------------------
# The WX-106 fingerprint: the heartbeat-not-found WARN. Pre-fix it fired for wxparser-svc-heartbeat.txt
# and wxreport-svc-heartbeat.txt every cycle; post-fix it must be zero.
not_found=$(printf '%s\n' "$POST" | cnt 'heartbeat file not found')
# Exercised = the monitor actually ran cycles in the window (so a zero count is real, not idle).
cycles=$(   printf '%s\n' "$POST" | cnt 'Monitor cycle complete')

vl_header
echo
echo    " WX-290 -- heartbeat blind spot (WX-106); the PASS/FAIL signal"
printf  '   %-52s %s\n' 'Monitor cycles completed since deploy:'          "$cycles   $([ "$cycles" -gt 0 ] && echo '[monitor is running -- absence is trustworthy]' || echo '[none yet -- WAIT]')"
printf  '   %-52s %s\n' '"heartbeat file not found" WARNs:'               "$not_found   $([ "$not_found" -eq 0 ] && echo '[expected 0 -- monitor sees the heartbeats]' || echo '[!! read the lines below]')"
[ "$not_found" -gt 0 ] && { echo "   --- heartbeat-not-found lines (which file is the monitor still missing?) ---";
                            printf '%s\n' "$POST" | grep 'heartbeat file not found' | tail -5 | sed 's/^/     /'; }
echo
# Verdict keys on the WX-106 fingerprint: any "heartbeat file not found" WARN post-deploy means the
# monitor is STILL resolving a filename the writer doesn't stamp (the fix didn't take for that service,
# or a new watched entry is misconfigured). Exercised = cycles completed.
vl_verdict "$not_found" "$cycles" \
  "a \"heartbeat file not found\" WARN post-deploy means the monitor is still seeking a filename the writer does not stamp -- read the lines above: if it is the OLD '...-svc-heartbeat.txt' name, the canonical-token fix did not take for that service; if it is a NEW watched entry, its config Name is unregistered. Either way, open a Bug." \
  "the monitor completed cycles with ZERO 'heartbeat file not found' WARNs -- it now reads the WxParser/WxReport heartbeats the writers stamp (WX-106 resolved). Send to Done."
