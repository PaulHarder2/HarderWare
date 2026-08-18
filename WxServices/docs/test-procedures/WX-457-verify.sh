#!/usr/bin/env bash
# WX-457-verify.sh - prove a comments-only trim really is comments-only, and that it
# introduced none of the mechanical defects a trim is prone to.
#
# Reusable: --file and --base point it at the next trim.
#
# 🔴 EVERY CHECK IS SCORED BY ITS EXIT STATUS, NEVER BY ITS OUTPUT TEXT, AND THAT IS THE
#    WHOLE ARCHITECTURE OF THIS SCRIPT. The previous version grepped stdout for "FAIL"
#    through a pipe. Under `set -o pipefail` the pipeline returned the CHECKER's non-zero
#    rather than grep's zero, so the `if` was false exactly when a guard HAD failed:
#    guards 1-3 could never fail the run. Text is what a human reads; status is what a
#    machine acts on. If you add a check here, capture its rc directly - no pipes.
#
# Usage:
#   ./WX-457-verify.sh [--file PATH] [--base REV] [--head REV]
#   ./WX-457-verify.sh --selftest
#
# Exit: 0 all checks passed · 1 a check failed · 2 usage/lookup error
#       3 a check COULD NOT RUN - never a pass
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/../../.." && pwd)"
FILE="WxServices/src/WxReport.Svc/ForecastReconciler.cs"
BASE="origin/master"; HEAD_REV="HEAD"; SELFTEST=0

while [ $# -gt 0 ]; do
  case "$1" in
    --file) FILE="$2"; shift 2 ;;
    --base) BASE="$2"; shift 2 ;;
    --head) HEAD_REV="$2"; shift 2 ;;
    --selftest) SELFTEST=1; shift ;;
    -h|--help) sed -n '2,25p' "$0"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

if [ "$SELFTEST" -eq 1 ]; then
  # 🔴 The selftest must exercise EVERY guard, and its own status must be the aggregate.
  #    An earlier version reported six green on a build where two guards had been gutted.
  echo "comment-hygiene selftest:"
  python3 "$HERE/comment-hygiene.py" --selftest; h=$?
  echo "AC-1 stripper selftest:"
  python3 "$HERE/comment-trim-ac1.py" --selftest; a=$?
  [ "$h" -eq 0 ] && [ "$a" -eq 0 ] && { echo "SELFTEST PASS"; exit 0; }
  echo "SELFTEST FAIL (hygiene=$h ac1=$a)"; exit 1
fi

cd "$REPO" || exit 2
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
BEFORE="$TMP/before.cs"; AFTER="$TMP/after.cs"

git show "$BASE:$FILE" > "$BEFORE" 2>/dev/null || { echo "cannot read $FILE at $BASE" >&2; exit 2; }
git show "$HEAD_REV:$FILE" > "$AFTER" 2>/dev/null || { echo "cannot read $FILE at $HEAD_REV" >&2; exit 2; }

BASE_SHA="$(git rev-parse "$BASE")"; HEAD_SHA="$(git rev-parse "$HEAD_REV")"

echo "============================================================"
echo " comment-trim verification"
echo "============================================================"
echo " file : $FILE"
echo " base : $BASE   (${BASE_SHA:0:8})"
echo " head : $HEAD_REV   (${HEAD_SHA:0:8})"
echo

# 🔴 REFUSE A VACUOUS RUN. Once this branch merges, origin/master == HEAD and the
#    default invocation compares a file with itself - every check passes on an empty
#    diff and the banner still says PASS. A verification that quietly becomes a no-op
#    the moment it lands is worse than none.
if [ "$BASE_SHA" = "$HEAD_SHA" ]; then
  echo "  REFUSING TO RUN: base and head are the same commit (${BASE_SHA:0:8})."
  echo "  There is no change to verify, and a PASS here would mean nothing."
  echo "  Pass --base explicitly, e.g. --base HEAD~1."
  exit 3
fi
if cmp -s "$BEFORE" "$AFTER"; then
  echo "  REFUSING TO RUN: $FILE is identical at both revisions."
  echo "  Nothing to verify. Check --file and --base."
  exit 3
fi

rc=0; cannot=0

echo " [1] AC-1 - the change is COMMENTS ONLY"
python3 "$HERE/comment-trim-ac1.py" "$BEFORE" "$AFTER"; s=$?
case "$s" in 0) : ;; 3) echo "   -> CANNOT CHECK"; cannot=1 ;; *) rc=1 ;; esac
echo

echo " [2] POSITIVE CONTROL - AC-1 must DETECT a planted code change"
# ⚠️ It must plant CODE. An earlier control inserted a /*comment*/, which AC-1 correctly
#    ignored - so the control reported failure while the check worked perfectly. And it
#    must target a line AC-1 can actually see: a mutation landing inside a region the
#    stripper mis-swallows would go undetected and the control would report "ok" on a
#    file whose real change is invisible.
python3 - "$AFTER" "$TMP/mutant.cs" <<'MUT'
import sys, re
src = open(sys.argv[1], encoding='utf-8-sig').read()
out, k = re.subn(r'(=\s*)(\d+)(\s*;)', lambda m: f"{m.group(1)}{int(m.group(2))+1}{m.group(3)}", src, count=1)
if k != 1:
    sys.stderr.write("no integer-assignment to mutate\n"); sys.exit(3)
open(sys.argv[2], 'w', encoding='utf-8').write(out)
MUT
m=$?
if [ "$m" -ne 0 ]; then
  echo "   CANNOT CHECK - could not plant a mutation; this control is VOID"; cannot=1
else
  python3 "$HERE/comment-trim-ac1.py" "$BEFORE" "$TMP/mutant.cs" >/dev/null 2>&1; s=$?
  if [ "$s" -eq 1 ]; then echo "   ok    planted code change detected; [1] is meaningful"
  else echo "   FAIL  planted code change NOT detected (rc=$s) - [1] proves nothing"; rc=1; fi
fi
echo

echo " [3] MECHANICAL GUARDS - findings INTRODUCED by this change"
# 🔴 SCORED BY COMPARISON AGAINST THE BASELINE, not by the AFTER file being clean.
#    A finding present in both revisions is a pre-existing habit of the file; only what
#    the change ADDED is attributable to it. The earlier version scored the two
#    separately, which made any file with existing habits permanently unpassable.
python3 "$HERE/comment-hygiene.py" --delta "$AFTER" "$BEFORE"; s=$?
[ "$s" -ne 0 ] && rc=1
echo

echo " [4] BASELINE - absolute guard state, both revisions (context, not scored)"
echo "   before:"; python3 "$HERE/comment-hygiene.py" "$BEFORE" 2>&1 | sed 's/^/  /' || true
echo "   after:";  python3 "$HERE/comment-hygiene.py" "$AFTER"  2>&1 | sed 's/^/  /' || true
echo

echo " [5] INFORMATIONAL - /// content delta (not scored)"
python3 "$HERE/comment-hygiene.py" --doc-delta "$AFTER" "$BEFORE" || true
echo

echo "============================================================"
if [ "$cannot" -ne 0 ]; then
  echo "  ====>  CANNOT CHECK - a check did not run. This is NOT a pass."
  echo "============================================================"; exit 3
fi
if [ "$rc" -eq 0 ]; then
  echo "  ====>  PASS"
  echo "  Test Result: PASS $(date -u +%Y-%m-%d) - comments-only proven; guards clean"
else
  echo "  ====>  FAIL - see above"
fi
echo "============================================================"
exit "$rc"
