#!/usr/bin/env bash
# WX-451-verify.sh - confirm the set-membership completeness check (WX-451) is live in
# production: that the deployed WxParser.Svc decides GFS run completeness by which hours
# are present rather than by how many, and that no run sits fully stored but unmarked.
#
# WX-451 replaced `storedHourCount >= expectedHours` with a set difference over
# 0..MaxForecastHours. The unit tests (19, SQLite-backed) prove the logic; only production
# proves it against SQL Server at scale -- GfsGrid holds ~3.3M rows and the EF translation
# of Where/Select/Distinct/ToListAsync is not the query SQLite ran.
#
# 🔴 THE OBVIOUS SIGNAL DOES NOT DISCRIMINATE, AND THIS IS THE WHOLE REASON THE SCRIPT
#    KEYS ON WHAT IT DOES. The complete-branch line is:
#        NEW:  "marked complete (121/121 hours stored)"
#        OLD:  "marked complete ({storedHourCount}/{expectedHours} hours stored)"
#    For a healthy run the old code printed 121/121 too -- BYTE-IDENTICAL. A verify script
#    keyed on that line would PASS against the unfixed code. It is an unfailable check.
#
# ✅ THE DISCRIMINATING SIGNAL is the "missing" clause in the INCOMPLETE branch:
#        NEW:  "is 41/121 hours complete - missing f041-f120 - will resume next cycle."
#        OLD:  "is 41/121 hours complete - will resume next cycle."
#    The old code had no missing-hours list at all, so this string can ONLY come from
#    1.61.3+. It appears on every cycle while a run is still downloading, which is the
#    normal state for several hours after each 00/06/12/18Z cycle becomes fetchable.
#
# ⚠️ TIMING: a run already marked complete is SKIPPED by FetchAndInsertAsync (it returns
#    early), so the new code does not run for it. The first exercise is the next GFS cycle,
#    fetchable ~3.5h after its nominal time. Expect WAIT for hours after a deploy.
#
# FAIL signature (the silent-freeze symptom this ticket exists to prevent): a run holding
# every expected forecast hour while IsComplete is still 0. That is what a broken
# evaluation looks like from outside -- no error, no exception, model data simply frozen
# at the last good run while consumers follow "newest run where IsComplete" forever.
# Checked against the DB, not the log, because the log cannot show a non-event.
# ⚠️ A sub-second transient is possible: the fetch stores the final hour and marks the run
#    in the same cycle. A count that PERSISTS across two runs of this script is real.
#
# Usage:  ./WX-451-verify.sh [--since 'YYYY-MM-DD HH:MM:SS'] [--log PATH] [--deploy-log PATH]
#         ./WX-451-verify.sh -h

set -uo pipefail                                    # verify-lib.sh:37 states the caller owns this;
                                                    # it relies on it so an unset config var fails
                                                    # loudly rather than silently widening a window.

SELF="${BASH_SOURCE[0]}"
TICKET='WX-451'                                     # self-identification + header
VERSION='1.61.3'                                    # the release VERSION under test -- the pin
COMPONENTS=('WxParserSvc')                          # the changed path executes only here, though
                                                    # all four services rebuild (shared library).
TITLE='GFS run completeness decided by set membership, not by a count'
MIN_WINDOW_MINUTES=30                               # the fingerprint appears during a run's download
                                                    # window; 30m samples background health without
                                                    # demanding a whole GFS cycle.
LOG='/mnt/c/HarderWare/Logs/wxparser-svc.log'
source "$(cd "$(dirname "$SELF")" && pwd)/verify-lib.sh"

vl_parse_args "$@"
vl_resolve_boundary     # sets SINCE/COMMIT/DEPLOY_INFO/BOUNDARY_SRC (WAIT-exits if undeployed)
vl_setup_window         # sets POST/LAST_TS/pre_start/elapsed/hh/mm/min_window_secs

# ---- metrics over the post-deploy window --------------------------------------
# EXERCISED: the missing-hours clause. Only 1.61.3+ can emit it (see header).
missing_lines=$(printf '%s\n' "$POST" | cnt 'hours complete')
exercised=$(    printf '%s\n' "$POST" | cnt 'hours complete - missing ')
# Some terminals/log encodings render the em-dash; accept either form rather than
# keying a PASS on a punctuation byte.
[ "$exercised" -eq 0 ] && exercised=$(printf '%s\n' "$POST" | cnt 'hours complete — missing ')

