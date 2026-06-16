#!/usr/bin/env bash
# WX-193-verify.sh - confirm WX-193 (a DIAGNOSTIC report suppresses the "What's changed"
# band except for a near-term severe onset) is live, by inspecting the diagnostic reports
# persisted in the database since the deploy.
#
# THE BUG (a WX-189 regression): WX-189 made the change set deterministic, so the band now
# renders from the computed changes[] via the fallback even when Claude wrote no
# changeSummary. But the WX-178 strip that gates the band was Scheduled-ONLY, and the
# diagnostic path (ReportWorker.SendStartupReportAsync) never stripped at all -- so a
# deploy-verification diagnostic surfaced a band for ordinary non-severe changes ("rain
# developing", a rain->thunderstorm upgrade with no near-term severe). Observed on the
# 1.32.0 startup diagnostic to paul_en.
#
# THE INVARIANT (post-fix): a diagnostic report carries the "What's changed" band ONLY for a
# newly-appearing near-term (local Day 1-3) severe onset -- identical to a scheduled report
# (ReportWorker.SuppressesScheduledChangeBand now accepts ReportKind.Diagnostic). The band
# heading renders as "What's changed:" (English) and a severe report's body always carries a
# "Severe" lead ("Severe storms" / "Severe weather"). So the UNAMBIGUOUS failure signature
# is a diagnostic send whose body carries the band but contains NO severe indicator at all --
# a band on an ordinary non-severe diagnostic, the exact regression. A diagnostic band on a
# body that DOES contain "Severe" might be a legitimate near-term onset OR a stale far-day /
# pre-existing-severe band (onset = false->true needs the prior snapshot, which the body
# alone can't establish); those are REPORTED for the manual justification step in WX-193.md,
# not gated here.
#
# Like WX-188, this is a DB-reading verify: the fix changes rendered OUTPUT, which leaves no
# service-log fingerprint, so the evidence lives in CommittedSends.EmailBody. It reuses
# verify-lib.sh ONLY for the version-pinned deploy boundary and the PASS/FAIL/WAIT decision.
#
# PRECONDITION: a deploy restarts the service, which sends exactly one startup (diagnostic)
# report -- the path the fix lives on. A clean result is only meaningful once at least one
# diagnostic has shipped post-deploy; otherwise -> WAIT (the new strip was never exercised).
#
# Usage:  WX-193-verify.sh [--since 'YYYY-MM-DD HH:MM:SS'] [--log PATH] [--deploy-log PATH] [-h]
# Shell:  bash (WSL). DB access is sqlcmd via powershell.exe against .\SQLEXPRESS /
#         WeatherData (override with WX193_DB_SERVER / WX193_DB). The PC runs in UTC.
#         Shared scaffold in verify-lib.sh.

# No -e by design: optional grep/parse stages may exit non-zero on an odd row and are handled
# inline; -e would abort the whole sweep on one.
set -uo pipefail

SELF="${BASH_SOURCE[0]}"
TICKET='WX-193'                                     # self-identification + header
VERSION='1.32.1'                                    # the release VERSION that shipped the fix -- the deploy pin
COMPONENTS=('WxReportSvc')                           # the service WX-193 ships in
TITLE='diagnostic "What'"'"'s changed" band suppression'
MIN_WINDOW_MINUTES=1                                # kept at 1 only to satisfy verify-lib's >0 validation; the actual window gate is overridden to ZERO below (min_window_secs=0) -- this is an event-based verify with NO wait time, gated solely on the exercised-diagnostic precondition
source "$(cd "$(dirname "$SELF")" && pwd)/verify-lib.sh"

# DB coordinates (overridable for a test instance).
DB_SERVER="${WX193_DB_SERVER:-.\\SQLEXPRESS}"
DB_NAME="${WX193_DB:-WeatherData}"

# Run a query; rows '|'-separated, headerless, CR-stripped. The query carries no embedded
# double quotes (only single-quoted SQL literals), so it nests inside -Q "...".
sqlq() {  # SQL -> rows on stdout
  powershell.exe -NoProfile -Command \
    "sqlcmd -S $DB_SERVER -d $DB_NAME -E -C -h -1 -W -s '|' -Q \"$1\"" 2>/dev/null | tr -d '\r'
}
# Dump one report's EmailBody, unbounded width.
sqlbody() {  # Id -> EmailBody on stdout
  powershell.exe -NoProfile -Command \
    "sqlcmd -S $DB_SERVER -d $DB_NAME -E -C -y 0 -Q \"SET NOCOUNT ON; SELECT EmailBody FROM CommittedSends WHERE Id=$1\"" 2>/dev/null | tr -d '\r'
}

vl_parse_args "$@"
# vl_resolve_boundary also requires a readable service log (a shared-lib precondition) even
# though this DB-only check never parses it; the deployed box always has wxreport-svc.log.
vl_resolve_boundary     # sets SINCE / COMMIT / BOUNDARY_SRC / DEPLOY_INFO (WAIT-exits if VERSION not deployed)

# ---- diagnostic sends since the deploy boundary ---------------------------------
# Diagnostic sends only (IsDiagnostic=1), actually delivered. A deploy emits one per
# startup-recipient locality.
CANDIDATES="$(sqlq "SET NOCOUNT ON; SELECT cs.Id, CONVERT(varchar(19), cs.CreatedAtUtc, 120) FROM CommittedSends cs WHERE cs.IsDiagnostic = 1 AND cs.SentAtUtc IS NOT NULL AND cs.CreatedAtUtc >= '$SINCE' ORDER BY cs.Id")"

