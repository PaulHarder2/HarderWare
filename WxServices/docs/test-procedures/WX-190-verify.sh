#!/usr/bin/env bash
# WX-190-verify.sh - confirm WX-190 (the forecast grid's Conditions cell now tiles each day
# into 24-hour clock bands with a clock legend, replacing the floating "Overnight" daypart) is
# live, by inspecting the report bodies persisted in the database since the deploy.
#
# THE CHANGE: StructuredReportRenderer.AppendExtendedForecast now renders, directly beneath the
# forecast grid, a one-line 24-hour-clock legend ("Times use a 24-hour clock: 00 = midnight, 12
# = noon, 24 = midnight." / the ES mirror), and ConditionsCellHtml tiles the day into XX-YY
# clock bands ("00-06", "12-24", a uniform day as "00-24") instead of episode lines labeled
# "Overnight"/"Morning"/... The legend is emitted on EVERY grid-bearing report, so it is the
# UNIQUE fingerprint that the new renderer is live.
#
# THE INVARIANT (post-fix): every grid-bearing report rendered since the deploy carries the
# legend. The UNAMBIGUOUS failure signature is a grid-bearing report (its body has the "Forecast
# for "/"Pronostico para " heading) whose body LACKS the legend -- that means the old binary is
# still running, or AppendExtendedForecast's legend regressed. That FAILs at any horizon.
#
# THE GATE (Paul, WX-190 design pass): this is a deterministic render change -- there is nothing
# to "settle" over wall-clock time, so there is NO time window. Instead PASS is gated on a
# minimum number of report-issuing CYCLES since the deploy (default 3), so the new renderer is
# confirmed across more than one real forecast -- exercising band merges, severe bands, and
# whole-day "00-24" collapses that the synthetic unit-test fixtures cannot. A cycle = one
# reconciled ForecastSnapshot that issued >=1 grid-bearing report; the deploy's startup
# DIAGNOSTIC report contributes its own snapshot, so it counts as one cycle.
#
# Like WX-188/WX-193, this is a DB-reading verify: the fix changes rendered OUTPUT, which leaves
# no service-log fingerprint, so the evidence lives in CommittedSends.EmailBody (persisted
# pre-meteogram-insertion, so the rendered grid + legend HTML is present). It reuses verify-lib.sh
# only for the version-pinned deploy boundary and the PASS/FAIL/WAIT decision.
#
# A capitalized "Overnight" daypart label is never emitted by the new deterministic surfaces;
# any post-deploy body still containing one is REPORTED (non-gating) for the manual review step
# in WX-190.md -- a raw grep can't tell a grid label from a word inside Claude's closing prose
# ("overnight lows"), so it informs rather than fails.
#
# Usage:  WX-190-verify.sh [--since 'YYYY-MM-DD HH:MM:SS'] [--log PATH] [--deploy-log PATH] [-h]
# Shell:  bash (WSL). DB access is sqlcmd via powershell.exe against .\SQLEXPRESS /
#         WeatherData (override with WX190_DB_SERVER / WX190_DB). The PC runs in UTC.
#         Shared scaffold in verify-lib.sh.

# No -e by design: optional grep/parse stages may exit non-zero on an odd row and are handled
# inline; -e would abort the whole sweep on one.
set -uo pipefail

SELF="${BASH_SOURCE[0]}"
TICKET='WX-190'                                     # self-identification + header
VERSION='1.33.0'                                    # the release VERSION that shipped the change -- the deploy pin
COMPONENTS=('WxReportSvc')                           # the service WX-190 ships in
TITLE='daypart clock-band grid + 24-hour-clock legend'
MIN_CYCLES=3                                        # PASS gate: minimum report-issuing cycles since deploy (the startup diagnostic counts as one)
MIN_WINDOW_MINUTES=1                                # kept at 1 only to satisfy verify-lib's >0 validation; the actual gate is the cycle count, NOT a wait -- min_window_secs is overridden to ZERO below
source "$(cd "$(dirname "$SELF")" && pwd)/verify-lib.sh"

# DB coordinates (overridable for a test instance).
DB_SERVER="${WX190_DB_SERVER:-.\\SQLEXPRESS}"
DB_NAME="${WX190_DB:-WeatherData}"

# The legend fingerprints (EN + ES) -- distinctive substrings only the new renderer emits. The
# legend text has no HTML-special characters, so it survives HtmlText escaping verbatim.
LEGEND_EN='24-hour clock: 00 = midnight'
LEGEND_ES='reloj de 24 horas: 00 = medianoche'
# Grid-bearing markers (the forecast-section heading, EN + ES) -- present iff the body has a
# grid, and emitted by BOTH the old and new renderers, so "grid present + legend absent" still
# catches an old binary. GRID_ES is the ASCII tail of "Pronostico para" (avoids the accented o,
# so no multibyte glob matching is needed).
GRID_EN='Forecast for '
GRID_ES='stico para '

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