# CONTEXT: runs actually marked complete since the deploy (not a discriminator - see header).
# ⚠️ THE TRAILING " (" IS LOAD-BEARING. FetchAndInsertAsync's early return logs
#   "is already marked complete - skipping."
# which also contains "marked complete" -- and it is emitted precisely on the cycles where
# EvaluateRunCompletenessAsync NEVER RUNS. Measured on the live log, a bare 'marked complete'
# matched 348 lines of which 294 were the skip line. Counting those would invert the meaning of
# this row: it would rise fastest when the code under test did nothing. The real line reads
#   "marked complete (121/121 hours stored)"
# so anchoring on the opening parenthesis selects it and excludes the skip.
# ⚠️ THE BACKSLASH IS REQUIRED. cnt is `grep -cE "$1" || true` -- EXTENDED regex, where a bare
#   "(" is an unmatched group and grep aborts with "Unmatched ( or \(". The `|| true` then
#   swallows that into an EMPTY count rather than an error, so the row silently prints nothing.
#   Measured on the live log: escaped 54, skip line 295, unescaped a swallowed grep failure.
marked=$(printf '%s\n' "$POST" | cnt 'marked complete \(')

# REGRESSION (log side): the negative-bound guard should NEVER fire in normal operation.
# It is logged once per process, so 1 is already a standing misconfiguration, not a blip.
badconfig=$(printf '%s\n' "$POST" | cnt 'MaxForecastHours is')

# REGRESSION (DB side): fully-stored but unmarked -- the silent-freeze symptom.
# ---- the DB-side regression check --------------------------------------------
# This is the ONLY detector for the silent-freeze FAIL signature the ticket exists to
# prevent, so whether it RAN is a precondition of any verdict -- see vl_verdict below.
stuck='?'          # '?' means NOT MEASURED, never "measured zero"
stuck_why=''       # why it was not measured; distinguishes absent-tooling from a failed call
db_ran=0           # 1 only when a number was actually obtained

# THE BOUND COMES FROM CONFIG, NEVER A LITERAL. Hardcoding 0..120 / =121 would make this
# check UNFAILABLE the moment WX-452 extends the horizon: a fully-stored run would then hold
# more than 121 in-range hours, the subquery could never equal the literal, and `stuck` would
# print [ok] forever while measuring nothing -- in exactly the configuration change the ticket
# names as making WX-451's defect reachable. Read the same single home the fetcher reads.
# ⚠️ WX-313's provider can overlay config from the DB Config table; measured 2026-08-17 that
#   table holds no Gfs keys, so appsettings is authoritative today. If that changes, this must
#   follow -- and when WX-447's DatasetExpectation table lands, the bound moves there.
CONF="$(cd "$(dirname "$SELF")/../.." && pwd)/appsettings.shared.json"
MAX_FH=''
if command -v jq >/dev/null 2>&1 && [ -f "$CONF" ]; then
    MAX_FH=$(jq -r '.Gfs.MaxForecastHours // empty' "$CONF" 2>/dev/null)
fi
case "$MAX_FH" in (''|*[!0-9]*) MAX_FH=''; esac

# TWO path forms, deliberately, and NOT interchangeable: bash tests existence through the WSL
# mount, PowerShell resolves only the Windows form. Passing the /mnt/c path to PowerShell fails
# with "not recognized as the name of a cmdlet". Caught by the WORKFLOW §7a smoke run, where
# `bash -n` passed and the script exited 0 while this check silently did nothing.
SQLCMD_WSL='/mnt/c/Program Files/Microsoft SQL Server/Client SDK/ODBC/170/Tools/Binn/SQLCMD.EXE'
SQLCMD_WIN='C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\170\Tools\Binn\SQLCMD.EXE'

if [ -z "$MAX_FH" ]; then
    stuck_why='could not read Gfs:MaxForecastHours from appsettings.shared.json (jq present?)'
elif ! command -v powershell.exe >/dev/null 2>&1 || [ ! -f "$SQLCMD_WSL" ]; then
    stuck_why='powershell.exe or SQLCMD.EXE not reachable from here'
