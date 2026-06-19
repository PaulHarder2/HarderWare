#!/usr/bin/env bash
# WX-171-verify.sh - confirm WX-171 (the renderer reads atomic localized tokens from the DB
# LanguageTemplates table instead of gluing the hard-coded ReportVocabulary, fixing the Spanish
# word-order/agreement defects incl. WX-174 "Helada lluvia" -> "Lluvia helada") is live, by
# inspecting the report bodies persisted since the deploy plus the new startup/per-cycle log
# fingerprints.
#
# THE CHANGE: StructuredReportRenderer now resolves every grammar-sensitive phrase as one atomic
# token via TemplateSet.Get(Tok.*), loaded from LanguageTemplates. The Spanish phrases are
# corrected as a unit, so the OLD renderer's English-order / English-agreement Spanish is never
# emitted again:
#     OLD (buggy)            NEW (correct)
#     Helada lluvia          Lluvia helada           (freezing order -- WX-174)
#     Bajo nublado           Nublado bajo            (sky-height order)
#     Ligera lluvia          Lluvia ligera           (intensity order)
#     lluvia probables       lluvia probable         (singular noun + singular adjective)
#     Tiempo severo probables Tiempo severo probable
#     (en)  Showers rain     Rain showers            (showers order)
#     (en)  ...within about 30 miles...  ...any nearby station...  (NoObservationNote reword)
# The forecast feminine-plural nouns (tormentas, Tormentas severas) were already correct and are
# UNCHANGED, so they are NOT part of the signature.
#
# THE INVARIANT (post-fix): no report rendered since the deploy contains any of the OLD buggy
# strings above. The UNAMBIGUOUS failure signature is a post-deploy body (es OR en) still carrying
# one of them -- that means the old binary is still running, or a token regressed. That FAILs at
# any horizon. (The strings are weather-dependent -- a dry/clear forecast carries none either way --
# so their ABSENCE is the regression guard; the deeper positive confirmation "saw 'Lluvia helada'
# rendered" is opportunistic and reported non-gating for the manual review step in WX-171.md.)
#
# WEATHER-INDEPENDENT new-binary fingerprint (the service LOG): on every start the new binary logs
# the WX-171 fail-closed startup completeness check ("Language template completeness check passed
# for all N loaded language(s)."), and on every cycle the per-cycle template refresh logs
# "LanguageTemplateStore loaded ...". Their presence proves the new code path is live regardless of
# the weather; their fail-closed ERROR siblings ("is INCOMPLETE" / "is incomplete") surface a
# broken language (non-gating health).
#
# THE GATE (Paul, WX-171 design pass -- "fuller live verify"): this is a deterministic render
# change, so there is NO time window. PASS is gated on (a) the new-binary startup fingerprint being
# present, (b) at least one Spanish (es) report actually delivered since the deploy -- the headline
# fix is Spanish, so confirming it end-to-end REQUIRES an es recipient to have received a report
# (configure one on the Recipients tab if none exists) -- and (c) a minimum number of report-issuing
# CYCLES (default 3), so more than one real forecast is exercised. Until all three hold -> WAIT.
#
# Like WX-188/190/193 this is a DB-reading verify: the fix changes rendered OUTPUT, whose evidence
# lives in CommittedSends.EmailBody (persisted pre-meteogram-insertion, so the rendered phrases are
# present). It reuses verify-lib.sh for the version-pinned deploy boundary, the log window, and the
# PASS/FAIL/WAIT decision. NOTE: sqlcmd mangles non-ASCII, so EVERY fingerprint below is ASCII
# (the corrected/buggy Spanish phrases happen to be accent-free; the "Pronostico para" heading is
# matched by its ASCII tail "stico para ", as WX-190 does).
#
# Usage:  WX-171-verify.sh [--since 'YYYY-MM-DD HH:MM:SS'] [--log PATH] [--deploy-log PATH] [-h]
# Shell:  bash (WSL). DB access is sqlcmd via powershell.exe against .\SQLEXPRESS / WeatherData
#         (override with WX171_DB_SERVER / WX171_DB). The PC runs in UTC. Shared scaffold in
#         verify-lib.sh.

