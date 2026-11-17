import re
s=open('body.cs').read()
def balanced(s,i,o,c):
    d=0
    while True:
        if s[i]==o: d+=1
        elif s[i]==c:
            d-=1
            if d==0: return i
        i+=1
def init_lists(s, pattern, typ):
    out=[];i=0
    pat=re.compile(pattern)
    while True:
        m=pat.search(s,i)
        if not m: out.append(s[i:]);break
        j=balanced(s,m.end()-1,'{','}')
        out.append(s[i:m.end()-1]); out.append('new %s(%s)'%(typ,s[m.end():j])); i=j+1
    return ''.join(out)
s=init_lists(s,r'relocations\.Add\(\{','Relocation')
s=init_lists(s,r'extract_half\(\{','HalfSource')
s=init_lists(s,r'HalfSource\{','HalfSource').replace('HalfSourcenew HalfSource','new HalfSource')
s=s.replace("""        struct HalfSource { uint reg, swizzle; bool absolute, negate; };\n""","")
s=s.replace("control_edges.emplace_back(index, direct_targets[index]);","control_edges.Add((index, direct_targets[index]));")
s=s.replace("control_edges.emplace_back(index, index + 1);","control_edges.Add((index, index + 1));")
s=s.replace("uint? write_count(","uint? write_count(")
s=s.replace("uint?{1U << instruction.Bits(72, 2)}","(uint?)(1U << (int)instruction.Bits(72, 2))")
s=re.sub(r'\(var (\w+)\) =>',r'(\1) =>',s)
s=init_lists(s,r'return \{','HalfSource').replace('return new HalfSource(new HalfSource(','return new HalfSource(')
s=re.sub(r'\bAUTO base\(',r'ulong @base(',s)
s=re.sub(r'(?<![\w.])base\(',r'@base(',s).replace('ulong @@base(','ulong @base(')
def code_sub(pattern, repl, s):
    out=[]
    for line in s.split('\n'):
        parts=re.split(r'("(?:[^"\\]|\\.)*"|//.*$)',line)
        out.append(''.join(p if (p.startswith('"') or p.startswith('//')) else re.sub(pattern,repl,p) for p in parts))
    return '\n'.join(out)
s=code_sub(r'(?<![\w])byte(?![\w])',r'byte_',s)
s=code_sub(r'(?<![\w])out(?![\w])',r'out_',s)
s=s.replace('} else if ((ones = U32(','} else if ((ones = U32(')
s=s.replace('''            [[fallthrough]];
        case 0x305:''','''            goto case 0x305;
        case 0x305:''')
s=s.replace('AUTO invert(uint& source, uint target)','void invert(ref uint source, uint target)')
for v in 'abc':
    s=s.replace('invert(%s, 25'%v,'invert(ref %s, 25'%v)
s=s.replace('} else if (uint ones = U32(negate_a) + U32(negate_b) + U32(negate_c)) {','} else if ((ones = U32(negate_a) + U32(negate_b) + U32(negate_c)) != 0) {')
for n in ['ordinary_operands','valid_cbuf','bind_texture_buffer','copies_link']:
    s=s.replace('AUTO %s('%n,'bool %s('%n)
for n in ['slot','value','to_radians']:
    s=s.replace('AUTO %s('%n,'uint %s('%n)
