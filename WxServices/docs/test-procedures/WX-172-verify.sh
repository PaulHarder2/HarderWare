#!/usr/bin/env bash
# WX-172-verify.sh - confirm the generation-on-enable feature is live and the resulting
# template/state invariants hold, by querying the WeatherData DB (the source of truth) plus
# the WxReport service log since the 1.35.0 deploy.
#
# THE CHANGE: enabling a language marks it PENDING; WxReport.Svc generates its localized
# templates from the en baseline via one Claude call, with a representability self-check and
# fail-closed validation, resolving to READY / BLOCKED / FAILED (encoded in
# Languages.GeneratedAtUtc + GenerationError). Recipients may only be assigned a READY language.
#
# THE INVARIANTS (post-deploy, current DB state -- the heart of this verify): for the
# state-machine + recipient gate to be sound, ALL of these must be ZERO:
#   1. ready_incomplete       -- a READY language MISSING >=1 representable baseline (en) token.
#                                This is the "all needed templates present" check Paul asked for:
#                                a language marked READY must carry the full token set.
#   2. ready_with_blocked     -- a READY language (GenerationError NULL) that nonetheless has a
#                                Representable=0 row (it should have been BLOCKED, not READY).
#   3. blocked_without_rows   -- a BLOCKED language (GenerationError set, GeneratedAtUtc set) with
#                                NO Representable=0 row (the block reason has no backing token).
#   4. recipients_not_ready   -- a recipient assigned a language that is NOT READY (the gate the
#                                Recipients tab enforces must hold in the data too; a NULL
#                                LanguageId is the service default and is fine).
#   5. ready_no_culture       -- a READY language with no CultureName (date/number formatting).
# Any nonzero count is the failure signature and FAILs at any horizon.
#
# THE GATE: deterministic DB state -- no time window. PASS is gated on >= MIN_CYCLES report
# cycles since the deploy (a "Starting report cycle." line marks one), so the new binary is
# proven live before its DB state is trusted. Until the version deploys / enough cycles log -> WAIT.
#
# DB-reading verify (like WX-171/188/190/193): the evidence is the Languages/LanguageTemplates/
# Recipients tables. Reuses verify-lib.sh for the version-pinned deploy boundary, the log window
# (report-cycle count + health), and the PASS/FAIL/WAIT decision.
#
# Usage:  WX-172-verify.sh [--since 'YYYY-MM-DD HH:MM:SS'] [--log PATH] [--deploy-log PATH] [-h]
# Shell:  bash (WSL). DB access is sqlcmd via powershell.exe against .\SQLEXPRESS / WeatherData
#         (override with WX172_DB_SERVER / WX172_DB). The PC runs in UTC. Shared scaffold in
#         verify-lib.sh.

# No -e by design: optional grep/parse stages may exit non-zero on an odd row and are handled
# inline; -e would abort the whole sweep on one.
set -uo pipefail

SELF="${BASH_SOURCE[0]}"
TICKET='WX-172'                                      # self-identification + header
VERSION='1.35.0'                                     # the release VERSION that shipped the change -- the deploy pin
COMPONENTS=('WxReportSvc')                            # the service WX-172 ships the generation engine in
TITLE='generation-on-enable -- template completeness + state/gate invariants'
MIN_CYCLES=2                                         # PASS gate: minimum report cycles since deploy (proves the new binary is live)
MIN_WINDOW_MINUTES=1                                 # satisfies verify-lib's >0 floor; the real gate is the cycle count + DB invariants, NOT a wait (min_window_secs forced to ZERO below)
source "$(cd "$(dirname "$SELF")" && pwd)/verify-lib.sh"

# DB coordinates (overridable for a test instance).
DB_SERVER="${WX172_DB_SERVER:-.\\SQLEXPRESS}"
DB_NAME="${WX172_DB:-WeatherData}"

# Run a query; columns '|'-separated, headerless, trimmed, CR-stripped. </dev/null so
# powershell.exe cannot drain a read-loop's stdin (the WX-198 under-count bug).
sqlq() {  # SQL -> rows on stdout
  powershell.exe -NoProfile -Command \
    "sqlcmd -S $DB_SERVER -d $DB_NAME -E -C -h -1 -W -s '|' -Q \"$1\"" </dev/null 2>/dev/null | tr -d '\r'
}

