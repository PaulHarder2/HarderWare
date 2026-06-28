#!/usr/bin/env bash
# WX-228-verify.sh - confirm the deterministic temperature-range summary is live, by
# inspecting the RAW structured-report narratives persisted since the 1.38.2 deploy.
#
# THE BUG: the reconciler model, asked to summarize a week of daily highs/lows but given
# only a single-POINT token ({q:temp:NN}, canonical C), wrapped that point in a fuzzy band
# word -- "well into the upper {q:temp:36} range" -- which the renderer turned into the
# meaningless "the upper 97 deg F range". There was no unit-neutral way to say a SPAN.
#
# THE FIX: a new unit-neutral {q:temp_range:lo:hi} token (two C endpoints, rendered as one
# converted span -- "97-99 deg F" / "36-37 deg C"), plus a deterministic pre-call pass
# (TemperatureRangeSummarizer) that characterizes the per-day highs/lows into one or two
# ranges and hands Claude the ready tokens to weave into native closing prose. The model no
# longer phrases temperature bands itself.
#
# WHY THE RAW NARRATIVE, NOT THE RENDERED EMAIL: the rendered span uses non-ASCII (en-dash,
# the degree sign), which sqlcmd mangles (the WX-203 lesson). CommittedSends.StructuredReport
# persists the unit-neutral narrative with the literal ASCII tokens, so both the adoption
# proof ("temp_range") and the failure signature (a POINT token wrapped in a band word) are
# ASCII and robust. The point token "{q:temp:" never matches inside "{q:temp_range:" -- the
# char after "temp" is ":" vs "_".
#
# THE SIGNATURES (on the raw narrative JSON):
#   adoption (exercised) : a narrative containing "temp_range" -- the model used the new token.
#   failure  (regression): a POINT temp token dressed as a band -- "(upper|lower|mid) {q:temp:",
#                          or "{q:temp:NN} range", or "{q:temp:NN}s" (the banned "in the low NNs"
#                          idiom). ANY hit means the prompt/render fix is not taking -- the old
#                          binary is live, or the model reverted to wrapping a point value.
#
# THE GATE (Paul, WX-228 design pass): the fix is DETERMINISTIC and fully covered by unit tests
# (TemperatureRangeSummarizerTests, StructuredReportBodyTests temp_range cases, the renderer
# conversion test). This live scan is a belt-and-suspenders no-regression + adoption check with
# NO time window: PASS needs >= 1 delivered report since deploy (precondition) that USED a
# temp_range token (exercised), and zero band-wrapped-point signatures. The closings of the most
# recent adopting report are printed per language for a human "reads naturally in es/de" spot-check
# (the natural-prose quality is a judgement the scan can't make).
#
# Usage:  WX-228-verify.sh [--since 'YYYY-MM-DD HH:MM:SS'] [--log PATH] [--deploy-log PATH] [-h]
# Shell:  bash (WSL). DB access is sqlcmd via powershell.exe against .\SQLEXPRESS / WeatherData.

# No -e by design: optional grep/parse stages may exit non-zero on an odd row and are handled inline.
set -uo pipefail

SELF="${BASH_SOURCE[0]}"
TICKET='WX-228'                                     # self-identification + header
VERSION='1.38.2'                                    # the release that shipped the fix -- the deploy pin
COMPONENTS=('WxReportSvc')                           # the service WX-228 ships in
TITLE='deterministic temperature-range summary'
MIN_WINDOW_MINUTES=1                                # satisfies verify-lib's >0 floor; the real gate is the adoption precondition + zero signatures, NOT a wait (min_window_secs forced to ZERO below)
source "$(cd "$(dirname "$SELF")" && pwd)/verify-lib.sh"

# DB coordinates (overridable for a test instance).
DB_SERVER="${WX228_DB_SERVER:-.\\SQLEXPRESS}"
DB_NAME="${WX228_DB:-WeatherData}"

# Failure signature (ASCII): a single-POINT temp token dressed as a band -- the pre-fix form.
#   (upper|lower|mid) <=6 chars then "{q:temp:"   -- "the upper {q:temp:36} range"
#   "{q:temp:NN}" <=6 chars then "range"          -- the trailing band noun
#   "{q:temp:NN}s"                                 -- the banned "in the low {q:temp:33}s" idiom
# "{q:temp:" cannot match inside "{q:temp_range:" (":" vs "_" after "temp").
BUG='\b(upper|lower|mid)[^.{}]{0,6}\{q:temp:|\{q:temp:[0-9.]+\}[^.{}]{0,6}\brange|\{q:temp:[0-9.]+\}s'

