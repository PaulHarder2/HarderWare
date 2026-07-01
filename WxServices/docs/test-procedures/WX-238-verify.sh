#!/usr/bin/env bash
# WX-238-verify.sh - confirm the approved-vocabulary glossary reached the free-composed
# narrative, by inspecting the NEWEST rendered es/da translation-QA reports for the two
# documented vocabulary divergences.
#
# THE BUG: the reconciler's free-composed narrative (changeSummary/closing, WX-128) free-
# generated target weather terms and drifted off the approved LanguageTemplates vocabulary --
# es "llovizna engelante" (approved: "llovizna helada"), da "Isregn" (approved: "frysende
# stovregn") with the wintry-mix term "vinterblandingsnedslag" unused. The structured body is
# token-substituted and never drifts; only the narrative did.
#
# THE FIX: a PromptGlossaryTokens registry + an approved-vocabulary glossary injected into the
# reconciler prompt (per language incl. English), so the narrative uses the approved term.
#
# THE SIGNATURES (on the newest rendered TARGET report HTML, winter-frozen scenario):
#   es PASS : contains "helada" (approved) AND does NOT contain "engelante" (the drift).
#   da PASS : contains "frysende" (approved freezing-drizzle term) AND "vinterblandingsnedslag".
# All match strings are ASCII substrings of the approved terms, so grep is encoding-robust
# against the UTF-8 report HTML (the "o/slash" in stovregn is sidestepped by matching "frysende").
#
# THE GATE (Paul, WX-238 design): Option B is prompt-anchoring -- probabilistic by design. A
# single clean re-run with the documented divergences gone is PASS. de/eo are re-run for
# regression but their Tier-1/Tier-3 findings are WX-239/WX-240 (not asserted here).
#
# Usage:  WX-238-verify.sh [--qa-dir PATH] [-h]
# Shell:  bash (WSL). Reads the QA run output under C:\HarderWare\translation-qa.
# Exit:   0 PASS, 1 FAIL (a divergence remains), 2 precondition not met (no run found).

set -uo pipefail

QA_DIR="/mnt/c/HarderWare/translation-qa"
while [ $# -gt 0 ]; do
    case "$1" in
        --qa-dir) QA_DIR="${2:-}"; shift 2 ;;
        -h|--help) grep -E '^#( |$)' "$0" | sed -E 's/^# ?//'; exit 0 ;;
        *) echo "WX-238-verify: unknown argument: $1 (try --help)" >&2; exit 2 ;;
    esac
done

# Newest rendered TARGET winter-frozen report for a language (e.g. es.<stamp>/es.<stamp>.winter-frozen.html),
# never the en.* reference that shares the directory.
newest_target_html() {
    local lang="$1" dir
    dir=$(ls -dt "$QA_DIR/$lang".*/ 2>/dev/null | head -1)
    [ -n "$dir" ] || return 1
    ls -t "$dir$lang".*.winter-frozen.html 2>/dev/null | head -1
}

# has FILE NEEDLE -> 0 if the (case-insensitive, fixed-string) needle is present.
has() { grep -qiF -- "$2" "$1"; }

fail=0
missing=0

check_present() {  # FILE NEEDLE LABEL
    if has "$1" "$2"; then echo "  OK   present : $3 (\"$2\")"; else echo "  FAIL absent  : $3 (\"$2\")"; fail=1; fi
}
check_absent() {   # FILE NEEDLE LABEL
    if has "$1" "$2"; then echo "  FAIL present : $3 (\"$2\") -- the drift is still there"; fail=1; else echo "  OK   absent  : $3 (\"$2\")"; fi
}

echo "WX-238 verify -- approved vocabulary in the rendered narrative"
echo "QA dir: $QA_DIR"
echo

# ── Spanish ──────────────────────────────────────────────────────────────────
es_html=$(newest_target_html es || true)
if [ -z "$es_html" ] || [ ! -f "$es_html" ]; then
    echo "es: no rendered winter-frozen report found -- re-run the QA generation first."
    missing=1
else
    echo "es: $(basename "$es_html")"
    check_present "$es_html" "helada"    "freezing drizzle -> approved 'helada'"
    check_absent  "$es_html" "engelante" "freezing drizzle drift 'engelante'"
fi
echo

# ── Danish ───────────────────────────────────────────────────────────────────
da_html=$(newest_target_html da || true)
if [ -z "$da_html" ] || [ ! -f "$da_html" ]; then
    echo "da: no rendered winter-frozen report found -- re-run the QA generation first."
    missing=1
else
    echo "da: $(basename "$da_html")"
    check_present "$da_html" "frysende"                "freezing drizzle -> approved 'frysende stovregn'"
    check_present "$da_html" "vinterblandingsnedslag"  "wintry mix -> approved 'vinterblandingsnedslag'"
fi
echo

if [ "$missing" -eq 1 ]; then
    echo "RESULT: PRECONDITION NOT MET -- a required run is missing. Re-run the QA generation, then re-run this checker."
    exit 2
fi
if [ "$fail" -eq 1 ]; then
    echo "RESULT: FAIL -- at least one vocabulary divergence remains. Strengthen the glossary/instruction wording and re-run."
    exit 1
fi
echo "RESULT: PASS -- the documented es/da vocabulary divergences are cleared."
exit 0
