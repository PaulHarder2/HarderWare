#!/usr/bin/env bash
# WX-166-verify.sh - confirm WX-166 (Languages registry + Recipient.LanguageId FK +
# code-based language read-path) deployed cleanly, by reading the WxReport service
# log after a deployment boundary.
#
# WX-166 replaces Recipients.Language (a free-text string) with a LanguageId FK to a
# new Languages table, and rewires the report read-path to resolve each recipient's
# language from a per-cycle langById dictionary (the codebase's dict-lookup
# convention). The dangerous failure modes a deploy can introduce are SCHEMA-shaped:
#   - the old binary still running against the NEW schema -> "Invalid column name
#     'Language'" (the dropped column) on every cycle, OR
#   - the new binary against an UN-migrated DB -> "Invalid column name 'LanguageId'"
#     / a broken FK, OR the new ctx.Languages.ToDictionaryAsync throwing.
# Either breaks report delivery. This script's job is to prove NONE of that happened
# and that report cycles keep completing on the new schema.
#
# What this script CANNOT verify (it reads the log, not the DB or the UI): that a
# given recipient's report actually rendered in their language, the seed/backfill row
# counts, the WxManager Languages tab guards, and the editor dropdown. Those are the
# MANUAL steps in docs/test-procedures/WX-166.md -- the language a report is rendered
# in is never written to the service log. So this is the deploy-smoke + schema-health
# + read-path-exercised slice; the .md procedure carries the deep functional checks.
#
# Usage:  WX-166-verify.sh [--since 'YYYY-MM-DD HH:MM:SS'] [--log PATH] [--deploy-log PATH] [-h]
#         (no arguments = the normal version-pinned run)
# Shell:  bash (WSL).  Log timestamps are UTC (the HarderWare PC runs on UTC).
# Lives in the repo beside its procedure (docs/test-procedures/WX-166.md): a
# change-specific verification rides the same PR as the code it checks (WORKFLOW.md
# §13). The shared scaffold (arg parsing, version-pinned deploy boundary via
# deploy-info.sh, the before/after window, header) lives in verify-lib.sh, sourced
# below; only WX-166's metrics + verdict logic are here.

set -uo pipefail

SELF="${BASH_SOURCE[0]}"
TICKET='WX-166'                                    # self-identification + header
VERSION='1.27.0'                                   # the release VERSION under test -- the deploy pin.
                                                   # Knowable at authoring (the bump ships with the
                                                   # change), so this script is review-complete.
COMPONENTS=('WxReportSvc')                          # the service whose log we read and whose read-path WX-166 changed
TITLE='Languages FK + read-path'                   # header description
MIN_WINDOW_HOURS=24                                # PASS withheld until a full active day of cycles accrues
source "$(cd "$(dirname "$SELF")" && pwd)/verify-lib.sh"

vl_parse_args "$@"
vl_resolve_boundary     # sets SINCE/COMMIT/DEPLOY_INFO/BOUNDARY_SRC (WAIT-exits if undeployed)
vl_setup_window         # sets POST/LAST_TS/pre_start/elapsed/hh/mm/min_window_secs (WAIT-exits if no lines)

# ---- metrics over the post-deploy window --------------------------------------
# The schema-regression pattern is the WX-166-specific FAIL signal: a reference to
# the dropped 'Language' column or the new 'LanguageId'/FK in an error means a
# mixed-version deploy (old binary on new schema, or new binary on un-migrated DB).
SCHEMA_RE="Invalid column name '(Language|LanguageId)'|FK_Recipients_Languages|Invalid object name '?Languages"
cycles=$(   printf '%s\n' "$POST" | cnt 'Report cycle complete')
sent=$(     printf '%s\n' "$POST" | cnt 'report sent')
schema_err=$(printf '%s\n' "$POST" | grep -E ' (ERROR|FATAL) ' | cnt "$SCHEMA_RE")
migrated=$( printf '%s\n' "$POST" | cnt 'AddLanguagesTableAndRecipientFk|Applying migration')
# General health, NEW vs the equal-length pre-deploy window (background errors cancel).
read errors_before errors new_errors < <(vl_health_delta ' ERROR ')

vl_header
echo
echo    " NEW SCHEMA LIVE? (read-path exercised on the new schema)"
printf  '   %-46s %s\n' 'Report cycles completed since deploy:'     "$cycles   $([ "$cycles" -gt 0 ] && echo '<- langById/FK read-path ran, no throw' || echo '[none yet]')"
printf  '   %-46s %s\n' 'Per-recipient reports sent:'               "$sent"
printf  '   %-46s %s\n' 'Migration applied line seen (optional):'   "$([ "$migrated" -gt 0 ] && echo "yes ($migrated)" || echo 'not in this log (EF may log elsewhere)')"
echo
echo    " SCHEMA HEALTH (the WX-166-specific failure signal)"
printf  '   %-46s %s\n' 'Schema-regression errors (Language/FK):'   "$schema_err   $([ "$schema_err" -eq 0 ] && echo '[expected 0]' || echo '[!! mixed-version deploy -- see below]')"
[ "$schema_err" -gt 0 ] && { echo "   --- schema-regression lines ---"; printf '%s\n' "$POST" | grep -E ' (ERROR|FATAL) ' | grep -E "$SCHEMA_RE" | tail -5 | sed 's/^/     /'; }
echo
echo    " BACKGROUND HEALTH (context only -- does NOT drive the verdict; the verdict keys on schema-regression)"
printf  '   %-46s %s\n' 'ERROR lines (before -> after):'            "$errors_before -> $errors   (new: $new_errors)"
[ "$new_errors" -gt 0 ] && { echo "   --- ERROR lines in window (may include pre-existing background) ---"; printf '%s\n' "$POST" | grep ' ERROR ' | tail -8 | sed 's/^/     /'; }
echo
# Verdict keys on WX-166's CHANGE-SPECIFIC failure signature -- schema_err, the count of
# schema-regression lines (dropped 'Language' column / new 'LanguageId'/FK errors) that
# appear only on a mixed-version deploy. It is the SAME count shown in SCHEMA HEALTH
# above (ERROR/FATAL-scoped), so the verdict can't contradict the displayed evidence.
# Unrelated background errors do NOT fail it. Exercised = a report cycle completed.
vl_verdict "$schema_err" "$cycles" \
  "Confirm the $VERSION deploy/restart AND that the migration applied (old binary on new schema, or new binary on un-migrated DB)." \
  "Now run the MANUAL checks in docs/test-procedures/WX-166.md (seed/backfill counts, Languages tab guards, editor dropdown, per-language render) before Done."