# The single invariant query: eleven counts in a fixed order. READY = enabled + generated + no error.
# A READY language is "complete" when its representable tokens cover every representable en token
# (the SQL form of the baseline-completeness rule, matching the renderer's send gate). The final
# column is the en baseline's own representable-token count: if it's 0 the completeness subqueries
# pass VACUOUSLY (nothing to be missing), so it is itself a failure -- the baseline seed is gone.
read -r -d '' INVARIANT_SQL <<'SQL'
SET NOCOUNT ON;
SELECT
 (SELECT COUNT(*) FROM Languages l WHERE l.IsEnabled=1 AND l.GeneratedAtUtc IS NOT NULL AND l.GenerationError IS NULL
    AND EXISTS (SELECT 1 FROM LanguageTemplates b JOIN Languages bl ON bl.Id=b.LanguageId AND bl.IsoCode='en'
      WHERE b.Representable=1 AND NOT EXISTS (SELECT 1 FROM LanguageTemplates t WHERE t.LanguageId=l.Id AND t.Token=b.Token AND t.Representable=1))),
 (SELECT COUNT(*) FROM Languages l WHERE l.IsEnabled=1 AND l.GeneratedAtUtc IS NOT NULL AND l.GenerationError IS NULL
    AND EXISTS (SELECT 1 FROM LanguageTemplates t WHERE t.LanguageId=l.Id AND t.Representable=0)),
 (SELECT COUNT(*) FROM Languages l WHERE l.IsEnabled=1 AND l.GeneratedAtUtc IS NOT NULL AND l.GenerationError IS NOT NULL
    AND NOT EXISTS (SELECT 1 FROM LanguageTemplates t WHERE t.LanguageId=l.Id AND t.Representable=0)),
 (SELECT COUNT(*) FROM Recipients r JOIN Languages l ON l.Id=r.LanguageId
    WHERE NOT (l.IsEnabled=1 AND l.GeneratedAtUtc IS NOT NULL AND l.GenerationError IS NULL)),
 (SELECT COUNT(*) FROM Languages l WHERE l.IsEnabled=1 AND l.GeneratedAtUtc IS NOT NULL AND l.GenerationError IS NULL
    AND (l.CultureName IS NULL OR LTRIM(RTRIM(l.CultureName))='')),
 (SELECT COUNT(*) FROM Languages WHERE IsEnabled=1 AND GeneratedAtUtc IS NOT NULL AND GenerationError IS NULL),
 (SELECT COUNT(*) FROM Languages WHERE IsEnabled=1 AND GeneratedAtUtc IS NOT NULL AND GenerationError IS NOT NULL),
 (SELECT COUNT(*) FROM Languages WHERE IsEnabled=1 AND GeneratedAtUtc IS NULL AND GenerationError IS NOT NULL),
 (SELECT COUNT(*) FROM Languages WHERE IsEnabled=1 AND GeneratedAtUtc IS NULL AND GenerationError IS NULL),
 (SELECT COUNT(*) FROM Languages WHERE IsEnabled=1),
 (SELECT COUNT(*) FROM LanguageTemplates b JOIN Languages bl ON bl.Id=b.LanguageId AND bl.IsoCode='en' WHERE b.Representable=1)
SQL

vl_parse_args "$@"
vl_resolve_boundary     # SINCE / COMMIT / DEPLOY_INFO (WAIT-exits if VERSION not deployed yet)
vl_setup_window         # POST (log lines since SINCE), LAST_TS, hh, mm (WAIT-exits if none)
min_window_secs=0       # deterministic DB state -- no wait time; the gate is the cycle count + invariants

# ---- DB invariants (current state -- the core correctness check) -----------------
ROW="$(sqlq "$INVARIANT_SQL" | tr -d ' ' | grep -E '^[0-9]+(\|[0-9]+){10}$' | head -1)"
if [ -z "$ROW" ]; then
  echo "WX-172 FAIL: the invariant query returned no parseable row (DB unreachable, or the schema/migration is not applied). Check sqlcmd connectivity to $DB_SERVER / $DB_NAME." >&2
  exit 3