# ---- delivered report sends since the deploy boundary ---------------------------
# All report kinds (scheduled / unscheduled / diagnostic), actually delivered (SentAtUtc set).
CANDIDATES="$(sqlq "SET NOCOUNT ON; SELECT cs.Id, cs.ForecastSnapshotId, CONVERT(varchar(19), cs.CreatedAtUtc, 120) FROM CommittedSends cs WHERE cs.SentAtUtc IS NOT NULL AND cs.CreatedAtUtc >= '$SINCE' ORDER BY cs.Id")"

since_epoch=$(date -u -d "$SINCE UTC" +%s)
reports=0         # grid-bearing report sends inspected
missing=0         # grid-bearing sends LACKING the legend -- a failure signature
gaps=0            # grid day-rows whose clock bands DON'T tile contiguously (an interior daypart gap) -- a failure signature
overnight=0       # post-deploy sends still containing a capitalized "Overnight" -- manual review (non-gating)
CYCLE_IDS=''      # ForecastSnapshotIds of grid-bearing reports (deduped at end -> cycle count)
LAST_TS="$SINCE"; last_epoch=$since_epoch
VIOLATIONS=''; GAP_VIOLATIONS=''; REVIEW=''

while IFS='|' read -r id snapid created; do
  id="$(printf '%s' "${id:-}" | tr -d '[:space:]')"
  case "$id" in ''|*[!0-9]*) continue;; esac          # skip blank / header noise rows
  snapid="$(printf '%s' "${snapid:-}" | tr -d '[:space:]')"
  created="$(printf '%s' "$created" | sed -e 's/^ *//' -e 's/ *$//')"

  c_epoch=$(date -u -d "$created UTC" +%s 2>/dev/null || echo "")
  [ -n "$c_epoch" ] || continue
  [ "$c_epoch" -gt "$last_epoch" ] && { last_epoch=$c_epoch; LAST_TS="$created"; }

  body="$(sqlbody "$id")"

  # Only grid-bearing reports are in scope: a welcome-only / no-forecast send has no grid, hence
  # legitimately no legend, and must not count as a cycle or a regression. Use the same GRID_EN /
  # GRID_ES fingerprints the grid-isolation awk uses (one definition, no second accent-aware glob).
  case "$body" in
    *"$GRID_EN"*|*"$GRID_ES"*) ;;
    *) continue;;
  esac

  reports=$(( reports + 1 ))
  case "$snapid" in ''|*[!0-9]*) ;; *) CYCLE_IDS="${CYCLE_IDS} ${snapid}";; esac

  case "$body" in
    *"$LEGEND_EN"*|*"$LEGEND_ES"*) ;;                  # legend present -> new renderer confirmed for this send
    *)
      missing=$(( missing + 1 ))
      VIOLATIONS="${VIOLATIONS}   send $id ($created UTC): grid-bearing report with NO 24-hour-clock legend -- old/broken renderer"$'\n'
      ;;
  esac

  # Per-day coverage (acceptance criterion #1): within the forecast grid, each day-row's clock
  # bands must cover the FULL day 00-24 -- start at "00", end at "24", and tile contiguously in
  # between (each band's end hour == the next band's start hour). A day that stops short ("00-06"
  # then "06-18", no "18-24") or has an interior gap ("00-06" then "12-18") is NOT fully covered.
  # THE ONE EXCEPTION (Paul, WX-190): the FINAL day-row may legitimately stop short of 24 -- the
  # forecast horizon can truncate the last day -- so it is exempt from the "must reach 24" rule
  # (it must still start at 00 and be contiguous).
  #
  # First isolate the grid TABLE (from the forecast heading to the legend); otherwise the hazard
  # banner's "{weekday} 00-06" prose and the closing paragraph would be mistaken for grid rows.
  # The body is one HTML line (StringBuilder, no newlines); RS=\1 (a byte it can't contain) slurps
  # the whole body as one awk record so index()/substr() span it. Within the grid, a row's only
  # XX-YY tokens are its Conditions cell's band labels (dates/temps/wind never match that shape).
  grid="$(printf '%s' "$body" | awk -v ge="$GRID_EN" -v gs="$GRID_ES" -v le="$LEGEND_EN" -v ls="$LEGEND_ES" \
    'BEGIN{RS="\1"} { p=index($0,ge); if(p==0)p=index($0,gs); q=index($0,le); if(q==0)q=index($0,ls); if(p>0 && q>p) printf "%s", substr($0,p,q-p) }')"
  bad_rows="$(printf '%s' "$grid" | sed 's:</tr>:</tr>\n:g' | awk '
    /[0-2][0-9]-[0-2][0-9]/ {
      n=0; s=$0; lbls=""; delete st; delete en
      while (match(s, /[0-2][0-9]-[0-2][0-9]/)) {
        tok=substr(s, RSTART, RLENGTH); n++; st[n]=substr(tok,1,2); en[n]=substr(tok,4,2)
        lbls=(lbls=="")?tok:(lbls" -> "tok); s=substr(s, RSTART+RLENGTH)
      }
      rows++; seq[rows]=lbls
      startbad[rows]=(st[1]!="00"); endbad[rows]=(en[n]!="24")
      cb=0; for (i=1;i<n;i++) if (en[i]!=st[i+1]) cb=1; contigbad[rows]=cb
    }
    END {
      for (r=1;r<=rows;r++) {
        bad = (startbad[r] || contigbad[r])
        if (r < rows && endbad[r]) bad=1      # the FINAL day-row is exempt from "must reach 24"
        if (bad) print seq[r]
      }
    }')"
  if [ -n "$bad_rows" ]; then
    while IFS= read -r row; do
      [ -n "$row" ] || continue
      gaps=$(( gaps + 1 ))
      GAP_VIOLATIONS="${GAP_VIOLATIONS}   send $id ($created UTC): bands [$row] do NOT cover the full day 00-24 (final-day rows are exempt from ending at 24)"$'\n'
    done <<< "$bad_rows"
  fi

  # Non-gating: a capitalized "Overnight" should never come from the new deterministic surfaces.
  case "$body" in
    *Overnight*)
      overnight=$(( overnight + 1 ))
      REVIEW="${REVIEW}   send $id ($created UTC): body contains \"Overnight\" -- confirm it is prose, not a grid/banner label (WX-190.md)"$'\n'
      ;;
  esac
