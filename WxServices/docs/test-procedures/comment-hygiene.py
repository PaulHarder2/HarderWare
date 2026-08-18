#!/usr/bin/env python3
"""Four mechanical guards over a C# comment trim. Proposed by Barry, round 99.

Each caught a real defect in WX-457, and each is cheap enough to run every time.

  usage:  comment-hygiene.py AFTER.cs [BEFORE.cs]      guard 4 needs BEFORE
          comment-hygiene.py --selftest
"""
import re, sys, tempfile, os

def lines(p): return open(p, encoding='utf-8-sig').read().splitlines()

def _is_c(l):   return l.strip().startswith('//')
def _txt(l):    return re.sub(r'^\s*///?/?\s?', '', l).strip()

def g1_lowercase_block_openers(L):
    """A comment block whose first word is lowercase — the signature of a stripped
    leading `WX-nnn: ` stamp. The file held zero before the trim."""
    out=[]
    for i,l in enumerate(L):
        if not _is_c(l): continue
        if i and _is_c(L[i-1]): continue          # not a block leader
        t=_txt(l)
        if not t: continue
        w=t.split()[0]
        if re.match(r'^[a-z]', w) and not re.match(r'^(https?|www)', w) \
           and '(' not in w and w not in ('e.g.','i.e.','cf.'):
            out.append((i+1, t[:70]))
    return out

def g2_doubled_words(L):
    """`the the` — produced when two comment lines are re-flowed into one."""
    out=[]
    for i,l in enumerate(L):
        if not _is_c(l): continue
        m=re.search(r'\b(\w+)\s+\1\b', _txt(l), re.I)
        if m: out.append((i+1, m.group(0)))
    # also across a line join inside one block
    for i in range(len(L)-1):
        if _is_c(L[i]) and _is_c(L[i+1]):
            a=_txt(L[i]).split(); b=_txt(L[i+1]).split()
            if a and b and a[-1].lower()==b[0].lower() and a[-1].isalpha():
                out.append((i+1, f"{a[-1]} / {b[0]} (across join)"))
    return out

def g3_dangling_punctuation(L):
    """A comment block ending on `—`, `:` or `(` — the punctuation belonged to a
    sentence whose object was the deleted reference."""
    out=[]
    for i,l in enumerate(L):
        if not _is_c(l): continue
        if i+1 < len(L) and _is_c(L[i+1]): continue   # not the block's last line
        t=_txt(l).rstrip()
        t=re.sub(r'(</\w+>|/>)$','',t).rstrip()       # ignore a trailing XML tag
        if t.endswith(('—','–',':','(',',')):
            out.append((i+1, t[-60:]))
    return out

def g4_doc_text_changed(A,B):
    """`///` text that changed. Not a defect by itself — a defect only when the
    claim is that doc comments were untouched."""
    da=[_txt(l) for l in A if l.strip().startswith('///')]
    db=[_txt(l) for l in B if l.strip().startswith('///')]
    return sum(1 for x,y in zip(db,da) if x!=y), len(db), len(da)

def run(after, before=None, quiet=False):
    A=lines(after); fails=0
    for name,res in (("lowercase block openers", g1_lowercase_block_openers(A)),
                     ("doubled words",           g2_doubled_words(A)),
                     ("dangling punctuation",    g3_dangling_punctuation(A))):
        if res:
            fails+=1
            if not quiet:
                print(f"  FAIL  {name}: {len(res)}")
                for ln,t in res[:6]: print(f"          L{ln}: {t}")
        elif not quiet: print(f"  ok    {name}: 0")
    if before:
        ch,nb,na=g4_doc_text_changed(A,lines(before))
        if not quiet:
            print(f"  {'FAIL' if ch else 'ok  '}  /// text changed: {ch} line(s)  "
                  f"(counts {nb} -> {na})")
            if ch: print("          -> an 'XML docs untouched' claim would be FALSE")
        if ch: fails+=1
    return fails

def selftest():
    """Every guard must be seen to fail. A guard nobody has watched fail is decoration."""
    base=["// A proper sentence here.","int x = 1;",
          "/// <summary>Fine.</summary>","int y = 2;"]
    cases=[("g1 lowercase opener", ["// compute the thing.","int x = 1;"]),
           ("g2 doubled word",     ["// the the thing is fine.","int x = 1;"]),
           ("g2 across join",      ["// ends with the","// the next line starts.","int x=1;"]),
           ("g3 dangling em-dash", ["// a sentence ending in —","int x = 1;"]),
           ("g3 dangling colon",   ["// a sentence ending in:","int x = 1;"])]
    ok=True
    d=tempfile.mkdtemp()
    clean=os.path.join(d,'clean.cs'); open(clean,'w').write('\n'.join(base))
    n=run(clean, quiet=True)
    print(f"  {'ok  ' if n==0 else 'FAIL'}  CLEAN CONTROL must pass: {n} failure(s)")
    ok &= (n==0)
    for label,src in cases:
        f=os.path.join(d,'m.cs'); open(f,'w').write('\n'.join(base+src))
        n=run(f, quiet=True)
        print(f"  {'ok  ' if n else 'FAIL'}  {label}: detected={bool(n)}")
        ok &= bool(n)
    b=os.path.join(d,'b.cs'); a=os.path.join(d,'a.cs')
    open(b,'w').write("/// original text\nint x=1;")
    open(a,'w').write("/// changed text\nint x=1;")
    ch,_,_=g4_doc_text_changed(lines(a),lines(b))
    print(f"  {'ok  ' if ch else 'FAIL'}  g4 /// text change: detected={bool(ch)}")
    ok &= bool(ch)
    return 0 if ok else 1

if __name__=='__main__':
    if '--selftest' in sys.argv: sys.exit(selftest())
    sys.exit(1 if run(sys.argv[1], sys.argv[2] if len(sys.argv)>2 else None) else 0)
