#!/usr/bin/env bash
# WX-188-verify.sh - confirm WX-188 (the Extended Forecast grid drops calendar days that
# are wholly past at send time) is live, by inspecting the rendered reports persisted in
# the database since the deploy.
#
# THE BUG: StructuredReportRenderer.AggregateDays bucketed every snapshot block by local
# calendar day with NO trim relative to send time. A report built after local midnight on
# a GFS 00Z run (whose snapshot begins the prior local evening) therefore led its Extended
# Forecast grid with YESTERDAY -- send 2270, built 01:01 CDT, opened with "Sun Jun 14"
# while its header and prose correctly anchored on Jun 15.
#
# THE INVARIANT (post-fix): the grid's first day row is the local day containing the SEND
# instant, never an earlier day. So for every report delivered since the deploy:
#       first_grid_local_date  >=  build_local_date
# where build_local_date is CommittedSends.CreatedAtUtc converted to the recipient's
# timezone, and first_grid_local_date is parsed from the rendered EmailBody's forecast
# table. A report that violates this is the failure signature.
#
# This is the first DB-READING verify script: the fix changes rendered OUTPUT, which
# leaves no service-log fingerprint, so the evidence lives in CommittedSends.EmailBody. It
# reuses verify-lib.sh ONLY for the version-pinned deploy boundary and the PASS/FAIL/WAIT
# decision; the metric is a SQL query (sqlcmd via powershell.exe), not a log grep.
#
# PRECONDITION: the bug only surfaces on a report built in the early local-morning hours
# (after local midnight, on the 00Z-run snapshot, before the day's later runs shift the
# snapshot start forward). A clean result is only meaningful once at least one such report
# has shipped post-deploy; otherwise -> WAIT (the invariant was never actually stressed).
#
# Usage:  WX-188-verify.sh [--since 'YYYY-MM-DD HH:MM:SS'] [--log PATH] [--deploy-log PATH] [-h]
# Shell:  bash (WSL). DB access is sqlcmd via powershell.exe against .\SQLEXPRESS /
#         WeatherData (override with WX188_DB_SERVER / WX188_DB). The PC runs in UTC.
#         Shared scaffold in verify-lib.sh.

# No -e by design: optional parses (date/grep on a malformed row) may exit non-zero and are
# handled inline; -e would abort the whole sweep on one odd row.
set -uo pipefail

SELF="${BASH_SOURCE[0]}"
TICKET='WX-188'                                    # self-identification + header
VERSION='1.30.1'                                   # the release VERSION that shipped the fix -- the deploy pin
COMPONENTS=('WxReportSvc')                          # the service WX-188 ships in
TITLE='forecast grid drops wholly-past days'       # header description
MIN_WINDOW_HOURS=24                                # a full day, so an early-morning (00Z-run) cycle accrues before PASS
source "$(cd "$(dirname "$SELF")" && pwd)/verify-lib.sh"

# DB coordinates (overridable for a test instance).
DB_SERVER="${WX188_DB_SERVER:-.\\SQLEXPRESS}"
DB_NAME="${WX188_DB:-WeatherData}"

# Run a query; rows '|'-separated, headerless, CR-stripped. The query carries no embedded
# double quotes (only single-quoted SQL literals), so it nests inside -Q "...".
sqlq() {  # SQL -> rows on stdout
  powershell.exe -NoProfile -Command \
    "sqlcmd -S $DB_SERVER -d $DB_NAME -E -C -h -1 -W -s '|' -Q \"$1\"" 2>/dev/null | tr -d '\r'
}
# Dump one report's EmailBody, unbounded width. -y 0 is mutually exclusive with -h, so any
# stray header/separator text is simply ignored by the date-cell grep downstream.
sqlbody() {  # Id -> EmailBody on stdout
  powershell.exe -NoProfile -Command \
    "sqlcmd -S $DB_SERVER -d $DB_NAME -E -C -y 0 -Q \"SET NOCOUNT ON; SELECT EmailBody FROM CommittedSends WHERE Id=$1\"" 2>/dev/null | tr -d '\r'
}

# Month abbreviation -> number, for parsing the grid's localized "ddd MMM d" date cell.
# The renderer localizes the date, so accept English AND Spanish abbreviations, case- and
# trailing-period-insensitive. Unknown -> 00 (the caller treats that as unparseable).
mon_num() {
  case "$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]' | tr -d '.')" in
    jan|ene) echo 01;; feb) echo 02;; mar) echo 03;; apr|abr) echo 04;;
    may) echo 05;; jun) echo 06;; jul) echo 07;; aug|ago) echo 08;;
    sep|set) echo 09;; oct) echo 10;; nov) echo 11;; dec|dic) echo 12;;
    *) echo 00;; esac
}