done <<< "$CANDIDATES"

# Distinct report-issuing cycles = distinct reconciled snapshots among the grid-bearing reports.
cycles=$(printf '%s\n' $CYCLE_IDS | sed '/^$/d' | sort -u | grep -c . || true)

# Window bookkeeping for vl_header. EVENT/COUNT-based verify, not accrual-based: there is no wait
# time, so the window gate is ZERO (min_window_secs=0) and the verdict rests on the cycle count.
# `elapsed` is computed (anchored on wall-clock NOW) only so the header shows a sensible "time
# since deploy".
now_epoch=$(date -u +%s)
if [ "$now_epoch" -gt "$last_epoch" ]; then last_epoch=$now_epoch; LAST_TS="$(date -u -d "@$now_epoch" '+%Y-%m-%d %H:%M:%S')"; fi
elapsed=$(( last_epoch - since_epoch )); [ "$elapsed" -gt 0 ] || elapsed=1
hh=$(( elapsed / 3600 )); mm=$(( (elapsed % 3600) / 60 ))
min_window_secs=0                                  # no wait time by design -- the lib's >0 floor is bypassed here

vl_header
echo
echo " WX-190 FINGERPRINT  (delivered report sends persisted since the deploy boundary)"
echo "   grid-bearing reports inspected        : $reports"
echo "   report-issuing cycles (distinct snaps): $cycles   (PASS needs >= $MIN_CYCLES; deploy diagnostic counts as one)"
echo "   grid-bearing reports MISSING legend   : $missing   (expect 0 -- the failure signature)"
echo "   day-rows not covering full 00-24      : $gaps   (expect 0 -- non-final days must span 00->24; final day may stop short)"
[ "$overnight" -gt 0 ] && echo "   bodies containing \"Overnight\"         : $overnight   (manual review per WX-190.md -- not gated)"
if [ "$missing" -gt 0 ] || [ "$gaps" -gt 0 ]; then
  echo
  echo " FAILURE-SIGNATURE LINES"
  [ "$missing" -gt 0 ] && printf '%s' "$VIOLATIONS"
  [ "$gaps" -gt 0 ] && printf '%s' "$GAP_VIOLATIONS"
fi
if [ "$overnight" -gt 0 ]; then
  echo
  echo " MANUAL-REVIEW (\"Overnight\" present -- confirm prose, not a grid/banner daypart label)"
  printf '%s' "$REVIEW"
fi
echo

# Verdict. A grid-bearing report missing the legend is the unambiguous failure signature and FAILs
# at any horizon (vl_verdict tests the regression count before the exercised gate, so a real
# violation is never masked). The exercised arg encodes the cycle gate: it is the real cycle count
# only once >= MIN_CYCLES, else 0 -> WAIT until enough cycles have issued. So: regression -> FAIL
# immediately (even on cycle 1); else < MIN_CYCLES cycles -> WAIT; else PASS.
# Either failure signature fails: a grid-bearing report missing the legend (old/broken renderer)
# OR a day-row whose bands don't tile contiguously (a daypart-coverage gap).
regression=$(( missing + gaps ))
exercised_arg=$(( cycles >= MIN_CYCLES ? cycles : 0 ))
vl_verdict "$regression" "$exercised_arg" \
  "a grid-bearing report either rendered without the 24-hour-clock legend (old binary still running, or the legend regressed) or showed a NON-final day whose clock bands do not cover the full 00-24 (a daypart-coverage gap; if it traces to a missing snapshot block, open a linked Bug per fix-forward)." \
  "the clock-band grid + legend are present and every day tiles contiguously across $cycles cycle(s); any \"Overnight\" rows above still need the WX-190.md prose-vs-label confirmation." \
  "$exercised_arg" "at least $MIN_CYCLES report-issuing cycles since the deploy (the startup diagnostic counts as one; $cycles so far)"
