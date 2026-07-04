#!/usr/bin/env bash
# WX-256-verify.sh - confirm the noon/midnight SOFT tokens deployed correctly: no language's
# report was SUPPRESSED over the new cosmetic tokens (the soft-token payoff), and WX-250 top-up
# is filling the words for the enabled target languages (the live proof of the en-only arch).
#
# THE CHANGE: noon/midnight are curated LanguageTemplates tokens (en-seeded, generated for
# targets), but a cosmetic time word must not fail-closed like a hazard. So
# Tok.Required = Tok.All - Tok.Soft{noon,midnight}, and the two suppression gates + the startup
# completeness check key on Tok.Required. A language missing ONLY noon/midnight still SENDS (the
# renderer degrades to the culture 12-hour form); hazard tokens stay hard fail-closed.
#
# DEPLOY EXPECTATION: on the 1.43.0 deploy the en tokens are seeded by migration; the enabled
# targets (es/de/da/eo/sq...) are momentarily missing them and MUST keep sending (12h fallback)
# while top-up fills them over the next cycles -- NO suppression window.
#
# THE SIGNATURES (WxReport.Svc log, since the 1.43.0 WxReportSvc deploy):
#   FAIL signature : any ERROR-level line mentioning "noon"/"midnight" -- i.e. a soft token caused
#                    an INCOMPLETE / per-recipient suppression (the fix is broken: Required still
#                    gates the soft tokens).
#   Exercised      : "Report cycle complete." lines -- the send/gate path ran (so a clean PASS
#                    means the gate actually processed cycles, not just silence).
#   Precondition   : >=1 "WX-250: '<iso>' topped up ..." since the boundary. Post-1.43.0 the ONLY
#                    missing baseline tokens are noon/midnight (nothing else changed), so a top-up
#                    of any target IS the live proof its noon/midnight words are being generated.
#   PASS = past the 10-min window, cycles ran, top-up filled >=1 target, and NO soft-token
#          suppression appeared. (Full five-language fill just repeats it; the manual DB query +
#          rendered-noon eyeball are in WX-256.md.)
#
# Usage:  WX-256-verify.sh [--since 'YYYY-MM-DD HH:MM:SS'] [--log PATH] [--deploy-log PATH]
#                          [--results-log PATH] [-h]
# Shell:  bash (WSL). Log timestamps are UTC (the HarderWare PC runs on UTC).
# Exit:   0 always (the verdict is in the output + the results log); 2 usage; 3 environment.

set -uo pipefail

SELF="${BASH_SOURCE[0]}"
TICKET='WX-256'
VERSION='1.43.0'
COMPONENTS=(WxReportSvc)
TITLE='noon/midnight soft tokens: no suppression + top-up fills the words'
MIN_WINDOW_MINUTES=10   # the mechanism-proven bar: no-suppress + first target filled

# shellcheck source=verify-lib.sh
. "$(cd "$(dirname "$SELF")" && pwd)/verify-lib.sh"

vl_parse_args "$@"
vl_resolve_boundary       # WAIT + exit if 1.43.0 is not in deploy-history.log yet
vl_setup_window           # WAIT + exit if no cycles are logged past the boundary

# ── metrics ──────────────────────────────────────────────────────────────────
# FAIL signature: an ERROR naming a soft token == the token gated (INCOMPLETE / suppression).
SUPPRESSED="$(printf '%s\n' "$POST" | grep -E ' ERROR ' | grep -iE 'noon|midnight' || true)"
regression=$(printf '%s\n' "$POST" | grep -E ' ERROR ' | grep -icE 'noon|midnight' || true)

# The send/gate path ran this many times.
exercised=$(printf '%s\n' "$POST" | grep -cF 'Report cycle complete.' || true)

# Top-up filled a target since the boundary (post-1.43.0 the only missing baseline tokens are
# noon/midnight), naming which languages.
TOPPED="$(printf '%s\n' "$POST" | grep -E "WX-250: '[a-z]+' topped up" || true)"
topped_langs=$(printf '%s\n' "$TOPPED" | grep -oE "'[a-z]+'" | sort -u | tr '\n' ' ' || true)
precond=$(printf '%s\n' "$POST" | grep -cE "WX-250: '[a-z]+' topped up" || true)

vl_header
echo
echo " SOFT-TOKEN SUPPRESSION (must be none)"
if [ "$regression" -gt 0 ]; then
    printf '%s\n' "$SUPPRESSED" | sed 's/^/   /'
else
    echo "   none -- no ERROR names noon/midnight since the deploy (soft tokens are not gating)."
fi
echo
echo " TOP-UP FILLING THE WORDS (the live proof)"
if [ "$precond" -gt 0 ]; then
    echo "   $precond top-up(s) since the boundary; target language(s): ${topped_langs:-?}"
    echo "   (post-1.43.0 the only missing baseline tokens are noon/midnight, so these ARE the fills)"
else
    echo "   none yet -- no target topped up since the boundary; top-up fills one language per ~5-min cycle."
fi
echo
echo " BACKGROUND HEALTH (new ERRORs vs the equal-length pre-deploy window)"
read -r before after new < <(vl_health_delta ' ERROR ')
echo "   ERROR lines: pre=$before post=$after new=$new"

vl_verdict "$regression" "$exercised" \
    "A soft token (noon/midnight) is suppressing a report -- check Tok.Required excludes Tok.Soft and the gates use Tok.Required." \
    "Soft tokens deployed with no suppression window and top-up is filling the target words; run the WX-256.md manual DB + rendered-noon checks to close." \
    "$precond" "a target language's noon/midnight generated by top-up (>=1 'topped up' since the boundary)"