vl_parse_args "$@"
# vl_resolve_boundary also requires a readable service log (a shared-lib precondition) even
# though this DB-only check never parses it; the deployed box always has wxreport-svc.log.
vl_resolve_boundary     # sets SINCE / COMMIT / BOUNDARY_SRC / DEPLOY_INFO (WAIT-exits if VERSION not deployed)

# ---- candidate reports since the deploy boundary --------------------------------
# Real recipient sends only (non-diagnostic, actually delivered), joined to the recipient
# timezone for the local-day conversion.
CANDIDATES="$(sqlq "SET NOCOUNT ON; SELECT cs.Id, CONVERT(varchar(19), cs.CreatedAtUtc, 120), r.Timezone FROM CommittedSends cs JOIN Recipients r ON cs.RecipientId = r.RecipientId WHERE cs.IsDiagnostic = 0 AND cs.SentAtUtc IS NOT NULL AND cs.CreatedAtUtc >= '$SINCE' ORDER BY cs.Id")"

since_epoch=$(date -u -d "$SINCE UTC" +%s)
inspected=0       # evaluable rows (resolvable tz + parseable timestamps)
checked=0         # reports whose grid date was actually parsed + compared (the real coverage)
early_checked=0   # checked reports built 00:00-05:59 local -- the window the bug surfaces in
regression=0      # checked reports whose grid leads with a day before the local build date
unparsed=0        # reports with no parseable grid (degraded/obs-less, or unrecognized locale) -- visible
skipped=0         # reports skipped for an unresolvable/empty recipient timezone
LAST_TS="$SINCE"; last_epoch=$since_epoch
VIOLATIONS=''; SKIPPED=''; UNPARSED=''

while IFS='|' read -r id created tz; do
  id="$(printf '%s' "${id:-}" | tr -d '[:space:]')"
  case "$id" in ''|*[!0-9]*) continue;; esac          # skip blank / header noise rows
  created="$(printf '%s' "$created" | sed -e 's/^ *//' -e 's/ *$//')"
  tz="$(printf '%s' "$tz" | sed -e 's/^ *//' -e 's/ *$//')"

  # Validate the recipient timezone against the IANA zoneinfo DB BEFORE using it. `TZ=...`
  # silently falls back to UTC for an empty or Windows-style id (e.g. "Central Standard
  # Time", which the .NET app accepts but glibc does not), which would compute the wrong
  # local day and could invert the verdict. Skip + count such rows rather than miscompute.
  if [ -z "$tz" ] || [ ! -e "/usr/share/zoneinfo/$tz" ]; then
    skipped=$(( skipped + 1 ))
    SKIPPED="${SKIPPED}   send $id: unresolvable timezone '${tz:-<empty>}'"$'\n'
    continue
  fi

  # Window end (for the elapsed gate / header). A malformed CreatedAtUtc -> skip the row
  # entirely (don't count it as inspected, don't anchor the window on it).
  c_epoch=$(date -u -d "$created UTC" +%s 2>/dev/null || echo "")
  [ -n "$c_epoch" ] || continue
  [ "$c_epoch" -gt "$last_epoch" ] && { last_epoch=$c_epoch; LAST_TS="$created"; }

  # Build instant in the recipient's local timezone (TZ-aware date honors zoneinfo/DST).
  build_day=$(TZ="$tz" date -d "$created UTC" +%Y-%m-%d 2>/dev/null || echo "")
  build_hour=$(TZ="$tz" date -d "$created UTC" +%H 2>/dev/null || echo "")
  [ -n "$build_day" ] && [ -n "$build_hour" ] || continue
  inspected=$(( inspected + 1 ))                      # an evaluable row (resolvable tz + timestamps)
  bh=$((10#$build_hour))                              # local build hour; 10# strips the leading zero

  # First grid date cell from the rendered body. Match EITHER language's forecast heading
  # ("Forecast for" / "Pronostico para"); a degraded / obs-less report omits the grid, and
  # ${body#...} is a no-op on no match, so guard explicitly and count a miss (never silent).
  # The Date cell is the first data-row <td> after the heading (the column headers use <th>,
  # not <td>); its text is localized ("Mon Jun 15" / "lun jun 15"), so take month + day and
  # ignore the weekday word -- mon_num accepts both languages.
  body="$(sqlbody "$id")"
  case "$body" in
    *"Forecast for"*)    after="${body#*Forecast for}";;
    *"Pron"*"stico para"*) after="${body#*stico para}";;   # es "Pronostico para" (accented o, match around it)
    *) unparsed=$(( unparsed + 1 )); UNPARSED="${UNPARSED}   send $id ($tz): no forecast grid in body"$'\n'; continue;;
  esac
  celltext="$(printf '%s' "$after" \
    | grep -oE '<td style="padding:6px 10px;">[^<]+</td>' | head -1 \
    | sed -E 's/<[^>]+>//g')"
  set -f; set -- $celltext; set +f                    # word-split (globbing off): $1 weekday, $2 month, $3 day
  g_mon="$(mon_num "${2:-}")"
  g_day="$(printf '%s' "${3:-}" | tr -cd '0-9')"
  if [ "$g_mon" = "00" ] || [ -z "$g_day" ]; then
    unparsed=$(( unparsed + 1 ))
    UNPARSED="${UNPARSED}   send $id ($tz): unrecognized grid date cell '${celltext:-<empty>}'"$'\n'
    continue
  fi

  # The grid was actually parsed -> this report counts toward coverage. A clean PASS requires
  # an early-morning report we genuinely parsed (early_checked), so an unparseable early
  # report can't satisfy the gate.
  checked=$(( checked + 1 ))
  [ "$bh" -lt 6 ] && early_checked=$(( early_checked + 1 ))

  grid_iso="$(printf '%s-%s-%02d' "${build_day%%-*}" "$g_mon" "$g_day")"
  g_epoch=$(date -u -d "$grid_iso" +%s 2>/dev/null || echo "")
  b_epoch=$(date -u -d "$build_day" +%s 2>/dev/null || echo "")
  [ -n "$g_epoch" ] && [ -n "$b_epoch" ] || continue
  # Year-boundary correction: a forecast grid sits within a few days of the build, so a
  # >180-day gap means the year inferred from the build is off by one (Dec build / Jan grid).
  if   [ $(( g_epoch - b_epoch )) -gt 15552000 ]; then g_epoch=$(date -u -d "$grid_iso -1 year" +%s 2>/dev/null || echo "$g_epoch");
  elif [ $(( b_epoch - g_epoch )) -gt 15552000 ]; then g_epoch=$(date -u -d "$grid_iso +1 year" +%s 2>/dev/null || echo "$g_epoch"); fi

  if [ "$g_epoch" -lt "$b_epoch" ]; then
    regression=$(( regression + 1 ))
    VIOLATIONS="${VIOLATIONS}   send $id ($tz): grid leads with \"$celltext\" but built local $build_day  ($created UTC)"$'\n'
  fi
