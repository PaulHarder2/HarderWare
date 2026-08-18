#!/usr/bin/env python3
"""AC-1: prove a change is comments-only.

Strips C# comments with a state machine that respects "..", @".." and '..',
then compares the surviving code with whitespace normalised away. If the two
files' code is identical, no behaviour changed.
"""
import sys

def strip_comments(src: str) -> str:
    out=[]; i=0; n=len(src)
    NORMAL,LINE,BLOCK,STR,VERB,CHAR = range(6)
    st=NORMAL
    while i < n:
        c=src[i]; d=src[i+1] if i+1<n else ''
        if st==NORMAL:
            if c=='/' and d=='/': st=LINE; i+=2; continue
            if c=='/' and d=='*': st=BLOCK; i+=2; continue
            if c=='@' and d=='"': out.append(c); out.append(d); st=VERB; i+=2; continue
            if c=='$' and d=='"': out.append(c); out.append(d); st=STR;  i+=2; continue
            if c=='"': out.append(c); st=STR; i+=1; continue
            if c=="'": out.append(c); st=CHAR; i+=1; continue
            out.append(c); i+=1; continue
        if st==LINE:
            if c=='\n': out.append(c); st=NORMAL
            i+=1; continue
        if st==BLOCK:
            if c=='*' and d=='/': st=NORMAL; i+=2; continue
            if c=='\n': out.append(c)
            i+=1; continue
        if st==STR:
            out.append(c)
            if c=='\\' and i+1<n: out.append(d); i+=2; continue
            if c=='"': st=NORMAL
            i+=1; continue
        if st==VERB:
            out.append(c)
            if c=='"':
                if d=='"': out.append(d); i+=2; continue   # "" escape
                st=NORMAL
            i+=1; continue
        if st==CHAR:
            out.append(c)
            if c=='\\' and i+1<n: out.append(d); i+=2; continue
            if c=="'": st=NORMAL
            i+=1; continue
    return ''.join(out)

def code_only(path: str) -> str:
    src=open(path,encoding='utf-8-sig').read()
    lines=[l.strip() for l in strip_comments(src).splitlines()]
    return '\n'.join(l for l in lines if l)

if __name__=='__main__':
    before,after=sys.argv[1],sys.argv[2]
    a,b=code_only(before),code_only(after)
    print(f"  code lines  before={a.count(chr(10))+1}  after={b.count(chr(10))+1}")
    if a==b:
        print("  AC-1 PASS — surviving code is byte-identical; the change is comments-only.")
        sys.exit(0)
    import difflib
    print("  AC-1 FAIL — code differs:")
    for l in list(difflib.unified_diff(a.splitlines(),b.splitlines(),'before','after',lineterm='',n=1))[:40]:
        print("   ",l)
    sys.exit(1)
