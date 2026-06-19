#!/usr/bin/env bash
# WX-203-verify.sh - confirm the es double-period fix is live, by inspecting the report bodies
# persisted since the 1.34.2 deploy.
#
# THE BUG: the es localized time ends in a period ("9:00 p. m."). When Claude's es prose ends a
# sentence right after a {q:time} token, ReportTokens.Substitute produced a DOUBLED period
# ("...tras 9:00 p. m..") -- the sentence period colliding with the abbreviation's own period.
#
# THE FIX: Substitute (the one chokepoint every prose render flows through) now collapses an
# ISOLATED doubled period to one, via a (?<!\.)\.\.(?!\.) regex that leaves an ellipsis ("...")
# intact. Spanish typography treats the abbreviation's period as also closing the sentence, so one
# period is correct. en's "9:00 PM" has no trailing period, so the rule never fires for English.
#
# THE INVARIANT (post-fix): no delivered es report body carries the doubled-period artifact. The
# realistic, ASCII-safe signature is the designator tail "m.." (both "p. m.." and "a. m.." end in
# it) -- the ONLY period-ending token value es renders, so a match is the bug, not a coincidence.
# ANY post-deploy body carrying it is the failure signature (the old binary is live, or the collapse
# regressed). That FAILs at any horizon.
#
# THE GATE (Paul, WX-203 design pass): the fix is DETERMINISTIC and fully covered by unit tests
# (StructuredReportBodyTests.Substitute_*); this live scan is a belt-and-suspenders no-regression
# check. There is NO time window. PASS requires >= 1 delivered es report since the deploy (the fix
# is es-relevant -- an en-only window proves nothing) AND zero artifacts. The triggering pattern (an
# es sentence ending on a time token) is rare, so a clean scan can pass without having exercised the
# exact collision -- the UNIT TESTS are the real proof; this guards against a live regression. The
# es-report precondition is the SAME one WX-171's verify waits on, so this adds no new wait.
#
# Like WX-171/188/190 this is a DB-reading verify: the evidence lives in CommittedSends.EmailBody
# (persisted pre-meteogram-insertion, so the rendered prose is present). It reuses verify-lib.sh for
# the version-pinned deploy boundary and the PASS/FAIL/WAIT decision. NOTE: sqlcmd mangles non-ASCII,
# so the signature is the ASCII tail "m.." (robust to the NNBSP inside the es designator, which sits
# BEFORE the "m").
#
# Usage:  WX-203-verify.sh [--since 'YYYY-MM-DD HH:MM:SS'] [--log PATH] [--deploy-log PATH] [-h]
# Shell:  bash (WSL). DB access is sqlcmd via powershell.exe against .\SQLEXPRESS / WeatherData.

# No -e by design: optional grep/parse stages may exit non-zero on an odd row and are handled inline.
set -uo pipefail

SELF="${BASH_SOURCE[0]}"
TICKET='WX-203'                                     # self-identification + header
VERSION='1.34.2'                                    # the release that shipped the fix -- the deploy pin
COMPONENTS=('WxReportSvc')                            # the service WX-203 ships in
TITLE='es localized-time double-period collapse'
MIN_WINDOW_MINUTES=1                                # satisfies verify-lib's >0 floor; the real gate is the es-report precondition + zero artifacts, NOT a wait (min_window_secs forced to ZERO below)
source "$(cd "$(dirname "$SELF")" && pwd)/verify-lib.sh"

# DB coordinates (overridable for a test instance).
DB_SERVER="${WX203_DB_SERVER:-.\\SQLEXPRESS}"
DB_NAME="${WX203_DB:-WeatherData}"

# Spanish-report marker (ASCII-safe): the es Current-Conditions heading, the es forecast-heading
# ASCII tail, the es closing label, or the es welcome greeting tail (same set WX-171 uses).
ES_MARKER='Condiciones actuales|stico para |En resumen:|Bienvenido a WxReport'

# Failure signature (ASCII): the es time designator tail doubled. "p. m." and "a. m." both end in
# "m.", so "m.." is the doubled period -- the only period-ending token value es renders. The pattern
# requires the pair be ISOLATED (a non-period char or end-of-line after it), so a legitimate run of
# three or more -- an ellipsis on an m-word ("tormenta tras tormenta...") or the preserved
# abbreviation+ellipsis run -- is NOT flagged, matching the fix's "leave runs of 3+ intact" rule.
ARTIFACT='m\.\.([^.]|$)'

