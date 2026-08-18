#!/usr/bin/env python3
"""AC-1: prove a change is comments-only.

Strips C# comments and compares the surviving code. Exit 0 = comments-only.

🔴 THIS SCRIPT'S OWN HISTORY IS THE REASON FOR ITS PARANOIA. An earlier version
   mis-parsed C# 11 raw string literals: it toggled "in string" on every quote, so a
   raw string with an ODD number of internal quotes left it thinking it was in code.
   A /* inside that content then opened a bogus block comment and it DELETED REAL CODE
   - from both revisions identically, which is exactly what made the difference
   invisible. It reported "comments-only proven" on a change to an int literal.
   15 .cs files in this repo use \"\"\", so this was reachable, not theoretical.

Exit codes:
  0  comments-only
  1  code differs
  2  usage error
  3  THE STRIPPER DOES NOT TRUST ITSELF - unbalanced parse or a code-looking line
     was consumed as a comment. NEVER treat this as a pass.
"""
import sys, re

NORMAL, LINE, BLOCK, STR, VERB, CHAR, RAW = range(7)

class Unbalanced(Exception): pass

def strip_comments(src):
    """Return (stripped_text, set_of_line_numbers_whose_content_was_consumed)."""
    out = []; i = 0; n = len(src); st = NORMAL
    line = 1; raw_len = 0; block_midline = False
    consumed = set()          # lines where we removed at least one non-space char
    def emit(c):
        out.append(c)
    while i < n:
        c = src[i]; d = src[i+1] if i+1 < n else ''
        if c == '\n': line += 1
        if st == NORMAL:
            # raw string: a run of 3+ quotes, optionally prefixed $ and/or @
            m = re.match(r'[$@]{0,2}"{3,}', src[i:])
            if m and '"""' in m.group(0):
                tok = m.group(0); raw_len = len(tok) - tok.index('"')
                emit(tok); i += len(tok); st = RAW; continue
            if c == '/' and d == '/': st = LINE; i += 2; continue
            if c == '/' and d == '*':
                # Was this /* the first thing on its line? A block comment opened at
                # line start is ordinary commented-out code. One opened MID-LINE is how
                # the raw-string bug manifested - a /* inside string content. Only the
                # mid-line case makes a consumed code-line suspicious.
                bol = src.rfind('\n', 0, i) + 1
                block_midline = src[bol:i].strip() != ''
                st = BLOCK; i += 2; continue
            # verbatim, either order: @" and $@" and @$"
            m = re.match(r'(?:@\$|\$@|@)"', src[i:])
            if m: emit(m.group(0)); i += len(m.group(0)); st = VERB; continue
            m = re.match(r'\$"', src[i:])
            if m: emit(m.group(0)); i += 2; st = STR; continue
            if c == '"': emit(c); i += 1; st = STR; continue
            if c == "'": emit(c); i += 1; st = CHAR; continue
            emit(c); i += 1; continue
        if st == LINE:
            if c == '\n': emit(c); st = NORMAL
            i += 1; continue
        if st == BLOCK:
            if c == '*' and d == '/': st = NORMAL; block_midline = False; i += 2; continue
            if c == '\n': emit(c)
            elif not c.isspace() and block_midline: consumed.add(line)
            i += 1; continue
        if st == RAW:
            m = re.match('"{%d,}' % raw_len, src[i:])
            if m: emit(m.group(0)); i += len(m.group(0)); st = NORMAL; continue
            emit(c); i += 1; continue
        if st == STR:
            emit(c)
            if c == '\\' and i + 1 < n: emit(d); i += 2; continue
            if c == '"': st = NORMAL
            if c == '\n': raise Unbalanced(f"unterminated string at line {line}")
            i += 1; continue
        if st == VERB:
            emit(c)
            if c == '"':
                if d == '"': emit(d); i += 2; continue
                st = NORMAL
            i += 1; continue
        if st == CHAR:
            emit(c)
            if c == '\\' and i + 1 < n: emit(d); i += 2; continue
            if c == "'": st = NORMAL
            i += 1; continue
    if st != NORMAL:
        raise Unbalanced(f"file ends inside state {st} - the parse is unbalanced")
    return ''.join(out), consumed

# A line the stripper consumed that looks like CODE rather than a comment.
# This is the durable guard: it catches ANY stripper blind spot, not just the raw-string
# one, because it asks "did we delete something that looks like code?" rather than
# "is our state machine right?".
CODEY = re.compile(r'[;{}]|=>|\breturn\b|\bclass\b|\bvoid\b|\bint\b|\bvar\b')
def suspicious(src, consumed):
    bad = []
    for ln in sorted(consumed):
        raw = src.splitlines()[ln-1] if ln-1 < len(src.splitlines()) else ''
        t = raw.strip()
        if t.startswith(('//', '/*', '*')) or not t:
            continue                      # plainly comment-shaped; fine
        if CODEY.search(t):
            bad.append((ln, t[:70]))
    return bad

