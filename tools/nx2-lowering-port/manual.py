import re
F='Nx2Lowering.Main.cs'
s=open(F).read()
def rep(old,new,count=1):
    global s
    n=s.count(old)
    if n<count: raise Exception('missing %r (%d)'%(old,n))
    s=s.replace(old,new)
s=re.sub(r'B\(\(([\w.\[\]]+)\)\.Value\)',r'(\1).HasValue',s)
rep('uint? active_link;','uint? active_link = null;')
rep('string operand_error;','string operand_error = null;')
rep('var a = half_source(0), b = half_source(1);','HalfSource a = half_source(0), b = half_source(1);')
rep('var a = half_source(0), b = half_source(opcode == 0x233 ? 1U : 2U);','HalfSource a = half_source(0), b = half_source(opcode == 0x233 ? 1U : 2U);')
rep('std.find(verified_traps.begin(), verified_traps.end(), index * 16U) == verified_traps.end()','Array.IndexOf(verified_traps, index * 16U) < 0')
rep('B((!store && (data % (count == 3 ? 4U : count))))','(!store && B(data % (count == 3 ? 4U : count)))')
rep('''dest % (count == 3 ? 4U : count)))))''','''B(dest % (count == 3 ? 4U : count))))))''')
rep('converted += round_up && (normalized & 255U) != 0;','converted += U32(round_up && (normalized & 255U) != 0);')
rep('} else if ((ones = U32(negate_a) + U32(negate_b) + U32(negate_c)) != 0) {','} else if (U32(negate_a) + U32(negate_b) + U32(negate_c) is uint ones && ones != 0) {')
rep('bool complete = !computed_branch && !B(geometry_exit);','bool complete = !computed_branch && geometry_exit == null;')
s=re.sub(r'std\.find\(call_targets\.begin\(\), call_targets\.end\(\),\s*U32\(relocation\.target\)\) == call_targets\.end\(\)','!call_targets.Contains(U32(relocation.target))',s)

def code_sub(pattern, repl, text):
    out=[]
    for line in text.split('\n'):
        parts=re.split(r'("(?:[^"\\]|\\.)*"|//.*$)',line)
        out.append(''.join(p if (p.startswith('"') or p.startswith('//')) else re.sub(pattern,repl,p) for p in parts))
    return '\n'.join(out)
def scoped_rename(decl, old, new, nth=1):
    # rename `old` from the declaration `decl` (its nth occurrence) to the end of the enclosing block
    global s
    pos=-1
    for _ in range(nth):
        pos=s.index(decl,pos+1)
    # enclosing block start
    d=0;k=pos
    while k>0:
        k-=1
        if s[k]=='}': d+=1
        elif s[k]=='{':
            if d==0: break
            d-=1
    d=0;e=k
    while True:
        if s[e]=='{': d+=1
        elif s[e]=='}':
            d-=1
            if d==0: break
        e+=1
    s=s[:pos]+code_sub(r'(?<![\w.])%s(?!\w)'%old,new,s[pos:e])+s[e:]
open(F,'w').write(s)
s=open(F).read()
scoped_rename('uint target = opcode == 0x9c3 ? uniform_register(U32(dest)) : U32(dest);','target','special_target')
scoped_rename('uint count = U32(BitOperations.PopCount(definition.Bits(72, 4)));','count','definition_count')
scoped_rename('uint a = insn.Bits(24, 8), c = insn.Bits(64, 8);','a','permute_a')
scoped_rename('uint combine = insn.Bits(74, 2);','combine','combine_mode')
scoped_rename('uint a = operand(0, false), b = operand(1, false);\n                uint temporary','a','compare_a')
scoped_rename('uint compare_a = operand(0, false), b = operand(1, false);','b','compare_b')
scoped_rename('uint b = opcode == 0xc11 ? uniform_register(insn.Bits(32, 8)) : insn.Bits(32, 8);','b','lea_b')
scoped_rename('uint mask = 1U << (int)(target);','mask','carry_mask')
scoped_rename('bool pending_main_branch = AnyOf(relocations, (Relocation relocation) => {\n                        return !relocation.call && relocation.target > index &&\n                               relocation.target !=','pending_main_branch','pending_geometry_branch')
scoped_rename('bool pending_branch = AnyOf(relocations, (Relocation relocation) => { return relocation.target > index; });','pending_branch','pending_any_branch')
open(F,'w').write(s)
