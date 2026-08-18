#!/usr/bin/env python3
"""Mechanical guards over a C# comment trim.

  comment-hygiene.py AFTER.cs                 run the scored guards
  comment-hygiene.py --doc-delta AFTER BEFORE informational /// delta, never scored
  comment-hygiene.py --selftest               prove every guard can fail

🔴 SCORED GUARDS ONLY AFFECT THE EXIT STATUS. Guard 4 (/// text changed) is NOT scored:
   doc text legitimately changes when a trim repairs a doc that had become false. It
   refutes a claim of "docs untouched" and nothing else, so it lives behind --doc-delta.
"""
import re, sys, tempfile, os

import importlib.util as _il, os as _os
_spec = _il.spec_from_file_location('_ac1', _os.path.join(_os.path.dirname(_os.path.abspath(__file__)), 'comment-trim-ac1.py'))
_ac1 = _il.module_from_spec(_spec); _spec.loader.exec_module(_ac1)

class Doc:
    """A file plus its comment map, built by the SHARED whole-file tokenizer.

    🔴 THIS DELIBERATELY DOES NOT HAVE ITS OWN COMMENT PARSER. It used to, and that
       parser reset state per line and knew only "..." and '...' - so a // inside a
       multi-line raw or verbatim string read as a comment, the exact blind spot the
       AC-1 stripper had been hardened against. One tokenizer was fixed and the other
       shipped. Two parsers cannot stay equal; one imported parser cannot drift.
    """
    def __init__(self, path):
        src = open(path, encoding='utf-8-sig').read()
        self.raw = src.splitlines()
        self.cmap = dict(_ac1.comment_map(src))     # 1-based line -> comment text
    def is_c(self, i):   return (i + 1) in self.cmap
    def txt(self, i):    return self.cmap.get(i + 1, '')
    def own_line(self, i):
        return i < len(self.raw) and self.raw[i].strip().startswith('//')
    def __len__(self):   return len(self.raw)

def lines(p): return Doc(p)

def g1_lowercase_block_openers(L):
    """A comment block whose first WORDED line starts lowercase - the signature of a
    stripped `WX-nnn: ` stamp. A bare `//` separator is not a leader and must not hide
    the paragraph after it."""
    out = []
    for i in range(len(L)):
        if not L.is_c(i): continue
        if not L.own_line(i): continue   # trailing annotation, not a block
        t = L.txt(i)
        if not t: continue                      # bare `//` - transparent, not a leader
        # leader = first worded comment line of the run
        j = i - 1
        while j >= 0 and L.is_c(j) and not L.txt(j): j -= 1
        if j >= 0 and L.is_c(j) and L.txt(j): continue
        w = t.split()[0]
        if re.match(r'^[a-z]', w) and not re.match(r'^(https?|www)', w) \
           and '(' not in w and w not in ('e.g.', 'i.e.', 'cf.'):
            out.append((i + 1, t[:70]))
    return out

_SENT_END = re.compile(r'[.!?:;]$')
def g2_doubled_words(L):
    out = []
    for i in range(len(L)):
        if not L.is_c(i): continue
        m = re.search(r'\b(\w+)\s+\1\b', L.txt(i), re.I)
        if m: out.append((i + 1, m.group(0)))
    for i in range(len(L) - 1):
        if not (L.is_c(i) and L.is_c(i + 1)): continue
        a, b = L.txt(i).split(), L.txt(i + 1).split()
        if not (a and b): continue
        # Same word across a join. Require SAME CASE and no sentence break: "...the" /
        # "The next..." is a legitimate new sentence, not a doubling.
        if a[-1] == b[0] and a[-1].isalpha() and not _SENT_END.search(a[-1]) \
           and not _SENT_END.search(L.txt(i)):
            out.append((i + 1, f"{a[-1]} / {b[0]} (across join)"))
    return out

def g3_dangling_punctuation(L):
    out = []
    for i in range(len(L)):
        if not L.is_c(i): continue
        if i + 1 < len(L) and L.is_c(i + 1) and L.txt(i + 1): continue
        t = re.sub(r'(</\w+>|/>)$', '', L.txt(i).rstrip()).rstrip()
        if t.endswith(('—', '–', ':', '(', ',')):
            out.append((i + 1, t[-60:]))
    return out