def code_only(path):
    src = open(path, encoding='utf-8-sig').read()
    stripped, consumed = strip_comments(src)
    sus = suspicious(src, consumed)
    lines = [l.strip() for l in stripped.splitlines()]
    return '\n'.join(l for l in lines if l), sus

def main():
    if len(sys.argv) < 3:
        print("usage: comment-trim-ac1.py BEFORE.cs AFTER.cs", file=sys.stderr); return 2
    before, after = sys.argv[1], sys.argv[2]
    try:
        a, sa = code_only(before)
        b, sb = code_only(after)
    except Unbalanced as e:
        print(f"  AC-1 CANNOT CHECK - {e}")
        print("  This is NOT a pass. The stripper could not parse the file.")
        return 3
    for label, sus in (("before", sa), ("after", sb)):
        if sus:
            print(f"  AC-1 CANNOT CHECK - {len(sus)} code-looking line(s) were consumed as comments in {label}:")
            for ln, t in sus[:6]: print(f"        L{ln}: {t}")
            print("  A stripper blind spot can hide a real code change. NOT a pass.")
            return 3
    print(f"  code lines  before={a.count(chr(10))+1}  after={b.count(chr(10))+1}")
    if a == b:
        print("  AC-1 PASS - surviving code is identical after whitespace normalisation;")
        print("              the change is comments-only.")
        return 0
    import difflib
    print("  AC-1 FAIL - code differs:")
    for l in list(difflib.unified_diff(a.splitlines(), b.splitlines(), 'before', 'after',
                                       lineterm='', n=1))[:40]:
        print("   ", l)
    return 1

def selftest():
    """Every fixture is a defect this stripper ONCE HAD or could plausibly have.
    Round 101 found the raw-string case by reproducing it end to end; it is fixture 1
    and must never regress."""
    import tempfile, os
    d = tempfile.mkdtemp()
    def w(name, text):
        p = os.path.join(d, name); open(p, 'w', encoding='utf-8').write(text); return p
    cases = []

    # 1. RAW STRING with an odd internal quote count and a /* inside it. The original
    #    defect: the stripper opened a bogus block comment and deleted real code from
    #    BOTH revisions identically, reporting "comments-only proven" on a code change.
    raw = 'class R {\n int v = 1;\n string t = """he said "hi /* here""";\n int th = %d;\n string z = """done */ ok""";\n}\n'
    cases.append(("raw string hides a code change", w('r1.cs', raw % 10), w('r2.cs', raw % 999), 1))

    # 2. // and /* inside an @$" verbatim string must not be read as comments.
    v = 'class C { string s = @$"http://x/*y"; int k = %d; }\n'
    cases.append(("@$ verbatim not mistaken for a comment", w('v1.cs', v % 1), w('v2.cs', v % 2), 1))

    # 3. FALSE-POSITIVE control: commented-out code is legitimate and must not trip the
    #    code-looking-line guard. A block comment opened at LINE START is ordinary.
    legit = 'class C {\n /*\n int old = 1;\n return old;\n */\n int nw = 2;\n}\n'
    cases.append(("commented-out code is not suspicious", w('l1.cs', legit), w('l2.cs', legit), 0))

    # 4. Unbalanced parse must report CANNOT CHECK, never a pass.
    unb = 'class C { int x = 1; /* never closed\n'
    cases.append(("unterminated block comment", w('u1.cs', unb), w('u2.cs', unb), 3))

    # 5. A genuine comments-only change must PASS.
    c1 = 'class C {\n // old wording\n int x = 1;\n}\n'
    c2 = 'class C {\n // New wording entirely.\n int x = 1;\n}\n'
    cases.append(("comments-only change passes", w('c1.cs', c1), w('c2.cs', c2), 0))

    ok = True
    for label, a, b, want in cases:
        import io, contextlib
        buf = io.StringIO()
        argv = sys.argv[:]
        sys.argv = ['x', a, b]
        with contextlib.redirect_stdout(buf):
            got = main()
        sys.argv = argv
        good = (got == want)
        ok &= good
        print(f"  {'ok  ' if good else 'FAIL'}  {label}: rc={got} (want {want})")
    return 0 if ok else 1

if __name__ == '__main__':
    if '--selftest' in sys.argv: sys.exit(selftest())
    sys.exit(main())