s=s.replace('AUTO isetp(','ulong isetp(')
s=s.replace('GeometryExitSetupNx2Instructions','Nx2GeometryExit.SetupInstructions')
s=re.sub(r'\.value_or\(([^)]*)\)',r' ?? \1',s)
# header
hdr_end=s.index('    bool bounded_control = !geometry_stage;')
hdr=s[:hdr_end]
new_hdr='''    public static Nx2LoweredProgram LowerToMaxwellWith(Nx2Instruction[] code, uint high_uniforms, Nx2LoweringOptions options) {
    bool geometry_stage = options.Geometry, compact_matrix = options.Tensor;
    var verified_traps = options.VerifiedTraps ?? [];
    var tessellation_stage = options.TessellationStage;
    ulong native_code_address = options.NativeCodeAddress;
    Nx2LoweredProgram result = new();
    // UR28 and up (when used) live just below R216: UR28 at R(216 - high_uniforms).
    uint high_uniform_base = 216 - high_uniforms;
    Vec<Relocation> relocations = new();
    Vec<uint> barrier_tokens = new();
    Vec<uint> call_targets = new();
    Vec<uint> call_links = new();
    bool main_exited = false;
    bool computed_branch = false; // a jump table: the program ends at its self-branch, not an EXIT
    bool uses_uniform_bank = false;
'''
k=hdr.index('    bool uses_async_copy')
rest=hdr[k:]
rest=rest.replace('''    std.optional<GeometryExitEnvelope> geometry_exit;
    if (code.Front().Bits(0, 12) == 0x34e) {
        if (!geometry_stage) return fail(0, "native program counter load outside geometry stage");
        geometry_exit = AnalyzeGeometryExit(code);
        if (!*geometry_exit)
            return fail(geometry_exit.error_offset / 16U, geometry_exit.error);
    }
    List<bool> control_entries(code.Size()), dead_instructions(code.Size()), checked_returns(code.Size());
    List<uint> bounded_links(code.Size(), ~0U), direct_targets(code.Size(), ~0U);
    List<std.pair<uint, uint>> control_edges;
''','''    Nx2GeometryExit geometry_exit = null;
    if (code.Front().Bits(0, 12) == 0x34e) {
        if (!geometry_stage) return fail(0, "native program counter load outside geometry stage");
        geometry_exit = Nx2GeometryExit.Analyze(code);
        if (!geometry_exit.Ok)
            return fail(geometry_exit.ErrorOffset / 16U, geometry_exit.Error);
    }
    bool[] control_entries = new bool[code.Length], dead_instructions = new bool[code.Length], checked_returns = new bool[code.Length];
    uint[] bounded_links = new uint[code.Length], direct_targets = new uint[code.Length];
    Array.Fill(bounded_links, ~0U);
    Array.Fill(direct_targets, ~0U);
    Vec<(uint, uint)> control_edges = new();
''')
rest=rest.replace("AUTO address_of(uint index)","uint address_of(uint index)").replace("AUTO fail(uint index, string reason)","Nx2LoweredProgram fail(uint index, string reason)").replace("AUTO finish(uint index)","Nx2LoweredProgram finish(uint index)").replace("AUTO ordinary_operands(","bool ordinary_operands(")
s=new_hdr+rest+s[hdr_end:]
# geometry exit members
s=s.replace('geometry_exit.handler','geometry_exit.Handler').replace('geometry_exit.has_returns','geometry_exit.HasReturns').replace('geometry_exit.exits','geometry_exit.Exits')
s=re.sub(r'if \(geometry_exit &&',r'if (geometry_exit != null &&',s)
s=re.sub(r'\(!geometry_exit \|\|',r'(geometry_exit == null ||',s)
s=s.replace('GeometryExitSetupInstructions','Nx2GeometryExit.SetupInstructions')
# result fields
for a,b in [('instructions','Instructions'),('source_instruction_offsets','SourceInstructionOffsets'),('source_size','SourceSize'),('required_texture_buffer','RequiredTextureBuffer'),('high_uniform_registers','HighUniformRegisters'),('error_offset','ErrorOffset'),('error','Error')]:
    s=s.replace('result.'+a,'result.'+b)
# RegisterPressure
s=s.replace('RegisterPressure pressure{insn};','Nx2RegisterPressure pressure = new(insn);').replace('PrepareRegisterPressure(','Nx2RegisterPressure.Prepare(')
s=s.replace('pressure.instruction','pressure.Instruction').replace('pressure.prefix','pressure.Prefix').replace('pressure.suffix','pressure.Suffix').replace('pressure.valid','pressure.Valid')
# Tess
s=s.replace('TessellationStage.','Nx2TessellationStage.')
# drop trailing  '};' after local functions -> '}'
s=s.replace('AUTO ','void ')
s=code_sub(r'\? (0x[0-9a-fA-F]+|\d+) : (0x[0-9a-fA-F]+|\d+)(?![\w.])',r'? \1U : \2U',s)
out=open('Nx2Lowering.Main.cs','w')
out.write('''// Port of Lichii's shader_recompiler/frontend/ampere/lower_to_maxwell.h (Eden, GPL-3.0-or-later;
// encoding reference: Mesa NAK sm70_encode.rs, MIT). Translated from C++; keep it close to the
// original so later changes there can be carried over.
#pragma warning disable IDE0011, IDE0055, IDE0063, IDE0018, IDE0059, IDE0060, IDE1006, IDE0045, IDE0046, CS0162
using System;
using System.Collections.Generic;
using System.Numerics;
using static Ryujinx.Graphics.Gpu.Shader.Nx2.Nx2LoweringHelpers;

namespace Ryujinx.Graphics.Gpu.Shader.Nx2
{
static partial class Nx2Lowering
{
''')
out.write(s)
out.write('\n}\n}\n')