# No -e by design: optional grep/parse stages may exit non-zero on an odd row and are handled
# inline; -e would abort the whole sweep on one.
set -uo pipefail

SELF="${BASH_SOURCE[0]}"
TICKET='WX-171'                                     # self-identification + header
VERSION='1.34.0'                                    # the release VERSION that shipped the change -- the deploy pin
COMPONENTS=('WxReportSvc')                           # the service WX-171 ships in
TITLE='atomic-token reshape -- DB-backed templates + corrected Spanish grammar'
MIN_CYCLES=3                                        # PASS gate: minimum report-issuing cycles since deploy (the startup diagnostic counts as one)
MIN_WINDOW_MINUTES=1                                # kept at 1 only to satisfy verify-lib's >0 validation; the actual gate is the cycle count + es-report precondition, NOT a wait -- min_window_secs is forced to ZERO below
source "$(cd "$(dirname "$SELF")" && pwd)/verify-lib.sh"

# DB coordinates (overridable for a test instance).
DB_SERVER="${WX171_DB_SERVER:-.\\SQLEXPRESS}"
DB_NAME="${WX171_DB:-WeatherData}"

# OLD buggy strings the new renderer NEVER emits -- the failure signature (ASCII; es + en). A
# match in any post-deploy body means the old binary is live or a token regressed. The
# singular-noun + plural-adjective agreement bug is the "(noun) (plural-outlook)" alternation;
# the feminine-plural forecast nouns (tormentas / Tormentas severas) are correct and excluded.
OLD_SIG='Helada lluvia|Helada llovizna|Bajo nublado|Alto nublado|Bajo mayormente nublado|Alto mayormente nublado|Ligera lluvia|Fuerte lluvia|Ligera llovizna|Ligera nieve|Fuerte nieve|(lluvia|nieve|llovizna|invernal|severo) (posibles|probables|previstas)|Showers rain|Showers snow|within about 30 miles'

# NEW corrected strings -- positive evidence, reported non-gating (present only when the weather
# produced the relevant phenomenon).
NEW_SIG='Lluvia helada|Llovizna helada|Nublado bajo|Nublado alto|Lluvia ligera|Lluvia fuerte|Llovizna ligera|Nieve ligera|Nieve fuerte|Mayormente nublado bajo|Mayormente nublado alto|Rain showers|Snow showers|any nearby station'

# Spanish-report marker (ASCII-safe): the es Current-Conditions heading, the es forecast-heading
# ASCII tail, the es closing label, or the es welcome greeting tail.
ES_MARKER='Condiciones actuales|stico para |En resumen:|Bienvenido a WxReport'

# Run a query; rows '|'-separated, headerless, CR-stripped. </dev/null so powershell.exe cannot
# drain the read-loop's stdin (the WX-198 under-count bug).
sqlq() {  # SQL -> rows on stdout
  powershell.exe -NoProfile -Command \
    "sqlcmd -S $DB_SERVER -d $DB_NAME -E -C -h -1 -W -s '|' -Q \"$1\"" </dev/null 2>/dev/null | tr -d '\r'
}
# Dump one report's EmailBody, unbounded width.
sqlbody() {  # Id -> EmailBody on stdout
  powershell.exe -NoProfile -Command \
    "sqlcmd -S $DB_SERVER -d $DB_NAME -E -C -y 0 -Q \"SET NOCOUNT ON; SELECT EmailBody FROM CommittedSends WHERE Id=$1\"" </dev/null 2>/dev/null | tr -d '\r'
}

vl_parse_args "$@"
# vl_resolve_boundary requires a readable service log (a shared-lib precondition); the deployed
# box always has wxreport-svc.log, and here we DO parse it (the startup/per-cycle fingerprints).
vl_resolve_boundary     # sets SINCE / COMMIT / BOUNDARY_SRC / DEPLOY_INFO (WAIT-exits if VERSION not deployed)