sqlq() {  # SQL -> rows on stdout (headerless, '|'-separated, CR-stripped). </dev/null so powershell can't drain the read loop's stdin.
  powershell.exe -NoProfile -Command \
    "sqlcmd -S $DB_SERVER -d $DB_NAME -E -C -h -1 -W -s '|' -Q \"$1\"" </dev/null 2>/dev/null | tr -d '\r'
}
sqlbody() {  # Id -> EmailBody on stdout, unbounded width.
  powershell.exe -NoProfile -Command \
    "sqlcmd -S $DB_SERVER -d $DB_NAME -E -C -y 0 -Q \"SET NOCOUNT ON; SELECT EmailBody FROM CommittedSends WHERE Id=$1\"" </dev/null 2>/dev/null | tr -d '\r'
}

vl_parse_args "$@"
vl_resolve_boundary     # SINCE / COMMIT / DEPLOY_INFO (WAIT-exits if VERSION not deployed)
min_window_secs=0       # deterministic fix -- no wait time; gate is the es-report precondition + zero artifacts

# Delivered report sends since the deploy boundary.
CANDIDATES="$(sqlq "SET NOCOUNT ON; SELECT cs.Id, CONVERT(varchar(19), cs.CreatedAtUtc, 120) FROM CommittedSends cs WHERE cs.SentAtUtc IS NOT NULL AND cs.CreatedAtUtc >= '$SINCE' ORDER BY cs.Id")"

reports=0         # report sends inspected
es_reports=0      # Spanish report sends inspected (the precondition)
artifacts=0       # bodies carrying the doubled-period artifact -- the failure signature
VIOLATIONS=''

while IFS='|' read -r id created; do
  id="$(printf '%s' "${id:-}" | tr -d '[:space:]')"
  case "$id" in ''|*[!0-9]*) continue;; esac          # skip blank / header noise rows
  created="$(printf '%s' "$created" | sed -e 's/^ *//' -e 's/ *$//')"

  body="$(sqlbody "$id")"
  reports=$(( reports + 1 ))

  is_es=0
  printf '%s' "$body" | grep -Eq "$ES_MARKER" && { is_es=1; es_reports=$(( es_reports + 1 )); }

  if printf '%s' "$body" | grep -Eq "$ARTIFACT"; then
    artifacts=$(( artifacts + 1 ))
    VIOLATIONS="${VIOLATIONS}   send $id ($created UTC, $([ "$is_es" = 1 ] && echo es || echo en)): doubled period (designator tail 'm..') -- old binary or collapse regression"$'\n'
  fi
done <<< "$CANDIDATES"

vl_header
echo
echo " WX-203 FINGERPRINT  (delivered report bodies since the deploy boundary)"
echo "   report sends inspected                   : $reports"
echo "   Spanish (es) report sends inspected      : $es_reports   (PASS needs >= 1 -- the fix is es-relevant; configure an es recipient if 0)"
echo "   doubled-period artifacts ('m..')         : $artifacts   (expect 0 -- the failure signature)"
if [ "$artifacts" -gt 0 ]; then
  echo
  echo " FAILURE-SIGNATURE BODIES"
  printf '%s' "$VIOLATIONS"
fi
echo

regression=$artifacts
# Fail-fast on any artifact; else PASS once >= 1 es report has been inspected with none.
precond=$(( ( (es_reports >= 1) || regression > 0 ) ? 1 : 0 ))
vl_verdict "$regression" "$es_reports" \
  "a post-deploy report body still carries the es doubled-period artifact (shown above) -- the old binary is live or the Substitute collapse regressed; check the deploy and ReportTokens.Substitute / DoubledPeriodPattern." \
  "no delivered body carried the doubled-period artifact across $reports report(s), $es_reports of them Spanish -- the Substitute collapse is live. (The unit tests are the deterministic proof; this confirms no live regression. A clean es body that simply never ended a sentence on a time token still passes -- the collision is rare.)" \
  "$precond" "at least 1 delivered es report since the deploy (got $es_reports)"
