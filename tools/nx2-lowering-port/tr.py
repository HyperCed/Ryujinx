import re,sys
import os
src=open(os.path.join(os.environ['LICHII'], 'src/shader_recompiler/frontend/ampere/lower_to_maxwell.h')).read().split('\n')
body=src[52:4094]  # lines 53..4094 (1-based): LowerToMaxwellWith
s='\n'.join(body)

T={'u64':'ulong','u32':'uint','s64':'long','s32':'int','u16':'ushort','u8':'byte','s16':'short','s8':'sbyte','size_t':'int','f32':'float','f64':'double'}

H={'u64':'U64','u32':'U32','s64':'S64','s32':'S32','u16':'U16','u8':'U8','size_t':'SZ'}
def balanced(s,i,open_c,close_c):
    # s[i]==open_c ; return index of matching close
    d=0
    while i<len(s):
        c=s[i]
        if c==open_c: d+=1
        elif c==close_c:
            d-=1
            if d==0: return i
        i+=1
    raise Exception('unbalanced')

# TYPE{expr} -> ((CT)(expr))
def brace_casts(s):
    out=[];i=0
    pat=re.compile(r'(?<![A-Za-z0-9_])(u64|u32|s64|s32|u16|u8|size_t)\{')
    while True:
        m=pat.search(s,i)
        if not m: out.append(s[i:]);break
        j=balanced(s,m.end()-1,'{','}')
        inner=s[m.end():j]
        if inner.strip()=='' :
            rep='((%s)0)'%T[m.group(1)]
        else:
            rep='%s(%s)'%(H[m.group(1)],brace_casts(inner))
        out.append(s[i:m.start()]);out.append(rep);i=j+1
    return ''.join(out)
s=brace_casts(s)
s=re.sub(r'static_cast<(u64|u32|s64|s32|u16|u8|size_t)>',lambda m:H[m.group(1)],s)
s=s.replace('static_cast<bool>','B')
s=re.sub(r'static_cast<([A-Za-z_:]+)>',lambda m:'(%s)'%m.group(1).replace('::','.'),s)
s=re.sub(r'(?<![A-Za-z0-9_])(u64|u32|s64|s32|u16|u8|size_t)(?![A-Za-z0-9_])',lambda m:T[m.group(1)],s)
s=re.sub(r'(0x[0-9a-fA-F]+|\d+)ULL\b',r'\1UL',s)
s=re.sub(r'(0x[0-9a-fA-F]+|\d+)LL\b',r'\1L',s)
s=s.replace('std::optional<uint>','uint?').replace('std::nullopt','null')
s=s.replace('std::string_view','string')
s=s.replace('(std::max)','Math.Max').replace('(std::min)','Math.Min').replace('std::max','Math.Max').replace('std::min','Math.Min')
s=s.replace('std::popcount','BitOperations.PopCount').replace('std::countr_zero','BitOperations.TrailingZeroCount').replace('std::countl_zero','BitOperations.LeadingZeroCount')
s=re.sub(r'\.words\[',r'[',s)
s=s.replace('.push_back(','.Add(').replace('.size()','.Size()').replace('.empty()','.Empty()').replace('.back()','.Back()').replace('.front()','.Front()').replace('.clear()','.Clear()').replace('.pop_back()','.PopBack()')
s=re.sub(r'\.insert\(([a-z_.]+)\.end\(\), ([a-z_.]+)\.begin\(\), \2\.end\(\)\)',r'.AddRange(\2)',s)
# for range loops
s=re.sub(r'for \(const auto& \[(\w+), (\w+)\] : ',r'foreach (var (\1, \2) in ',s)
s=re.sub(r'for \(const auto \[(\w+), (\w+)\] : ',r'foreach (var (\1, \2) in ',s)
s=re.sub(r'for \((?:const )?(uint|int|ulong|auto&?|const auto&) (\w+) : \{([^}]*)\}\)',lambda m:'foreach (%s %s in new %s[] {%s})'%('var' if 'auto' in m.group(1) else m.group(1),m.group(2),'uint' if 'auto' in m.group(1) else m.group(1),m.group(3)),s)
s=re.sub(r'for \((?:const )?(uint|int|ulong|auto&?|auto) (\w+) : ([^)]+)\)',lambda m:'foreach (%s %s in %s)'%('var' if 'auto' in m.group(1) else m.group(1),m.group(2),m.group(3)),s)
# algorithms with lambdas
s=re.sub(r'std::(any_of|none_of|all_of)\(\s*([\w.]+)\.begin\(\),\s*\2\.end\(\),\s*',lambda m:{'any_of':'AnyOf(','none_of':'NoneOf(','all_of':'AllOf('}[m.group(1)]+m.group(2)+', ',s)
s=re.sub(r'std::find\(([\w.]+)\.begin\(\),\s*\1\.end\(\),\s*([^)]+?)\)\s*==\s*\1\.end\(\)',r'!\1.Contains(\2)',s)
s=re.sub(r'std::find\(([\w.]+)\.begin\(\),\s*\1\.end\(\),\s*([^)]+?)\)\s*!=\s*\1\.end\(\)',r'\1.Contains(\2)',s)
# named lambdas
s=re.sub(r'(\s*)const auto (\w+) = \[[&=]?\]\s*\{',lambda m:'%sAUTO %s() {'%(m.group(1),m.group(2)),s)
s=re.sub(r'(\s*)auto (\w+) = \[[&=]?\]\s*\{',lambda m:'%sAUTO %s() {'%(m.group(1),m.group(2)),s)
def named(m):
    ret=m.group(3) or 'AUTO'
    ret=ret.strip()
    return '%s%s %s(%s) {'%(m.group(1),ret,m.group(2),m.group(4))