# ---- weather-independent new-binary fingerprints (service log) ------------------
# Completeness check passed (startup, every start) and the per-cycle template refresh load line.
# Their fail-closed ERROR siblings are surfaced as non-gating health below.
completeness_ok=$(win "$SINCE" '' | cnt 'Language template completeness check passed')
reload_lines=$(win "$SINCE" '' | cnt 'LanguageTemplateStore loaded')
incomplete_startup=$(win "$SINCE" '' | cnt 'is INCOMPLETE')
incomplete_send=$(win "$SINCE" '' | cnt "language '[a-z][a-z]*' is incomplete")

# ---- delivered report sends since the deploy boundary ---------------------------
CANDIDATES="$(sqlq "SET NOCOUNT ON; SELECT cs.Id, cs.ForecastSnapshotId, CONVERT(varchar(19), cs.CreatedAtUtc, 120) FROM CommittedSends cs WHERE cs.SentAtUtc IS NOT NULL AND cs.CreatedAtUtc >= '$SINCE' ORDER BY cs.Id")"

since_epoch=$(date -u -d "$SINCE UTC" +%s)
reports=0         # report sends inspected
es_reports=0      # Spanish report sends inspected (the headline-fix precondition)
old_hits=0        # bodies carrying an OLD buggy string -- the failure signature
new_hits=0        # bodies carrying a NEW corrected string -- positive evidence (non-gating)
CYCLE_IDS=''      # ForecastSnapshotIds (deduped at end -> cycle count)
LAST_TS="$SINCE"; last_epoch=$since_epoch
VIOLATIONS=''; POSITIVES=''

while IFS='|' read -r id snapid created; do
  id="$(printf '%s' "${id:-}" | tr -d '[:space:]')"
  case "$id" in ''|*[!0-9]*) continue;; esac          # skip blank / header noise rows
  snapid="$(printf '%s' "${snapid:-}" | tr -d '[:space:]')"
  created="$(printf '%s' "$created" | sed -e 's/^ *//' -e 's/ *$//')"

  c_epoch=$(date -u -d "$created UTC" +%s 2>/dev/null || echo "")
  [ -n "$c_epoch" ] || continue
  [ "$c_epoch" -gt "$last_epoch" ] && { last_epoch=$c_epoch; LAST_TS="$created"; }

  body="$(sqlbody "$id")"
  reports=$(( reports + 1 ))
  case "$snapid" in ''|*[!0-9]*) ;; *) CYCLE_IDS="${CYCLE_IDS} ${snapid}";; esac

  is_es=0
  printf '%s' "$body" | grep -Eq "$ES_MARKER" && { is_es=1; es_reports=$(( es_reports + 1 )); }

  # Failure signature: any OLD buggy string in the body.
  hit="$(printf '%s' "$body" | grep -Eo "$OLD_SIG" | sort -u | paste -sd ', ' -)"
  if [ -n "$hit" ]; then
    old_hits=$(( old_hits + 1 ))
    VIOLATIONS="${VIOLATIONS}   send $id ($created UTC, $([ "$is_es" = 1 ] && echo es || echo en)): OLD buggy phrase(s) [$hit] -- old binary or token regression"$'\n'
  fi

  # Positive evidence: any NEW corrected string (non-gating).
  pos="$(printf '%s' "$body" | grep -Eo "$NEW_SIG" | sort -u | paste -sd ', ' -)"
  if [ -n "$pos" ]; then
    new_hits=$(( new_hits + 1 ))
    POSITIVES="${POSITIVES}   send $id ($created UTC, $([ "$is_es" = 1 ] && echo es || echo en)): corrected phrase(s) [$pos]"$'\n'
  fi
done <<< "$CANDIDATES"

# Distinct report-issuing cycles = distinct reconciled snapshots among the inspected reports.
cycles=$(printf '%s\n' $CYCLE_IDS | sed '/^$/d' | sort -u | grep -c . || true)