since_epoch=$(date -u -d "$SINCE UTC" +%s)
diagnostics=0     # diagnostic sends inspected (the exercised precondition)
banded=0          # diagnostic sends whose body carries the "What's changed:" band
regression=0      # banded diagnostics with NO severe indicator -- the unambiguous failure
review=0          # banded diagnostics WITH severe -- manual-justify per WX-193.md (non-gating)
LAST_TS="$SINCE"; last_epoch=$since_epoch
VIOLATIONS=''; REVIEW=''

while IFS='|' read -r id created; do
  id="$(printf '%s' "${id:-}" | tr -d '[:space:]')"
  case "$id" in ''|*[!0-9]*) continue;; esac          # skip blank / header noise rows
  created="$(printf '%s' "$created" | sed -e 's/^ *//' -e 's/ *$//')"

  c_epoch=$(date -u -d "$created UTC" +%s 2>/dev/null || echo "")
  [ -n "$c_epoch" ] || continue
  [ "$c_epoch" -gt "$last_epoch" ] && { last_epoch=$c_epoch; LAST_TS="$created"; }
  diagnostics=$(( diagnostics + 1 ))

  body="$(sqlbody "$id")"
  # The band heading is "What's changed:" (English). A Spanish-only diagnostic localizes it;
  # paul_en is English, the deploy-verification recipient -- broaden this grep if a
  # Spanish-only startup recipient must be covered.
  case "$body" in
    *"What's changed:"*) banded=$(( banded + 1 ));;
    *) continue;;                                      # no band -> correctly suppressed, nothing to check
  esac

  # A banded diagnostic is justified ONLY by a near-term severe onset. A severe report's body
  # always carries a "Severe" lead; its absence makes the band unambiguously wrong.
  case "$body" in
    *Severe*|*severe*)
      review=$(( review + 1 ))
      REVIEW="${REVIEW}   send $id ($created UTC): band present WITH severe -- justify near-term onset per WX-193.md"$'\n'
      ;;
    *)
      regression=$(( regression + 1 ))
      VIOLATIONS="${VIOLATIONS}   send $id ($created UTC): \"What's changed\" band on a diagnostic with NO severe -- the WX-189 regression"$'\n'
      ;;
  esac
done <<< "$CANDIDATES"

# Window bookkeeping for vl_header / vl_verdict (this script doesn't use the log window).
# This is an EVENT-based verify, not an accrual-based one: the whole test is a single startup
# diagnostic that exists the instant the deploy comes up, so there is NO reason to wait. The
# window gate is therefore set to ZERO (min_window_secs=0) and the verdict rests solely on the
# exercised-diagnostic precondition. `elapsed` is still computed (anchored on wall-clock NOW, not
# the lone diagnostic's timestamp) purely so the header reports a sensible "time since deploy".
now_epoch=$(date -u +%s)
if [ "$now_epoch" -gt "$last_epoch" ]; then last_epoch=$now_epoch; LAST_TS="$(date -u -d "@$now_epoch" '+%Y-%m-%d %H:%M:%S')"; fi
elapsed=$(( last_epoch - since_epoch )); [ "$elapsed" -gt 0 ] || elapsed=1
hh=$(( elapsed / 3600 )); mm=$(( (elapsed % 3600) / 60 ))
min_window_secs=0                                  # no wait time by design (see above) -- the lib's >0 floor is bypassed here

vl_header
echo
echo " WX-193 FINGERPRINT  (diagnostic sends persisted since the deploy boundary)"
echo "   diagnostic sends inspected            : $diagnostics   (PRECONDITION + EXERCISED -- a deploy emits one)"
echo "   diagnostics carrying a band           : $banded   (post-fix: only a near-term severe onset should)"
echo "   band on a NON-severe diagnostic       : $regression   (expect 0 -- the failure signature)"
[ "$review" -gt 0 ] && echo "   band WITH severe (manual-justify)     : $review   (review per WX-193.md -- not gated)"
if [ "$regression" -gt 0 ]; then
  echo
  echo " FAILURE-SIGNATURE LINES"
  printf '%s' "$VIOLATIONS"
fi
if [ "$review" -gt 0 ]; then
  echo
  echo " MANUAL-JUSTIFY (band present with severe -- confirm a Day 1-3 onset, not a far-day/pre-existing band)"
  printf '%s' "$REVIEW"
fi
echo

# Verdict. A band on a non-severe diagnostic is the unambiguous failure signature and FAILs
# at any horizon -- vl_verdict tests the regression count before the exercised gate, so a real
# violation is never masked. `diagnostics` is the exercised count: a clean PASS requires at
# least one diagnostic send post-deploy (the path the strip lives on actually ran). A window
# with no diagnostic yet -> WAIT, not a false PASS.
vl_verdict "$regression" "$diagnostics" \
  "a diagnostic report still rendered a \"What's changed\" band with no severe present -- the WX-189 regression; check SuppressesScheduledChangeBand accepts ReportKind.Diagnostic and that SendStartupReportAsync applies WithoutChangeBand before rendering." \
  "no diagnostic carried a non-severe band; the WX-189 regression is gone (any banded-with-severe rows above still need the WX-193.md near-term-onset justification)." \
  "$diagnostics" "a diagnostic (startup) send in the window"
