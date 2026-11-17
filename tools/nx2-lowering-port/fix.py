import re,subprocess,sys,collections
F='Nx2Lowering.Main.cs'
OPEN='([{'; CLOSE=')]}'
def build():
    out=subprocess.run(['dotnet','build','-c','Release','compare/compare.csproj','--no-restore','-v','q','-clp:NoSummary'],capture_output=True,text=True).stdout
    errs=[]
    for m in re.finditer(r'Main\.cs\((\d+),(\d+)\): error (CS\d+): ([^\[]*)',out):
        errs.append((int(m.group(1)),int(m.group(2)),m.group(3),m.group(4).strip()))
    return sorted(set(errs))
def scan_expr(text, i, stops):
    # from i, return end index (exclusive) of expression, stopping at depth-0 stop tokens
    d=0; j=i
    while j<len(text):
        c=text[j]
        if text.startswith('//',j):
            j=text.index('\n',j); continue
        if c=='"':
            j+=1
            while text[j]!='"': j+= 2 if text[j]=='\\' else 1
        elif c in OPEN: d+=1
        elif c in CLOSE:
            if d==0: return j
            d-=1
        elif d==0:
            for st in stops:
                if text.startswith(st,j):
                    if st in ('<','>','&','|') and text.startswith(st*2,j) and st*2 not in stops: break
                    if st=='=' and (text.startswith('==',j) or text[j-1] in '=!<>'): break
                    if st in ('<','>') and text[j+1]=='=': return j
                    if st=='&' and text.startswith('&&',j): return j
                    if st=='|' and text.startswith('||',j): return j
                    return j
        j+=1
    return j
def strip_ws_end(text,i,j):
    while j>i and text[j-1] in ' \n\t': j-=1
    return j
BOOL_STOPS=['&&','||','?',':',';',',']
def offset(lines,l,c):
    return sum(len(x)+1 for x in lines[:l-1])+c-1
def primary_end(text,i):
    # an identifier chain / call / index / parenthesized group from i
    j=i
    if text[j]=='(':
        d=0
        while True:
            if text[j]=='(': d+=1
            elif text[j]==')':
                d-=1
                if d==0: return j+1
            j+=1
    while j<len(text) and (text[j].isalnum() or text[j] in '_.@'):
        j+=1
    while j<len(text) and text[j] in '([':
        o=text[j]; c=')' if o=='(' else ']'; d=0
        while True:
            if text[j]==o: d+=1
            elif text[j]==c:
                d-=1
                if d==0: break
            j+=1
        j+=1
        while j<len(text) and (text[j].isalnum() or text[j] in '_.'):
            j+=1
    return j
def apply(text, edits):
    # edits: (start,end,newtext) non overlapping applied right to left
    for st,en,nt in sorted(edits,key=lambda e:-e[0]):
        text=text[:st]+nt+text[en:]
    return text
def is_boolish(e):
    e=e.strip()
    if e.startswith('B(') or e in('true','false'): return True
    return False
CONV={'uint':'U32','int':'SZ','ulong':'U64','long':'S64','bool':'B','ushort':'U16','byte':'U8'}
for it in range(40):
    errs=build()
    print('iteration',it,len(errs),collections.Counter(e[2] for e in errs).most_common(8),flush=True)
    if not errs: break
    text=open(F).read(); lines=text.split('\n')
    edits=[]; used=set(); handled=0
    for (l,c,code,msg) in errs:
        i=offset(lines,l,c)
        if any(a<=i<b for a,b in used): continue
        e=None
        if code=='CS0019' and ("'&&'" in msg or "'||'" in msg):
            end=scan_expr(text,i,[';',',','?',':'])
            # split chain into operands at depth 0
            ops=[];d=0;k=i;start=i
            while k<end:
                ch=text[k]
                if ch in OPEN: d+=1
                elif ch in CLOSE: d-=1
                elif d==0 and (text.startswith('&&',k) or text.startswith('||',k)):
                    ops.append((start,k)); start=k+2; k+=2; continue
                k+=1
            ops.append((start,end))
            sub=[]
            for a,b in ops:
                while True:
                    while text[a] in ' \n': a+=1
                    if text.startswith('//',a): a=text.index('\n',a); continue
                    break
                b=strip_ws_end(text,a,b)
                seg=text[a:b]
                if re.search(r'(==|!=|<=|>=|(?<![<-])<(?!<)|(?<![>=])>(?!>))',seg) and not re.search(r'[&|^]',re.sub(r'\([^()]*\)','',seg)) : continue
                if seg.startswith('!') or is_boolish(seg): continue
                sub.append((a,b,'B('+seg+')'))
            if sub:
                edits+=sub; used.add((i,end)); handled+=1
        elif code=='CS0023' and "'!'" in msg:
            if text[i]=='!':
                j=i+1
                while text[j]==' ': j+=1
                end=primary_end(text,j)
                edits.append((j,end,'B('+text[j:end]+')')); used.add((i,end)); handled+=1
        elif code in('CS0029','CS0266','CS1503'):
            m=re.search(r"(?:type '|de ')([\w?]+)' en '([\w?]+)'",msg)
            if m and m.group(2) in CONV:
                end=scan_expr(text,i,[';',',','&&','||','?',':'] if m.group(2)=='bool' else [';',',','?',':'] if code=='CS1503' else [';',','])
                end=strip_ws_end(text,i,end)
                seg=text[i:end]
                if re.match(r'(B|U32|U64|S64|SZ)\(',seg) and primary_end(text,i)==end:
                    continue
                if m.group(1)=='uint?':
                    edits.append((i,end,'('+seg+').Value'))
                else:
                    edits.append((i,end,CONV[m.group(2)]+'('+seg+')'))
                used.add((i,end)); handled+=1
        elif code=='CS0193' and text[i]=='*':
            end=primary_end(text,i+1)
            edits.append((i,end,text[i+1:end]+'.Value')); used.add((i,end)); handled+=1
        elif code=='CS0019' and ("'<<'" in msg or "'>>'" in msg):
            # find the shift operator at depth 0 after i
            end=scan_expr(text,i,[';',',','?',':','&&','||','==','!=','&','|','^'])
            k=i;d=0;pos=None
            while k<end:
                ch=text[k]
                if ch in OPEN: d+=1
                elif ch in CLOSE: d-=1
                elif d==0 and (text.startswith('<<',k) or text.startswith('>>',k)) and not text.startswith('<<=',k) and not text.startswith('>>=',k):
                    pos=k; k+=2; continue
                k+=1
            if pos is not None:
                a=pos+2
                while text[a]==' ': a+=1
                b=scan_expr(text,a,[';',',','?',':','&&','||','==','!=','<','>','&','|','^'])
                b=strip_ws_end(text,a,b)
                seg=text[a:b]
                if not seg.startswith('(int)'):
                    edits.append((a,b,'(int)('+seg+')')); used.add((i,end)); handled+=1
    if not handled:
        for e in errs[:40]: print(e)
        break
    open(F,'w').write(apply(text,edits))