GUARDS = [("lowercase block openers", g1_lowercase_block_openers),
          ("doubled words",           g2_doubled_words),
          ("dangling punctuation",    g3_dangling_punctuation)]

def doc_delta(after, before):
    # zip_longest, not zip: zip stops at the shorter input, so /// lines APPENDED or
    # REMOVED at the end of a file were invisible - a delta of exactly the kind this
    # guard exists to report.
    from itertools import zip_longest
    da = [after.txt(i) for i in range(len(after)) if after.raw[i].strip().startswith('///')]
    db = [before.txt(i) for i in range(len(before)) if before.raw[i].strip().startswith('///')]
    ch = sum(1 for x, y in zip_longest(db, da, fillvalue=None) if x != y)
    return ch, len(db), len(da)

def findings(path):
    """Guard findings as TEXT, so before/after can be compared across shifted lines."""
    L = lines(path); out = {}
    for name, fn in GUARDS:
        out[name] = {t for _, t in fn(L)}
    return out

def run(path, quiet=False):
    L = lines(path); fails = 0
    for name, fn in GUARDS:
        res = fn(L)
        if res:
            fails += 1
            if not quiet:
                print(f"  FAIL  {name}: {len(res)}")
                for ln, t in res[:6]: print(f"          L{ln}: {t}")
        elif not quiet:
            print(f"  ok    {name}: 0")
    return 1 if fails else 0

