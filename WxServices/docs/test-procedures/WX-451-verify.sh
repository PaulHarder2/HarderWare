#!/usr/bin/env bash
# WX-451-verify.sh - confirm the set-membership completeness check (WX-451) is live in
# production: that the deployed WxParser.Svc decides GFS run completeness by which hours
# are present rather than by how many, and that no run sits fully stored but unmarked.
#
# WX-451 replaced `storedHourCount >= expectedHours` with a set difference over
# 0..MaxForecastHours. The unit tests (25: 16 pure, 9 SQLite-backed) prove the logic; only production
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

set -uo pipefail   # verify-lib.sh requires this of its callers, so an unset variable fails loudly
                   # rather than silently widening a window. Find the statement with:
                   #   grep -n "caller owns 'set -uo pipefail'" verify-lib.sh
                   # ⚠️ ANCHOR, NOT A LINE NUMBER. This cited verify-lib.sh:37 until round 2 finding
                   # 12; the number was accurate when written and any insertion above it silently
                   # redirects the reader. Estate rule, 2026-08-15: cite a quoted anchor, never
                   # ":NNN" -- three launcher citations in the shared docs drifted twice in one
                   # morning. An anchor trades ROT for SILENCE, which is the better failure: a
                   # stale number points confidently somewhere wrong, a stale anchor matches nothing.

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
# EvaluateRunCompletenessAsync NEVER RUNS. Counting those would invert the meaning of this row:
# it would rise fastest when the code under test did nothing. The real line reads
#   "marked complete (121/121 hours stored)"
# so anchoring on the opening parenthesis selects it and excludes the skip.
#
# THE PROPERTY, which is what to re-check, rather than three counts that rot at different rates:
#   grep -c 'already marked complete'  +  grep -c 'marked complete \('  ==  grep -c 'marked complete'
# i.e. the two anchors partition the bare match exactly. Verified 2026-08-17 on the live log at
# 351 / 296 / 55, and the SKIP LINE DOMINATES by roughly five to one -- which is why the bare form
# is not merely imprecise but actively misleading.
# ⚠️ An earlier revision of this comment gave 348/294 in one place and 54/295 in another and
#   presented them as one decomposition; they were snapshots taken minutes apart from a growing
#   log, so they could not sum. Stating the invariant instead makes the block re-checkable rather
#   than a claim about a moment that has passed.
# ⚠️ THE BACKSLASH IS REQUIRED. cnt is `grep -cE "$1" || true` -- EXTENDED regex, where a bare
#   "(" is an unmatched group and grep aborts with "Unmatched ( or \(". The `|| true` then
#   swallows that into an EMPTY count rather than an error, so the row silently prints nothing.
#   Measured: unescaped yields a swallowed grep failure and an empty count, not an error.
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
# names as making WX-451's defect reachable.
#
# 🔴 AND "CONFIG" IS THREE LAYERS, NOT ONE. Reading only appsettings.shared.json reintroduced the
# same unfailable check ONE LAYER UP (review round 2, finding 1) -- a wrong-but-numeric bound is
# worse than no bound, because it satisfies the db_ran precondition while measuring nothing.
# WxParser.Svc/Program.cs layers them in this order, LAST WINS:
#
#   1. appsettings.shared.json                       repo copy read here
#   2. appsettings.local.json  from installRoot      DEPLOYED copy is services/wxparser/, which is
#                                                    bind-mounted :ro at /opt/wxservices/ -- and it
#                                                    DOES carry a Gfs section (Wgrib2Path) today
#   3. DB Config table  (WX-313)                     "layered LAST so it wins over the JSON files"
#
# Neither overlay defines MaxForecastHours as of 2026-08-17, so layer 1 supplies it -- BY LUCK,
# not by the old comment's claim that "appsettings is authoritative". All three are read below and
# the winning layer is PRINTED, so a future override is visible rather than silently wrong.
# (A fourth definition site exists: GfsConfig.MaxForecastHours defaults to 120 in C# when no layer
# supplies the key. That is why an absent key yields WAIT here rather than an assumed 120.)
# When WX-447's DatasetExpectation table lands, the bound moves there and this follows.
WXS_DIR="$(cd "$(dirname "$SELF")/../.." && pwd)"
REPO_DIR="$(cd "$WXS_DIR/.." && pwd)"
MAX_FH=''
MAX_FH_LAYER=''
if command -v jq >/dev/null 2>&1; then
    for _layer in "appsettings.shared.json:$WXS_DIR/appsettings.shared.json" \
                  "services/wxparser/appsettings.local.json:$REPO_DIR/services/wxparser/appsettings.local.json"; do
        _name=${_layer%%:*}; _path=${_layer#*:}
        [ -f "$_path" ] || continue
        _v=$(jq -r '.Gfs.MaxForecastHours // empty' "$_path" 2>/dev/null)
        case "$_v" in (''|*[!0-9]*) continue; esac
        MAX_FH="$_v"; MAX_FH_LAYER="$_name"          # no break: later layers override
    done
else
    MAX_FH_LAYER='jq absent'
fi

# TWO path forms, deliberately, and NOT interchangeable: bash tests existence through the WSL
# mount, PowerShell resolves only the Windows form. Passing the /mnt/c path to PowerShell fails
# with "not recognized as the name of a cmdlet". Caught by the WORKFLOW §7a smoke run, where
# `bash -n` passed and the script exited 0 while this check silently did nothing.
SQLCMD_WSL='/mnt/c/Program Files/Microsoft SQL Server/Client SDK/ODBC/170/Tools/Binn/SQLCMD.EXE'
SQLCMD_WIN='C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\170\Tools\Binn\SQLCMD.EXE'

if ! command -v powershell.exe >/dev/null 2>&1 || [ ! -f "$SQLCMD_WSL" ]; then
    stuck_why='powershell.exe or SQLCMD.EXE not reachable from here'
else
    # LAYER 3, read only now: the DB overlay wins over both JSON files, and reading it needs the
    # very tooling the check needs. No chicken-and-egg -- when that tooling is missing the check
    # cannot run at all, so which layer would have won is moot.
    _dbq="SET NOCOUNT ON; IF OBJECT_ID('dbo.Config','U') IS NOT NULL SELECT [Value] FROM dbo.Config WHERE [Key] IN ('Gfs:MaxForecastHours','Gfs__MaxForecastHours');"
    _dbv=$(powershell.exe -NoProfile -Command \
        "& '$SQLCMD_WIN' -S '.\\SQLEXPRESS' -d WeatherData -E -C -h -1 -W -t 60 -Q \"$_dbq\"" 2>&1 \
        | tr -d '\r' | grep -E '^[0-9]+$' | head -1)
    case "$_dbv" in (''|*[!0-9]*) : ;; (*) MAX_FH="$_dbv"; MAX_FH_LAYER='DB Config table (WX-313)';; esac
fi

if [ -z "$MAX_FH" ]; then
    # DELIBERATELY NOT ASSUMING 120. The C# binder would default to it, but an absent key here
    # means we could not READ the config, and a bound we guessed cannot verify a bound we did not.
    [ -n "$stuck_why" ] || stuck_why="no layer supplied Gfs:MaxForecastHours (${MAX_FH_LAYER:-all layers absent or unreadable})"
elif ! command -v powershell.exe >/dev/null 2>&1 || [ ! -f "$SQLCMD_WSL" ]; then
    : # stuck_why already set above
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
# NAME THE WINNING LAYER. Three layers can supply this and the last one wins; printing only the
# value would hide an overlay silently taking over -- which is the whole of round 2's finding 1.
printf  '   %-54s %s\n' '  ...supplied by layer:' "${MAX_FH_LAYER:-none}"
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
# THE PRECONDITION IS "CAN THIS RUN REACH A VERDICT", NOT "DID THE DB CHECK RUN" -- and conflating
# the two turned a conclusive FAIL into a WAIT (round 2, finding 7). vl_verdict tests precond BEFORE
# the failure count, deliberately and correctly: a test that is not applicable must not report FAIL.
# But `badconfig` is a LOG-ONLY signature that needs no database at all, so with a negative bound and
# no sqlcmd the old form printed WAIT / reason=precondition-absent while the failure sat measured and
# conclusive on the line above -- and the Jira paste string said the opposite of what was observed.
verdict_precond=0
[ "$db_ran" -eq 1 ] && verdict_precond=1     # the DB detector ran -> a verdict is reachable
[ "$badconfig" -gt 0 ] && verdict_precond=1  # already conclusive without it -> so is a verdict

# FAIL_HINT, NOT PASS_NOTE. The transient-vs-real rule is the one thing the operator needs at the
# moment of a failure, and it was in the fourth slot where its condition cannot hold: regressions =
# badconfig + stuck, so on PASS `stuck` is necessarily 0 and the advice printed only when vacuous,
# while on a real FAIL vl_verdict emitted no hint at all (round 2, finding 5).
vl_verdict "$regressions" "$exercised" \
  "A same-cycle transient is possible -- the fetch can store the last hour and mark the run in one cycle. RE-RUN THIS SCRIPT before treating it as real: non-zero on two consecutive runs is the silent-freeze symptom (WX-451.md section 3). If 'negative MaxForecastHours' is the non-zero row instead, correct Gfs:MaxForecastHours -- while it holds, no run can ever be marked complete." \
  "set-membership completeness is live; the missing-hours clause proves 1.61.3+ evaluated a run against the real database." \
  "$verdict_precond" \
  "either the database regression check to run (the only detector for the silent-freeze signature; see 'why not measured' above) or an already-conclusive log-only regression"