sqlq() {  # SQL -> rows on stdout (headerless, '|'-separated, CR-stripped). </dev/null so powershell can't drain the read loop's stdin.
  powershell.exe -NoProfile -Command \
    "sqlcmd -S $DB_SERVER -d $DB_NAME -E -C -h -1 -W -s '|' -Q \"$1\"" </dev/null 2>/dev/null | tr -d '\r'
}
sqlsr() {  # Id -> StructuredReport (raw unit-neutral narrative JSON) on stdout, unbounded width.
  powershell.exe -NoProfile -Command \
    "sqlcmd -S $DB_SERVER -d $DB_NAME -E -C -y 0 -Q \"SET NOCOUNT ON; SELECT StructuredReport FROM CommittedSends WHERE Id=$1\"" </dev/null 2>/dev/null | tr -d '\r'
}

vl_parse_args "$@"
vl_resolve_boundary     # SINCE / COMMIT / DEPLOY_INFO (WAIT-exits if VERSION not deployed)
min_window_secs=0       # deterministic fix -- no wait time; gate is adoption precondition + zero signatures
# DB/event-based verify: no vl_setup_window, so set the window bookkeeping vl_verdict expects
# (elapsed/hh/mm) here -- elapsed = deploy boundary -> now (mirrors WX-203; the shared helper is WX-209).
since_epoch=$(date -u -d "$SINCE UTC" +%s 2>/dev/null) \
  || { echo "could not parse boundary '$SINCE'" >&2; exit 2; }
now_epoch=$(date -u +%s)
elapsed=$(( now_epoch - since_epoch )); [ "$elapsed" -gt 0 ] || elapsed=1
hh=$(( elapsed / 3600 )); mm=$(( (elapsed % 3600) / 60 ))

# Delivered report sends since the deploy boundary.
CANDIDATES="$(sqlq "SET NOCOUNT ON; SELECT cs.Id, CONVERT(varchar(19), cs.CreatedAtUtc, 120) FROM CommittedSends cs WHERE cs.SentAtUtc IS NOT NULL AND cs.CreatedAtUtc >= '$SINCE' ORDER BY cs.Id")"

reports=0          # delivered report sends inspected (the precondition)
adopting=0         # sends whose narrative used a temp_range token (the exercised count)
signatures=0       # narratives carrying a band-wrapped point token -- the failure signature
LAST_ADOPTING=''   # most recent adopting send Id, for the natural-prose spot-check
VIOLATIONS=''

while IFS='|' read -r id created; do
  id="$(printf '%s' "${id:-}" | tr -d '[:space:]')"
  case "$id" in ''|*[!0-9]*) continue;; esac          # skip blank / header noise rows
  created="$(printf '%s' "$created" | sed -e 's/^ *//' -e 's/ *$//')"

  sr="$(sqlsr "$id")"
  reports=$(( reports + 1 ))

  printf '%s' "$sr" | grep -q 'temp_range' && { adopting=$(( adopting + 1 )); LAST_ADOPTING="$id"; }

  if printf '%s' "$sr" | grep -Eq "$BUG"; then
    signatures=$(( signatures + 1 ))
    VIOLATIONS="${VIOLATIONS}   send $id ($created UTC): a point temp token wrapped as a band -- old binary or the model reverted to band-wrapping a {q:temp:} point"$'\n'
  fi
done <<< "$CANDIDATES"

vl_header
echo
echo " WX-228 FINGERPRINT  (raw structured-report narratives since the deploy boundary)"
echo "   delivered report sends inspected         : $reports"
echo "   sends using a {q:temp_range} token       : $adopting   (PASS needs >= 1 -- proves the model adopted the token)"
echo "   band-wrapped point-token signatures      : $signatures   (expect 0 -- the failure signature)"
if [ "$signatures" -gt 0 ]; then
  echo
  echo " FAILURE-SIGNATURE BODIES"
  printf '%s' "$VIOLATIONS"
fi

# Natural-prose spot-check: dump the most recent adopting report's per-language closings so a
# human can confirm the highs/lows read idiomatically (esp. es/de). Best-effort -- jq only.
if [ -n "$LAST_ADOPTING" ] && command -v jq >/dev/null 2>&1; then
  echo
  echo " CLOSING PROSE (send $LAST_ADOPTING) -- confirm each language reads naturally:"
  sqlsr "$LAST_ADOPTING" | jq -r '.narrative | to_entries[] | "   [\(.key)] \(.value.closing)"' 2>/dev/null \
    || echo "   (could not parse narrative JSON for the spot-check)"
fi
echo

precond=$(( reports >= 1 ? 1 : 0 ))
vl_verdict "$signatures" "$adopting" \
  "a delivered narrative still wraps a {q:temp:} point in a band word (shown above) -- the old binary is live or the model reverted; check the deploy, ReconcilerPrompts temp_range guidance, and TemperatureRangeSummarizer." \
  "every delivered narrative that summarized temperatures used a {q:temp_range} span token and none wrapped a point in a band word -- the deterministic summary is live. (The unit tests are the deterministic proof; this confirms adoption + no live regression. Eyeball the closings above for natural es/de phrasing.)" \
  "$precond" "at least 1 delivered report since the deploy (got $reports)"