def selftest():
    """🔴 TWO THINGS, AND THE SECOND IS THE ONE THAT MATTERS.
    (a) each fixture must be DETECTED; (b) each guard, when GUTTED, must make its own
    fixture stop being detected. Without (b) a selftest reports green over a guard that
    detects nothing - measured, on this very file's previous version."""
    d = tempfile.mkdtemp()
    base = ["// A proper sentence here.", "int x = 1;"]
    fixtures = {
        "lowercase block openers": ["// compute the thing.", "int y = 1;"],
        "doubled words":           ["// the the thing is fine.", "int y = 1;"],
        "dangling punctuation":    ["// a sentence ending in —", "int y = 1;"],
    }
    extra = {
        "doubled words":  [["// ends with the", "// the next line starts.", "int y=1;"]],
        "lowercase block openers": [["//", "// bare separator must not hide me.", "int y=1;"]],
    }
    ok = True
    clean = os.path.join(d, 'clean.cs'); open(clean, 'w', encoding='utf-8').write('\n'.join(base))
    n = run(clean, quiet=True)
    print(f"  {'ok  ' if n == 0 else 'FAIL'}  CLEAN CONTROL must pass: rc={n}")
    ok &= (n == 0)
    for name, fn in GUARDS:
        f = os.path.join(d, 'm.cs')
        open(f, 'w', encoding='utf-8').write('\n'.join(base + fixtures[name]))
        detected = bool(fn(lines(f)))
        # 🔴 THIS PER-GUARD LINE *IS* THE COVERAGE. Gut any guard and its own fixture
        #    stops being detected, so the selftest fails. An earlier version added a
        #    "discriminates-when-gutted" line on top; it compared a stub that always
        #    returns [] against itself and was therefore unconditionally true - a check
        #    that cannot fail, inside the selftest that exists to prevent exactly that.
        print(f"  {'ok  ' if detected else 'FAIL'}  {name}: fixture detected={detected}")
        ok &= detected
    for name, cases in extra.items():
        fn = dict((n_, f_) for n_, f_ in GUARDS)[name]
        for k, src in enumerate(cases):
            f = os.path.join(d, f'e{k}.cs'); open(f, 'w', encoding='utf-8').write('\n'.join(base + src))
            det = bool(fn(lines(f)))
            print(f"  {'ok  ' if det else 'FAIL'}  {name} [extra {k}]: detected={det}")
            ok &= det
    # 🔴 STRING-LITERAL controls. These are the reason this file no longer owns a
    #    parser: each was invisible to the previous per-line comment finder.
    Q = chr(34) * 3          # the raw-string delimiter, built rather than written
    lit = {
      "// inside a multi-line verbatim string":
        ['class C {', '  string s = @"line one // not a comment',
         'line two /* nor this */";', '  int x = 1;', '}'],
      "// inside a raw string literal":
        ['class C {', '  string s = ' + repr(Q + 'a // b ' + Q) + ';', '  int x = 1;', '}'],
    }
    for label, src in lit.items():
        f = os.path.join(d, 'lit.cs'); open(f, 'w', encoding='utf-8').write(chr(10).join(src))
        doc = lines(f)
        leaked = [t for t in doc.cmap.values()
                  if 'not a comment' in t or 'nor this' in t or 'a // b' in t]
        print(f"  {'ok  ' if not leaked else 'FAIL'}  literal control, {label}: leaked={leaked}")
        ok &= not leaked

    # doc_delta must see /// lines APPENDED or REMOVED at the end, which zip() hid.
    b_ = os.path.join(d, 'db.cs'); a_ = os.path.join(d, 'da.cs')
    open(b_, 'w', encoding='utf-8').write('/// one' + chr(10) + 'int x=1;' + chr(10))
    open(a_, 'w', encoding='utf-8').write('/// one' + chr(10) + '/// two appended' + chr(10) + 'int x=1;' + chr(10))
    ch, nb, na = doc_delta(lines(a_), lines(b_))
    print(f"  {'ok  ' if ch == 1 else 'FAIL'}  doc_delta sees an APPENDED /// line: changed={ch} ({nb}->{na})")
    ok &= (ch == 1)
    ch, nb, na = doc_delta(lines(b_), lines(a_))
    print(f"  {'ok  ' if ch == 1 else 'FAIL'}  doc_delta sees a REMOVED /// line: changed={ch} ({nb}->{na})")
    ok &= (ch == 1)

    # FALSE-POSITIVE controls: legitimate text that must NOT trip a guard.
    fp = {"legit new sentence across a join": ["// A line ending in the", "// The next sentence starts.", "int y=1;"],
          "trailing comment, proper case":    ["int y = 1;  // Fine here.", "int z = 2;"]}
    for label, src in fp.items():
        f = os.path.join(d, 'fp.cs'); open(f, 'w', encoding='utf-8').write('\n'.join(base + src))
        n = run(f, quiet=True)
        print(f"  {'ok  ' if n == 0 else 'FAIL'}  FP control, {label}: rc={n}")
        ok &= (n == 0)
    return 0 if ok else 1

if __name__ == '__main__':
    if '--selftest' in sys.argv: sys.exit(selftest())
    if '--delta' in sys.argv:
        # 🔴 THE BASELINE IS SCORED BY COMPARISON, NOT BY BEING CLEAN. A finding present
        #    in BOTH revisions is a pre-existing habit of the file and is NOT this
        #    change's fault; only a finding the change ADDED can be attributed to it.
        #    Scoring the baseline as "must be clean" would make any real file unpassable.
        a, b = sys.argv[2], sys.argv[3]
        fa, fb = findings(a), findings(b)
        newf = 0; pre = 0
        for name, _ in GUARDS:
            added = fa[name] - fb[name]; carried = fa[name] & fb[name]
            pre += len(carried)
            if added:
                newf += len(added)
                print(f"  FAIL  {name}: {len(added)} INTRODUCED by this change")
                for t in sorted(added)[:6]: print(f"          {t[:72]}")
            else:
                print(f"  ok    {name}: 0 introduced" + (f" ({len(carried)} pre-existing, not this change)" if carried else ""))
        if pre and not newf:
            print(f"  note  {pre} pre-existing finding(s) carried through unchanged - not scored here")
        sys.exit(1 if newf else 0)
    if '--doc-delta' in sys.argv:
        a, b = sys.argv[2], sys.argv[3]
        ch, nb, na = doc_delta(lines(a), lines(b))
        print(f"        /// text changed: {ch} line(s)  (counts {nb} -> {na})")
        sys.exit(0)
    sys.exit(run(sys.argv[1]))
