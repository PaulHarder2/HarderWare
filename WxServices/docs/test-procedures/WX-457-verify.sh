#!/usr/bin/env bash
# WX-457-verify.sh - prove a comments-only trim really is comments-only, and that it
# introduced none of the mechanical defects a trim is prone to.
#
# Reusable. Nothing here is specific to WX-457 except the defaults: pass a different
# --file and --base to run it against the next trim (ReportWorker.cs is the intended
# next subject).
#
# 🔴 WHY THIS EXISTS RATHER THAN A HUMAN READING THE DIFF. Three reviewers read the
#    WX-457 diff and between them found 12 defects. FOUR of the mechanical ones were
#    found only by these checks, and one of those was created BY a repair of the same
#    defect class two hours after the reviewers' passes - so no amount of reading could
#    have caught it. Reading finds meaning; these find mechanism.
#
# Usage:
#   ./WX-457-verify.sh [--file PATH] [--base REV] [--head REV]
#   ./WX-457-verify.sh --selftest      # prove the checks can FAIL
#
# Exit: 0 all checks pass · 1 a check failed · 2 usage/lookup error
set -uo pipefail   # NOT -e: we want every check to run and report, not stop at the first

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/../../.." && pwd)"
FILE="WxServices/src/WxReport.Svc/ForecastReconciler.cs"
BASE="origin/master"; HEAD_REV="HEAD"

while [ $# -gt 0 ]; do
  case "$1" in
    --file)  FILE="$2"; shift 2 ;;
    --base)  BASE="$2"; shift 2 ;;
    --head)  HEAD_REV="$2"; shift 2 ;;
    --selftest)
        echo "AC-1 verifier selftest:"; python3 "$HERE/comment-trim-ac1.py" --selftest 2>/dev/null \
          || echo "  (comment-trim-ac1.py has no --selftest; its control is exercised below)"
        echo "Comment-hygiene selftest:"; python3 "$HERE/comment-hygiene.py" --selftest; exit $? ;;
    -h|--help) sed -n '2,22p' "$0"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

cd "$REPO" || exit 2
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
BEFORE="$TMP/before.cs"; AFTER="$TMP/after.cs"

# Derive BOTH revisions from git. Deliberately not from a scratch copy: a scratch file
# is unversioned, and a verification that depends on one cannot be re-run by anyone else.
git show "$BASE:$FILE" > "$BEFORE" 2>/dev/null || { echo "cannot read $FILE at $BASE" >&2; exit 2; }
git show "$HEAD_REV:$FILE" > "$AFTER" 2>/dev/null || { echo "cannot read $FILE at $HEAD_REV" >&2; exit 2; }

echo "============================================================"
echo " WX-457 comment-trim verification"
echo "============================================================"
echo " file : $FILE"
echo " base : $BASE   ($(git rev-parse --short "$BASE"))"
echo " head : $HEAD_REV   ($(git rev-parse --short "$HEAD_REV"))"
echo

rc=0

echo " AC-1 - the change is COMMENTS ONLY"
python3 "$HERE/comment-trim-ac1.py" "$BEFORE" "$AFTER" || rc=1
echo
echo " POSITIVE CONTROL - the AC-1 check must be able to FAIL"
# Plant a real CODE change in a copy. If this passes, the check is decoration.
# ⚠️ It must be CODE. An earlier version of this control inserted a /*comment*/, which
# AC-1 correctly ignored - so the control "failed" while the check was working perfectly.
# A control that plants the wrong KIND of change measures nothing.
python3 - "$AFTER" "$TMP/mutant.cs" <<'MUT'
import sys, re
src = open(sys.argv[1], encoding='utf-8-sig').read()
# flip the first integer literal in an assignment - unambiguously code, never a comment
out, k = re.subn(r'(=\s*)(\d+)(\s*;)', lambda m: f"{m.group(1)}{int(m.group(2))+1}{m.group(3)}", src, count=1)
if k != 1:
    sys.stderr.write("MUTATION FAILED TO APPLY - the control below is void\n"); sys.exit(3)
open(sys.argv[2], 'w', encoding='utf-8').write(out)
MUT
if [ $? -ne 0 ]; then echo "   FAIL  could not plant a mutation; this control is VOID"; rc=1; fi
if python3 "$HERE/comment-trim-ac1.py" "$BEFORE" "$TMP/mutant.cs" >/dev/null 2>&1; then
    echo "   FAIL  a planted code change was NOT detected - AC-1 above proves nothing"; rc=1
else
    echo "   ok    planted code change detected; the AC-1 result above is meaningful"
fi
echo
echo " MECHANICAL GUARDS - defects a trim introduces that reading misses"
# Guards 1-3 are pass/fail. Guard 4 (/// text changed) is INFORMATIONAL: doc text
# legitimately changes when a trim repairs a doc that had become false, as this one did.
# It fails only a claim of "XML docs untouched" - so it is reported, not scored.
python3 "$HERE/comment-hygiene.py" "$AFTER" | grep -vE '/// text changed' || true
if python3 "$HERE/comment-hygiene.py" "$AFTER" | grep -qE '^  FAIL'; then rc=1; fi
echo
echo " INFORMATIONAL - /// content delta (NOT a failure; see above)"
python3 "$HERE/comment-hygiene.py" "$AFTER" "$BEFORE" 2>&1 | grep -E '/// text changed' | sed 's/FAIL/    /'
echo
echo " BASELINE - the same guards against the PRE-TRIM file"
echo "   (they must be clean BEFORE, or they are flagging pre-existing noise)"
python3 "$HERE/comment-hygiene.py" "$BEFORE" 2>&1 | sed 's/^/  /'
echo
echo "============================================================"
if [ "$rc" -eq 0 ]; then
  echo "  ====>  PASS"
  echo "  Test Result: PASS $(date -u +%Y-%m-%d) - comments-only proven; guards clean"
else
  echo "  ====>  FAIL - see above"
fi
echo "============================================================"
exit "$rc"
