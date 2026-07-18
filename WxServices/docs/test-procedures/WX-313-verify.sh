#!/usr/bin/env bash
# WX-313-verify.sh - confirm the DB-backed configuration provider (WX-313) is live: that a
# deployed service actually reads the Config table through IConfiguration on the real box,
# by reading a service log after the deploy boundary.
#
# WX-313 adds a DbConfigurationProvider that overlays the Config DB table onto configuration,
# wired into all four services (and WxManager) and reloaded immediately after EnsureSchemaAsync.
# The provider is RESILIENT BY DESIGN: an unreachable DB or a not-yet-created table overlays
# nothing and never throws, so it cannot emit a service-failing signature -- there is no
# failure fingerprint for this library to key a FAIL on (n is 0 by construction). What the log
# CAN attest is that the provider RAN and read the real table:
#     DbConfigurationProvider.cs:  "DbConfigurationProvider loaded N configuration key(s) from the Config table."
# That success line (Debug; DEBUG is live during stabilization) is the EXERCISED signal -- it
# appears at startup once the schema exists (after the reload). So:
#   - >=1 "loaded" line since the deploy  -> the provider is wired and read the real Config
#     table in production (the code path ran). PASS once the window has filled.
#   - 0 "loaded" lines                    -> the provider has not run yet (service not restarted
#     into 1.56.0, or logging below Debug) -> WAIT.
# The resilient WARN ("could not read the Config table") is shown for CONTEXT only, not as a
# FAIL signature: exactly one per service is EXPECTED at the very first 1.56.0 boot -- the
# build-time load runs before EnsureSchemaAsync creates the table -- and is immediately
# followed by a successful "loaded" line at the post-schema reload. A WARN with NO subsequent
# "loaded" line is the thing to investigate (the DB is unreachable; see WX-313.md).
#
# The SUBSTANTIVE DB->config proof (a seeded key reaching IConfiguration) is the manual probe in
# WX-313.md section 2 -- it needs a DB write + a service restart, which a read-only log check
# can't drive. The unit tests (MetarParser.Tests/DbConfigurationProviderTests) prove the
# rows->config / last-wins / reload behaviour; this script proves the provider is deployed and
# runs against the real DB.
#
# Usage:  WX-313-verify.sh [--since 'YYYY-MM-DD HH:MM:SS'] [--log PATH] [--deploy-log PATH] [-h]
# Shell:  bash (WSL).  Log timestamps are UTC (the HarderWare PC runs on UTC).
# Lives beside its procedure (docs/test-procedures/WX-313.md); rides the same PR as the code it
# checks (WORKFLOW.md section 13). Shared scaffold (args, version-pinned boundary, window,
# header, verdict) is verify-lib.sh.

set -uo pipefail

SELF="${BASH_SOURCE[0]}"
TICKET='WX-313'                                     # self-identification + header
VERSION='1.56.0'                                    # the release VERSION under test -- the pin
COMPONENTS=('WxMonitorSvc')                         # the provider runs in all four services; WxMonitor
                                                    # is the one the manual probe (WX-313.md) restarts.
                                                    # Point --log at another service's log to verify it.
TITLE='DB-backed configuration provider reads the Config table in production'
MIN_WINDOW_MINUTES=15                               # the "loaded" fingerprint is a STARTUP event (present
                                                    # from the first post-deploy boot); the window only
                                                    # samples background health, so a short wait suffices.
LOG='/mnt/c/HarderWare/Logs/wxmonitor-svc.log'      # read the MONITOR log (set before sourcing: verify-lib := default)
source "$(cd "$(dirname "$SELF")" && pwd)/verify-lib.sh"

vl_parse_args "$@"
vl_resolve_boundary     # sets SINCE/COMMIT/DEPLOY_INFO/BOUNDARY_SRC (WAIT-exits if undeployed)
vl_setup_window         # sets POST/LAST_TS/pre_start/elapsed/hh/mm/min_window_secs (WAIT-exits if no lines)

# ---- metrics over the post-deploy window --------------------------------------
# EXERCISED: the provider read the real Config table (success line, any key count incl. 0).
loaded=$(printf '%s\n' "$POST" | cnt 'DbConfigurationProvider loaded')
# CONTEXT only (not a FAIL signature -- see header): the resilient could-not-read WARN.
warns=$( printf '%s\n' "$POST" | cnt 'DbConfigurationProvider could not read the Config table')
# Background health: new ERRORs the deploy introduced vs the equal-length pre-window.
read err_before err_after err_new < <(vl_health_delta ' ERROR ')

vl_header
echo
echo    " WX-313 -- DB config provider read the real Config table (the EXERCISED signal)"
printf  '   %-52s %s\n' 'provider "loaded ... key(s)" lines since deploy:' "$loaded   $([ "$loaded" -gt 0 ] && echo '[provider ran against the real DB]' || echo '[none yet -- WAIT]')"
printf  '   %-52s %s\n' 'resilient "could not read" WARNs (context only):' "$warns   $([ "$warns" -le 1 ] && echo '[<=1/service = expected first-boot transient]' || echo '[read the lines below + WX-313.md]')"
[ "$warns" -gt 0 ] && { echo "   --- resilient WARN lines (each should be followed by a later 'loaded' line) ---";
                        printf '%s\n' "$POST" | grep 'DbConfigurationProvider could not read the Config table' | tail -5 | sed 's/^/     /'; }
echo
echo    " Background health (new ERRORs the deploy introduced, vs the equal pre-window)"
printf  '   %-52s %s\n' 'ERROR lines  (before / after / new):' "$err_before / $err_after / $err_new"
echo
# n = 0 by construction: the provider is resilient by design, so it emits no service-failing
# signature the log can key a FAIL on (see header). PASS therefore attests "provider deployed
# and read the real DB (exercised)"; the DB->config proof is the manual probe in WX-313.md.
vl_verdict 0 "$loaded" \
  "" \
  "provider read the real Config table; now run WX-313.md section 2 (seed a probe key, restart, confirm the loaded count rises, then delete it) for the end-to-end DB->config proof."
