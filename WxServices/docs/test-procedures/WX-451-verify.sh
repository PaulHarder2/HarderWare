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
marked=$(printf '%s\n' "$POST" | cnt 'marked complete')

# REGRESSION (log side): the negative-bound guard should NEVER fire in normal operation.
# It is logged once per process, so 1 is already a standing misconfiguration, not a blip.
badconfig=$(printf '%s\n' "$POST" | cnt 'MaxForecastHours is')

# REGRESSION (DB side): fully-stored but unmarked -- the silent-freeze symptom.
stuck='?'
# TWO path forms, deliberately, and they are NOT interchangeable: bash tests existence
# through the WSL mount, PowerShell resolves only the Windows form. Passing the /mnt/c
# path to PowerShell fails with "not recognized as the name of a cmdlet" -- which this
# block would then swallow into a graceful '?' that reads as "no sqlcmd here" rather
# than "the call was malformed". Caught by the WORKFLOW §7a smoke run; `bash -n` and a
# clean exit both passed while the check was silently doing nothing.
SQLCMD_WSL='/mnt/c/Program Files/Microsoft SQL Server/Client SDK/ODBC/170/Tools/Binn/SQLCMD.EXE'
SQLCMD_WIN='C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\170\Tools\Binn\SQLCMD.EXE'
if command -v powershell.exe >/dev/null 2>&1 && [ -f "$SQLCMD_WSL" ]; then
    q="SET NOCOUNT ON; SELECT COUNT(*) FROM GfsModelRuns r WHERE r.IsComplete = 0 AND ("
    q="$q SELECT COUNT(DISTINCT g.ForecastHour) FROM GfsGrid g WHERE g.ModelRunUtc = r.ModelRunUtc"
    q="$q AND g.ForecastHour BETWEEN 0 AND 120 ) = 121;"
    stuck=$(powershell.exe -NoProfile -Command \
        "& '$SQLCMD_WIN' -S '.\\SQLEXPRESS' -d WeatherData -E -C -h -1 -W -t 60 -Q \"$q\"" 2>/dev/null \
        | tr -d '\r' | grep -E '^[0-9]+$' | head -1)
    [ -n "$stuck" ] || stuck='?'
fi

regressions=$badconfig
[ "$stuck" != '?' ] && regressions=$(( regressions + stuck ))

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
printf  '   %-54s %s\n' 'runs fully stored but NOT marked complete (want 0):' "$stuck   $([ "$stuck" = '0' ] && echo '[ok]' || { [ "$stuck" = '?' ] && echo '[NOT MEASURED - no sqlcmd/powershell from here]' || echo '[FROZEN? re-run to rule out a same-cycle transient]'; })"
echo
echo    " Background health (new ERRORs the deploy introduced, vs the equal pre-window)"
printf  '   %-54s %s\n' 'ERROR lines  (before / after / new):' "$err_before / $err_after / $err_new"
echo
[ "$stuck" = '?' ] && echo " ⚠️  The DB-side regression check did not run, so a PASS below attests the log"
[ "$stuck" = '?' ] && echo "    signal ONLY. Re-run where sqlcmd is reachable before treating it as complete."
vl_verdict "$regressions" "$exercised" \
  "" \
  "set-membership completeness is live; the missing-hours clause proves 1.61.3+ evaluated a run against the real database. If 'fully stored but not marked' is non-zero on two consecutive runs, that is the silent-freeze symptom -- treat as FAIL and read WX-451.md section 3."
