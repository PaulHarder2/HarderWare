#!/usr/bin/env bash
# WX-265-267-verify.sh - confirm the daypart-token rename (WX-265) deployed cleanly and the
# WX-250 top-up is backfilling the new DayPart1 token for the enabled target languages.
#
# THE CHANGE (WX-265): PartMorning/PartAfternoon/PartEvening -> DayPart2/3/4, plus a NEW DayPart1
# ("early hours") for the 00-06 block. A data migration renames the Token key in BOTH
# LanguageTemplates and PromptGlossaryTokens and seeds the en DayPart1 row. DayPart1 ships
# Required (not Soft), so on deploy the enabled targets (es/eo/de...) are momentarily missing it
# and WX-250 top-up fills them one-per-cycle -- an EXPECTED transient, timed to a window with no
# scheduled sends pending (the WX-267 runbook records the decision).
#
# EXPECTED (NOT a failure): a startup "Language '<target>' is INCOMPLETE - ... [DayPart1]" alert
# for each target not yet filled. These are the accepted transient; they stop once top-up fills
# every target (confirm via the DB query in WX-265-267.md).
#
# FAIL signatures (WxReport.Svc log, since the 1.45.2 WxReportSvc deploy):
#   (a) "name unknown token" / "PromptGlossaryToken(s)" ERROR -- the PromptGlossaryTokens rename
#       was MISSED, so daypart anchoring silently dropped (the two-table trap this change avoids).
#   (b) "Language 'en' is INCOMPLETE" -- the migration failed to rename/seed the ENGLISH rows, so
#       the baseline itself is broken (every English recipient would fail closed).
#   Either is a real regression of THIS change; a TARGET-only INCOMPLETE naming DayPart1 is not.
#   Exercised   : "Report cycle complete." -- the send/gate path ran.
#   Precondition: >=1 "WX-250: '<iso>' topped up" since the boundary -- top-up is filling DayPart1
#                 (post-1.45.2 the only NEW missing baseline token is DayPart1).
#   PASS = past the window, cycles ran, top-up filled >=1 target, and neither FAIL signature
#          appeared. (Full es/eo/de fill + the DB token check are the manual steps in WX-265-267.md.)
#
# Usage:  WX-265-267-verify.sh [--since 'YYYY-MM-DD HH:MM:SS'] [--log PATH] [--deploy-log PATH]
#                              [--results-log PATH] [-h]
# Shell:  bash (WSL). Log timestamps are UTC (the HarderWare PC runs on UTC).
# Exit:   0 always (the verdict is in the output + the results log); 2 usage; 3 environment.

set -uo pipefail

SELF="${BASH_SOURCE[0]}"
TICKET='WX-265'
VERSION='1.45.2'
COMPONENTS=(WxReportSvc)
TITLE='daypart-token rename + DayPart1: clean deploy (both tables), no en/glossary breakage, top-up backfill'
MIN_WINDOW_MINUTES=20   # ~3 targets x one-per-5-min cycle, plus margin

# shellcheck source=verify-lib.sh
. "$(cd "$(dirname "$SELF")" && pwd)/verify-lib.sh"

vl_parse_args "$@"
vl_resolve_boundary       # WAIT + exit if 1.45.2 is not in deploy-history.log yet
vl_setup_window           # WAIT + exit if no cycles are logged past the boundary

# ── FAIL signatures (regressions unique to this change) ──────────────────────
# (a) PromptGlossaryTokens rename missed -> glossary anchoring dropped at load.
GLOSSARY_DROP="$(printf '%s\n' "$POST" | grep -E ' ERROR ' | grep -iE 'name unknown token|PromptGlossaryToken' || true)"
gdrop=$(printf '%s\n' "$POST" | grep -E ' ERROR ' | grep -icE 'name unknown token|PromptGlossaryToken' || true)
# (b) English baseline itself incomplete -> migration failed on the en rows.
EN_INCOMPLETE="$(printf '%s\n' "$POST" | grep -E ' ERROR ' | grep -E "Language 'en' is INCOMPLETE" || true)"
eninc=$(printf '%s\n' "$POST" | grep -E ' ERROR ' | grep -cE "Language 'en' is INCOMPLETE" || true)

