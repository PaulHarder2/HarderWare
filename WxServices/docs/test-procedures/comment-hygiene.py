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

def lines(p): return open(p, encoding='utf-8-sig').read().splitlines()

# A comment line is any line CONTAINING a comment, not only one starting with it:
# a trailing `x = 1; // note` was invisible to every guard in the first version.
def _comment_text(line):
    """The comment part of a line, or None. Respects string literals."""
    i, n, st = 0, len(line), 'N'
    while i < n:
        c = line[i]; d = line[i+1] if i+1 < n else ''
        if st == 'N':
            if c == '/' and d == '/': return re.sub(r'^/+\s?', '', line[i:]).strip()
            if c == '"': st = 'S'; i += 1; continue
            if c == "'": st = 'C'; i += 1; continue
            i += 1; continue
        if st == 'S':
            if c == '\\': i += 2; continue
            if c == '"': st = 'N'
            i += 1; continue
        if st == 'C':
            if c == '\\': i += 2; continue
            if c == "'": st = 'N'
            i += 1; continue
    return None

def _is_c(l): return _comment_text(l) is not None
def _txt(l):  return _comment_text(l) or ''

def g1_lowercase_block_openers(L):
    """A comment block whose first WORDED line starts lowercase - the signature of a
    stripped `WX-nnn: ` stamp. A bare `//` separator is not a leader and must not hide
    the paragraph after it."""
    out = []
    for i, l in enumerate(L):
        if not _is_c(l): continue
        if not l.strip().startswith('//'): continue   # trailing annotation, not a block
        t = _txt(l)
        if not t: continue                      # bare `//` - transparent, not a leader
        # leader = first worded comment line of the run
        j = i - 1
        while j >= 0 and _is_c(L[j]) and not _txt(L[j]): j -= 1
        if j >= 0 and _is_c(L[j]) and _txt(L[j]): continue
        w = t.split()[0]
        if re.match(r'^[a-z]', w) and not re.match(r'^(https?|www)', w) \
           and '(' not in w and w not in ('e.g.', 'i.e.', 'cf.'):
            out.append((i + 1, t[:70]))
    return out

_SENT_END = re.compile(r'[.!?:;]$')
def g2_doubled_words(L):
    out = []
    for i, l in enumerate(L):
        if not _is_c(l): continue
        m = re.search(r'\b(\w+)\s+\1\b', _txt(l), re.I)
        if m: out.append((i + 1, m.group(0)))
    for i in range(len(L) - 1):
        if not (_is_c(L[i]) and _is_c(L[i + 1])): continue
        a, b = _txt(L[i]).split(), _txt(L[i + 1]).split()
        if not (a and b): continue
        # Same word across a join. Require SAME CASE and no sentence break: "...the" /
        # "The next..." is a legitimate new sentence, not a doubling.
        if a[-1] == b[0] and a[-1].isalpha() and not _SENT_END.search(a[-1]) \
           and not _SENT_END.search(_txt(L[i])):
            out.append((i + 1, f"{a[-1]} / {b[0]} (across join)"))
    return out

def g3_dangling_punctuation(L):
    out = []
    for i, l in enumerate(L):
        if not _is_c(l): continue
        if i + 1 < len(L) and _is_c(L[i + 1]) and _txt(L[i + 1]): continue
        t = re.sub(r'(</\w+>|/>)$', '', _txt(l).rstrip()).rstrip()
        if t.endswith(('—', '–', ':', '(', ',')):
            out.append((i + 1, t[-60:]))
    return out

GUARDS = [("lowercase block openers", g1_lowercase_block_openers),
          ("doubled words",           g2_doubled_words),
          ("dangling punctuation",    g3_dangling_punctuation)]

def doc_delta(after, before):
    da = [_txt(l) for l in after if l.strip().startswith('///')]
    db = [_txt(l) for l in before if l.strip().startswith('///')]
    return sum(1 for x, y in zip(db, da) if x != y), len(db), len(da)

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