s=re.sub(r'(\s*)const auto (\w+) = \[[&=]?\]\(([^)]*)\)(?:\s*->\s*([\w?<>, ]+))?\s*\{',lambda m:'%s%s %s(%s) {'%(m.group(1),(m.group(4) or 'AUTO').strip(),m.group(2),m.group(3)),s)
s=re.sub(r'(\s*)auto (\w+) = \[[&=]?\]\(([^)]*)\)(?:\s*->\s*([\w?<>, ]+))?\s*\{',lambda m:'%s%s %s(%s) {'%(m.group(1),(m.group(4) or 'AUTO').strip(),m.group(2),m.group(3)),s)
# inline lambdas
s=re.sub(r'\[[&=]?\]\(([^)]*)\)\s*(?:->\s*\w+\s*)?\{',r'(\1) => {',s)
# params
s=re.sub(r'const Instruction& (\w+)',r'Nx2Instruction \1',s)
s=re.sub(r'const Relocation& (\w+)',r'Relocation \1',s)
s=s.replace('Instruction','Nx2Instruction').replace('Nx2Nx2Instruction','Nx2Instruction')
s=re.sub(r'const auto& ',r'var ',s)
s=re.sub(r'auto& ',r'var ',s)
s=re.sub(r'const auto ',r'var ',s)
s=re.sub(r'(?<![\w.])auto ',r'var ',s)
s=re.sub(r'(^|\s|\()const (bool|uint|ulong|long|int|ushort|byte|var|string|uint\?)\b',r'\1\2',s)
s=re.sub(r'constexpr (bool|uint|ulong|long|int)\b',r'const \1',s)
s=s.replace('->','.').replace('::','.')
s=s.replace('std.vector','List')
s=s.replace('sizeof(Nx2Instruction)','16U')
s=re.sub(r'std\.array<([\w?]+), (\d+)> (\w+)\{\};',r'\1[] \3 = new \1[\2];',s)
s=re.sub(r'(?:constexpr |const )?std\.array<([\w?]+), \d+> (\w+)\{([^}]*)\};',r'\1[] \2 = {\3};',s)
s=re.sub(r'std\.array<([\w?]+), \d+>\{([^}]*)\}',r'new \1[] {\2}',s)
s=re.sub(r'std\.array<([\w?]+), (\d+)> (\w+);',r'\1[] \3 = new \1[\2];',s)
s=re.sub(r'\b(uint|int|ulong|bool) (\w+)\[(\d+)\]\{([^}]*)\};',r'\1[] \2 = {\4};',s)
s=re.sub(r'(?<=[ ,])([a-z_]\w*)\{\}(?=[,;])',r'\1 = default',s)
open('body.cs','w').write(s)