# Non-en INCOMPLETE lines, split by whether they name DayPart1 (the token THIS deploy adds; the
# ERROR line lists the missing tokens). Naming DayPart1 -> the EXPECTED transient (top-up hasn't
# filled that language yet). NOT naming DayPart1 -> a target missing some OTHER Required token
# (e.g. a rename that failed to reach that language's DayPart2/3/4 rows) -> an UNEXPECTED
# regression, folded into the FAIL count so it is never silently swallowed as "expected".
TARGET_INCOMPLETE_ALL="$(printf '%s\n' "$POST" | grep -E ' ERROR ' | grep -E "Language '[a-z]+' is INCOMPLETE" | grep -vE "Language 'en' is INCOMPLETE" || true)"
TARGET_INCOMPLETE="$(printf '%s\n' "$TARGET_INCOMPLETE_ALL" | grep -F 'DayPart1' || true)"
UNEXPECTED_INCOMPLETE="$(printf '%s\n' "$TARGET_INCOMPLETE_ALL" | grep -vF 'DayPart1' | sed '/^[[:space:]]*$/d' || true)"
unexpected=$(printf '%s\n' "$UNEXPECTED_INCOMPLETE" | grep -c . || true)
regression=$(( gdrop + eninc + unexpected ))

# The send/gate path ran this many times.
exercised=$(printf '%s\n' "$POST" | grep -cF 'Report cycle complete.' || true)

# Top-up filled a target since the boundary (post-1.45.2 the only NEW missing baseline token is
# DayPart1), naming which languages.
TOPPED="$(printf '%s\n' "$POST" | grep -E "WX-250: '[a-z]+' topped up" || true)"
topped_langs=$(printf '%s\n' "$TOPPED" | grep -oE "'[a-z]+'" | sort -u | tr '\n' ' ' || true)
precond=$(printf '%s\n' "$POST" | grep -cE "WX-250: '[a-z]+' topped up" || true)

vl_header
echo
echo " RENAME BREAKAGE (must be none: glossary-drop, en INCOMPLETE, or a target missing a NON-DayPart1 token)"
if [ "$regression" -gt 0 ]; then
    printf '%s\n' "$GLOSSARY_DROP" "$EN_INCOMPLETE" "$UNEXPECTED_INCOMPLETE" | sed '/^[[:space:]]*$/d; s/^/   /'
else
    echo "   none -- no glossary-drop ERROR, en is complete, and no target is missing a non-DayPart1 token."
fi
echo
echo " EXPECTED TRANSIENT (target missing ONLY DayPart1 until top-up fills it -- NOT a failure)"
if [ -n "$TARGET_INCOMPLETE" ]; then
    printf '%s\n' "$TARGET_INCOMPLETE" | sed 's/^/   /'
    echo "   ^ expected while DayPart1 is Required + unfilled; must cease once top-up completes (DB check in WX-265-267.md)."
else
    echo "   none -- no target is INCOMPLETE (either already filled or none enabled)."
fi
echo
echo " TOP-UP BACKFILLING DayPart1 (the live proof)"
if [ "$precond" -gt 0 ]; then
    echo "   $precond top-up(s) since the boundary; target language(s): ${topped_langs:-?}"
    echo "   (post-1.45.2 the only NEW missing baseline token is DayPart1, so these ARE the fills)"
else
    echo "   none yet -- no target topped up since the boundary; top-up fills one language per ~5-min cycle."
fi
echo
echo " BACKGROUND HEALTH (new ERRORs vs the equal-length pre-deploy window)"
read -r before after new < <(vl_health_delta ' ERROR ')
echo "   ERROR lines: pre=$before post=$after new=$new  (the target-INCOMPLETE alerts above are the expected transient)"

vl_verdict "$regression" "$exercised" \
    "The daypart rename broke a baseline: a PromptGlossaryTokens rename was missed (glossary-drop) or the en rows were not renamed/seeded (en INCOMPLETE) -- see RENAME BREAKAGE." \
    "The rename deployed cleanly (both tables, en complete) and top-up is backfilling DayPart1; run the WX-265-267.md DB token check + confirm full es/eo/de fill to close." \
    "$precond" "a target language topped up (>=1 'topped up' since the boundary -- DayPart1 backfill under way)"