# Window bookkeeping for vl_header. EVENT/COUNT-based verify, not accrual-based: no wait time, so
# the window gate is ZERO and the verdict rests on the cycle count + the es-report precondition.
now_epoch=$(date -u +%s)
if [ "$now_epoch" -gt "$last_epoch" ]; then last_epoch=$now_epoch; LAST_TS="$(date -u -d "@$now_epoch" '+%Y-%m-%d %H:%M:%S')"; fi
elapsed=$(( last_epoch - since_epoch )); [ "$elapsed" -gt 0 ] || elapsed=1
hh=$(( elapsed / 3600 )); mm=$(( (elapsed % 3600) / 60 ))
min_window_secs=0                                  # no wait time by design -- the lib's >0 floor is bypassed here

vl_header
echo
echo " WX-171 FINGERPRINT  (service log + report bodies since the deploy boundary)"
echo "   new-binary startup check passed       : $completeness_ok   (expect >= 1 -- 'completeness check passed' per start)"
echo "   per-cycle template refresh load lines : $reload_lines   (expect >= 1 -- 'LanguageTemplateStore loaded' per cycle)"
echo "   report sends inspected                : $reports"
echo "   Spanish (es) report sends inspected   : $es_reports   (PASS needs >= 1 -- the headline fix is Spanish; configure an es recipient if 0)"
echo "   report-issuing cycles (distinct snaps): $cycles   (PASS needs >= $MIN_CYCLES; deploy diagnostic counts as one)"
echo "   bodies with OLD buggy phrase          : $old_hits   (expect 0 -- the failure signature)"
echo "   bodies with NEW corrected phrase      : $new_hits   (positive evidence; weather-dependent, non-gating)"
[ "$incomplete_startup" -gt 0 ] && echo "   startup 'is INCOMPLETE' ERROR lines   : $incomplete_startup   (a language is missing tokens -- review, non-gating)"
[ "$incomplete_send" -gt 0 ] && echo "   send-gate 'is incomplete' ERROR lines : $incomplete_send   (a recipient failed closed on an incomplete language -- review, non-gating)"
if [ "$old_hits" -gt 0 ]; then
  echo
  echo " FAILURE-SIGNATURE LINES"
  printf '%s' "$VIOLATIONS"
fi
if [ "$new_hits" -gt 0 ]; then
  echo
  echo " POSITIVE EVIDENCE  (corrected phrases rendered -- the end-to-end confirmation WX-171.md asks for)"
  printf '%s' "$POSITIVES"
fi
echo

# Verdict. PASS is gated on: the new-binary startup fingerprint present, >= 1 es report delivered,
# and >= MIN_CYCLES report-issuing cycles -- all encoded in `precond`. A real failure signature
# (old_hits > 0) sets the precond too, so it reports as a hard FAIL immediately rather than being
# deferred to WAIT below the cycle/es threshold. regression = bodies carrying an old buggy phrase.
regression=$old_hits
precond=$(( ( (cycles >= MIN_CYCLES && es_reports > 0 && completeness_ok > 0) || regression > 0 ) ? 1 : 0 ))
vl_verdict "$regression" "$cycles" \
  "a post-deploy report still rendered the old English-order/agreement Spanish (or an en showers/no-obs regression) -- the old binary is still running, or a LanguageTemplates token regressed; check the deploy and the seed." \
  "the DB-backed atomic-token renderer is live: no body carried an old buggy phrase across $cycles cycle(s), $es_reports Spanish report(s) delivered, and the startup completeness check passed -- confirm the POSITIVE EVIDENCE phrases above are correctly ordered/agreeing per WX-171.md (and that a dry-weather es report simply carried no precip phrases, not a silenced one)." \
  "$precond" "the new-binary startup check + >= 1 es report + >= $MIN_CYCLES report cycles (startup check passed=$completeness_ok, es reports=$es_reports, cycles=$cycles)"