else
    expected=$(( MAX_FH + 1 ))
    q="SET NOCOUNT ON; SELECT COUNT(*) FROM GfsModelRuns r WHERE r.IsComplete = 0 AND ("
    q="$q SELECT COUNT(DISTINCT g.ForecastHour) FROM GfsGrid g WHERE g.ModelRunUtc = r.ModelRunUtc"
    q="$q AND g.ForecastHour BETWEEN 0 AND $MAX_FH ) = $expected;"
    # stderr is CAPTURED, not discarded: a login failure, a -t timeout or a stopped SQL Server
    # all produce empty stdout, and silently folding those into "no sqlcmd here" would report a
    # wrong diagnosis for a check that DID have its tooling. That swallow was left behind when
    # the path form was fixed; this is the class fix rather than the instance fix.
    db_out=$(powershell.exe -NoProfile -Command \
        "& '$SQLCMD_WIN' -S '.\\SQLEXPRESS' -d WeatherData -E -C -h -1 -W -t 60 -Q \"$q\"" 2>&1 \
        | tr -d '\r')
    stuck=$(printf '%s\n' "$db_out" | grep -E '^[0-9]+$' | head -1)
    if [ -n "$stuck" ]; then
        db_ran=1
    else
        stuck='?'
        stuck_why="query FAILED though both binaries were found: $(printf '%s' "$db_out" | head -1 | cut -c1-90)"
    fi
fi

regressions=$badconfig
[ "$db_ran" -eq 1 ] && regressions=$(( regressions + stuck ))

# Background health: new ERRORs the deploy introduced vs the equal-length pre-window.
read err_before err_after err_new < <(vl_health_delta ' ERROR ')

vl_header
echo
echo    " WX-451 -- the DISCRIMINATING signal (a string only 1.61.3+ can emit)"
printf  '   %-54s %s\n' 'completeness lines since deploy (any form):' "$missing_lines"
printf  '   %-54s %s\n' '...carrying the missing-hours clause:' "$exercised   $([ "$exercised" -gt 0 ] && echo '[set-membership code RAN in production]' || echo '[none yet -- WAIT, see TIMING in header]')"
printf  '   %-54s %s\n' 'runs marked complete (context, NOT a discriminator):' "$marked"
echo
echo    " Regression signatures"
printf  '   %-54s %s\n' 'negative MaxForecastHours ERROR (want 0):' "$badconfig   $([ "$badconfig" -eq 0 ] && echo '[ok]' || echo '[MISCONFIGURED - correct Gfs:MaxForecastHours]')"
printf  '   %-54s %s\n' 'expected-hour bound, read from config (not literal):' "${MAX_FH:-UNRESOLVED}   $([ -n "$MAX_FH" ] && echo "[0..$MAX_FH, so $(( MAX_FH + 1 )) expected]" || echo '[see below]')"
printf  '   %-54s %s\n' 'runs fully stored but NOT marked complete (want 0):' "$stuck   $([ "$db_ran" -eq 1 ] && { [ "$stuck" = '0' ] && echo '[ok]' || echo '[FROZEN? re-run to rule out a same-cycle transient]'; } || echo '[NOT MEASURED]')"
[ "$db_ran" -eq 0 ] && printf '   %-54s %s\n' '  why not measured:' "$stuck_why"
echo
echo    " Background health (new ERRORs the deploy introduced, vs the equal pre-window)"
printf  '   %-54s %s\n' 'ERROR lines  (before / after / new):' "$err_before / $err_after / $err_new"
echo
# THE DB CHECK IS A PRECONDITION, NOT A FOOTNOTE. It is the only detector for the silent-freeze
# signature, so a PASS without it would certify the one thing this ticket exists to catch as
# unexamined -- and vl_verdict would even print a Jira paste string saying so. An advisory
# warning above the verdict does not gate anything; this does (precond <= 0 => WAIT).
vl_verdict "$regressions" "$exercised" \
  "" \
  "set-membership completeness is live; the missing-hours clause proves 1.61.3+ evaluated a run against the real database. If 'fully stored but not marked' is non-zero on two consecutive runs, that is the silent-freeze symptom -- treat as FAIL and read WX-451.md section 3." \
  "$db_ran" \
  "the database regression check to run (it is the only detector for the silent-freeze signature; see 'why not measured' above)"