done <<< "$CANDIDATES"

# Window bookkeeping for vl_header / vl_verdict (this script doesn't use the log window).
elapsed=$(( last_epoch - since_epoch )); [ "$elapsed" -gt 0 ] || elapsed=1
hh=$(( elapsed / 3600 )); mm=$(( (elapsed % 3600) / 60 ))
min_window_secs=$(( MIN_WINDOW_MINUTES * 60 ))

vl_header
echo
echo " WX-188 FINGERPRINT  (reports persisted since the deploy boundary)"
echo "   reports evaluated (resolvable tz + timestamps)  : $inspected"
echo "   reports whose forecast grid was checked         : $checked"
echo "   early-morning checked (00:00-05:59 local)       : $early_checked   (a PASS needs >=1)"
echo "   grid-leads-with-a-past-day violations           : $regression   (expect 0 -- the failure signature)"
[ "$unparsed" -gt 0 ] && echo "   no parseable grid (degraded / unknown locale)   : $unparsed"
[ "$skipped" -gt 0 ]  && echo "   skipped (unresolvable recipient timezone)       : $skipped"
if [ "$regression" -gt 0 ]; then
  echo
  echo " FAILURE-SIGNATURE LINES"
  printf '%s' "$VIOLATIONS"
fi
if [ "$unparsed" -gt 0 ]; then
  echo
  echo " NO PARSEABLE GRID (not checked -- investigate if unexpectedly high)"
  printf '%s' "$UNPARSED"
fi
if [ "$skipped" -gt 0 ]; then
  echo
  echo " SKIPPED (recipient timezone not in the IANA database -- not evaluated)"
  printf '%s' "$SKIPPED"
fi
echo

# Verdict. A grid-leads-with-a-past-day violation is the failure signature and FAILs at any
# horizon -- vl_verdict tests the regression count before the exercised/window gates, so a
# real violation is never masked. `early_checked` is passed as the "exercised" count, so a
# clean PASS requires at least one early-morning report whose grid we ACTUALLY parsed and
# verified -- not merely an early build we counted but couldn't read (a Spanish-only or
# degraded report). A quiet stretch, or one with only unparseable early reports, WAITs
# rather than PASSing on untested evidence.
vl_verdict "$regression" "$early_checked" \
  "a report still rendered its forecast grid leading with a day before its local build date -- check AggregateDays' nowUtc trim and the Render/RenderDegraded call sites." \
  "every checked report leads its grid with the send-instant local day; the 'leads with yesterday' regression is gone."