fi
IFS='|' read -r ready_incomplete ready_with_blocked blocked_without_rows recipients_not_ready ready_no_culture \
  ready_count blocked_count failed_count pending_count enabled_count en_representable <<< "$ROW"
# The en baseline having zero representable tokens makes every completeness subquery pass
# vacuously, so it is itself a failure (the seed/migration is broken).
baseline_missing=$(( en_representable == 0 ? 1 : 0 ))

# ---- log fingerprints since the deploy boundary (from POST) -----------------------
cycles=$(printf '%s\n' "$POST" | cnt 'Starting report cycle\.')                       # exercised: report cycles ran on the new binary
gen_ready=$(printf '%s\n' "$POST" | cnt "WX-172: '[a-z][a-z]*' generated and READY")  # positive evidence (opportunistic; only if a language was enabled)
gen_blocked=$(printf '%s\n' "$POST" | cnt 'WX-172: .* BLOCKED')                       # context: a language came back blocked
gen_failed=$(printf '%s\n' "$POST" | cnt 'WX-172: .* generation FAILED')             # context: a transient generation failure (retries)
gen_threw=$(printf '%s\n' "$POST" | cnt 'WX-172: generation for .* threw')           # context: an unexpected generation exception
read -r err_before err_after err_new < <(vl_health_delta ' ERROR ')                  # background health vs the equal-length pre-deploy window

regression=$(( ready_incomplete + ready_with_blocked + blocked_without_rows + recipients_not_ready + ready_no_culture + baseline_missing ))

vl_header
echo
echo " WX-172 DB INVARIANTS  (current WeatherData state -- ALL must be 0)"
echo "   READY languages missing >=1 baseline token : $ready_incomplete   (the 'all templates present' check)"
echo "   READY languages carrying a blocked row     : $ready_with_blocked"
echo "   BLOCKED languages with no blocked row       : $blocked_without_rows"
echo "   recipients assigned a NON-READY language    : $recipients_not_ready"
echo "   READY languages with no CultureName         : $ready_no_culture"
echo "   en baseline has zero representable tokens   : $baseline_missing   (1 = seed/migration broken; checks above are vacuous)"
echo "                                          total : $regression   (expect 0 -- the failure signature)"
echo
echo " STATE CENSUS  (context)"
echo "   enabled=$enabled_count  ready=$ready_count  blocked=$blocked_count  failed=$failed_count  pending=$pending_count  en_tokens=$en_representable"
echo
echo " LOG FINGERPRINT  (wxreport-svc.log since the deploy boundary)"
echo "   report cycles (exercised)                   : $cycles   (PASS needs >= $MIN_CYCLES)"
echo "   'generated and READY' lines                 : $gen_ready   (positive evidence; only when a language was enabled)"
echo "   BLOCKED lines                               : $gen_blocked   (context -- a language needs a renderer/code change)"
echo "   generation FAILED lines                     : $gen_failed   (context -- transient; the language retries)"
echo "   generation threw lines                      : $gen_threw   (context -- unexpected exception; review)"
echo
echo " BACKGROUND HEALTH  (equal-length pre-deploy window cancels steady noise)"
echo "   ERROR lines   before=$err_before  after=$err_after  new=$err_new"
echo

precond=$(( (cycles >= MIN_CYCLES || regression > 0) ? 1 : 0 ))
vl_verdict "$regression" "$cycles" \
  "a WX-172 state invariant is violated (counts above) -- a READY language is missing tokens, a READY/BLOCKED state disagrees with its rows, a recipient is on a non-READY language, or a READY language lacks a culture. Inspect Languages/LanguageTemplates/Recipients; a missing-token READY language means generation stamped READY without a full set (check the en baseline / parity gate)." \
  "every WX-172 DB invariant holds across $cycles report cycle(s): every READY language carries the full baseline token set, READY/BLOCKED states agree with their rows, no recipient is on a non-READY language, and every READY language has a culture." \
  "$precond" "at least $MIN_CYCLES report cycles since the deploy (got $cycles)"
