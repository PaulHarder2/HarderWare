#!/usr/bin/env bash
# WX-249-verify.sh - confirm non-destructive language disable deployed correctly: the WX-171
# startup completeness check is now SCOPED TO ENABLED languages (a disabled language's durable
# rows no longer trip a false INCOMPLETE ERROR), and the service is healthy on 1.44.0.
#
# THE CHANGE: disabling a language keeps its curated LanguageTemplates rows (IsEnabled gates USE,
# not existence) instead of deleting them. The startup completeness check loops ENABLED languages
# only, so a dormant-incomplete language stays silent; the generator no longer discards a top-up
# for a language disabled mid-call.
#
# WHAT THIS SCRIPT CAN PROVE (log-only): the service restarted on 1.44.0 and its scoped
# completeness check ran clean -- the "passed for all N enabled language(s)" line is the WX-249
# signature (old code said "loaded language(s)"), and NO enabled language logged INCOMPLETE. The
# core AC -- a disabled language's reviewed rows survive -- is proven by the LanguageToggleTests
# unit suite and the MANUAL disable/re-enable + DB check in WX-249.md (an operator WxManager
# action this script cannot drive).
#
# SIGNATURES (WxReport.Svc log, since the 1.44.0 deploy):
#   FAIL signature : any "is INCOMPLETE" ERROR -- an ENABLED language missing a HARD token (a real
#                    regression, or the scoping broke and a disabled language leaked through).
#   Precondition   : >=1 "completeness check passed for all <N> enabled language(s)" -- the WX-249
#                    scoped check ran at startup (the "enabled" wording proves the new code is live).
#   Exercised      : "Report cycle complete." lines -- the service ran cycles.
#   PASS = past the window, cycles ran, the scoped completeness line appeared, and NO INCOMPLETE
#          ERROR. (The disable-preservation proof is the unit tests + WX-249.md manual check.)
#
# Usage:  WX-249-verify.sh [--since 'YYYY-MM-DD HH:MM:SS'] [--log PATH] [--deploy-log PATH]
#                          [--results-log PATH] [-h]
# Shell:  bash (WSL). Log timestamps are UTC (the HarderWare PC runs on UTC).
# Exit:   0 always (the verdict is in the output + the results log); 2 usage; 3 environment.

set -uo pipefail

SELF="${BASH_SOURCE[0]}"
TICKET='WX-249'
VERSION='1.44.0'
COMPONENTS=(WxReportSvc)
TITLE='non-destructive disable: completeness check scoped to enabled languages, service healthy'
MIN_WINDOW_MINUTES=5   # a restart + one clean cycle is enough; the deep proof is unit + manual

# shellcheck source=verify-lib.sh
. "$(cd "$(dirname "$SELF")" && pwd)/verify-lib.sh"

vl_parse_args "$@"
vl_resolve_boundary       # WAIT + exit if 1.44.0 is not in deploy-history.log yet
vl_setup_window           # WAIT + exit if no cycles are logged past the boundary

# ── metrics ──────────────────────────────────────────────────────────────────
# FAIL signature: an "is INCOMPLETE" ERROR == an enabled language missing a hard token (regression),
# or the scoping failed and a disabled language leaked into the check.
INCOMPLETE="$(printf '%s\n' "$POST" | grep -E ' ERROR ' | grep -F 'is INCOMPLETE' || true)"
regression=$(printf '%s\n' "$POST" | grep -E ' ERROR ' | grep -cF 'is INCOMPLETE' || true)

# The send/gate path ran this many times.
exercised=$(printf '%s\n' "$POST" | grep -cF 'Report cycle complete.' || true)

# Precondition: the WX-249 scoped completeness line at startup ("enabled" wording == new code live).
PASSED="$(printf '%s\n' "$POST" | grep -E 'completeness check passed for all .* enabled language' || true)"
precond=$(printf '%s\n' "$POST" | grep -cE 'completeness check passed for all .* enabled language' || true)

vl_header
echo
echo " COMPLETENESS-CHECK REGRESSION (must be none)"
if [ "$regression" -gt 0 ]; then
    printf '%s\n' "$INCOMPLETE" | sed 's/^/   /'
else
    echo "   none -- no 'is INCOMPLETE' ERROR since the deploy (no enabled language is missing a hard token)."
fi
echo
echo " SCOPED COMPLETENESS CHECK RAN (the WX-249 signature)"
if [ "$precond" -gt 0 ]; then
    printf '%s\n' "$PASSED" | sed 's/^/   /'
    echo "   (the \"enabled language(s)\" wording confirms the 1.44.0 scoped check is live; old code said \"loaded\".)"
else
    echo "   none yet -- no 'passed for all N enabled language(s)' startup line since the boundary."
fi
echo
echo " BACKGROUND HEALTH (new ERRORs vs the equal-length pre-deploy window)"
read -r before after new < <(vl_health_delta ' ERROR ')
echo "   ERROR lines: pre=$before post=$after new=$new"

vl_verdict "$regression" "$exercised" \
    "An enabled language logged INCOMPLETE -- a real hard-token gap, or the enabled-scoping broke and a disabled language leaked into the check." \
    "The scoped completeness check ran clean on 1.44.0 (enabled-only); run the WX-249.md manual disable/re-enable + DB check to close the disable-preservation AC." \
    "$precond" "the scoped startup completeness line ('passed for all N enabled language(s)')"
