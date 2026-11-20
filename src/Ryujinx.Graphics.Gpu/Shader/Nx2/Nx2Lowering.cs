// Port of Lichii's shader_recompiler/frontend/ampere/lower_to_maxwell.h (Eden, GPL-3.0-or-later;
// encoding reference: Mesa NAK sm70_encode.rs, MIT). Translated from C++; keep it close to the
// original so later changes there can be carried over.
#pragma warning disable IDE0011, IDE0055, IDE0063, IDE0018, IDE0059, IDE0060, IDE1006, IDE0045, IDE0046, CS0162
using System;
using System.Collections.Generic;
using System.Numerics;
using static Ryujinx.Graphics.Gpu.Shader.Nx2.Nx2LoweringHelpers;

namespace Ryujinx.Graphics.Gpu.Shader.Nx2
{
public static partial class Nx2Lowering
{
    public static Nx2LoweredProgram LowerToMaxwellWith(Nx2Instruction[] code, uint high_uniforms, Nx2LoweringOptions options) {
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
    bool uses_async_copy = AnyOf(code, (instruction) => {
        return instruction.Bits(0, 12) == 0xfae;
    });
    foreach (var instruction in code) {
        uint op = instruction.Bits(0, 12);
        uses_uniform_bank |= op == 0xf66 || op == 0xf6c;
        uses_uniform_bank |= op == 0xc3e || op == 0x499 || op == 0x2bf || op == 0x4a4;
        uses_uniform_bank |= op == 0x987 ||
            ((op == 0x387 || op == 0x983) && B(instruction.Bits(91, 1)));
        uses_uniform_bank |= op == 0x3c4 || op == 0x2bd || op == 0x896 || op == 0xf60 || op == 0xc11 || op == 0xd08 || op == 0xc0c || op == 0x986 || op == 0x9a8 || op == 0xc12 || op == 0xc17 || op == 0xe24 || op == 0xc24 || op == 0xd00 || op == 0xd09 || op == 0x9c3 || op == 0xc45 || op == 0x2a4 || op == 0x891 || op == 0x890 || op == 0x290 || op == 0x28c || op == 0x88c || op == 0x287 || op == 0x887 || op == 0x958 || op == 0xab9 || op == 0xabb || op == 0x899 || op == 0xc20 || op == 0xe21 || op == 0xe23 || op == 0xc02 ||
                             op == 0x886 || op == 0x3c2 || op == 0xc82 || op == 0x882 || op == 0x292 || op == 0x892 || op == 0x291 || op == 0x981 || op == 0xc13 || op == 0xc10 || op == 0xc23 || op == 0x526 || op == 0x2be || op == 0x8a4 || op == 0xd05 || (op == 0x984 && B(instruction.Bits(91, 1))) || (op == 0x988 && B(instruction.Bits(91, 1))) || op == 0x89c || op == 0x883 || op == 0x2a5 || op == 0xd06 || op == 0xf63 || op == 0xf66 || op == 0xf69 || op == 0xc0b || op == 0x8a5 || op == 0x299 || op == 0xc09 || op == 0xc07 || (op == 0x81c && B(instruction.Bits(67, 1))) || op == 0xc0a ||
                             (op == 0x83b && B(instruction.Bits(91, 1)));
    }
    uint address_of(uint index) { return 0x50U + index * 8 + ((index + 1) / 3) * 8; };
    Nx2LoweredProgram fail(uint index, string reason) {
        result.Instructions.Clear();
        result.SourceInstructionOffsets.Clear();
        result.ErrorOffset = index * 16U;
        result.Error = reason;
        return result;
    };
    Nx2LoweredProgram finish(uint index) {
        foreach (var relocation in relocations) {
            if (relocation.target > index) return fail(index, "native branch or call leaves captured program");
            uint target = relocation.output_target != ~0U ? relocation.output_target :
                result.SourceInstructionOffsets[SZ(relocation.target)];
            long relative = S64(address_of(target)) - address_of(relocation.output_index) - 8;
            if (relative < -0x800000 || relative > 0x7fffff) return fail(index, "lowered branch distance");
            result.Instructions[relocation.output_index] |= (U64(relative) & 0xffffff) << 20;
        }
        result.SourceSize = (index + 1) * 16U;
        return result;
    };
    if (code.Empty() || code.Size() > 65536) return fail(0, "invalid native program size");
    Nx2GeometryExit geometry_exit = null;
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
    bool bounded_control = !geometry_stage;
    control_entries[0] = true;
    uint? write_count(Nx2Instruction instruction) {
        switch (instruction.Bits(0, 12)) {
        case 0x202: case 0x802: case 0x803: case 0x424: case 0x724: case 0x824: case 0xe24: case 0x227:
        // IMAD (0x224) and its uniform-guard form (0x8a4, lowered to 0x824), USHF (0x899,
        // lowered to one SHR/SHL) and the uniform-register LEA (0xc11, as 0xc12) write only Rd;
        // Ocarina of Time's per-tap gathers and call links are proven through them.
        case 0x224: case 0x8a4: case 0x899: case 0xc11:
        case 0x810: case 0x819: case 0x845: case 0x906: case 0x820:
        case 0x812: case 0x589:
        case 0x212: case 0x207: case 0x807: case 0x208: case 0x808:
        case 0x220: case 0xa20: case 0x221: case 0x421: case 0x621:
        case 0x223: case 0x423: case 0x623: case 0x823: case 0xa23:
        case 0x243: case 0x245: case 0x306:
        case 0x919: case 0xc12: case 0x300: case 0x210: case 0x308:
            return instruction.Bits(16, 8) == 255 ? 0U : 1U;
        case 0x225: case 0x625: case 0x825:
            return 2;
        case 0x305:
            return instruction.Bits(75, 2) == 3 ? 2U : 1U;
        case 0x83b:
            return instruction.Bits(72, 2) == 3 ? null : (uint?)(1U << (int)instruction.Bits(72, 2));
        case 0x381: case 0x981: case 0x984: case 0xb82: {
            uint width = instruction.Bits(73, 3);
            if (width > 6) return null;
            return width == 6 ? 4U : width == 5 ? 2U : 1U;
        }
        case 0x20c: case 0x80c: case 0x81c: case 0x387: case 0x388: case 0xb1d: case 0x348: case 0x918:
        case 0x886: case 0x95b:
            return 0;
        default:
            return null;
        }
    };
    bool ordinary_operands(Nx2Instruction instruction) {
        foreach (uint bit in new uint[] {16U, 24U, 32U, 64U}) {
            if (bit == 32 && instruction.Bits(9, 3) != 1) continue;
            uint reg = instruction.Bits(bit, 8);
            if (reg >= (uses_uniform_bank ? high_uniform_base : 246U) && reg != 255) return false;
        }
        return true;
    };
    for (uint index = 0; index < code.Size(); ++index) {
        var instruction = code[index];
        uint opcode = instruction.Bits(0, 12);
        if (opcode == 0x944 || opcode == 0x945 || opcode == 0x947) {
            if (opcode == 0x944 && index == 0) bounded_control = false;
            uint mode = instruction[2] & ~0x3ffffU;
            if (mode != (opcode == 0x944 ? 0x03c00000U : 0x03800000U) &&
                !(opcode == 0x947 && mode == 0x0b800000)) bounded_control = false;
            ulong raw = (instruction[1] & (opcode == 0x947 ? ~3U : ~0U)) |
                            (U64(instruction[2] & 0x3ffff) << 32);
            long relative = S64(raw << 14) >> 14;
            long target = S64(index) * 16 + 16 + relative;
            if (target < 0 || B(U32(target % 16)) || U64(target / 16) >= code.Size()) {
                bounded_control = false;
            } else {
                direct_targets[index] = U32(target / 16);
                control_entries[direct_targets[index]] = true;
                control_edges.Add((index, direct_targets[index]));
            }
            if (opcode == 0x944 && index + 1 < code.Size()) {
                control_entries[index + 1] = true;
                control_edges.Add((index, index + 1));
            }
        } else if ((opcode & 0x1ff) >= 0x140 && (opcode & 0x1ff) <= 0x15f &&
                   opcode != 0x941 && opcode != 0x942 && opcode != 0x948 &&
                   opcode != 0x348 && opcode != 0x94d && opcode != 0x950 && opcode != 0x95b) {
            bounded_control = false;
        }
    }
    for (uint index = 1; bounded_control && index < code.Size(); ++index) {
        if (code[index].Bits(0, 12) != 0x944) continue;
        if (code[index][0] != 0x7944) { bounded_control = false; break; }
        bool[] overwritten = new bool[256];
        uint setup = index;
        for (uint prior = index; prior-- > 0;) {
            var instruction = code[prior];
            uint destination = instruction.Bits(16, 8);
            if ((instruction[0] & ~0x00ff0000U) == 0x00007802 &&
                instruction[1] == (index + 1) * 16 && instruction[2] == 0xf00 &&
                !B((instruction[3] & 0xc00001ffU)) && ordinary_operands(instruction) &&
                destination < 246 && !overwritten[destination]) {
                bounded_links[index] = destination;
                setup = prior;
                break;
            }
            var count = write_count(instruction);
            if (!(count).HasValue || !ordinary_operands(instruction) || destination + count.Value > 255) break;
            for (uint component = 0; component < count.Value; ++component)
                overwritten[destination + component] = true;
        }
        if (bounded_links[index] == ~0U) { bounded_control = false; break; }
        for (uint entry = setup + 1; entry <= index; ++entry)
            if (control_entries[entry]) bounded_control = false;
    }
    for (uint index = 0; bounded_control && index < code.Size(); ++index) {
        if (code[index].Bits(0, 12) != 0x944) continue;
        uint link = bounded_links[index], target = direct_targets[index];
        if (target == 0) { bounded_control = false; break; }
        var previous = code[target - 1];
        uint previous_opcode = previous.Bits(0, 12);
        if (previous.Bits(12, 4) != 7 || previous.Bits(87, 4) != 7 ||
            (previous_opcode != 0x947 && previous_opcode != 0x94d && previous_opcode != 0x950)) {
            bounded_control = false;
            break;
        }
        foreach (var (source, destination) in control_edges)
            if (destination == target && code[source].Bits(0, 12) != 0x944) bounded_control = false;
        bool[] aliases = new bool[256];
        aliases[link] = true;
        bool returned = false;
        for (uint pc = target; pc < code.Size(); ++pc) {
            var instruction = code[pc];
            if (pc != target && control_entries[pc]) break;
            if (instruction.Bits(0, 12) == 0x950) {
                ulong raw = instruction[1] | (U64(instruction[2] & 0x3ffff) << 32);
                long relative = S64(raw << 14) >> 14;
                returned = (instruction[0] & 0x00ffffffU) == 0x00007950 &&
                           (instruction[2] & ~0x3ffffU) == 0x03c00000 &&
                           !B((instruction[3] & 0xc00001ffU)) &&
                           S64(pc) * 16 + 16 + relative == 0 &&
                           aliases[instruction.Bits(24, 8)];
                if (returned) checked_returns[pc] = true;
                break;
            }
            var count = write_count(instruction);
            uint destination = instruction.Bits(16, 8);
            if (!(count).HasValue || !ordinary_operands(instruction) || destination + count.Value > 255 ||
                (B(count.Value) && destination <= link && link < destination + count.Value)) break;
            bool copy = false;
            if (instruction.Bits(12, 4) == 7) {
                if ((instruction[0] & 0xff00ffffU) == 0x00007202U &&
                    instruction[1] < 256 && instruction[2] == 0xf00)
                    copy = aliases[instruction[1]];
                if ((instruction[0] & 0xff00ffffU) == 0xff007224U &&
                    instruction[1] == 0xff && (instruction[2] & ~0xffU) == 0x078e0000)
                    copy = aliases[instruction.Bits(64, 8)];
            }
            for (uint component = 0; component < count.Value; ++component)
                aliases[destination + component] = component == 0 && copy;
        }
        if (!returned) bounded_control = false;
    }
    for (uint index = 0; bounded_control && index < code.Size(); ++index)
        if (code[index].Bits(0, 12) == 0x950 && !checked_returns[index]) bounded_control = false;
    for (uint index = 0; bounded_control && index < code.Size(); ++index) {
        var instruction = code[index];
        uint target = direct_targets[index];
        if (instruction.Bits(0, 12) != 0x947 || instruction.Bits(12, 4) != 7 ||
            instruction.Bits(32, 2) == 2 ||
            B(instruction.Bits(16, 8)) || instruction.Bits(87, 4) != 7 || target <= index ||
            target == ~0U || B((instruction[3] & 0xc00001ffU)) ||
            (B(instruction.Bits(91, 1)) ? instruction.Bits(24, 8) >= 64 : instruction.Bits(24, 8) != 0))
            continue;
        bool skipped_call = false;
        for (uint pc = index + 1; pc < target; ++pc)
            skipped_call |= code[pc].Bits(0, 12) == 0x944;
        if (!skipped_call) continue;
        bool entered = false;
        foreach (var (source, destination) in control_edges)
            entered |= index < destination && destination < target &&
                       !(index < source && source < target);
        if (!entered)
            for (uint pc = index + 1; pc < target; ++pc) dead_instructions[pc] = true;
    }
    for (uint index = 0; index < code.Size(); ++index) {
        var insn = code[index];
        result.SourceInstructionOffsets.Add(U32(result.Instructions.Size()));
        if (dead_instructions[index]) continue;
        if (geometry_exit != null && (index < Nx2GeometryExit.SetupInstructions ||
            index * 16U == geometry_exit.Handler + 16U))
            continue;
        uint opcode = insn.Bits(0, 12);
        if (insn.Bits(12, 4) == 15) continue; // @!PT: never executes (compiler padding)
        uint? active_link = null;
        uint active_entry = 0;
        if (main_exited && (geometry_exit == null || index * 16U < geometry_exit.Handler)) {
            for (int call = 0; call < call_targets.Size(); ++call) {
                if (call_targets[call] <= index && call_targets[call] >= active_entry) {
                    active_entry = call_targets[call];
                    active_link = call_links[call];
                }
            }
            // RET, BRA, NOP and ISETP/FSETP (predicate outputs) have no destination GPR; bits 16..23
            // are not a write to R0. IMAD.WIDE writes a register pair.
            bool no_destination = opcode == 0x950 || opcode == 0x947 || opcode == 0x918 ||
                                        (opcode & 0x1ff) == 0x00c || (opcode & 0x1ff) == 0x00b;
            uint written = write_count(insn) ?? 1;
            if ((active_link).HasValue && !no_destination &&
                insn.Bits(16, 8) <= active_link.Value && active_link.Value < insn.Bits(16, 8) + written)
                return fail(index, "native callee overwrites link register");
        }
        Nx2RegisterPressure pressure = new(insn);
        bool requires_shadow = false;
        foreach (uint bit in new uint[] {16U, 24U, 32U, 64U}) {
            if (opcode == 0x81c) break;
            // Bits 32-39 name a register in register forms and in these opcodes: TEX/TLD, TXD's
            // gradients, and the data operand of the stores, ISBEWR (0x927), the global atomic
            // (0x9a8), RED (0x98e) and SURED (0x59f), all listed with operand 32 in RegisterPressure.
            if (bit == 32 && insn.Bits(9, 3) != 1 && opcode != 0xb60 && opcode != 0xb66 && opcode != 0xb6c && opcode != 0xf6c && opcode != 0x986 && opcode != 0x987 && opcode != 0xf66 && opcode != 0x988 && opcode != 0x927 && opcode != 0x9a8 && opcode != 0x98e && opcode != 0x59f) continue;
            uint reg = insn.Bits(bit, 8);
            if (reg >= (uses_uniform_bank ? high_uniform_base : 246U) && reg < 255)
                requires_shadow = true;
        }
        if (requires_shadow) {
            if (B(uses_uniform_bank) && B((high_uniform_base & 3))) {
                // Shadowing works in aligned groups of four: lower again with the high uniform
                // base rounded down to a multiple of four (LowerToMaxwell retries).
                result.HighUniformRegisters =
                    Math.Max(result.HighUniformRegisters, (high_uniforms + 3) & ~3U);
            }
            if (!uses_uniform_bank || B((high_uniform_base & 3)))
                return fail(index, "native program uses lowering scratch registers");
            pressure = Nx2RegisterPressure.Prepare(insn, high_uniform_base);
            if (!pressure.Valid) return fail(index, "native register shadowing mode");
            insn = pressure.Instruction;
            result.Instructions.AddRange(pressure.Prefix);
        }
        int instruction_start = SZ(result.Instructions.Size());
        int relocation_start = SZ(relocations.Size());
        // UR0-UR21 live in R216-R237 and UR22-UR27 in R240-R245 (R239 holds the uniform
        // predicates), so even/odd pairs stay adjacent; UR28 and up below R216. Sites that
        // still check reg < 16 rely on UR0-UR15 being R216-R231.
        uint uniform_register(uint reg) {
            if (reg < 22) return 216 + reg;
            if (reg < 28) return 218 + reg;
            if (reg == 63) return 255;
            if (reg < 63 && reg - 28 < high_uniforms) return high_uniform_base + (reg - 28);
            if (reg < 63) result.HighUniformRegisters = Math.Max(result.HighUniformRegisters, (reg - 26) & ~1U);
            return ~0U;
        };
        bool rewritten_uniform = false;
        bool uniform_guard = false;
        uint uniform_output = 7; // ULOP3's UPn output: computed in P(6-n), then merged into R239
        if (opcode == 0x2a4 || opcode == 0x4a4 || opcode == 0x8a4 || opcode == 0x891 || opcode == 0x890 || opcode == 0x290 ||
            opcode == 0x292 || opcode == 0x892 || opcode == 0x291 || opcode == 0x896 || opcode == 0x8a5) {
            if (insn.Bits(12, 4) != 7 && opcode != 0x292 && opcode != 0x892) {
                bool guarded_add = (opcode == 0x290 || opcode == 0x890) &&
                    !B(insn.Bits(74, 1)) && insn.Bits(81, 6) == 0x3f &&
                    insn.Bits(77, 4) == 15 && insn.Bits(87, 4) == 15;
                bool guarded_lea = (opcode == 0x291 || opcode == 0x891) &&
                    !B(insn.Bits(74, 1)) && !B(insn.Bits(80, 1)) && insn.Bits(81, 3) == 7;
                bool guarded_multiply = opcode == 0x2a4 &&
                    (insn[2] & ~0x080002ffU) == 0x078e0000U;
                if (!guarded_add && !guarded_lea && !guarded_multiply)
                    return fail(index, "native uniform arithmetic predication");
            }
            uniform_guard = insn.Bits(12, 4) != 7;
            rewritten_uniform = true;
            uint dst = uniform_register(insn.Bits(16, 8));
            uint a = uniform_register(insn.Bits(24, 8));
            uint c = uniform_register(insn.Bits(64, 8));
            if (dst == ~0U || a == ~0U || c == ~0U) return fail(index, "native uniform arithmetic register");
            insn[0] = (insn[0] & 0xffff) | (dst << 16) | (a << 24);
            insn[2] = (insn[2] & ~0x080000ffU) | c;
            if (opcode == 0x2a4 || opcode == 0x290 || opcode == 0x292 || opcode == 0x291) {
                uint b = uniform_register(insn.Bits(32, 8));
                if (b == ~0U) return fail(index, "native uniform arithmetic second register");
                insn[1] = (insn[1] & ~0xffU) | b;
            }
            if ((opcode == 0x292 || opcode == 0x892) && insn.Bits(80, 4) != 0xe) {
                if (B(insn.Bits(80, 1))) return fail(index, "native uniform logic predicate output");
                uniform_output = insn.Bits(81, 3);
                insn[2] = (insn[2] & ~(7U << 17)) | ((6 - uniform_output) << 17);
            }
            opcode = opcode == 0x2a4 ? 0x224 : opcode == 0x4a4 ? 0x424 : opcode == 0x8a4 ? 0x824 : opcode == 0x891 ? 0x811 : opcode == 0x890 ? 0x810
                   : opcode == 0x292 ? 0x212 : opcode == 0x892 ? 0x812 : opcode == 0x291 ? 0x211
                   : opcode == 0x896 ? 0x816 : opcode == 0x8a5 ? 0x825U : 0x210U;
        }
        // 0x899 (USHF) lowers to one SHR/SHL, so a uniform guard is swapped in like these.
        bool uniform_predication = opcode == 0xabb || opcode == 0xab9 || opcode == 0xc82 || opcode == 0x882 ||
                                         opcode == 0x899 || opcode == 0x299 || opcode == 0x499;
        uint native_predicate = insn.Bits(12, 4);
        bool guarded_uniform = (uniform_predication || uniform_guard) && native_predicate != 7;
        // A uniform guard UPn lives in R239 bit 6-n and is swapped into P(6-n) for the op.
        uint guard_slot = 6 - (native_predicate & 7);
        ulong predicate = U64(guarded_uniform ? ((native_predicate & 8) | guard_slot) : native_predicate) << 16;
        ulong dest = insn.Bits(16, 8);
        ulong @base(ulong opcode_bits) { return opcode_bits | predicate | dest; };
        // NVN2 fullscreen vertex shaders read bank 24, which the game never binds (the console reads
        // zeros from an unbound bank); Maxwell has 18, so read it from bank 17, also never bound.
        uint bank() {
            uint value = insn.Bits(54, 5);
            return value == 24 ? 17U : value;
        };
        ulong cbuf() {
            return (U64(insn.Bits(38, 16) / 4) << 20) | (U64(bank()) << 34);
        };
        bool valid_cbuf() {
            return !B(insn.Bits(91, 1)) && bank() < 18 &&
                   (insn.Bits(38, 16) % 4) == 0;
        };
        string operand_error = null;
        void emit(ulong word) { result.Instructions.Add(word); };
        // Bound Maxwell texture instructions share one descriptor constant buffer per program.
        bool bind_texture_buffer(uint binding) {
            if (result.RequiredTextureBuffer != ~0U && result.RequiredTextureBuffer != binding)
                return false;
            result.RequiredTextureBuffer = binding;
            return true;
        };
        // P(guard slot) while a uniform guard is swapped in; P6 for lowerings that use it as
        // scratch (R2UR, MATCH, HFMA2.RELU), which save and restore it with the defaults below.
        uint guard_mask = guard_slot < 7 ? 1U << (int)(guard_slot) : 0U;
        void save_predicate(uint mask = 1U << 6) {
            emit(0x38e8000000070000UL | 253 | (U64(255) << 8) | (U64(mask) << 20));
        };
        void restore_predicate(uint mask = 1U << 6) {
            emit(0x38f0000000070000UL | (U64(253) << 8) | (U64(mask) << 20));
        };
        if (guarded_uniform) {
            save_predicate(guard_mask);
            emit(0x38f0000000070000UL | (U64(239) << 8) | (U64(guard_mask) << 20));
        }
        uint output_mask = uniform_output == 7 ? 0U : 1U << (int)((6 - uniform_output));
        if (B(output_mask)) {
            // Keep the guest P(6-n) in R254 and start from the current UPn (a false guard keeps it).
            emit(0x38e8000000070000UL | 254 | (U64(255) << 8) | (U64(output_mask) << 20));
            emit(0x38f0000000070000UL | (U64(239) << 8) | (U64(output_mask) << 20));
        }
        // Pd = LUT(a, b, c) over predicate operands (index | negate << 3): each input becomes
        // 0 or ~0 (SEL), and LOP3 sets Pd when the result is nonzero.
        void predicate_lut(uint lut, uint a, uint b, uint c, uint target,
                                       bool uniform_c = false, uint second_target = 7, uint second_lut = 0) {
            uint[] inputs = {a, b, c};
            for (uint i = 0; i < 3; ++i) {
                if (i == 2 && uniform_c) {
                    // UPn from R239 bit 6-n as 0 or ~0 (UPT is ~0); a negation flips the table.
                    if ((c & 7) == 7) {
                        emit(0x0100000000000000UL | predicate | 252 | (U64(15) << 12) | (U64(~0U) << 20));
                    } else {
                        emit(0x3800000000000000UL | predicate | 252 | (U64(239) << 8) |
                             (U64((6 - (c & 7)) | (1U << 8)) << 20));
                        emit(0x5c10000000000000UL | predicate | 252 | (U64(255) << 8) |
                             (U64(252) << 20) | (U64(1) << 48));
                    }
                    if (B(c & 8)) {
                        lut = ((lut & 0x55) << 1) | ((lut & 0xaa) >> 1);
                        second_lut = ((second_lut & 0x55) << 1) | ((second_lut & 0xaa) >> 1);
                    }
                    continue;
                }
                emit(0x38a0000000000000UL | predicate | (250 + i) | (U64(255) << 8) |
                     (U64(0x7ffff) << 20) | (U64(1) << 56) | (U64(inputs[i] ^ 8U) << 39));
            }
            if (second_target != 7) {
                uint first_mask = target == 7 ? 0U : 1U << (int)(target);
                uint second_mask = 1U << (int)(second_target);
                for (uint i = 0; i < 2; ++i) {
                    uint reg = 248 + i;
                    emit(0x5be0000000000000UL | predicate | reg | (U64(250) << 8) |
                         (U64(251) << 20) | (U64(i == 0 ? lut : second_lut) << 28) |
                         (U64(252) << 39) | (U64(7) << 48));
                    emit(0x0400000000000000UL | predicate | reg | (U64(reg) << 8) |
                         (U64(i == 0 ? first_mask : second_mask) << 20));
                }
                emit(0x5c40000000000000UL | predicate | 248 | (U64(248) << 8) |
                     (U64(249) << 20) | (U64(1) << 41) | (U64(7) << 48));
                emit(0x38f0000000000000UL | predicate | (U64(248) << 8) |
                     (U64(first_mask | second_mask) << 20));
                return;
            }
            emit(0x5be0000000000000UL | predicate | 255 | (U64(250) << 8) | (U64(251) << 20) |
                 (U64(lut) << 28) | (U64(3) << 36) | (U64(252) << 39) | (U64(target) << 48));
        };
        void mov_imm(uint reg, uint value) {
            emit(0x0100000000000000UL | predicate | reg | (U64(15) << 12) | (U64(value) << 20));
        };
        uint materialize(uint kind, uint reg, uint scratch, bool absolute, bool negate) {
            if (kind == 3) {
                uint mapped = uniform_register(reg);
                if (mapped == ~0U) { operand_error = "native uniform source register"; return 255; }
                reg = mapped;
            } else if (kind == 1) {
                mov_imm(scratch, insn[1]);
                reg = scratch;
            } else if (kind == 2) {
                if (!valid_cbuf()) { operand_error = "native constant buffer address"; return 255; }
                emit(0x4c98000000000000UL | predicate | scratch | (U64(15) << 39) | cbuf());
                reg = scratch;
            }
            if (absolute || negate) {
                if (reg != scratch) emit(0x5c98000000000000UL | predicate | scratch | (U64(reg) << 20) | (U64(15) << 39));
                if (absolute) emit(0x0400000000000000UL | predicate | scratch | (U64(scratch) << 8) | (U64(0x7fffffff) << 20));
                if (negate) emit(0x0400000000000000UL | predicate | scratch | (U64(scratch) << 8) | (U64(0x80000000) << 20) | (U64(2) << 53));
                reg = scratch;
            }
            return reg;
        };
        uint operand(uint which, bool floating = true) {
            uint form = insn.Bits(9, 3);
            uint reg = default, kind = default, abs = default, neg = default;
            if (which == 0) {
                reg = insn.Bits(24, 8); abs = insn.Bits(73, 1); neg = insn.Bits(72, 1);
            } else {
                if (form != 1 && form != 2 && form != 3 && form != 4 && form != 5 && form != 6 && form != 7) {
                    operand_error = "native ALU source form"; return 255;
                }
                bool wide = which == 1 ? (form == 4 || form == 5 || form == 6) : (form == 2 || form == 3 || form == 7);
                if (wide) {
                    kind = form == 6 || form == 7 ? 3 : (form == 4 || form == 2) ? 1U : 2U;
                    if (kind == 3) reg = insn.Bits(32, 8);
                    if (kind == 2 || kind == 3) { abs = insn.Bits(62, 1); neg = insn.Bits(63, 1); }
                } else {
                    bool upper = which == 1 ? (form == 2 || form == 3 || form == 7) : true;
                    reg = insn.Bits(upper ? 64U : 32U, 8);
                    abs = insn.Bits(upper ? 74U : 62U, 1); neg = insn.Bits(upper ? 75U : 63U, 1);
                }
            }
            return materialize(kind, reg, 250 + which, B(floating) && B(abs), B(floating) && B(neg));
        };
        HalfSource half_source(uint which) {
            if (which == 0) return new HalfSource(insn.Bits(24, 8), insn.Bits(74, 2),
                                    insn.Bits(73, 1) != 0, insn.Bits(72, 1) != 0);
            uint form = insn.Bits(9, 3);
            bool wide = which == 1 ? (form == 4 || form == 5) : (form == 2 || form == 3);
            if (wide) {
                bool immediate = form == 2 || form == 4;
                uint reg = materialize(immediate ? 1U : 2U, 255, 250 + which, false, false);
                return new HalfSource(reg, immediate ? 0U : insn.Bits(60, 2),
                        !immediate && B(insn.Bits(62, 1)), !immediate && B(insn.Bits(63, 1)));
            }
            bool upper = which == 2 || form == 2 || form == 3;
            return new HalfSource(insn.Bits(upper ? 64U : 32U, 8), insn.Bits(upper ? 81U : 60U, 2),
                    insn.Bits(upper ? 83U : 62U, 1) != 0, insn.Bits(upper ? 84U : 63U, 1) != 0);
        };
        void extract_half(HalfSource source, uint lane, uint target) {
            bool high = source.swizzle == 3 || (source.swizzle == 0 && lane == 1);
            emit(0x5ca8000000000000UL | predicate | target | (U64(2) << 8) |
                 (U64(source.swizzle == 1 ? 2U : 1U) << 10) | (U64(source.reg) << 20) |
                 (U64(high) << 41) | (U64(source.negate) << 45) | (U64(source.absolute) << 49));
        };
        void xmad(uint target, uint a, uint b, uint c, bool a_high,
                              bool b_high, uint mode, bool immediate) {
            emit((immediate ? 0x3600000000000000UL : 0x5b00000000000000UL) |
                 predicate | target | (U64(a) << 8) |
                 (U64(immediate ? ((b >> (int)((b_high ? 16U : 0U))) & 0xffff) : b) << 20) |
                 (U64(!immediate && b_high) << 35) | (U64(c) << 39) |
                 (U64(mode) << 50) | (U64(a_high) << 53));
        };
        void multiply_low(uint a, uint b, uint c, bool immediate) {
            xmad(250, a, b, c, false, false, 0, immediate);
            xmad(251, a, b, 255, true, false, 0, immediate);
            xmad(251, a, b, 251, false, true, 0, immediate);
            emit(@base(0x5c18000000000000UL) | (U64(251) << 8) |
                (U64(250) << 20) | (U64(16) << 39));
        };
        void right_shift_u64_low(uint target, uint low, uint high, uint shift) {
            mov_imm(250, 32);
            emit(0x5c10000000000000UL | predicate | 251 | (U64(250) << 8) |
                 (U64(shift) << 20) | (U64(1) << 48));
            emit(0x5c28000000000000UL | predicate | 250 | (U64(low) << 8) | (U64(shift) << 20));
            emit(0x5c48000000000000UL | predicate | 251 | (U64(high) << 8) | (U64(251) << 20));
            emit(0x5c40000000000000UL | predicate | 250 | (U64(250) << 8) |
                 (U64(251) << 20) | (U64(1) << 41) | (U64(7) << 48));
            emit(0x1c00000000000000UL | predicate | 251 | (U64(shift) << 8) |
                 (U64(0xffffffe0U) << 20));
            emit(0x5c28000000000000UL | predicate | 251 | (U64(high) << 8) | (U64(251) << 20));
            emit(0x5c40000000000000UL | predicate | target | (U64(250) << 8) |
                 (U64(251) << 20) | (U64(1) << 41) | (U64(7) << 48));
        };
        switch (opcode) {
        case 0x304: { // F2F.F16.F32, scalar round-to-nearest conversion.
            if (insn[2] != 0x00200800 || B(insn.Bits(24, 8)) ||
                B((insn[1] & ~0xc00000ffU)))
                return fail(index, "native scalar float conversion mode");
            emit(@base(0x5ca8000000000000UL) | (U64(1) << 8) | (U64(2) << 10) |
                 (U64(insn.Bits(32, 8)) << 20) | (U64(insn.Bits(62, 1)) << 49) |
                 (U64(insn.Bits(63, 1)) << 45));
            break;
        }
        case 0xc3e: {
            uint source = uniform_register(insn.Bits(32, 8));
            if (insn.Bits(24, 8) != 255 || B((insn[1] & ~0xffU)) ||
                insn[2] != 0x080000ff || B((insn[3] & 0xc00001ffU)) || source == ~0U)
                return fail(index, "native uniform float packing mode");
            emit(@base(0x5ca8000000000000UL) | (U64(1) << 8) | (U64(2) << 10) |
                 (U64(source) << 20));
            emit(0x5800000000000000UL | predicate | 250 | (U64(source) << 8) |
                 (U64(source) << 20) | (U64(7) << 39) | (U64(8) << 48));
            mov_imm(251, 0x7e00);
            emit(0x5c40000000000000UL | predicate | 250 | (U64(250) << 8) |
                 (U64(251) << 20) | (U64(7) << 48));
            emit(@base(0x5c40000000000000UL) | (dest << 8) | (U64(250) << 20) |
                 (U64(1) << 41) | (U64(7) << 48));
            break;
        }
        case 0x23e: case 0xa3e: { // F2FP.PACK_AB / MERGE_C, round-to-nearest
            if (B(insn.Bits(62, 2)) || B(insn.Bits(73, 5)) || B(insn.Bits(79, 2)) ||
                B(insn.Bits(81, 10)) || B(insn.Bits(91, 1))) {
                return fail(index, "native float packing modifiers");
            }
            bool merge = B(insn.Bits(78, 1));
            if (B(insn.Bits(72, 1))) return fail(index, "native float packing selector");
            uint a = merge ? operand(2, false) : operand(0, false);
            uint b = operand(1, false);
            // Canonical F2F writes the converted half in its low 16 bits.
            if (merge) {
                emit(0x5c98000000000000UL | predicate | 250 | (U64(a) << 20) | (U64(15) << 39));
            } else {
                emit(0x5ca8000000000000UL | predicate | 250 | (U64(1) << 8) |
                     (U64(2) << 10) | (U64(a) << 20));
            }
            emit(0x5ca8000000000000UL | predicate | 251 | (U64(1) << 8) |
                 (U64(2) << 10) | (U64(b) << 20));
            emit(@base(0x5c18000000000000UL) | (U64(250) << 8) | (U64(251) << 20) | (U64(16) << 39));
            break;
        }
        case 0x232: case 0x832: case 0xa32: { // HMUL2
            if (B(insn.Bits(78, 2)) || B(insn.Bits(81, 10)) || B(insn.Bits(91, 1)))
                return fail(index, "native half multiply mode");
            HalfSource a = half_source(0), b = half_source(1);
            uint precision = B(insn.Bits(76, 1)) ? 2 : insn.Bits(80, 1);
            emit(@base(0x5d08000000000000UL) | (U64(a.reg) << 8) | (U64(b.reg) << 20) |
                 (U64(b.swizzle) << 28) | (U64(b.absolute) << 30) | (U64(a.negate != b.negate) << 31) |
                 (U64(insn.Bits(77, 1)) << 32) | (U64(precision) << 39) |
                 (U64(a.absolute) << 44) | (U64(a.swizzle) << 47));
            break;
        }
        case 0x230: case 0x430: case 0x630: { // HADD2
            if (B(insn.Bits(76, 1)) || B(insn.Bits(79, 1)) || B(insn.Bits(81, 10)) || B(insn.Bits(91, 1)))
                return fail(index, "native half add mode");
            var a = half_source(0);
            var b = half_source(opcode == 0x230 ? 1U : 2U);
            if (B(insn.Bits(78, 1))) {
                if (a.reg != 255 || !a.negate || B(a.absolute) || a.swizzle != 0 ||
                    b.swizzle < 2 || B(insn.Bits(77, 1)) || B(insn.Bits(80, 1)))
                    return fail(index, "native float-output half add mode");
                extract_half(b, 0, U32(dest));
            } else {
                emit(@base(0x5d10000000000000UL) | (U64(a.reg) << 8) | (U64(b.reg) << 20) |
                     (U64(b.swizzle) << 28) | (U64(b.absolute) << 30) | (U64(b.negate) << 31) |
                     (U64(insn.Bits(77, 1)) << 32) | (U64(insn.Bits(80, 1)) << 39) |
                     (U64(a.negate) << 43) | (U64(a.absolute) << 44) | (U64(a.swizzle) << 47));
            }
            break;
        }
        case 0x231: case 0x431: case 0x631: case 0x831: case 0xa31: { // HFMA2
            bool relu = insn.Bits(79, 1) != 0;
            // With .RELU, bits 87-90 are a predicate operand: the clamp applies only where it holds.
            if (B(insn.Bits(78, 1)) || B(insn.Bits(85, 1)) || B(insn.Bits(91, 1)) ||
                (relu ? insn.Bits(77, 1) != 0 : insn.Bits(87, 4) != 0))
                return fail(index, "native half fused multiply-add mode");
            if (relu && native_predicate != 7) {
                // Guard the whole expansion before its temporary P6 updates.
                relocations.Add(new Relocation(U32(result.Instructions.Size()), index + 1));
                emit(0xe24000000000000fUL | (U64(native_predicate ^ 8U) << 16));
                predicate = U64(7) << 16;
            }
            var a = half_source(0);
            var b = half_source(1);
            var c = half_source(2);
            if (B(insn.Bits(86, 1))) {
                if (b.swizzle != 0 || b.absolute || b.reg >= 250)
                    return fail(index, "native half mixed-sign source mode");

                // H0_NH1 swaps the FP16 halves and negates the new low half.
                emit(0x36f8000000000000UL | predicate | 251 | (U64(b.reg) << 8) |
                     (U64(16) << 20) | (U64(b.reg) << 39));
                emit(0x0400000000000000UL | predicate | 251 | (U64(251) << 8) |
                     (U64(0x8000) << 20) | (U64(2) << 53));
                b.reg = 251;
            }
            if (b.absolute || c.absolute || (a.absolute && a.reg >= 248))
                return fail(index, "native half fused multiply-add absolute source");
            if (a.absolute) {
                emit(0x0400000000000000UL | predicate | 250 | (U64(a.reg) << 8) |
                     (U64(a.swizzle == 1 ? 0x7fffffffU : 0x7fff7fffU) << 20));
                a.reg = 250;
                a.absolute = false;
            }
            uint precision = B(insn.Bits(76, 1)) ? 2 : insn.Bits(80, 1);
            emit(0x5d00000000000000UL | predicate | (relu ? 254U : dest) | (U64(a.reg) << 8) | (U64(b.reg) << 20) |
                 (U64(b.swizzle) << 28) | (U64(c.negate) << 30) | (U64(a.negate != b.negate) << 31) |
                 (U64(insn.Bits(77, 1)) << 32) | (U64(c.swizzle) << 35) |
                 (U64(precision) << 37) | (U64(c.reg) << 39) | (U64(a.swizzle) << 47));
            if (relu) {
                // Ampere's trailing predicate enables ReLU; it is not a predicate output.
                // Keep the ordinary FMA result when it is false, including negative zero.
                uint relu_predicate = insn.Bits(87, 4);
                if (relu_predicate != 7) {
                    emit(@base(0x5c98000000000000UL) | (U64(254) << 20) | (U64(15) << 39));
                    relocations.Add(new Relocation(U32(result.Instructions.Size()), index + 1));
                    emit(0xe24000000000000fUL | (U64(relu_predicate ^ 8U) << 16));
                }
                // Clamp the rounded binary16 lanes, preserving positive infinity
                // and canonicalizing either sign of NaN without saturating to one.
                save_predicate();
                for (uint lane = 0; lane < 2; ++lane) {
                    emit(0x3828000000070000UL | 251 | (U64(254) << 8) | (U64(lane * 16) << 20));
                    emit(0x0400000000070000UL | 251 | (U64(251) << 8) | (U64(0xffff) << 20));
                    emit(0x0400000000070000UL | 252 | (U64(251) << 8) | (U64(0x7fff) << 20));
                    mov_imm(250, 0x7c00);
                    emit(0x5b60000000070000UL | 7 | (U64(6) << 3) | (U64(250) << 8) |
                         (U64(252) << 20) | (U64(7) << 39) | (U64(1) << 49));
                    emit(0x3828000000070000UL | 252 | (U64(251) << 8) | (U64(15) << 20));
                    mov_imm(250, 1);
                    emit(0x5c10000000070000UL | 252 | (U64(252) << 8) | (U64(250) << 20) | (U64(1) << 48));
                    emit(0x5c40000000070000UL | 251 | (U64(251) << 8) | (U64(252) << 20) | (U64(7) << 48));
                    mov_imm(250, 0x7fff);
                    emit(0x5ca0000000070000UL | 251 | (U64(250) << 8) | (U64(251) << 20) | (U64(6) << 39));
                    if (lane == 0) {
                        emit(@base(0x5c98000000000000UL) | (U64(251) << 20) | (U64(15) << 39));
                    } else {
                        emit(0x3848000000070000UL | 251 | (U64(251) << 8) | (U64(16) << 20));
                        emit(@base(0x5c40000000000000UL) | (dest << 8) | (U64(251) << 20) |
                             (U64(1) << 41) | (U64(7) << 48));
                    }
                }
                restore_predicate();
            }
            break;
        }
        case 0x233: case 0x433: case 0x633: { // HSET2, packed half comparisons and predicate combination.
            uint combine = insn.Bits(69, 2);
            if (B(insn.Bits(64, 5)) || combine > 2 || B(insn.Bits(81, 6)) || B(insn.Bits(91, 1)) ||
                B((opcode == 0x233 && B((insn[1] & 0x0fffff00U)))))
                return fail(index, "native packed half comparison mode");
            HalfSource a = half_source(0), b = half_source(opcode == 0x233 ? 1U : 2U);
            emit(@base(0x5d18000000000000UL) | (U64(a.reg) << 8) | (U64(b.reg) << 20) |
                 (U64(b.swizzle) << 28) | (U64(b.absolute) << 30) | (U64(b.negate) << 31) |
                 (U64(insn.Bits(76, 4)) << 35) | (U64(insn.Bits(87, 4)) << 39) |
                 (U64(a.negate) << 43) | (U64(a.absolute) << 44) | (U64(combine) << 45) |
                 (U64(a.swizzle) << 47) | (U64(insn.Bits(71, 1)) << 49) | (U64(insn.Bits(80, 1)) << 50));
            break;
        }
        case 0x240: case 0x840: { // HMNMX2, exact half promotion for min/max
            if (B(insn.Bits(76, 11)) || B(insn.Bits(91, 1)))
                return fail(index, "native half min/max mode");
            var a = half_source(0);
            var b = opcode == 0x840 ? new HalfSource(255, 0, false, false) : half_source(1);
            if (a.swizzle == 1 || b.swizzle == 1 || (b.reg >= 250 && b.reg != 255))
                return fail(index, "native half min/max source mode");
            for (uint lane = 0; lane < 2; ++lane) {
                extract_half(a, lane, 250);
                if (opcode == 0x840) {
                    uint half = (insn[1] >> (int)((lane * 16))) & 0xffff;
                    mov_imm(251, half);
                    extract_half(new HalfSource(251, 2, false, false), 0, 251);
                } else {
                    extract_half(b, lane, 251);
                }
                uint target = lane == 0 ? 252U : 250U;
                emit(0x5c60000000000000UL | predicate | target | (U64(250) << 8) | (U64(251) << 20) |
                     (U64(insn.Bits(87, 3)) << 39) | (U64(insn.Bits(90, 1)) << 42));
                emit(0x5ca8000000000000UL | predicate | target | (U64(1) << 8) |
                     (U64(2) << 10) | (U64(target) << 20));
            }
            emit(@base(0x5c18000000000000UL) | (U64(250) << 8) | (U64(252) << 20) | (U64(16) << 39));
            break;
        }
        case 0xc24: case 0xe24: { // IMAD low word with a uniform multiplier or addend.
            // 0xe24 keeps the uniform addend in bits 32-37; bit 63 negates it ("RZ, RZ, -UR4").
            bool negate_c = opcode == 0xe24 && insn.Bits(63, 1) != 0;
            if ((insn[2] & ~0x080002ffU) != 0x078e0000 ||
                B((insn[1] & ~(0xffU | (negate_c ? 0x80000000U : 0U)))))
                return fail(index, "native uniform-addend multiply mode");
            uint a = operand(0, false), b = operand(1, false);
            uint c = operand(2, false);
            if (negate_c) {
                emit(0x5c10000000000000UL | predicate | 252 | (U64(255) << 8) | (U64(c) << 20) |
                     (U64(1) << 48));
                c = 252;
            }
            multiply_low(a, b, c, false);
            break;
        }
        case 0x224: { // IMAD register / zero-product move
            if (insn.Bits(24, 8) == 255 && insn[1] == 255 && B(insn.Bits(75, 1)) &&
                (insn[2] & ~0xaffU) == 0x078e0000) {
                emit(@base(0x5c10000000000000UL) | (U64(255) << 8) |
                    (U64(insn.Bits(64, 8)) << 20) | (U64(1) << 48));
                break;
            }
            if (insn.Bits(24, 8) == 255 && insn[1] == 255 &&
                (insn[2] & ~0xffU) == 0x078e0000) {
                emit(@base(0x5c98000000000000UL) | (U64(15) << 39) | (U64(insn.Bits(64, 8)) << 20));
                break;
            }
            if (insn.Bits(24, 8) == 255 && insn[1] == 255 && (insn[2] & ~0x07800affU) == 0x000e0400 &&
                (!rewritten_uniform || insn.Bits(87, 3) == 7)) {
                // IMAD.X RZ, RZ, c, Pn: the high word of a 64-bit add (bit 75 inverts c).
                uint c = insn.Bits(64, 8);
                mov_imm(250, 1);
                emit(0x5ca0000000000000UL | predicate | 250 | (U64(250) << 8) | (U64(255) << 20) |
                     (U64(insn.Bits(87, 3)) << 39) | (U64(insn.Bits(90, 1)) << 42));
                if (B(insn.Bits(75, 1))) {
                    emit(0x0400000000000000UL | predicate | 251 | (U64(c) << 8) | (U64(0xffffffffU) << 20) | (U64(2) << 53));
                    c = 251;
                }
                emit(@base(0x5c10000000000000UL) | (U64(c) << 8) | (U64(250) << 20));
                break;
            }
            if ((insn[2] & ~0x2ffU) != 0x078e0000 || B((insn[1] & ~0xffU)))
                return fail(index, "native integer register multiply mode");
            multiply_low(insn.Bits(24, 8), insn.Bits(32, 8), insn.Bits(64, 8), false);
            break;
        }
        case 0x2a5: { // UIMAD.WIDE.U32 UR(d):UR(d+1) = URa * URb + URc:UR(c+1)
            uint a = uniform_register(insn.Bits(24, 8)), b = uniform_register(insn.Bits(32, 8));
            uint c = insn.Bits(64, 8);
            uint c_low = uniform_register(c), c_high = c == 63 ? 255U : uniform_register(c + 1);
            uint d_low = uniform_register(insn.Bits(16, 8)), d_high = uniform_register(insn.Bits(16, 8) + 1);
            if (native_predicate != 7 || B((insn[1] & ~0xffU)) || (insn[2] & ~0xffU) != 0x0f8e0000 ||
                B((insn.Bits(16, 8) & 1)) || B((c != 63 && B((c & 1)))) || a == ~0U || b == ~0U || c_low == ~0U ||
                c_high == ~0U || d_low == ~0U || d_high == ~0U || d_low == 255)
                return fail(index, "native uniform wide multiply mode");
            // High word in R253 (base-2^16 long multiplication, as IMAD.HI), low word in R250.
            xmad(250, a, b, 255, false, false, 0, false);
            xmad(251, a, b, 250, true, false, 2, false);
            xmad(250, a, b, 251, false, true, 1, false);
            xmad(252, a, b, 251, true, true, 2, false);
            emit(0x3828000000070000UL | 250 | (U64(250) << 8) | (U64(16) << 20));
            emit(0x5c10000000070000UL | 253 | (U64(250) << 8) | (U64(252) << 20));
            xmad(250, a, b, 255, false, false, 0, false);
            xmad(251, a, b, 255, true, false, 0, false);
            xmad(251, a, b, 251, false, true, 0, false);
            emit(0x5c18000000070000UL | 250 | (U64(251) << 8) | (U64(250) << 20) | (U64(16) << 39));
            emit(0x5c10000000070000UL | 250 | (U64(250) << 8) | (U64(c_low) << 20) | (U64(1) << 47));
            emit(0x5c10000000070000UL | d_high | (U64(253) << 8) | (U64(c_high) << 20) | (U64(1) << 43));
            emit(0x5c10000000070000UL | d_low | (U64(250) << 8) | (U64(255) << 20));
            break;
        }
        case 0x226: { // IDP.2A.LO.U16.U8: a.lo16 * b.byte0 + a.hi16 * b.byte1 + c
            if ((insn[2] & ~0xffU) != 0x1000 || B((insn[1] & ~0xffU)))
                return fail(index, "native packed dot-product mode");
            uint a = insn.Bits(24, 8), b = insn.Bits(32, 8), c = insn.Bits(64, 8);
            for (uint component = 0; component < 2; ++component) {
                emit(0x3828000000000000UL | predicate | (250 + component) | (U64(b) << 8) | (U64(component * 8) << 20));
                emit(0x0400000000000000UL | predicate | (250 + component) | (U64(250 + component) << 8) | (U64(0xff) << 20));
            }
            xmad(252, a, 250, c, false, false, 0, false);
            xmad(U32(dest), a, 251, 252, true, false, 0, false);
            break;
        }
        case 0x225: case 0x227: case 0x625: case 0x825: case 0x827: case 0xa27: {
            // IMAD.HI Rd / IMAD.WIDE Rd:Rd+1 = a * b + c:c+1 (.U32 or signed, bit 73), with an optional
            // carry-out (bits 81-83). b is a register, an immediate (0x8xx) or a cbuf (0xa27); 0x625 takes
            // b from bits 64-71 and adds a cbuf pair.
            bool wide = opcode == 0x225 || opcode == 0x625 || opcode == 0x825;
            bool immediate = opcode == 0x825 || opcode == 0x827, signed_product = B(insn.Bits(73, 1));
            uint c = opcode == 0x625 ? 255U : insn.Bits(64, 8), carry_output = insn.Bits(81, 3);
            if ((insn[2] & ~0x000e02ffU) != 0x07800000 ||
                ((opcode == 0x225 || opcode == 0x227) && B((insn[1] & ~0xffU))) ||
                ((opcode == 0xa27 || opcode == 0x625) && (!valid_cbuf() || B(insn.Bits(62, 2)))) ||
                (opcode == 0x625 && insn.Bits(38, 16) > 0xfff8) || (c != 255 && B((c & 1))) ||
                (wide && (dest == 255 || B((dest & 1)))) || (signed_product && carry_output != 7))
                return fail(index, "native high multiply mode");
            uint a = insn.Bits(24, 8);
            uint b = immediate ? insn[1] : opcode == 0xa27 ? 254U : insn.Bits(opcode == 0x625 ? 64U : 32U, 8);
            if (opcode == 0xa27) emit(0x4c98000000000000UL | predicate | 254 | (U64(15) << 39) | cbuf());
            if (signed_product) {
                // The signed high word is the unsigned one minus (a < 0 ? b : 0) + (b < 0 ? a : 0),
                // which R253 holds (R252 is free until the multiplication).
                uint b_register = immediate ? 254U : b;
                if (immediate) mov_imm(254, b);
                emit(0x3828000000000000UL | predicate | 253 | (U64(a) << 8) | (U64(31) << 20) | (U64(1) << 48));
                emit(0x5c40000000000000UL | predicate | 253 | (U64(253) << 8) | (U64(b_register) << 20) | (U64(7) << 48));
                emit(0x3828000000000000UL | predicate | 252 | (U64(b_register) << 8) | (U64(31) << 20) | (U64(1) << 48));
                emit(0x5c40000000000000UL | predicate | 252 | (U64(252) << 8) | (U64(a) << 20) | (U64(7) << 48));
                emit(0x5c10000000000000UL | predicate | 253 | (U64(253) << 8) | (U64(252) << 20));
            }
            // Base-2^16 long multiplication. Each intermediate sum fits U32.
            xmad(250, a, b, 255, false, false, 0, immediate);
            xmad(251, a, b, 250, true, false, 2, immediate);
            xmad(250, a, b, 251, false, true, 1, immediate);
            xmad(252, a, b, 251, true, true, 2, immediate);
            emit(0x3828000000000000UL | predicate | 250 | (U64(250) << 8) | (U64(16) << 20));
            if (signed_product)
                emit(0x5c10000000000000UL | predicate | 250 | (U64(250) << 8) | (U64(253) << 20) | (U64(1) << 48));
            if (!wide && c == 255 && carry_output == 7) {
                emit(@base(0x5c10000000000000UL) | (U64(250) << 8) | (U64(252) << 20));
                break;
            }
            // High word in R253 and low word in R250 (as UIMAD.WIDE.U32), then c:c+1 is added.
            emit(0x5c10000000000000UL | predicate | 253 | (U64(250) << 8) | (U64(252) << 20));
            xmad(250, a, b, 255, false, false, 0, immediate);
            xmad(251, a, b, 255, true, false, 0, immediate);
            xmad(251, a, b, 251, false, true, 0, immediate);
            emit(0x5c18000000000000UL | predicate | 250 | (U64(251) << 8) | (U64(250) << 20) | (U64(16) << 39));
            uint c_low = c, c_high = c == 255 ? 255U : c + 1;
            if (opcode == 0x625) {
                emit(0x4c98000000000000UL | predicate | 252 | (U64(15) << 39) | cbuf());
                emit(0x4c98000000000000UL | predicate | 254 | (U64(15) << 39) | (cbuf() + (U64(1) << 20)));
                c_low = 252;
                c_high = 254;
            }
            uint high = wide ? U32(dest) + 1 : U32(dest);
            emit(0x5c10000000000000UL | predicate | 250 | (U64(250) << 8) | (U64(c_low) << 20) | (U64(1) << 47));
            if (carry_output == 7) {
                emit(0x5c10000000000000UL | predicate | high | (U64(253) << 8) | (U64(c_high) << 20) | (U64(1) << 43));
                if (wide) emit(@base(0x5c10000000000000UL) | (U64(250) << 8) | (U64(255) << 20));
                break;
            }
            // Maxwell IADD has no .X with .CC: R251 takes the low carry, R254 counts the high word's.
            emit(0x5c10000000000000UL | predicate | 251 | (U64(255) << 8) | (U64(255) << 20) | (U64(1) << 43));
            emit(0x5c10000000000000UL | predicate | 253 | (U64(253) << 8) | (U64(c_high) << 20) | (U64(1) << 47));
            emit(0x5c10000000000000UL | predicate | 254 | (U64(255) << 8) | (U64(255) << 20) | (U64(1) << 43));
            emit(0x5c10000000000000UL | predicate | 253 | (U64(253) << 8) | (U64(251) << 20) | (U64(1) << 47));
            emit(0x5c10000000000000UL | predicate | 254 | (U64(254) << 8) | (U64(255) << 20) | (U64(1) << 43));
            emit(0x5c98000000000000UL | predicate | high | (U64(253) << 20) | (U64(15) << 39));
            if (wide) emit(@base(0x5c98000000000000UL) | (U64(250) << 20) | (U64(15) << 39));
            emit(0x5b60000000000000UL | predicate | 7 | (U64(carry_output) << 3) | (U64(254) << 8) |
                 (U64(255) << 20) | (U64(7) << 39) | (U64(5) << 49));
            break;
        }
        case 0x95c:
            if (insn[0] != 0x795c || insn[1] != 4 || insn[2] != 0x00300000 ||
                Array.IndexOf(verified_traps, index * 16U) < 0)
                return fail(index, "native runtime breakpoint/trap");
            emit(0x50b0000000000f00UL | predicate);
            break;
        case 0xa24: { // IMAD, constant-buffer multiplier
            if ((insn[2] & ~0x2ffU) != 0x078e0000 || !valid_cbuf() || B(insn.Bits(62, 2)))
                return fail(index, "native integer constant-buffer multiply mode");
            emit(0x4c98000000000000UL | predicate | 252 | (U64(15) << 39) | cbuf());
            multiply_low(insn.Bits(24, 8), 252, insn.Bits(64, 8), false);
            break;
        }
        case 0x424: case 0x624: { // IMAD.MOV.U32: zero product plus immediate/cbuf
            if (opcode == 0x624 && insn.Bits(24, 8) == 255 &&
                (insn[2] & ~0x07800000U) == 0x000e06ffU) {
                uint source = operand(2, false);
                mov_imm(250, 1);
                emit(0x5ca0000000000000UL | predicate | 250 | (U64(250) << 8) | (U64(255) << 20) |
                    (U64(insn.Bits(87, 3)) << 39) | (U64(insn.Bits(90, 1)) << 42));
                emit(@base(0x5c10000000000000UL) | (U64(source) << 8) | (U64(250) << 20));
                break;
            }
            bool negated_cbuf_addend = opcode == 0x624 && B(insn.Bits(63, 1));
            if (insn.Bits(24, 8) != 255 || insn.Bits(64, 8) != 255 || insn[2] != 0x078e00ff ||
                negated_cbuf_addend) {
                // Bit 63 negates the constant buffer addend (bit 62 would be its absolute value).
                // A zero product with a negated addend also takes this path: 0 * 0 - c.
                if ((insn[2] & ~0x2ffU) != 0x078e0000 || (opcode == 0x624 && B(insn.Bits(62, 1))))
                    return fail(index, "native multiply wide addend mode");
                uint a = operand(0, false), b = operand(1, false);
                uint c = operand(2, false);
                if (negated_cbuf_addend) {
                    emit(0x5c10000000000000UL | predicate | 252 | (U64(255) << 8) | (U64(c) << 20) | (U64(1) << 48));
                    c = 252;
                }
                multiply_low(a, b, c, false);
                break;
            }
            if (insn.Bits(24, 8) != 255 || insn.Bits(64, 8) != 255 ||
                insn[2] != 0x078e00ff)
                return fail(index, "native integer move mode");
            if (opcode == 0x624) {
                if (!valid_cbuf() || B(insn.Bits(62, 2)))
                    return fail(index, "native integer move constant buffer");
                emit(@base(0x4c98000000000000UL) | (U64(15) << 39) | cbuf());
            } else {
                emit(@base(0x0100000000000000UL) | (U64(15) << 12) |
                     (U64(insn[1]) << 20));
            }
            break;
        }
        case 0x824: { // Captured low-word IMAD forms; bit 75 negates the addend.
            bool negate_c = insn.Bits(75, 1) != 0;
            if (insn[1] == 1 && (insn[2] & ~(0x07800800U | 0xffU)) == 0x000e0600U) {
                // IMAD.X Rd, Ra, 1, Rc, Pn: Ra + Rc plus the carry predicate in bits 87-90 (~Rc with bit 75).
                uint c = insn.Bits(64, 8);
                mov_imm(252, 1);
                emit(0x5ca0000000000000UL | predicate | 252 | (U64(252) << 8) | (U64(255) << 20) |
                     (U64(insn.Bits(87, 3)) << 39) | (U64(insn.Bits(90, 1)) << 42));
                if (negate_c) {
                    emit(0x0400000000000000UL | predicate | 251 | (U64(c) << 8) | (U64(0xffffffffU) << 20) | (U64(2) << 53));
                    c = 251;
                }
                emit(0x5c10000000000000UL | predicate | 250 | (U64(insn.Bits(24, 8)) << 8) |
                     (U64(c) << 20));
                emit(@base(0x5c10000000000000UL) | (U64(250) << 8) | (U64(252) << 20));
                break;
            }
            if (insn[1] == 1 && (insn[2] & 0xfffff700U) == 0x078e0200) {
                emit(@base(0x5c10000000000000UL) | (U64(insn.Bits(24, 8)) << 8) |
                     (U64(insn.Bits(64, 8)) << 20) | (U64(negate_c) << 48));
                break;
            }
            uint multiplier = insn[1];
            if (insn.Bits(64, 8) == 255 && !negate_c && multiplier > 1 && !B((multiplier & (multiplier - 1))) &&
                (insn[2] & ~0x2ffU) == 0x078e0000) {
                // IMAD.SHL by a power of two: one shift (also the form Eden's jump-table tracking
                // expects between the clamp and the table load).
                uint shift = 0;
                while ((1U << (int)(shift)) != multiplier) ++shift;
                emit(@base(0x3848000000000000UL) | (U64(insn.Bits(24, 8)) << 8) | (U64(shift) << 20));
                break;
            }
            if (insn[1] != 48 || insn[2] != 0x078e02ff) {
                if ((insn[2] & ~0xaffU) != 0x078e0000)
                    return fail(index, "native integer multiply mode");
                uint c = insn.Bits(64, 8);
                if (negate_c && c != 255) {
                    emit(0x5c10000000000000UL | predicate | 252 | (U64(255) << 8) | (U64(c) << 20) |
                         (U64(1) << 48));
                    c = 252;
                }
                multiply_low(insn.Bits(24, 8), insn[1], c, true);
                break;
            }
            ulong source = insn.Bits(24, 8);
            emit(0x5c18000000000000UL | predicate | 250 | (source << 8) | (source << 20) | (U64(1) << 39));
            emit(@base(0x3848000000000000UL) | (U64(250) << 8) | (U64(4) << 20));
            break;
        }
        case 0x806: { // VOTE: register ballot, ordinary predicate input and output.
            if (B(insn[1]) || insn.Bits(72, 2) > 2 || B((insn[2] & ~0x078e0300U)))
                return fail(index, "native vote mode");
            emit(@base(0x50d8000000000000UL) | (U64(insn.Bits(87, 4)) << 39) |
                 (U64(insn.Bits(81, 3)) << 45) | (U64(insn.Bits(72, 2)) << 48));
            break;
        }
        case 0x886: { // VOTEU ballot and independent uniform predicate output.
            uint output = insn.Bits(81, 3), target = insn.Bits(16, 8);
            uint mapped_target = uniform_register(target);
            if (mapped_target == ~0U || B(insn.Bits(24, 8)) || B(insn[1]) ||
                B((insn[2] & ~0x078e0300U)) || insn.Bits(72, 2) > 2 || native_predicate != 7)
                return fail(index, "native uniform vote mode");
            uint temporary = output == 7 ? 7U : 6U - output;
            uint mask = 1U << (int)(temporary);
            if (output != 7) emit(0x38e8000000070000UL | 253 | (U64(255) << 8) | (U64(mask) << 20));
            emit(0x50d8000000070000UL | mapped_target |
                (U64(insn.Bits(87, 4)) << 39) | (U64(temporary) << 45) | (U64(insn.Bits(72, 2)) << 48));
            if (output != 7) {
                emit(0x38e8000000070000UL | 239 | (U64(239) << 8) | (U64(mask) << 20));
                emit(0x38f0000000070000UL | (U64(253) << 8) | (U64(mask) << 20));
            }
            break;
        }
        case 0x3c2: { // R2UR: broadcast the first lane satisfying the execution predicate.
            uint target = uniform_register(U32(dest));
            if (target == ~0U || target == 255 || B(insn[1]) || insn[2] != 0x000e0000)
                return fail(index, "native register-to-uniform mode");
            save_predicate();
            emit(0x50d8000000070000UL | 250 | (U64(native_predicate) << 39) |
                 (U64(6) << 45) | (U64(1) << 48));
            emit(0x5c10000000070000UL | 251 | (U64(250) << 8) | (U64(255) << 20) | (U64(1) << 49));
            emit(0x5c40000000070000UL | 250 | (U64(250) << 8) | (U64(251) << 20) | (U64(7) << 48));
            emit(0x5c30000000070000UL | 250 | (U64(250) << 20));
            emit(0xef10000000070000UL | 251 | (U64(insn.Bits(24, 8)) << 8) |
                 (U64(250) << 20) | (U64(1) << 29) | (U64(31) << 34) | (U64(7) << 48));
            if (native_predicate == 7) {
                // Every lane executing it is in the ballot, so no lane-less fallback can be taken.
                // Leaving the old value selectable would make it a source of the result for the
                // texture and global memory tracking (a handle traced to the uniform's last load).
                emit(0x5c98000000070000UL | target | (U64(15) << 39) | (U64(251) << 20));
            } else {
                emit(0x5ca0000000070000UL | target | (U64(251) << 8) |
                     (U64(target) << 20) | (U64(6) << 39));
            }
            restore_predicate();
            break;
        }
        case 0x81c: { // PLOP3.LUT; bit 67 makes the third source a uniform predicate.
            uint target = insn.Bits(81, 3);
            uint second_target = insn.Bits(84, 3);
            uint lut = insn.Bits(64, 3) | (insn.Bits(72, 5) << 3);
            if (second_target != 7) {
                if (target == second_target || B(insn.Bits(24, 8)) || B(insn[1]) ||
                    B(insn.Bits(91, 5)) || B((insn[3] & 0xc00001ffU)))
                    return fail(index, "native dual predicate LUT mode");
                predicate_lut(lut, insn.Bits(87, 4), insn.Bits(77, 4), insn.Bits(68, 4),
                              target, insn.Bits(67, 1) != 0, second_target, insn.Bits(16, 8));
                break;
            }
            if (target == 7 || insn.Bits(84, 3) != 7 ||
                B(insn.Bits(16, 8)) || B(insn[1]))
                return fail(index, "native predicate LUT mode");
            if (!B(insn.Bits(67, 1)) && (lut == 0xfe || lut == 0xa8)) {
                emit(0x5090000000000000UL | predicate | (U64(target) << 3) | 7 |
                     (U64(insn.Bits(87, 4)) << 12) | (U64(1) << 24) |
                     (U64(insn.Bits(77, 4)) << 29) | (U64(insn.Bits(68, 4)) << 39) |
                     (U64(lut == 0xfe) << 45));
                break;
            }
            if (!B(insn.Bits(67, 1)) && (lut == 0x80 ||
                (lut == 8 && insn.Bits(68, 4) == 7 && insn.Bits(77, 4) == 7))) {
                emit(0x5090000000000000UL | predicate | (U64(target) << 3) | 7 |
                     (U64(insn.Bits(87, 4) ^ (lut == 8 ? 8U : 0U)) << 12) | (U64(insn.Bits(77, 4)) << 29) |
                     (U64(insn.Bits(68, 4)) << 39));
                break;
            }
            predicate_lut(lut, insn.Bits(87, 4), insn.Bits(77, 4), insn.Bits(68, 4), target,
                          insn.Bits(67, 1) != 0);
            break;
        }
        case 0x89c: { // UPLOP3.LUT: load the uniform predicates into P0-P6, combine, merge back.
            uint target = insn.Bits(81, 3);
            uint lut = insn.Bits(64, 3) | (insn.Bits(72, 5) << 3);
            if (insn.Bits(84, 3) != 7 || B(insn.Bits(67, 1)) || B(insn.Bits(16, 8)) || B(insn[1]))
                return fail(index, "native uniform predicate LUT mode");
            if (target == 7) break;
            uint slot(uint operand) { return (operand & 7) == 7 ? operand : (operand & 8) | (6 - (operand & 7)); };
            predicate = U64(slot(native_predicate)) << 16; // a UPn guard is loaded as well
            emit(0x38e8000000070000UL | 254 | (U64(255) << 8) | (U64(0x7f) << 20));
            emit(0x38f0000000070000UL | (U64(239) << 8) | (U64(0x7f) << 20));
            predicate_lut(lut, slot(insn.Bits(87, 4)), slot(insn.Bits(77, 4)), slot(insn.Bits(68, 4)), 6 - target);
            emit(0x38e8000000070000UL | 239 | (U64(239) << 8) | (U64(1U << (int)((6 - target))) << 20));
            emit(0x38f0000000070000UL | (U64(254) << 8) | (U64(0x7f) << 20));
            break;
        }
        case 0x883: { // UP2UR UR, UPR, URZ, mask: bit k of the result is UPk (R239 bit 6-k).
            uint target = uniform_register(insn.Bits(16, 8)), mask = insn[1];
            if (target == ~0U || insn.Bits(24, 8) != 63 || native_predicate != 7 ||
                insn[2] != 0x08000000 || mask > 0x7f)
                return fail(index, "native uniform predicate move mode");
            if (target == 255) break;
            mov_imm(target, 0);
            for (uint k = 0; k < 7; ++k) {
                if (!B(((mask >> (int)(k)) & 1))) continue;
                emit(0x3800000000070000UL | 250 | (U64(239) << 8) | (U64((6 - k) | (1U << 8)) << 20));
                emit(0x36f0000000070000UL | target | (U64(250) << 8) | (U64(k | (1U << 8)) << 20) |
                     (U64(target) << 39));
            }
            break;
        }
        case 0x882: { // UMOV immediate, into any uniform register (Hyrule Warriors: @!UP0 UMOV UR59, 0x20).
            uint target = uniform_register(U32(dest));
            if (target == ~0U || B(insn[2])) return fail(index, "native uniform immediate move mode");
            if (target != 255) mov_imm(target, insn[1]);
            break;
        }
        case 0xc82: { // UMOV UR, UR (or URZ), including uniform execution predicates.
            // Any uniform destination (Hyrule Warriors: @UP0 UMOV UR59, UR58).
            uint source = insn[1] < 64 ? uniform_register(insn[1]) : ~0U;
            uint target = uniform_register(U32(dest));
            if (target == ~0U || source == ~0U || insn[2] != 0x08000000)
                return fail(index, "native uniform move mode");
            if (target != 255)
                emit(0x5c98000000000000UL | predicate | target | (U64(source) << 20) | (U64(15) << 39));
            break;
        }
        case 0xab9: case 0xabb: { // ULDC, direct or indexed.
            uint uniform_dest = insn.Bits(16, 8), index_reg = insn.Bits(24, 8);
            uint width = insn.Bits(73, 3);
            bool subword_load = width < 4;
            uint offset = insn.Bits(38, 16);
            uint alignment = width < 4 ? 1U << (int)((width / 2)) : 4U;
            uint target = uniform_register(uniform_dest);
            uint index_target = opcode == 0xabb ? uniform_register(index_reg) : 255U;
            if (target == ~0U || target == 255 || (!subword_load && width != 4 && width != 5) ||
                (opcode == 0xab9 && subword_load && offset >= 0x8000) ||
                B((width == 5 && (B((uniform_dest & 1)) || uniform_register(uniform_dest + 1) != target + 1))) ||
                // An index register can align the offset; the host reads the aligned word either way.
                bank() >= 18 || B((opcode == 0xab9 && B(offset % alignment))) || B(insn.Bits(62, 2)) ||
                (insn[2] & ~0xe00U) != (opcode == 0xab9 ? 0U : 0x08000000U) ||
                index_target == ~0U)
                return fail(index, "native uniform constant load mode");
            if (opcode == 0xab9 && width == 4) {
                emit(0x4c98000000000000UL | predicate | target | (U64(15) << 39) | cbuf());
            } else {
                emit(0xef90000000000000UL | predicate | target |
                     (U64(index_target) << 8) | (U64(offset) << 20) |
                     (U64(bank()) << 36) | (U64(width) << 48));
            }
            break;
        }
        case 0x899: { // USHF.L.U32; accept a zero word outside the selected half.
            uint dst = insn.Bits(16, 8), low = insn.Bits(24, 8), high = insn.Bits(64, 8);
            uint dst_reg = uniform_register(dst), low_reg = uniform_register(low);
            uint high_reg = uniform_register(high);
            bool dst_ok = dst != 63 && dst_reg != ~0U;
            bool low_ok = low != 63 && low_reg != ~0U, high_ok = high != 63 && high_reg != ~0U;
            if (dst_ok && low_reg != ~0U && high_reg != ~0U &&
                insn[2] == (0x08001200U | high)) {
                mov_imm(252, insn[1]);
                right_shift_u64_low(dst_reg, low_reg, high_reg, 252);
                break;
            }
            // A UPn guard (Hyrule Warriors: @UP1 USHF.R.U32.HI) is swapped into the predicate above.
            if (insn[2] == (0x08011400U | high) && low == 63 && high_ok &&
                dst_ok && insn[1] <= 32) {
                emit(0x3828000000000000UL | predicate | dst_reg |
                     (U64(high_reg) << 8) | (U64(insn[1]) << 20) | (U64(1) << 48));
                break;
            }
            // Logical right shifts of one register (bit 76 right, bit 80 high word, bits 73-74
            // U32): USHF.R.U32 URd, URa, n, URZ and USHF.R.U32.HI URd, URZ, n, URb.
            bool right_low = insn[2] == 0x0800163f && low_ok;
            bool right_high = insn[2] == (0x08011600U | high) && low == 63 && high_ok;
            if ((right_low || right_high) && dst_ok && insn[1] <= 32) {
                emit(0x3828000000000000UL | predicate | dst_reg |
                     (U64(right_high ? high_reg : low_reg) << 8) | (U64(insn[1]) << 20));
                break;
            }
            bool high_result = insn[2] == (0x08010600U | high) && low == 63 && high_ok;
            bool low_result = insn[2] == 0x0800063f && low_ok;
            if (!dst_ok || insn[1] > 32 || (!high_result && !low_result))
                return fail(index, "native uniform shift mode");
            emit(0x3848000000000000UL | predicate | dst_reg |
                 (U64(high_result ? high_reg : low_reg) << 8) | (U64(insn[1]) << 20));
            break;
        }
        case 0xb82: { // LDC indexed byte offset; bits 78-79 the mode as Maxwell's (2 = IS)
            // .IS: Ra + offset addresses all constant buffers as one space, the buffer in its high
            // half (Kirby Air Riders' vertex shaders), which Eden's Maxwell LDC implements.
            uint width = insn.Bits(73, 3), mode = insn.Bits(78, 2);
            if (width > 5 || (mode != 0 && mode != 2) ||
                B((insn[2] & ~0xce00U)) || B((insn[1] & 0xf800003fU)) ||
                bank() >= 18 ||
                B((width == 5 && (B((dest & 1)) || dest >= 254))))
                return fail(index, "native indexed constant load mode");
            emit(@base(0xef90000000000000UL) | (U64(insn.Bits(24, 8)) << 8) |
                 (U64(insn.Bits(38, 16)) << 20) | (U64(bank()) << 36) | (U64(mode) << 44) | (U64(width) << 48));
            break;
        }
        case 0xf89: { // SHFL with immediate lane and segmentation/clamp mask, any mode.
            uint lane = insn.Bits(53, 5), mask = insn.Bits(40, 13), mode = insn.Bits(58, 2);
            if (insn[2] != 0x000e0000 || B(insn.Bits(32, 8)) || B(insn.Bits(60, 4)))
                return fail(index, "native immediate shuffle mode");
            emit(@base(0xef10000000000000UL) | (U64(insn.Bits(24, 8)) << 8) |
                 (U64(lane) << 20) | (U64(1) << 28) | (U64(1) << 29) |
                 (U64(mode) << 30) | (U64(mask) << 34) | (U64(7) << 48));
            break;
        }
        case 0x822: { // FSWZADD: per lane 0 = a+b, 1 = a-b, 2 = b-a, 3 = b (nvdisasm PP/PN/NP/ZP).
            uint swizzle = insn[1];
            if (swizzle > 0xff || insn[2] > 255)
                return fail(index, "native swizzled add mode");
            // Maxwell's lane codes 1 and 2 are the other way round (1 = b-a, 2 = a-b).
            uint maxwell = ((swizzle & 0x55) << 1) | ((swizzle & 0xaa) >> 1);
            emit(@base(0x50f8000000000000UL) | (U64(insn.Bits(24, 8)) << 8) |
                 (U64(insn.Bits(64, 8)) << 20) | (U64(maxwell) << 28));
            break;
        }
        case 0xf6c: {
            uint handle = uniform_register(insn.Bits(40, 6));
            uint coordinates = insn.Bits(24, 8), derivatives = insn.Bits(32, 8);
            uint dimension = insn.Bits(61, 3), second_dest = insn.Bits(64, 8);
            uint mask = insn.Bits(72, 4), channels = U32(BitOperations.PopCount(mask));
            uint register_limit = high_uniform_base;
            if ((dimension != 1 && dimension != 5) || B(insn.Bits(46, 15)) ||
                (insn[2] & ~0x04000fffU) != 0x081e0000U || mask == 0 ||
                B((insn[3] & 0xc00001ffU)) || handle == ~0U || handle == 255 ||
                coordinates + (dimension == 5 ? 3U : 2U) > register_limit ||
                derivatives + 4 > register_limit ||
                dest + Math.Min(channels, 2U) > register_limit || B((channels > 1 && B((dest & 1)))) ||
                B((channels > 2 ? second_dest != 255 &&
                    (second_dest + channels - 2 > register_limit || (channels == 4 && B((second_dest & 1))) ||
                     (second_dest < dest + 2 && dest < second_dest + channels - 2))
                              : second_dest != 255)))
                return fail(index, "native uniform gradient texture mode");
            emit(0x5c98000000000000UL | predicate | 248 | (U64(15) << 39) | (U64(handle) << 20));
            for (uint i = 0; i < (dimension == 5 ? 3U : 2U); ++i)
                emit(0x5c98000000000000UL | predicate | (249 + i) | (U64(15) << 39) |
                     (U64(coordinates + i) << 20));
            emit(0xde40000000000000UL | predicate | 248 | (U64(248) << 8) |
                 (U64(derivatives) << 20) | (U64(dimension == 5 ? 3U : 2U) << 28) |
                 (U64(mask) << 31) | (U64(insn.Bits(90, 1)) << 49) | (U64(7) << 51));
            for (uint i = 0; i < channels; ++i) {
                if (i >= 2 && second_dest == 255) break;
                emit(0x5c98000000000000UL | predicate | (i < 2 ? dest + i : second_dest + i - 2) |
                     (U64(15) << 39) | (U64(248 + i) << 20));
            }
            break;
        }
        case 0xb6c: { // TXD 2D or ARRAY_2D, any channels; the two destination pairs anywhere.
            uint binding = insn.Bits(54, 5), offset = insn.Bits(40, 14);
            uint coordinates = insn.Bits(24, 8), derivatives = insn.Bits(32, 8);
            // Ampere dimensions: 1 = 2D, 5 = ARRAY_2D; Maxwell TXD's are 2 and 3. The coordinates
            // (x, y[, layer]) and the four gradients sit in the same registers for both.
            uint dimension = insn.Bits(61, 3);
            uint high = insn.Bits(64, 8); // destination of the third and fourth channels
            // Bit 90 is forwarded below (Maxwell's NODEP); Splatoon Raiders sets it. Bits 72-75 are
            // the channel mask: the first two enabled channels go to Rd, Rd+1, the others to the
            // second destination (RZ drops them; a third channel alone may be in an odd register).
            uint mask = insn.Bits(72, 4), channels = U32(BitOperations.PopCount(mask));
            if (B((insn[2] & ~(0xfffU | (1U << 26))) != 0x001e0000) || mask == 0 || B(insn.Bits(59, 2)) ||
                (dimension != 1 && dimension != 5) || binding >= 18 || offset > 0x1fff ||
                B((channels > 1 && B((dest & 1)))) || dest >= 248 || (channels <= 2 && high != 255) ||
                B((high != 255 && ((channels > 3 && B((high & 1))) || high >= 248))) ||
                coordinates >= 248 || derivatives >= 247)
                return fail(index, "native gradient texture mode");
            if (result.RequiredTextureBuffer != ~0U && result.RequiredTextureBuffer != binding)
                return fail(index, "native texture descriptor buffer conflict");
            result.RequiredTextureBuffer = binding;
            // Maxwell writes the channels to consecutive registers: when the second destination
            // doesn't follow Rd (Splatoon Raiders: R12/R18, R18/R44), write them to scratch and move
            // each into place.
            bool consecutive = channels <= 2 || high == dest + 2;
            ulong target = consecutive ? dest : 248;
            emit(0xde00000000000000UL | predicate | target | (U64(coordinates) << 8) |
                 (U64(derivatives) << 20) | (U64(dimension == 5 ? 3U : 2U) << 28) | (U64(mask) << 31) |
                 (U64(offset) << 36) | (U64(insn.Bits(90, 1)) << 49) | (U64(7) << 51));
            if (!consecutive) {
                for (uint i = 0; i < channels; ++i) {
                    if (i >= 2 && high == 255) break;
                    uint to = i < 2 ? U32(dest) + i : high + (i - 2);
                    emit(0x5c98000000000000UL | predicate | to | (U64(15) << 39) | (U64(248 + i) << 20));
                }
            }
            break;
        }
        case 0x989: { // SHFL, immediate lane and register clamp/segmentation mask, any mode.
            if (B((insn[1] & 0xf01fffffU)) || B((insn[2] & ~0x000e00ffU)))
                return fail(index, "native register-mask shuffle mode");
            emit(@base(0xef10000000000000UL) | (U64(insn.Bits(24, 8)) << 8) |
                 (U64(insn.Bits(53, 5)) << 20) | (U64(1) << 28) | (U64(insn.Bits(58, 2)) << 30) |
                 (U64(insn.Bits(64, 8)) << 39) | (U64(insn.Bits(81, 3)) << 48));
            break;
        }
        case 0x589: { // SHFL, register lane and immediate clamp/segmentation mask, any mode.
            uint lane = insn.Bits(32, 8);
            if (B((insn[2] & ~0x000e0000U)) || B(insn.Bits(53, 5)) || B(insn.Bits(60, 4)) ||
                B((insn[3] & 0xc00001ffU)) ||
                B((lane >= (uses_uniform_bank ? high_uniform_base : 246U) && lane != 255)))
                return fail(index, "native shuffle mode");
            emit(@base(0xef10000000000000UL) | (U64(insn.Bits(24, 8)) << 8) | (U64(lane) << 20) |
                 (U64(1) << 29) | (U64(insn.Bits(58, 2)) << 30) |
                 (U64(insn.Bits(40, 13)) << 34) | (U64(insn.Bits(81, 3)) << 48));
            break;
        }
        case 0x98e: { // RED.E.{ADD..XOR}.STRONG.GPU U32 (op in bits 87-89), register address.
            uint type = insn.Bits(73, 3);
            uint operation = insn.Bits(87, 3);
            bool uniform_address = insn.Bits(24, 8) == 255;
            uint variable = 0x03800e00U | (uniform_address ? 0x3fU : 0U);
            uint mode = uniform_address ? 0x0c10e100U : 0x0010e100U;
            int offset = S32(insn.Bits(40, 24) << 8) >> 8;
            if ((insn[2] & ~variable) != mode ||
                (type != 0 && type != 1 && type != 3) || (type == 3 && operation != 0))
                return fail(index, "native global reduction mode");
            uint address = uniform_address ? uniform_register(insn.Bits(64, 6))
                                                : insn.Bits(24, 8);
            if (address == ~0U || B((address & 1)) || offset < -0x80000 || offset > 0x7ffff)
                return fail(index, "native global reduction address");
            emit(0xebf8000000000000UL | predicate | insn.Bits(32, 8) | (U64(address) << 8) |
                 (U64(type) << 20) | (U64(operation) << 23) |
                 (U64(U32(offset) & 0xfffff) << 28) | (U64(1) << 48));
            break;
        }
        case 0x2bd: { // UFLO.U32, the highest set-bit index (.SH, bit 74: its shift amount).
            uint target = uniform_register(U32(dest));
            uint source = uniform_register(insn.Bits(32, 8));
            if (dest == 63 || target == ~0U || source == ~0U || B((insn[1] & ~0xffU)) ||
                (insn[2] & ~0x400U) != 0x080e0000 || native_predicate != 7)
                return fail(index, "native uniform leading bit mode");
            emit(0x5c30000000000000UL | predicate | target | (U64(source) << 20) |
                 (U64(insn.Bits(74, 1)) << 41));
            break;
        }
        case 0x2be: { // UBREV, as a bit-reversing zero shift like BREV.
            uint target = uniform_register(U32(dest));
            uint source = uniform_register(insn.Bits(32, 8));
            if (dest == 63 || target == ~0U || source == ~0U || B((insn[1] & ~0xffU)) ||
                insn[2] != 0x08000000 || native_predicate != 7)
                return fail(index, "native uniform bit reverse mode");
            emit(0x3828000000000000UL | predicate | target | (U64(source) << 8) |
                 (U64(1) << 39) | (U64(1) << 40));
            break;
        }
        case 0x2bf: { // UPOPC, as POPC of the uniform's register.
            uint target = uniform_register(U32(dest));
            uint source = uniform_register(insn.Bits(32, 8));
            if (dest == 63 || target == ~0U || source == ~0U || B((insn[1] & ~0x800000ffU)) ||
                insn[2] != 0x08000000 || native_predicate != 7)
                return fail(index, "native uniform population count mode");
            emit(0x5c08000000000000UL | predicate | target | (U64(source) << 20) |
                 (U64(insn.Bits(63, 1)) << 40));
            break;
        }
        case 0x3a1: { // MATCH.ALL (.U32, or .U64 on bit 73) with only the all-equal predicate retained.
            uint source = insn.Bits(24, 8), target = insn.Bits(81, 3);
            uint words = B(insn.Bits(73, 1)) ? 2U : 1U;
            uint scratch = target == 6 ? 5U : 6U; // the comparison, saved around the match
            if (dest != 255 || source >= 254 || B((words == 2 && B((source & 1)))) || target == 7 ||
                native_predicate != 7 || B(insn[1]) || (insn[2] & ~0xe0200U) != 0)
                return fail(index, "native warp match mode");
            save_predicate(1U << (int)(scratch));
            // Compare every lane with the lowest active one, then require all to agree.
            emit(0x50d8000000070000UL | 250 | (U64(7) << 39) | (U64(7) << 45) | (U64(1) << 48));
            emit(0x5c10000000070000UL | 251 | (U64(250) << 8) | (U64(255) << 20) | (U64(1) << 49));
            emit(0x5c40000000070000UL | 250 | (U64(250) << 8) | (U64(251) << 20) | (U64(7) << 48));
            emit(0x5c30000000070000UL | 250 | (U64(250) << 20));
            for (uint word = 0; word < words; ++word)
                emit(0xef10000000070000UL | (251 + word) | (U64(source + word) << 8) |
                    (U64(250) << 20) | (U64(1) << 29) | (U64(31) << 34) | (U64(7) << 48));
            emit(0x5b60000000070000UL | 7 | (U64(scratch) << 3) | (U64(source) << 8) |
                (U64(251) << 20) | (U64(7) << 39) | (U64(2) << 49));
            if (words == 2)
                emit(0x5b60000000070000UL | 7 | (U64(scratch) << 3) | (U64(source + 1) << 8) |
                    (U64(252) << 20) | (U64(scratch) << 39) | (U64(2) << 49));
            emit(0x50d8000000070000UL | 255 | (U64(scratch) << 39) | (U64(target) << 45));
            restore_predicate(1U << (int)(scratch));
            break;
        }
        case 0x80d: { // CSMTEST: a test of shading-mode state Eden does not model, taken as false.
            // Kirby Air Riders' vertex-side shaders run "CSMTEST.A.FCSM_TR P0" then "@P0 EXIT":
            // never leave early. Outputs of PT discard the result.
            uint first = insn.Bits(81, 3), second = insn.Bits(84, 3);
            if (insn.Bits(87, 4) != 7 || B(insn.Bits(16, 16)))
                return fail(index, "native CSM test mode");
            foreach (uint target in new uint[] {first, second})
                if (target != 7)
                    emit(0x5090000000000000UL | predicate | (U64(target) << 3) | 7 | (U64(7) << 12) |
                         (U64(1) << 15) | (U64(7) << 29) | (U64(7) << 39));
            break;
        }
        case 0x3c4: { // REDUX.SUM (modulo 2^32), .MIN/.MAX (unsigned), .OR or .XOR over the actual active lanes.
            uint source = insn.Bits(24, 8), target = uniform_register(U32(dest));
            uint mode = insn[2] & ~0x200U;
            bool maximum = mode == 0x14000, minimum = mode == 0x10000;
            bool signed_order = B(insn.Bits(73, 1));
            // OR and XOR: inactive lanes contribute zero, as for the sum. Maxwell LOP: 1 = OR, 2 = XOR.
            uint logical = insn[2] == 0x4000 ? 1U : insn[2] == 0x8000 ? 2U : 0U;
            if (target == ~0U || target == 255 || B(insn[1]) ||
                (insn[2] != 0xc000 && !maximum && !minimum && !B(logical)) ||
                B((signed_order && !maximum && !minimum)) || native_predicate != 7)
                return fail(index, "native warp sum mode");
            emit(0x50d8000000070000UL | 250 | (U64(7) << 39) | (U64(7) << 45) | (U64(1) << 48));
            mov_imm(target, minimum ? ~0U : 0U);
            for (uint lane = 0; lane < 32; ++lane) {
                emit(0xef10000000070000UL | 251 | (U64(source) << 8) | (U64(lane) << 20) |
                    (U64(1) << 28) | (U64(1) << 29) | (U64(31) << 34) | (U64(7) << 48));
                if (signed_order)
                    emit(0x0400000000070000UL | 251 | (U64(251) << 8) |
                         (U64(0x80000000U) << 20) | (U64(2) << 53));
                emit(0x3828000000070000UL | 252 | (U64(250) << 8) | (U64(lane) << 20));
                emit(0x0400000000070000UL | 252 | (U64(252) << 8) | (U64(1) << 20));
                emit(0x5c10000000070000UL | 252 | (U64(255) << 8) | (U64(252) << 20) | (U64(1) << 48));
                // Inactive lanes: 0, or ~0 for the minimum (OR with the inverted lane mask).
                emit(0x5c40000000070000UL | 251 | (U64(251) << 8) | (U64(252) << 20) | (U64(7) << 48) |
                     (U64(minimum) << 40) | (U64(minimum) << 41));
                if (minimum) {
                    emit(0x5c20000000070000UL | target | (U64(target) << 8) | (U64(251) << 20) | (U64(7) << 39));
                } else if (maximum) {
                    emit(0x5c20000000070000UL | target | (U64(target) << 8) | (U64(251) << 20) | (U64(15) << 39));
                } else if (B(logical)) {
                    emit(0x5c40000000070000UL | target | (U64(target) << 8) | (U64(251) << 20) |
                        (U64(logical) << 41) | (U64(7) << 48));
                } else {
                    emit(0x5c10000000070000UL | target | (U64(target) << 8) | (U64(251) << 20));
                }
            }
            if (signed_order)
                emit(0x0400000000070000UL | target | (U64(target) << 8) |
                     (U64(0x80000000U) << 20) | (U64(2) << 53));
            break;
        }
        case 0xf8c: { // ATOMS.POPC.INC.32 with no returned value: one increment per active lane.
            uint stride = insn.Bits(78, 2), offset = insn.Bits(40, 24);
            if (dest != 255 || B((insn[1] & 0xff)) || B((offset & 3)) ||
                (insn[2] & ~0xc000U) != 0x0d00003f)
                return fail(index, "native shared increment mode");
            uint address = insn.Bits(24, 8);
            if (B(stride) && address != 255) {
                emit(0x3848000000000000UL | predicate | 251 | (U64(address) << 8) |
                    (U64(stride + 1) << 20));
                address = 251;
            }
            mov_imm(250, 1);
            emit(0xec00000000000000UL | predicate | 255 | (U64(address) << 8) | (U64(250) << 20) |
                (U64(offset >> 2) << 30));
            break;
        }
        case 0x38c: { // ATOMS (ADD/MIN/MAX/INC/DEC/AND/OR/XOR/EXCH, U32 or S32), aligned byte offset.
            // The operation (bits 87-90) is numbered as in Maxwell ATOMS (52-55); bit 73 is S32.
            uint offset = insn.Bits(40, 24), operation = insn.Bits(87, 4);
            if (B((insn[2] & ~0x07800200U)) || operation > 8 || B((offset & 3)))
                return fail(index, "native shared atomic mode");
            emit(0xec00000000000000UL | predicate | dest |
                (U64(insn.Bits(24, 8)) << 8) | (U64(insn.Bits(32, 8)) << 20) |
                (U64(insn.Bits(73, 1)) << 28) | (U64(offset >> 2) << 30) | (U64(operation) << 52));
            break;
        }
        case 0x3a8: { // ATOMG.E.STRONG.GPU U32 at a 64-bit register address plus an offset.
            uint address = insn.Bits(24, 8), operation = insn.Bits(87, 4);
            int offset = S32(insn.Bits(40, 24) << 8) >> 8;
            if ((insn[2] & ~0x07800000U) != 0x001ee100 || operation > 8 || B((address & 1)) || address >= 246 ||
                offset < -0x80000 || offset > 0x7ffff)
                return fail(index, "native global atomic register mode");
            emit(0xed00000000000000UL | predicate | dest | (U64(address) << 8) |
                (U64(insn.Bits(32, 8)) << 20) | (U64(U32(offset) & 0xfffff) << 28) |
                (U64(1) << 48) | (U64(operation) << 52));
            break;
        }
        case 0x9a8: { // ATOMG.E.ADD.STRONG.GPU U32 through a uniform pointer plus an offset.
            uint address = uniform_register(insn.Bits(64, 6));
            int offset = S32(insn.Bits(40, 24) << 8) >> 8;
            if (insn.Bits(24, 8) != 255 || offset < -0x80000 || offset > 0x7ffff ||
                (insn[2] & ~0x3fU) != 0x081ee140 || address == ~0U || B((address & 1)))
                return fail(index, "native global atomic mode");
            emit(0xed00000000000000UL | predicate | dest | (U64(address) << 8) |
                (U64(insn.Bits(32, 8)) << 20) | (U64(U32(offset) & 0xfffff) << 28) |
                (U64(1) << 48));
            break;
        }
        case 0x998:
        case 0x99c: {
            bool store = opcode == 0x99c;
            uint data = insn.Bits(store ? 32U : 16U, 8);
            uint coordinate = insn.Bits(24, 8), handle = insn.Bits(64, 8);
            uint channels = insn.Bits(72, 4), dimension = insn.Bits(61, 3);
            uint count = U32(BitOperations.PopCount(channels));
            uint reserved = uses_uniform_bank ? high_uniform_base : 246U;
            if (B((insn[1] & ~(store ? 0xe00000ffU : 0xe0000000U))) ||
                (insn[2] & ~0xfffU) != (store ? 0x00100000U : 0x001e0000U) ||
                B((insn[3] & 0xc00001ffU)) || dimension < 3 || dimension > 5 ||
                channels == 0 || handle >= reserved || data + count > reserved ||
                coordinate + (dimension >= 4 ? 3U : 2U) > reserved ||
                B((store && (dest != 0 || (channels != 1 && channels != 3 && channels != 15)))) ||
                (!store && B(data % (count == 3 ? 4U : count))))
                return fail(index, "native bindless formatted surface mode");
            emit((store ? 0xeb20000000000000UL : 0xeb00000000000000UL) | predicate |
                data | (U64(coordinate) << 8) | (U64(channels) << 20) |
                (U64(dimension) << 33) | (U64(handle) << 39));
            break;
        }
        case 0xb97: { // SULD.P.1D/buffer/2D/2D_ARRAY/3D.IGN, direct descriptor, channel mask at 72-75.
            uint binding = insn.Bits(54, 5), offset = insn.Bits(40, 14);
            uint coordinate = insn.Bits(24, 8);
            uint channels = insn.Bits(72, 4);
            uint dimension = insn.Bits(61, 3); // Maxwell's dimension codes.
            uint reserved = uses_uniform_bank ? high_uniform_base : 246U;
            uint count = U32(BitOperations.PopCount(channels));
            if ((insn[2] & ~0xf00U) != 0x001e0000 || channels == 0 || dimension == 2 || dimension > 5 ||
                binding >= 18 || offset > 0x1fff || B(insn.Bits(59, 2)) || dest == 255 ||
                coordinate > (dimension >= 4 ? 252U : dimension == 3 ? 253U : 254U) ||
                B((dimension < 2 && (coordinate >= reserved || dest + count > reserved ||
                                   B(dest % (count == 3 ? 4U : count))))))
                return fail(index, "native formatted surface load mode");
            if (result.RequiredTextureBuffer != ~0U && result.RequiredTextureBuffer != binding)
                return fail(index, "native surface descriptor buffer conflict");
            result.RequiredTextureBuffer = binding;
            emit(@base(0xeb00000000000000UL) | (U64(coordinate) << 8) |
                (U64(channels) << 20) | (U64(dimension) << 33) | (U64(offset) << 36) | (U64(1) << 51));
            break;
        }
        case 0xb9d: {
            uint binding = insn.Bits(54, 5), offset = insn.Bits(40, 14);
            uint data = insn.Bits(32, 8), coordinate = insn.Bits(24, 8);
            uint reserved = uses_uniform_bank ? high_uniform_base : 246U;
            if (insn[2] != 0x0010a900 || insn.Bits(61, 3) != 3 ||
                insn.Bits(16, 8) != 0 || binding >= 18 || offset > 0x1fff ||
                B(insn.Bits(59, 2)) || data >= reserved || coordinate + 1 >= reserved)
                return fail(index, "native byte-addressed surface store mode");
            if (!bind_texture_buffer(binding))
                return fail(index, "native byte-addressed surface descriptor buffer conflict");
            emit(0xeb38000000000000UL | predicate | data | (U64(coordinate) << 8) |
                (U64(4) << 20) | (U64(1) << 23) | (U64(3) << 33) | (U64(offset) << 36));
            break;
        }
        case 0xb9b: { // SUST.P.1D/buffer/2D/3D.IGN, direct descriptor.
            uint binding = insn.Bits(54, 5), offset = insn.Bits(40, 14);
            uint data = insn.Bits(32, 8), coordinate = insn.Bits(24, 8);
            uint dimension = insn.Bits(61, 3);
            uint mask = insn.Bits(72, 4);
            uint mode = insn[2] & ~0xf00U;
            uint reserved = uses_uniform_bank ? high_uniform_base : 246U;
            bool strong = mode == 0x0010a000; // Strong stores are fenced on both sides.
            if (!B(mask) || (mode != 0x00100000 && !strong) || dimension == 2 || dimension > 5 ||
                binding >= 18 || offset > 0x1fff || B(insn.Bits(59, 2)) || data > 252 ||
                coordinate > (dimension >= 4 ? 252U : dimension == 3 ? 253U : 254U) ||
                B((dimension < 2 && (coordinate >= reserved ||
                                   data + U32(BitOperations.PopCount(mask)) > reserved ||
                                   (mask != 1 && mask != 3 && mask != 15)))))
                return fail(index, "native formatted surface store mode");
            // A second descriptor buffer: SUST.B with the handle, loaded from that buffer, in R250.
            bool bindless = !bind_texture_buffer(binding);
            if (bindless) {
                emit(0x4c98000000070000UL | 250 | (U64(15) << 39) | (U64(offset) << 20) | (U64(binding) << 34));
            }
            if (strong) emit(0xef98000000000000UL | predicate | (U64(1) << 8)); // MEMBAR.GL
            emit(0xeb20000000000000UL | predicate | data | (U64(coordinate) << 8) |
                (U64(mask) << 20) | (U64(dimension) << 33) |
                (bindless ? U64(250) << 39 : (U64(offset) << 36) | (U64(1) << 51)));
            if (strong) emit(0xef98000000000000UL | predicate | (U64(1) << 8));
            break;
        }
        case 0x593: {
            uint binding = insn.Bits(54, 5), offset = insn.Bits(40, 14);
            uint data = insn.Bits(32, 8), coordinate = insn.Bits(24, 8);
            uint dimension = insn.Bits(61, 3), operation = insn.Bits(87, 4);
            uint coordinates = dimension >= 4 ? 3U : dimension == 3 ? 2U : 1U;
            uint reserved = uses_uniform_bank ? high_uniform_base : 246U;
            if ((insn[2] & ~0x07800000U) != 0x001ee000 || operation > 8 ||
                dimension == 2 || dimension > 5 || binding >= 18 || offset > 0x1fff ||
                B(insn.Bits(59, 2)) || coordinate + coordinates > reserved ||
                (data != 255 && data >= reserved) || (dest != 255 && dest >= reserved) ||
                B((insn[3] & 0xc00001ffU)))
                return fail(index, "native formatted surface atomic mode");
            if (result.RequiredTextureBuffer != ~0U && result.RequiredTextureBuffer != binding)
                return fail(index, "native surface descriptor buffer conflict");
            result.RequiredTextureBuffer = binding;
            emit(@base(0xea00000000000000UL) | (U64(coordinate) << 8) | (U64(data) << 20) |
                 (U64(operation) << 29) | (U64(dimension) << 33) | (U64(offset) << 36));
            break;
        }
        case 0x59f: { // SURED.D.STRONG.GPU.IGN U32, direct descriptor, buffer, 2D or 3D.
            uint binding = insn.Bits(54, 5), offset = insn.Bits(40, 14);
            uint data = insn.Bits(32, 8), coordinate = insn.Bits(24, 8);
            uint dimension = insn.Bits(61, 3);
            uint coordinates = dimension == 1 ? 1U : dimension == 5 ? 3U : 2U;
            uint reserved = uses_uniform_bank ? high_uniform_base : 246U;
            // 3D (5, as in Orchid) reads x, y, z from three consecutive registers.
            if ((insn[2] & ~0x03800000U) != 0x0010e000 ||
                (dimension != 1 && dimension != 3 && dimension != 5) || binding >= 18 ||
                offset > 0x1fff || B(insn.Bits(59, 2)) || coordinate + coordinates > reserved)
                return fail(index, "native surface reduction mode");
            if (result.RequiredTextureBuffer != ~0U && result.RequiredTextureBuffer != binding)
                return fail(index, "native surface descriptor buffer conflict");
            result.RequiredTextureBuffer = binding;
            // Maxwell SURED: operand 0-7, coordinates 8-15, op 24, U32 zero at 20,
            // type 33 (1 = buffer, 3 = 2D, 5 = 3D), bound offset 36, bound 51.
            emit(0xeb40000000000000UL | predicate | data | (U64(coordinate) << 8) |
                (U64(insn.Bits(87, 3)) << 24) | (U64(dimension) << 33) | (U64(offset) << 36) | (U64(1) << 51));
            break;
        }
        case 0x301: // BREV register.
            if (B(insn[2]) || insn[1] > 255) return fail(index, "native bit reverse mode");
            emit(@base(0x3828000000000000UL) | (U64(insn[1]) << 8) |
                (U64(1) << 39) | (U64(1) << 40));
            break;
        case 0x219: case 0xa19: case 0xc19: { // U32 register (or cbuf, 0xa19, or uniform, 0xc19) shifts with a zero word outside the selected result.
            uint low = insn.Bits(24, 8), high = insn.Bits(64, 8);
            bool constant = opcode == 0xa19;
            // A constant shift amount is loaded into R251 once the form is known.
            uint shift = opcode == 0xc19 ? uniform_register(insn.Bits(32, 8)) : constant ? 251U : insn.Bits(32, 8);
            bool shift_operand = constant ? valid_cbuf() && !B(insn.Bits(62, 2)) : insn[1] <= 255;
            void load_shift() { if (constant) materialize(2, 0, 251, false, false); };
            bool right = insn.Bits(76, 1) != 0, high_result = insn.Bits(80, 1) != 0;
            if (opcode == 0x219 && shift_operand &&
                (insn[2] & ~0xffU) == 0x00001200U) {
                right_shift_u64_low(U32(dest), low, high, shift);
                break;
            }
            if (opcode == 0x219 && low == 255 && shift_operand &&
                (insn[2] & ~0x8ffU) == 0x00011400U &&
                !B((insn[3] & 0xc00001ffU))) {
                bool wrap = insn.Bits(75, 1) != 0;
                if (!wrap)
                    emit(0x3820000000000000UL | predicate | 250 | (U64(shift) << 8) |
                         (U64(31) << 20) | (U64(7) << 39));
                emit(@base(0x5c28000000000000UL) | (U64(high) << 8) |
                     (U64(wrap ? shift : 250U) << 20) | (U64(1) << 39) | (U64(1) << 48));
                break;
            }
            // SHF.R.U32 of the low word with a zero high word: a plain logical right shift.
            if (right && !high_result && high == 255 && insn.Bits(73, 2) == 3 && shift != ~0U &&
                shift_operand &&
                (insn[2] & ~(0x000118ffU | (opcode == 0xc19 ? 0x08000000U : 0U))) == 0x00000600U) {
                load_shift();
                emit(@base(0x5c28000000000000UL) | (U64(low) << 8) | (U64(shift) << 20));
                break;
            }
            if (!shift_operand || insn.Bits(73, 2) != 3 || shift == ~0U ||
                (insn[2] & ~(0x000118ffU | (opcode == 0xc19 ? 0x08000000U : 0U))) != 0x00000600U ||
                (right ? (!high_result || low != 255) : (high_result ? low != 255 : high != 255)))
                return fail(index, "native register shift mode");
            load_shift();
            emit(@base(right ? 0x5cf8000000000000UL : 0x5bf8000000000000UL) |
                (U64(right ? high : 255U) << 8) | (U64(shift) << 20) |
                (U64(right ? 255U : (high_result ? high : low)) << 39) | (U64(insn.Bits(75, 1)) << 50));
            break;
        }
        case 0x300: case 0xd00: { // FLO, including a uniform source.
            if ((insn[2] & ~(0x600U | (opcode == 0xd00 ? 0x08000000U : 0U))) != 0x000e0000U)
                return fail(index, "native leading bit mode");
            uint source = operand(1, false);
            emit(@base(0x5c30000000000000UL) | (U64(source) << 20) |
                (U64(insn.Bits(63, 1)) << 40) | (U64(insn.Bits(74, 1)) << 41) |
                (U64(insn.Bits(73, 1)) << 48));
            break;
        }
        case 0x309: case 0xd09: { // POPC, including a uniform source.
            if (insn[2] != (opcode == 0xd09 ? 0x08000000U : 0U))
                return fail(index, "native population count mode");
            uint source = operand(1, false);
            emit(@base(0x5c08000000000000UL) | (U64(source) << 20) | (U64(insn.Bits(63, 1)) << 40));
            break;
        }
        case 0x918:
            if (B(insn[1]) || B(insn[2])) return fail(index, "native NOP mode");
            emit(0x50b0000000000f00UL | predicate);
            break;
        case 0x992: { // MEMBAR.SC: preserve workgroup/device visibility.
            uint scope = insn.Bits(76, 3);
            if (B(insn[1]) || insn[2] != (scope << 12) ||
                (scope != 0 && scope != 2 && scope != 3 && scope != 5))
                return fail(index, "native memory barrier scope");
            uint legacy_scope = scope == 0 ? 0 : scope == 2 ? 1 : scope == 3 ? 2U : 3U;
            emit(0xef98000000000000UL | predicate | (U64(legacy_scope) << 8));
            break;
        }
        case 0x9ab: // ERRBAR: wait for prior memory operations on the host backend.
            if (B(insn[1]) || B(insn[2])) return fail(index, "native error barrier mode");
            emit(0xef98000000000000UL | predicate | (U64(1) << 8));
            break;
        case 0xb1d: // BAR.SYNC 0 (bit 80: .DEFER_BLOCKING), full workgroup.
            if (B(insn[1]) || B((insn[2] & ~0x10000U)) || native_predicate != 7)
                return fail(index, "native workgroup barrier mode");
            emit(0xf0a8000000000000UL | predicate | (U64(1) << 43) |
                (U64(1) << 44) | (U64(1) << 39) | (U64(7) << 39));
            break;
        case 0xfae: { // LDGSTS.E.BYPASS.128 [shared], [global.64], enable
            // Complete copies synchronously. Disabled sources zero-fill without touching global memory.
            uint shared = U32(dest), global = insn.Bits(24, 8);
            uint limit = uses_uniform_bank ? high_uniform_base : 246U;
            bool promoted_copy = (insn[2] & ~0x0780003fU) == 0x08101d40U;
            uint descriptor = insn.Bits(64, 6);
            bool descriptor_copy = (insn[2] & ~0x0380007fU) == 0x0c101c00U &&
                                         descriptor < 63 && !B((descriptor & 1));
            if (B((insn[1] & 0x0000f000U)) ||
                B((!promoted_copy && !descriptor_copy)) ||
                shared >= limit || B((global & 1)) || global + 1 >= limit)
                return fail(index, "native global-to-shared copy mode");
            int global_offset = S32(insn.Bits(32, 12) << 20) >> 20;
            int shared_offset = S32(insn.Bits(48, 16) << 16) >> 12;
            for (uint component = 0; component < 4; ++component) mov_imm(248 + component, 0);
            uint enable = insn.Bits(87, 3) |
                               (insn.Bits(promoted_copy ? 90U : 70U, 1) << 3);
            uint skip = U32(result.Instructions.Size());
            emit(0);
            emit(0xeed0000000000000UL | predicate | 248 | (U64(global) << 8) |
                 (U64(U32(global_offset) & 0xffffffU) << 20) |
                 (U64(1) << 45) | (U64(6) << 48));
            uint target = U32(result.Instructions.Size());
            uint distance = address_of(target) - address_of(skip) - 8;
            result.Instructions[skip] = 0xe24000000000000fUL |
                                        (U64(enable ^ 8U) << 16) | (U64(distance) << 20);
            emit(0xef58000000000000UL | predicate | 248 | (U64(shared) << 8) |
                 (U64(U32(shared_offset) & 0xffffffU) << 20) | (U64(6) << 48));
            break;
        }
        case 0x388: case 0x984: case 0x988: { // Shared memory load/store with scaled register offsets.
            uint width = insn.Bits(73, 3), stride = insn.Bits(78, 2);
            // LDS [Ra.Xn + URb + offset]: bit 91 adds a uniform register (bits 32-39). STS with one
            // (0x988, Hyrule Warriors: STS [UR9+0x130], R32) has it in bits 64-71, the data at 32.
            bool uniform_base = (opcode == 0x984 || opcode == 0x988) && B(insn.Bits(91, 1));
            uint base_register = uniform_base ? uniform_register(insn.Bits(opcode == 0x988 ? 64U : 32U, 8)) : 255U;
            uint ignored = uniform_base ? (opcode == 0x988 ? 0x080000ffU : 0x08000000U) : 0U;
            if (width > 6 || (insn[2] & ~ignored) != ((width << 9) | (stride << 14)) ||
                (opcode == 0x984 && !uniform_base && B(insn.Bits(32, 8))) || (opcode == 0x988 && !uniform_base) ||
                base_register == ~0U)
                return fail(index, "native shared memory addressing mode");
            uint address = insn.Bits(24, 8);
            if (B(stride) && address != 255) {
                emit(0x3848000000000000UL | predicate | 250 |
                    (U64(address) << 8) | (U64(stride + 1) << 20));
                address = 250;
            }
            if (base_register != 255) {
                emit(0x5c10000000000000UL | predicate | 250 | (U64(address) << 8) | (U64(base_register) << 20));
                address = 250;
            }
            bool store = opcode != 0x984;
            uint data = store ? insn.Bits(32, 8) : U32(dest);
            emit((store ? 0xef58000000000000UL : 0xef48000000000000UL) |
                predicate | data | (U64(address) << 8) | (U64(insn.Bits(40, 24)) << 20) |
                (U64(width) << 48));
            break;
        }
        case 0x23a: { // MOVM.16.MT88: transpose an 8x8 16-bit matrix held one row pair per lane.
            // Lane t holds row t/4, columns 2(t%4)..+1; the result is the transposed matrix in the
            // same layout, so each half comes from lane 8(t%4) + t/8 (+4) at half (t/4)%2.
            uint source = insn.Bits(24, 8);
            if (B(insn[1]) || B(insn[2]) || source >= 246 || dest >= 246)
                return fail(index, "native matrix move mode");
            emit(0xf0c8000000000000UL | predicate | 246);                                        // laneid
            emit(0x0400000000000000UL | predicate | 249 | (U64(246) << 8) | (U64(3) << 20));
            emit(0x3848000000000000UL | predicate | 249 | (U64(249) << 8) | (U64(3) << 20));     // 8(t%4)
            emit(0x3828000000000000UL | predicate | 247 | (U64(246) << 8) | (U64(3) << 20));     // t/8
            emit(0x5c10000000000000UL | predicate | 249 | (U64(249) << 8) | (U64(247) << 20));
            emit(0xef10000000000000UL | predicate | 247 | (U64(source) << 8) | (U64(249) << 20) |
                 (U64(1) << 29) | (U64(31) << 34) | (U64(7) << 48));
            emit(0x1c00000000000000UL | predicate | 249 | (U64(249) << 8) | (U64(4) << 20));
            emit(0xef10000000000000UL | predicate | 248 | (U64(source) << 8) | (U64(249) << 20) |
                 (U64(1) << 29) | (U64(31) << 34) | (U64(7) << 48));
            emit(0x3828000000000000UL | predicate | 249 | (U64(246) << 8) | (U64(2) << 20));
            emit(0x0400000000000000UL | predicate | 249 | (U64(249) << 8) | (U64(1) << 20));
            emit(0x3848000000000000UL | predicate | 249 | (U64(249) << 8) | (U64(4) << 20));     // 0 or 16
            emit(0x5c28000000000000UL | predicate | 247 | (U64(247) << 8) | (U64(249) << 20));
            emit(0x0400000000000000UL | predicate | 247 | (U64(247) << 8) | (U64(0xffff) << 20));
            emit(0x5c28000000000000UL | predicate | 248 | (U64(248) << 8) | (U64(249) << 20));
            emit(0x5c18000000000000UL | predicate | dest | (U64(248) << 8) | (U64(247) << 20) | (U64(16) << 39));
            break;
        }
        case 0x23c: { // HMMA.1688/.16816.F16, software warp expansion (no tensor extension required).
            uint a = insn.Bits(24, 8), b = insn.Bits(32, 8), c = insn.Bits(64, 8);
            uint mode = insn[2] & ~0xffU;
            uint depth = mode == 0 ? 8U : 16U;
            uint a_count = depth / 4, b_count = depth / 8;
            uint limit = uses_uniform_bank ? high_uniform_base : 246U;
            if (native_predicate != 7 || (mode != 0 && mode != 0x800) ||
                B((insn[1] & ~0xffU)) || B((insn[3] & 0xc00001ffU)) ||
                B(dest % 2) || dest + 2 > limit || B(a % a_count) || a + a_count > limit ||
                B(b % b_count) || b + b_count > limit || B((c != 255 && (B(c % 2) || c + 2 > limit))))
                return fail(index, "native half matrix multiply mode");
            if (compact_matrix) {
                // A software collective, not an NV cooperative-matrix operation. The backend
                // shares a bounded loop helper instead of duplicating hundreds of instructions
                // at every HMMA site. Bit 48 tags software; bit 49 selects K=8 rather than K=16.
                emit(0x5d30000000000000UL | predicate | dest | (U64(a) << 8) |
                     (U64(b) << 20) | (U64(c) << 39) | (U64(1) << 48) |
                     (U64(depth == 8) << 49));
                break;
            }
            // Four accumulators and five temporaries, all in R246..R254. In particular, R240..245
            // can hold live GPRs or emulated uniform registers and must not be borrowed.
            void shuffle(uint target, uint source, uint lane) {
                emit(0xef10000000000000UL | predicate | target | (U64(source) << 8) |
                     (U64(lane) << 20) | (U64(1) << 29) | (U64(0x1f) << 34) | (U64(7) << 48));
            };
            void round_half(uint reg) {
                emit(0x5ca8000000000000UL | predicate | reg | (U64(1) << 8) |
                     (U64(2) << 10) | (U64(reg) << 20));
            };
            for (uint row = 0; row < 2; ++row)
                for (uint col = 0; col < 2; ++col)
                    extract_half(new HalfSource(c == 255 ? 255 : c + row, 0, false, false), col, 246 + row * 2 + col);
            for (uint pair = 0; pair < depth / 2; ++pair) {
                emit(0xf0c8000000000000UL | predicate | 250); // LaneId
                emit(0x0400000000000000UL | predicate | 251 | (U64(250) << 8) | (U64(3) << 20));
                emit(0x3848000000000000UL | predicate | 251 | (U64(251) << 8) | (U64(3) << 20));
                emit(0x0400000000000000UL | predicate | 250 | (U64(250) << 8) | (U64(0x1c) << 20));
                mov_imm(254, pair & 3);
                foreach (uint reg in new uint[] {250U, 251U})
                    emit(0x5c10000000000000UL | predicate | reg | (U64(reg) << 8) | (U64(254) << 20));
                for (uint row = 0; row < 2; ++row) {
                    for (uint half = 0; half < 2; ++half) {
                        shuffle(252, a + (pair >= 4 ? 2U : 0U) + row, 250);
                        extract_half(new HalfSource(252, 0, false, false), half, 252);
                        for (uint col = 0; col < 2; ++col) {
                            mov_imm(254, col * 4);
                            emit(0x5c10000000000000UL | predicate | 254 | (U64(251) << 8) | (U64(254) << 20));
                            shuffle(253, b + pair / 4, 254);
                            extract_half(new HalfSource(253, 0, false, false), half, 253);
                            uint acc = 246 + row * 2 + col;
                            emit(0x5980000000000000UL | predicate | acc | (U64(252) << 8) |
                                 (U64(253) << 20) | (U64(acc) << 39));
                        }
                    }
                }
                if ((pair & 3) == 3) {
                    for (uint reg = 246; reg < 250; ++reg) {
                        round_half(reg);
                        if (pair + 1 < depth / 2) extract_half(new HalfSource(reg, 2, false, false), 0, reg);
                    }
                }
            }
            for (uint row = 0; row < 2; ++row)
                emit(0x5c18000000000000UL | predicate | (dest + row) |
                     (U64(247 + row * 2) << 8) | (U64(246 + row * 2) << 20) | (U64(16) << 39));
            break;
        }
        case 0x83b: { // LDSM.16.M88.{1,2,4} (ldmatrix): 8x8 16-bit matrices from shared memory.
            // Lanes 8m..8m+7 give the row addresses of matrix m; lane t receives the 32 bits at
            // row t/4, byte (t%4)*4 of each matrix in Rd+m. Emulated with SHFL.IDX + LDS.32.
            uint count = 1U << (int)(insn.Bits(72, 2));
            bool transpose = insn.Bits(78, 1) != 0;
            bool uniform_offset = B(insn.Bits(91, 1));
            if (native_predicate != 7 || insn.Bits(72, 2) == 3 ||
                B((insn[2] & ~0x08004300U)) || B((!uniform_offset && B(insn.Bits(32, 8)))) ||
                B(dest % count) || dest + count > (uses_uniform_bank ? high_uniform_base : 246U))
                return fail(index, "native matrix load mode");
            uint address = insn.Bits(24, 8);
            ulong offset = insn.Bits(40, 24);
            if (uniform_offset) {
                uint uniform = uniform_register(insn.Bits(32, 8));
                if (uniform == ~0U) return fail(index, "native matrix load uniform offset");
                emit(0x5c10000000000000UL | predicate | 254 | (U64(address) << 8) | (U64(uniform) << 20));
                address = 254;
            } else if (address != 255 && address >= dest && address < dest + count) {
                emit(0x5c10000000000000UL | predicate | 254 | (U64(address) << 8) | (U64(255) << 20));
                address = 254;
            }
            if (transpose) {
                // .MT88: lane t receives stored rows 2(t%4) and 2(t%4)+1 at column t/4 (16 bits each).
                emit(0xf0c8000000000000UL | predicate | 246);                                    // laneid
                emit(0x0400000000000000UL | predicate | 247 | (U64(246) << 8) | (U64(3) << 20));
                emit(0x3848000000000000UL | predicate | 247 | (U64(247) << 8) | (U64(1) << 20)); // row 2(t%4)
                emit(0x3828000000000000UL | predicate | 248 | (U64(246) << 8) | (U64(2) << 20));
                emit(0x3848000000000000UL | predicate | 248 | (U64(248) << 8) | (U64(1) << 20)); // byte 2(t/4)
                for (uint matrix = 0; matrix < count; ++matrix) {
                    for (uint half = 0; half < 2; ++half) {
                        emit(0x1c00000000000000UL | predicate | 249 | (U64(247) << 8) |
                             (U64(matrix * 8 + half) << 20));
                        emit(0xef10000000000000UL | predicate | 250 | (U64(address) << 8) | (U64(249) << 20) |
                             (U64(1) << 29) | (U64(31) << 34) | (U64(7) << 48));
                        emit(0x5c10000000000000UL | predicate | 250 | (U64(250) << 8) | (U64(248) << 20));
                        emit(0xef48000000000000UL | predicate | (251 + half) | (U64(250) << 8) |
                             (offset << 20) | (U64(2) << 48));                                  // LDS.U16
                    }
                    emit(0x5c18000000000000UL | predicate | (dest + matrix) | (U64(252) << 8) |
                         (U64(251) << 20) | (U64(16) << 39));
                }
                break;
            }
            emit(0xf0c8000000000000UL | predicate | 250);                                    // S2R laneid
            emit(0x3828000000000000UL | predicate | 251 | (U64(250) << 8) | (U64(2) << 20)); // row
            emit(0x0400000000000000UL | predicate | 250 | (U64(250) << 8) | (U64(3) << 20));
            emit(0x3848000000000000UL | predicate | 250 | (U64(250) << 8) | (U64(2) << 20)); // column bytes
            for (uint matrix = 0; matrix < count; ++matrix) {
                uint lane = 251;
                if (B(matrix)) {
                    emit(0x1c00000000000000UL | predicate | 252 | (U64(251) << 8) | (U64(matrix * 8) << 20));
                    lane = 252;
                }
                emit(0xef10000000000000UL | predicate | 253 | (U64(address) << 8) | (U64(lane) << 20) |
                     (U64(1) << 29) | (U64(31) << 34) | (U64(7) << 48));
                emit(0x5c10000000000000UL | predicate | 253 | (U64(253) << 8) | (U64(250) << 20));
                emit(0xef48000000000000UL | predicate | (dest + matrix) | (U64(253) << 8) |
                     (offset << 20) | (U64(4) << 48));
            }
            break;
        }
        case 0x919: case 0x9c3: { // S2R / S2UR: special registers the Maxwell S2R handles.
            // Same numbering as Maxwell: lane id (0), invocation id/info, Y direction, thread kill
            // (17-19, 29), W scale factors (30-31, Kirby Air Riders), thread and workgroup ids,
            // lane masks (56-60).
            uint special = insn.Bits(72, 8);
            if (special == 132 && !B(insn[1]) && insn[2] == (special << 8)) {
                // SR_VARIABLE_RATE: without variable-rate shading every fragment is 1x1, its width
                // and height in bytes 1 and 2 (Nintendo Switch Sports Resort multiplies them).
                uint special_target = opcode == 0x9c3 ? uniform_register(U32(dest)) : U32(dest);
                if (special_target == ~0U) return fail(index, "native uniform special destination");
                mov_imm(special_target, 0x00010100);
                break;
            }
            if (B(insn[1]) || insn[2] != (special << 8) ||
                B((special != 0 && (special < 17 || special > 19) && (special < 29 || special > 35) &&
                 special != 37 && special != 38 && special != 39 &&
                 special != 56 && special != 57 && special != 58 && special != 59 && special != 60)))
                return fail(index, "native special register");
            uint target = opcode == 0x9c3 ? uniform_register(U32(dest)) : U32(dest);
            if (target == ~0U) return fail(index, "native uniform special destination");
            emit(0xf0c8000000000000UL | predicate | target | (U64(special) << 20));
            break;
        }
        case 0x28c: case 0x88c: {
            uint output = insn.Bits(81, 3);
            uint a = uniform_register(insn.Bits(24, 8)), b = opcode == 0x88c ? 252U : uniform_register(insn.Bits(32, 8));
            uint combine = insn.Bits(87, 3), combine_op = insn.Bits(74, 2);
            if (output > 6 || insn.Bits(84, 3) != 7 || B(insn.Bits(72, 1)) || combine_op == 3 ||
                a == ~0U || b == ~0U)
                return fail(index, "native uniform comparison mode");
            uint temporary = 6 - output, combine_slot = combine == 7 ? 7U : 6 - combine;
            uint combine_mask = combine == 7 ? 0U : 1U << (int)(combine_slot);
            bool guarded = native_predicate != 7;
            uint temporary_mask = 1U << (int)(temporary);
            uint load_mask = combine_mask | (guarded ? temporary_mask | 1U << (int)(guard_slot) : 0U);
            uint mask = temporary_mask | load_mask;
            if (opcode == 0x88c)
                emit(0x0100000000000000UL | (U64(7) << 16) | 252 | (U64(15) << 12) |
                     (U64(insn[1]) << 20));
            emit(0x38e8000000070000UL | 253 | (U64(255) << 8) | (U64(mask) << 20));
            if (B(load_mask)) emit(0x38f0000000070000UL | (U64(239) << 8) | (U64(load_mask) << 20));
            emit(0x5b60000000000000UL |
                (U64(guarded ? (native_predicate & 8) | guard_slot : 7U) << 16) | 7 | (U64(temporary) << 3) |
                (U64(a) << 8) | (U64(b) << 20) | (U64(combine_slot) << 39) |
                (U64(insn.Bits(90, 1)) << 42) | (U64(combine_op) << 45) | (U64(insn.Bits(73, 1)) << 48) |
                (U64(insn.Bits(76, 3)) << 49));
            emit(0x38e8000000070000UL | 239 | (U64(239) << 8) | (U64(temporary_mask) << 20));
            emit(0x38f0000000070000UL | (U64(253) << 8) | (U64(mask) << 20));
            break;
        }
        case 0x287: case 0x887: { // USEL, UP0/UP1; preserve ordinary predicates.
            uint condition = insn.Bits(87, 3);
            uint target = uniform_register(U32(dest));
            uint a = uniform_register(insn.Bits(24, 8)), b = opcode == 0x887 ? 252U : uniform_register(insn.Bits(32, 8));
            if (condition > 6 || target == ~0U || a == ~0U || b == ~0U ||
                (insn[2] & ~0x07800000U) != 0x08000000U)
                return fail(index, "native uniform select mode");
            // The clamp's UISETP may sit a few instructions back, with only instructions between
            // that write no uniform state and do not branch (Nintendo Switch Sports' jump tables),
            // and no branch seen so far landing between them.
            uint compare = index;
            for (uint back = 1; back <= 8 && back <= index; ++back) {
                uint earlier = code[index - back].Bits(0, 12), datapath = earlier & 0x1ff;
                if (earlier == 0x88c) {
                    compare = index - back;
                    break;
                }
                if ((datapath >= 0x080 && datapath <= 0x0bf) || (datapath >= 0x1c2 && datapath <= 0x1c4) ||
                    (earlier >= 0x940 && earlier <= 0x95f))
                    break;
            }
            bool target_between = AnyOf(relocations, (Relocation relocation) => { return relocation.target > compare && relocation.target <= index; });
            if (opcode == 0x887 && native_predicate == 7 && compare < index && !target_between &&
                insn[1] <= 0x7ffff &&
                code[compare].Bits(12, 4) == 7 && code[compare].Bits(24, 8) == insn.Bits(24, 8) &&
                code[compare][1] == insn[1] &&
                code[compare][2] == (0x0bf01070U | (condition << 17)) &&
                insn[2] == (0x08000000U | (condition << 23))) {
                // Preserve the unsigned clamp idiom for indirect-table analysis.
                emit(0x3820000000070000UL | target | (U64(a) << 8) |
                     (U64(insn[1]) << 20) | (U64(7) << 39));
                break;
            }
            if (opcode == 0x887)
                emit(0x0100000000000000UL | (U64(7) << 16) | 252 | (U64(15) << 12) |
                     (U64(insn[1]) << 20));
            uint temporary = 6 - condition;
            uint mask = (1U << (int)(temporary)) | (native_predicate != 7 ? 1U << (int)(guard_slot) : 0U);
            emit(0x38e8000000070000UL | 253 | (U64(255) << 8) | (U64(mask) << 20));
            emit(0x38f0000000070000UL | (U64(239) << 8) | (U64(mask) << 20));
            emit(0x5ca0000000000000UL | (U64((native_predicate & 8) | (native_predicate != 7 ? guard_slot : 7U)) << 16) |
                target | (U64(a) << 8) | (U64(b) << 20) |
                (U64(temporary) << 39) | (U64(insn.Bits(90, 1)) << 42));
            emit(0x38f0000000070000UL | (U64(253) << 8) | (U64(mask) << 20));
            break;
        }
        case 0x381: case 0x981: { // LDG.E of 8 to 128 bits, register or uniform 64-bit address.
            // Sizes as Maxwell's: U8, S8, U16, S16, 32, 64, 128.
            uint size = insn.Bits(73, 3);
            bool scoped_descriptor = opcode == 0x981 &&
                insn[2] == 0x0c1ebd00;
            uint address = insn.Bits(24, 8);
            if (size > 6 || (opcode == 0x381 && B(insn.Bits(32, 8))) ||
                (size == 5 && B((dest & 1))) || (size == 6 && B((dest & 3))))
                return fail(index, "native global load mode");
            if (opcode == 0x381) {
                // Bit 77 (strong/scoped ordering) needs nothing extra from a Maxwell LDG.
                // Bits 64-67 are the load predicate, handled below.
                if ((insn[2] & ~0x2e0fU) != 0x001e8100 || B((address & 1)) || address >= 246)
                    return fail(index, "native global load mode");
            } else {
                // The uniform base sits where a register source would (bits 32-37).
                uint uniform_address =
                    B(insn.Bits(38, 2)) ? ~0U : uniform_register(insn.Bits(32, 6));
                // desc[URn][Ra.64]: the uniform pair is a memory (cache policy) descriptor and
                // Ra the whole address.
                bool descriptor_address = scoped_descriptor ||
                    (insn[2] & ~(0x3fU | 0xe00U)) == 0x0c1e1100;
                if (B((!descriptor_address &&
                     (insn[2] & ~(0x04000000U | 0x3fU | 0xe00U | 0x2000U)) != 0x081e8100)) ||
                    uniform_address == ~0U || B((uniform_address & 1)))
                    return fail(index, "native global load uniform addressing mode");
                if (descriptor_address) {
                    if (B((address & 1)) || address >= 246)
                        return fail(index, "native global load register address pair");
                } else if (address == 255) {
                    address = uniform_address;
                } else {
                    if (B(insn.Bits(90, 1)) && B((B((address & 1)) || address >= 246)))
                        return fail(index, "native global load register address pair");
                    emit(0x5c10000000000000UL | predicate | 250 | (U64(uniform_address) << 8) |
                         (U64(address) << 20) | (U64(1) << 47));
                    emit(0x5c10000000000000UL | predicate | 251 | (U64(uniform_address + 1) << 8) |
                         (U64(B(insn.Bits(90, 1)) ? address + 1 : 255) << 20) | (U64(1) << 43));
                    address = 250;
                }
            }
            if (opcode == 0x381 && B(insn.Bits(64, 4))) {
                // Load predicate (from Orchid): a false one returns zero without reading memory.
                // Its index is reverse encoded (7-P), independently of the execution predicate.
                // Staged in R248+ so the zero fill cannot clobber an aliased address register.
                if (native_predicate != 7) {
                    relocations.Add(new Relocation(U32(result.Instructions.Size()), index + 1));
                    emit(0xe24000000000000fUL | (U64(native_predicate ^ 8U) << 16));
                    predicate = U64(7) << 16;
                }
                uint components = size < 4 ? 1U : 1U << (int)((size - 4));
                uint load_predicate = (7 - insn.Bits(64, 3)) | (insn.Bits(67, 1) << 3);
                for (uint i = 0; i < components; ++i) mov_imm(248 + i, 0);
                emit(0xeed0000000000000UL | (U64(load_predicate) << 16) | 248 |
                     (U64(address) << 8) | (U64(insn.Bits(40, 24)) << 20) |
                     (U64(1) << 45) | (U64(size) << 48));
                for (uint i = 0; i < components; ++i) {
                    if (dest != 255)
                        emit(0x5c98000000000000UL | predicate | (dest + i) |
                             (U64(248 + i) << 20) | (U64(15) << 39));
                }
                break;
            }
            emit(@base(0xeed0000000000000UL) | (U64(address) << 8) |
                 (U64(insn.Bits(40, 24)) << 20) | (U64(1) << 45) |
                 (U64(scoped_descriptor) << 46) | (U64(size) << 48));
            break;
        }
        case 0x386: case 0x986: { // Global store of 32/64/128 bits, 64-bit address.
            uint data = insn.Bits(32, 8);
            uint size = insn.Bits(73, 3);
            if (size > 6 ||
                (data != 255 && ((size == 5 && B((data & 1))) || (size == 6 && B((data & 3))) ||
                                 data + (size < 4 ? 1U : 1U << (int)((size - 4))) > 246)))
                return fail(index, "native global store size");
            if (data == 255 && size > 4) {
                // Zeros wider than one register: store from zeroed scratch registers (aligned to the
                // access: R248-R251 for 128 bits, R250-R251 for 64).
                data = size == 6 ? 248U : 250U;
                for (uint i = 0; i < (1U << (int)((size - 4))); ++i) mov_imm(data + i, 0);
            }
            uint address = insn.Bits(24, 8);
            bool extended = insn.Bits(72, 1) != 0;
            if (opcode == 0x986) {
                uint uniform_address = uniform_register(insn.Bits(64, 6));
                // desc[URn][Ra.64]: the uniform pair is a memory (cache policy) descriptor and
                // Ra the whole address.
                bool descriptor_address = (insn[2] & ~(0x3fU | 0xe00U)) == 0x0c101100;
                if (B((!descriptor_address && (insn[2] & ~(0x04000000U | 0x3fU | 0xe00U)) != 0x0810a100)) ||
                    uniform_address == ~0U || B((uniform_address & 1)) || !extended)
                    return fail(index, "native global store uniform addressing mode");
                if (descriptor_address) {
                    if (B((address & 1)) || address >= 246)
                        return fail(index, "native global store register address pair");
                } else if (address == 255) address = uniform_address;
                else {
                    if (B(insn.Bits(90, 1)) && B((B((address & 1)) || address >= 254)))
                        return fail(index, "native global store register address pair");
                    emit(0x5c10000000000000UL | predicate | 250 | (U64(uniform_address) << 8) |
                        (U64(address) << 20) | (U64(1) << 47));
                    emit(0x5c10000000000000UL | predicate | 251 | (U64(uniform_address + 1) << 8) |
                        (U64(B(insn.Bits(90, 1)) ? address + 1 : 255) << 20) | (U64(1) << 43));
                    address = 250;
                }
            } else if ((insn[2] & ~0xe00U) != 0x0010a100) {
                return fail(index, "native global store register addressing mode");
            }
            emit(0xeed8000000000000UL | predicate | data |
                (U64(address) << 8) | (U64(insn.Bits(40, 24)) << 20) |
                (U64(extended) << 45) | (U64(size) << 48));
            break;
        }
        case 0x95b: // Fragment discard; execution predicate is preserved.
            if (B(insn[1]) || insn[2] != 0x03800000)
                return fail(index, "native fragment discard mode");
            emit(0xe33000000000000fUL | predicate);
            break;
        case 0x311: { // F2I.FTZ.U64.TRUNC, register source, destination pair
            if (insn[2] != 0x0021d800 || B((insn[1] & ~0xffU)) || B(insn.Bits(24, 8)) ||
                B((dest & 1)) || dest >= 246)
                return fail(index, "native unsigned 64-bit float conversion mode");
            emit(@base(0x5cb0000000000000UL) | (U64(3) << 8) | (U64(2) << 10) |
                 (U64(insn.Bits(32, 8)) << 20) | (U64(3) << 39) | (U64(1) << 44));
            break;
        }
        case 0x312: { // I2F.F32.(U|S)64, register pair source; sign (74) and rounding (78-79) as Maxwell's
            if ((insn[2] & ~0xc400U) != 0x00301000 || B((insn[1] & ~0xffU)) || B(insn.Bits(24, 8)) ||
                B((insn.Bits(32, 8) & 1)) || insn.Bits(32, 8) >= 254)
                return fail(index, "native 64-bit integer-to-float mode");
            emit(@base(0x5cb8000000000000UL) | (U64(2) << 8) | (U64(3) << 10) | (U64(insn.Bits(74, 1)) << 13) |
                 (U64(insn.Bits(32, 8)) << 20) | (U64(insn.Bits(78, 2)) << 39));
            break;
        }

        case 0x243:
            // F2IP.U8.F32 Rd, Ra, Rb, RZ: Rb's byte in bits 0-7 and Ra's in bits 8-15. The default
            // rounding (bits 78-79 clear) packs unorm values already scaled by 255.
            if (B((insn[1] & ~0xffU)) ||
                (insn[2] != 0x0000c4ffU && insn[2] != 0x000004ffU))
                return fail(index, "native packed float-to-byte mode");
            if (insn.Bits(24, 8) != 255 || insn[2] == 0x000004ffU) {
                void to_byte(uint source, uint target) {
                    emit(0x5800000000000000UL | predicate | target | (U64(source) << 8) |
                         (U64(source) << 20) | (U64(7) << 39) | (U64(7) << 48));
                    emit(0x5c40000000000000UL | predicate | target | (U64(source) << 8) |
                         (U64(target) << 20) | (U64(7) << 48));
                    mov_imm(252, 0);
                    emit(0x5c60000000000000UL | predicate | target | (U64(target) << 8) |
                         (U64(252) << 20) | (U64(15) << 39));
                    mov_imm(252, 0x437f0000U);
                    emit(0x5c60000000000000UL | predicate | target | (U64(target) << 8) |
                         (U64(252) << 20) | (U64(7) << 39));
                    emit(0x5cb0000000000000UL | predicate | target | (U64(2) << 8) | (U64(2) << 10) |
                         (U64(target) << 20) | (U64(insn.Bits(78, 2)) << 39));
                };
                to_byte(insn.Bits(32, 8), 250);
                if (insn.Bits(24, 8) == 255) {
                    emit(@base(0x5c98000000000000UL) | (U64(15) << 39) | (U64(250) << 20));
                    break;
                }
                to_byte(insn.Bits(24, 8), 251);
                emit(@base(0x5c18000000000000UL) | (U64(251) << 8) | (U64(250) << 20) | (U64(8) << 39));
                break;
            }
            insn[0] &= 0x00ffffffU;
            insn[2] = 0x0021e000U;
            opcode = 0x305;
            goto case 0x305;
        case 0x305: case 0xb05: case 0xd05: { // F2I NTZ, register/constant-buffer/uniform source
            uint mode = insn[2] & ~(opcode == 0xd05 ? 0x08000000U : 0U);
            bool signed_half = opcode == 0x305 && (mode == 0x00103100 || mode == 0x00107100);
            bool half = signed_half || (opcode == 0x305 && (mode & ~0x100U) == 0x0010f000);
            bool small = (mode & ~0xc000U) == 0x00212800;
            bool short_trunc = opcode == 0x305 && (mode & ~0x100U) == 0x0021e800;
            // Bit 62: |Rs| on the register form, and |c[..]| on the constant-buffer form; bit 63
            // negates either.
            bool absolute = opcode != 0xd05 && !half && B(insn.Bits(62, 1));
            bool negate = opcode != 0xd05 && !half && B(insn.Bits(63, 1));
            bool byte_ = (mode & ~0x10100U) == 0x0020e000;
            bool word = (mode & ~0x1c100U) == 0x00203000;
            bool extended_word =
                word && (insn.Bits(78, 2) == 2 || (insn.Bits(78, 2) == 3 && !B(insn.Bits(80, 1))));
            if ((!half && !small && !short_trunc && !byte_ && !word) ||
                (signed_half && (B(insn.Bits(24, 8)) || B((insn[3] & 0xc00001ffU)))) ||
                ((byte_ || small || short_trunc || extended_word) &&
                 (B(insn.Bits(24, 8)) || B((insn[3] & 0xc00001ffU)) ||
                  B((opcode == 0xb05 && B(small) && B(insn.Bits(59, 3)))) ||
                  insn.Bits(91, 1) != U32(opcode == 0xd05))) ||
                (opcode != 0xb05 && B((insn[1] & ~(half ? 0x100000ffU :
                    0xffU | (absolute ? 0x40000000U : 0U) | (negate ? 0x80000000U : 0U))))) ||
                (opcode == 0xd05 && uniform_register(insn.Bits(32, 8)) == ~0U)) {
                return fail(index, "native float-to-integer conversion mode");
            }
            bool is_signed = B(insn.Bits(72, 1));
            uint source = opcode == 0xb05 ? materialize(2, 0, 252, absolute, negate)
                       : opcode == 0xd05 ? uniform_register(insn.Bits(32, 8)) : insn.Bits(32, 8);
            if (half) {
                extract_half(new HalfSource(source, B(insn.Bits(60, 1)) ? 3U : 2U, false, false), 0, 252);
                source = 252;
            }
            if (opcode == 0x305 && (absolute || negate))
                source = materialize(0, source, 249, absolute, negate);
            if (signed_half) {
                mov_imm(253, 0x47800000);
                emit(0x5800000000000000UL | predicate | 250 | (U64(252) << 8) |
                     (U64(253) << 20) | (U64(7) << 39) | (U64(1) << 48) | (U64(1) << 54));
                emit(0x5800000000000000UL | predicate | 251 | (U64(252) << 8) |
                     (U64(253) << 20) | (U64(7) << 39) | (U64(4) << 48));
                mov_imm(253, 0xc7800000);
                emit(0x5800000000000000UL | predicate | 253 | (U64(252) << 8) |
                     (U64(253) << 20) | (U64(7) << 39) | (U64(1) << 48));
                emit(0x5c40000000000000UL | predicate | 252 | (U64(252) << 8) |
                     (U64(250) << 20) | (U64(7) << 48));
                emit(@base(0x5cb0000000000000UL) | (U64(2) << 8) | (U64(2) << 10) |
                     (U64(1) << 12) | (U64(252) << 20) | (U64(insn.Bits(78, 2)) << 39));
                mov_imm(250, 0x7fffffff);
                emit(0x5c40000000000000UL | predicate | 251 | (U64(251) << 8) |
                     (U64(250) << 20) | (U64(7) << 48));
                mov_imm(250, 0x80000000);
                emit(0x5c40000000000000UL | predicate | 253 | (U64(253) << 8) |
                     (U64(250) << 20) | (U64(7) << 48));
                emit(@base(0x5c40000000000000UL) | (dest << 8) | (U64(251) << 20) |
                     (U64(1) << 41) | (U64(7) << 48));
                emit(@base(0x5c40000000000000UL) | (dest << 8) | (U64(253) << 20) |
                     (U64(1) << 41) | (U64(7) << 48));
                break;
            }
            if (byte_) {
                emit(0x5800000000000000UL | predicate | 250 | (U64(source) << 8) |
                     (U64(source) << 20) | (U64(7) << 39) | (U64(7) << 48));
                emit(0x5c40000000000000UL | predicate | 250 | (U64(source) << 8) |
                     (U64(250) << 20) | (U64(7) << 48));
                mov_imm(252, is_signed ? 0xc3000000U : 0U);
                emit(0x5c60000000000000UL | predicate | 250 | (U64(250) << 8) |
                     (U64(252) << 20) | (U64(15) << 39));
                mov_imm(252, is_signed ? 0x42fe0000U : 0x437f0000U);
                emit(0x5c60000000000000UL | predicate | 250 | (U64(250) << 8) |
                     (U64(252) << 20) | (U64(7) << 39));
                source = 250;
            } else if (!is_signed) {
                // Clear NaNs without consuming a guest predicate. Clamp the
                // conversion below 2^32, retaining an overflow mask for saturation.
                emit(0x5800000000000000UL | predicate | 250 | (U64(source) << 8) |
                     (U64(source) << 20) | (U64(7) << 39) | (U64(7) << 48));
                emit(0x5c40000000000000UL | predicate | 250 | (U64(source) << 8) |
                     (U64(250) << 20) | (U64(7) << 48));
                if (!small && !short_trunc) {
                mov_imm(252, 0x4f800000);
                emit(0x5800000000000000UL | predicate | 251 | (U64(250) << 8) |
                     (U64(252) << 20) | (U64(7) << 39) | (U64(6) << 48));
                mov_imm(252, 0x4f7fffff);
                emit(0x5c60000000000000UL | predicate | 250 | (U64(250) << 8) |
                     (U64(252) << 20) | (U64(7) << 39));
                }
                source = 250;
            }
            emit(@base(0x5cb0000000000000UL) | (U64(small || short_trunc ? 1U : 2U) << 8) | (U64(2) << 10) |
                 (U64(is_signed) << 12) | (U64(source) << 20) |
                 (U64(insn.Bits(78, 2)) << 39) | (U64(insn.Bits(80, 1)) << 44));
            if (!is_signed && !small && !short_trunc && !byte_) {
                emit(@base(0x5c40000000000000UL) | (dest << 8) | (U64(251) << 20) |
                     (U64(1) << 41) | (U64(7) << 48));
            }
            break;
        }
        case 0xb6f: case 0x370: { // TXQ texture dimensions (0x370: TXQ.B).
            // TXQ.B takes the handle in Ra and the level in Ra+1, as Maxwell's. Up to two outputs
            // go to Rd, Rd+1 directly; more, or a second destination (bits 64-71) for the third and
            // fourth, come out packed in scratch and are copied as for TEX.
            bool bindless = opcode == 0x370;
            uint binding = insn.Bits(54, 5); // descriptor buffer, as for TLD
            uint mask = insn.Bits(72, 4), components = U32(BitOperations.PopCount(mask));
            uint second_dest = insn.Bits(64, 8);
            bool direct = second_dest == 255;
            if (insn.Bits(59, 5) != (bindless ? 1U : 0U) || mask == 0 || (direct && components > 2) ||
                B(insn.Bits(76, 14)) || B(insn.Bits(91, 1)) || (!bindless && binding >= 18) ||
                B((bindless && (B(insn.Bits(32, 27)) || insn.Bits(24, 8) >= 245))) ||
                B((!direct && (second_dest >= 245 || dest >= 245))) ||
                insn.Bits(40, 14) > 0x1fff || B((B(direct) && components == 2 && B((dest & 1)))) || dest >= 254)
                return fail(index, "native texture dimension query mode");
            if (!bindless) {
                if (result.RequiredTextureBuffer != ~0U && result.RequiredTextureBuffer != binding)
                    return fail(index, "native texture descriptor buffer conflict");
                result.RequiredTextureBuffer = binding;
            }
            emit((bindless ? 0xdf50000000000000UL : 0xdf48000000000000UL) | predicate | (direct ? dest : 250) |
                 (U64(insn.Bits(24, 8)) << 8) | (U64(1) << 22) | (U64(mask) << 31) |
                 (U64(bindless ? 0U : insn.Bits(40, 14)) << 36) | (U64(insn.Bits(90, 1)) << 49));
            for (uint i = 0; !direct && i < components; ++i) {
                uint target = i < 2 ? (dest == 255 ? 255U : U32(dest) + i) : second_dest + (i - 2);
                if (target != 255)
                    emit(0x5c98000000000000UL | predicate | target | (U64(15) << 39) | (U64(250 + i) << 20));
            }
            break;
        }
        case 0xb66: { // TLD.SCR, direct texel fetch at level zero (.LZ) or a given level (.LL)
            uint mask = insn.Bits(72, 4);
            bool level = insn.Bits(87, 3) == 3; // .LL: the level follows the coordinates
            // Bit 76: a texel offset after the coordinates and level. 2D only, as Maxwell TLDS
            // 2D.LZ.AOFFI (x, y in Ra, Ra+1, offsets in Rb) or 2D.LL.AOFFI (level, offsets in Rb, Rb+1).
            bool offsets = insn.Bits(76, 1) != 0;
            // Bit 79 (.F16): half2 results, (c0, c1) in the first register and (c2, c3) in the
            // second, as Maxwell TLDS.F16 (bit 59 clear) stores them.
            bool half = insn.Bits(79, 1) != 0;
            uint dimension = insn.Bits(61, 3);
            bool array = dimension == 5;
            bool volume = dimension == 2;
            uint source_a = insn.Bits(24, 8), source_b = insn.Bits(32, 8);
            uint second_dest = insn.Bits(64, 8);
            // TLDS has no single-register G+B or R+B swizzle (Hyrule Warriors fetches G+B): take
            // alpha too, as the three-channel form, with it going to scratch R246.
            if (second_dest == 255 && (mask == 5 || mask == 6)) {
                mask |= 8;
                second_dest = 246;
            }
            bool split = second_dest != 255;
            uint[] rg_masks = {1, 2, 4, 8, 3, 9, 10, 12};
            uint[] rgba_masks = {7, 11, 13, 14, 15};
            uint swizzle = ~0U;
            if (split) {
                for (uint i = 0; i < rgba_masks.Size(); ++i) if (rgba_masks[i] == mask) swizzle = i;
            } else {
                for (uint i = 0; i < rg_masks.Size(); ++i) if (rg_masks[i] == mask) swizzle = i;
            }
            if (!B(insn.Bits(60, 1)) || (dimension != 0 && dimension != 1 && !array && !volume) ||
                swizzle == ~0U || B((insn.Bits(76, 5) & ~0x9U)) || B((half && array && level)) ||
                B((offsets && (dimension != 1 || B((source_a & 1)) || source_a >= 254 || source_b == 255 ||
                             B((level && (B((source_b & 1)) || source_b >= 254)))))) ||
                insn.Bits(81, 3) != 7 || B((insn.Bits(87, 3) != 1 && !level)) || B((level && volume)) ||
                B((level && dimension == 1 && (B((source_a & 1)) || source_a >= 254))) ||
                B((level && array && (B((source_b & 1)) || source_b >= 254))) ||
                B(insn.Bits(59, 1)) ||
                insn.Bits(40, 14) > 0x1fff || insn.Bits(54, 5) >= 18 ||
                dest == 255 || B((!half && B((mask & (mask - 1))) && B((B((dest & 1)) || dest >= 254)))) ||
                B((!half && split && mask == 15 && (B((second_dest & 1)) || second_dest >= 254))) ||
                B((array && (B((source_a & 1)) || source_a >= 249))) ||
                B((volume && (B((source_a & 1)) || source_a >= 254)))) {
                return fail(index, "native texel fetch mode or descriptor binding");
            }
            uint binding = insn.Bits(54, 5);
            if (!bind_texture_buffer(binding)) {
                if (level || offsets || half)
                    return fail(index, "native texture descriptor buffer conflict");
                // A second descriptor buffer: TLD.B (LZ) with the coordinates in R250.. (array
                // index or Z after Ra) and the handle, loaded from that buffer, in R253.
                void copy(uint target, uint source) {
                    emit(0x5c98000000000000UL | predicate | target | (U64(15) << 39) | (U64(source) << 20));
                };
                copy(250, source_a);
                if (array || volume) copy(251, source_a + 1);
                if (dimension != 0) copy(array || volume ? 252U : 251U, source_b);
                emit(0x4c98000000070000UL | 253 | (U64(15) << 39) | (U64(insn.Bits(40, 14)) << 20) |
                     (U64(binding) << 34));
                emit(0xdd00000000000000UL | predicate | 250 | (U64(250) << 8) | (U64(253) << 20) |
                     (U64(array ? 3U : volume ? 4U : dimension == 0 ? 0U : 2U) << 28) | (U64(mask) << 31) |
                     (U64(insn.Bits(90, 1)) << 49) | (U64(7) << 51));
                uint components = U32(BitOperations.PopCount(mask));
                for (uint i = 0; i < components; ++i)
                    copy(i < 2 || !split ? U32(dest) + i : second_dest + (i - 2), 250 + i);
                break;
            }
            if (array && level) {
                // TLDS has no array form with a level: TLD takes (layer, x, y) from Ra and the level
                // from Rb (layer, x in Ra, Ra+1; y, level in Rb, Rb+1 here). Results come out packed,
                // then go where the Ampere destinations (Rd, Rd+1, then the second pair) take them.
                for (uint i = 0; i < 2; ++i) {
                    emit(0x5c98000000000000UL | predicate | (250 + i) | (U64(15) << 39) | (U64(source_a + i) << 20));
                    emit(0x5c98000000000000UL | predicate | (252 + i) | (U64(15) << 39) | (U64(source_b + i) << 20));
                }
                emit(0xdc00000000000000UL | predicate | 246 | (U64(250) << 8) | (U64(253) << 20) |
                     (U64(3) << 28) | (U64(mask) << 31) | (U64(insn.Bits(40, 14)) << 36) |
                     (U64(insn.Bits(90, 1)) << 49) | (U64(7) << 51) | (U64(1) << 55));
                uint components = U32(BitOperations.PopCount(mask));
                for (uint i = 0; i < components; ++i) {
                    uint target = !split || i < 2 ? U32(dest) + i : second_dest + (i - 2);
                    if (target != 255 && (split || i < 2))
                        emit(0x5c98000000000000UL | predicate | target | (U64(15) << 39) | (U64(246 + i) << 20));
                }
                break;
            }
            if (array) {
                emit(0x5c98000000000000UL | predicate | 250 | (U64(15) << 39) | (U64(source_a + 1) << 20));
                emit(0x5c98000000000000UL | predicate | 251 | (U64(15) << 39) | (U64(source_b) << 20));
                source_b = 250;
            }
            emit((0xda00000000000000UL & ~(U64(half) << 59)) | predicate | dest | (U64(source_a) << 8) |
                 (U64(source_b) << 20) | (U64(second_dest) << 28) |
                 (U64(insn.Bits(40, 14)) << 36) | (U64(insn.Bits(90, 1)) << 49) |
                 (U64(swizzle) << 50) |
                 (U64(offsets ? (level ? 12U : 4U)
                      : array ? 8U : volume ? 7U : dimension == 0 ? (level ? 1U : 0U) : (level ? 5U : 2U)) << 53));
            break;
        }
        case 0x367: { // TLD.B at level zero (.LZ), the handle in a register.
            // The values are the coordinates, then the handle, split over Ra and Rb as for TEX.B
            // (ARRAY_2D: layer, x | y, handle); Maxwell TLD.B takes the handle first in Rb.
            uint[] coordinate_count = {1, 2, 3, 0, 0, 3, 0, 0};
            uint[] maxwell_type = {0, 2, 4, 0, 0, 3, 0, 0};
            uint mask = insn.Bits(72, 4), second_dest = insn.Bits(64, 8);
            uint source_a = insn.Bits(24, 8), source_b = insn.Bits(32, 8);
            uint dimension = insn.Bits(61, 3), coordinates = coordinate_count[dimension];
            uint count = coordinates + 1, in_a = (count + 1) / 2;
            if (coordinates == 0 || !B(insn.Bits(59, 1)) || !B(insn.Bits(60, 1)) || B(insn.Bits(40, 19)) ||
                B(insn.Bits(76, 5)) || insn.Bits(81, 3) != 7 || insn.Bits(87, 3) != 1 || B(insn.Bits(91, 1)) ||
                mask == 0 || source_a == 255 || source_a + in_a > 246 || source_b == 255 ||
                source_b + (count - in_a) > 246)
                return fail(index, "native bindless texel fetch mode");
            uint value(uint i) { return i < in_a ? source_a + i : source_b + (i - in_a); };
            void copy(uint target, uint source) {
                emit(0x5c98000000000000UL | predicate | target | (U64(15) << 39) | (U64(source) << 20));
            };
            for (uint i = 0; i < coordinates; ++i) copy(250 + i, value(i));
            copy(246, value(coordinates));
            emit(0xdd00000000000000UL | predicate | 250 | (U64(250) << 8) | (U64(246) << 20) |
                 (U64(maxwell_type[dimension]) << 28) | (U64(mask) << 31) | (U64(insn.Bits(90, 1)) << 49) |
                 (U64(7) << 51));
            uint components = U32(BitOperations.PopCount(mask));
            bool split = second_dest != 255;
            for (uint i = 0; i < components; ++i) {
                uint target = 255;
                if (!split || i < 2) target = dest == 255 ? 255U : U32(dest) + i;
                else target = second_dest + (i - 2);
                if (target != 255) copy(target, 250 + i);
            }
            break;
        }
        case 0xf66: { // TLD.SCR.LZ with the handle in a uniform register (bit 91).
            // Maxwell TLD.B with the coordinates in R250.. (as 0xb66's second-buffer form: x | y,
            // layer, x | y or x, y | z over Ra and Rb) and the handle in Rb.
            uint handle = uniform_register(insn.Bits(40, 6));
            uint mask = insn.Bits(72, 4), second_dest = insn.Bits(64, 8);
            uint source_a = insn.Bits(24, 8), source_b = insn.Bits(32, 8);
            uint dimension = insn.Bits(61, 3);
            bool array = dimension == 5, volume = dimension == 2;
            bool split = second_dest != 255;
            uint components = U32(BitOperations.PopCount(mask));
            if (handle == ~0U || handle >= 246 || B(insn.Bits(46, 13)) || !B(insn.Bits(91, 1)) ||
                !B(insn.Bits(60, 1)) || B(insn.Bits(59, 1)) || B(insn.Bits(76, 5)) || insn.Bits(81, 3) != 7 ||
                insn.Bits(87, 3) != 1 || (dimension != 0 && dimension != 1 && !array && !volume) ||
                mask == 0 || B(((array || volume) && source_a + 1 >= high_uniform_base)) ||
                B((dest != 255 && dest + (split ? Math.Min(components, 2U) : components) > high_uniform_base)) ||
                (split && components > 2 && second_dest + components - 2 > high_uniform_base))
                return fail(index, "native bindless texel fetch mode");
            void copy(uint target, uint source) {
                emit(0x5c98000000000000UL | predicate | target | (U64(15) << 39) | (U64(source) << 20));
            };
            copy(250, source_a);
            if (array || volume) copy(251, source_a + 1);
            if (dimension != 0) copy(array || volume ? 252U : 251U, source_b);
            copy(246, handle);
            emit(0xdd00000000000000UL | predicate | 250 | (U64(250) << 8) | (U64(246) << 20) |
                 (U64(array ? 3U : volume ? 4U : dimension == 0 ? 0U : 2U) << 28) | (U64(mask) << 31) |
                 (U64(insn.Bits(90, 1)) << 49) | (U64(7) << 51));
            for (uint i = 0; i < components; ++i) {
                uint target = !split || i < 2 ? (dest == 255 ? 255U : U32(dest) + i)
                                                   : second_dest + (i - 2);
                if (target != 255) copy(target, 250 + i);
            }
            break;
        }
        case 0x91a: { // DEPBAR.LE SBn, count, {list}: scoreboard wait, represented by IR data dependencies.
            // Any scoreboard (bits 44-46), count (38-43) and wait list (32-37) is the same no-op here.
            // DEPBAR.LE SB0, n (scoreboard 0x8000) waits on LDGSTS copy groups. The copies below
            // complete synchronously, so only make their shared memory stores visible.
            uint scoreboard = insn[1] & ~0xfffU;
            if (insn[0] != 0x791a || (insn[1] & ~0x7fffU) != 0x8000 || B(insn[2])) {
                return fail(index, "native dependency barrier mode");
            }
            if (uses_async_copy && scoreboard == 0x8000) emit(0xef98000000000000UL | predicate); // MEMBAR.CTA
            break;
        }
        case 0x9af: { // LDGDEPBAR: commit a copy group (the copies are already complete).
            if (insn[0] != 0x79af || B(insn[1]) || B(insn[2]))
                return fail(index, "native copy-group commit mode");
            emit(0xef98000000000000UL | predicate);
            break;
        }
        case 0xb69: case 0xf69: case 0x36a: { // TMML (LOD query), 1D/2D/3D/CUBE. Results are 8.8 fixed point, as on Maxwell.
            // 0xf69: the handle in a uniform register (bit 91); 0x36a, TMML.B: the handle in Rb and
            // every coordinate in Ra. Both become Maxwell TMML.B with the handle in Rb.
            bool bindless = opcode != 0xb69;
            uint handle = opcode == 0xf69 ? uniform_register(insn.Bits(40, 6))
                             : opcode == 0x36a ? insn.Bits(32, 8) : 255;
            uint mask = insn.Bits(72, 4), second_dest = insn.Bits(64, 8);
            uint source_a = insn.Bits(24, 8), source_b = insn.Bits(32, 8);
            uint binding = insn.Bits(54, 5), dimension = insn.Bits(61, 3);
            // Coordinates: all from Ra when Rb is RZ, else split as for TEX (2D: Ra, Rb).
            uint[] coordinate_count = {1, 2, 3, 3};
            uint coordinates = dimension < 4 ? coordinate_count[dimension] : 0;
            uint in_a = source_b == 255 || opcode == 0x36a ? coordinates : (coordinates + 1) / 2;
            if (coordinates == 0 || B(insn.Bits(76, 4)) || B(insn.Bits(80, 10)) || mask == 0 ||
                insn.Bits(59, 1) != (opcode == 0x36a ? 1U : 0U) || source_a + in_a > 246 ||
                B((in_a < coordinates && source_b + (coordinates - in_a) > 246)) ||
                B((opcode == 0xf69 && (handle >= 246 || B(insn.Bits(46, 13)) || !B(insn.Bits(91, 1))))) ||
                B((opcode == 0x36a && (handle >= 246 || B(insn.Bits(40, 19)) || B(insn.Bits(91, 1))))) ||
                B((!bindless && (insn.Bits(40, 14) > 0x1fff || binding >= 18 ||
                               (result.RequiredTextureBuffer != ~0U && result.RequiredTextureBuffer != binding)))))
                return fail(index, "native texture LOD query mode");
            if (!bindless) result.RequiredTextureBuffer = binding;
            for (uint i = 0; i < coordinates; ++i)
                emit(0x5c98000000000000UL | predicate | (250 + i) | (U64(15) << 39) |
                     (U64(i < in_a ? source_a + i : source_b + (i - in_a)) << 20));
            if (bindless) emit(0x5c98000000000000UL | predicate | 246 | (U64(15) << 39) | (U64(handle) << 20));
            emit((bindless ? 0xdf60000000000000UL : 0xdf58000000000000UL) | predicate | 250 | (U64(250) << 8) |
                 (U64(bindless ? 246U : 255U) << 20) | (U64(dimension * 2) << 28) | (U64(mask) << 31) |
                 (U64(bindless ? 0U : insn.Bits(40, 14)) << 36) | (U64(insn.Bits(90, 1)) << 49));
            uint components = U32(BitOperations.PopCount(mask));
            bool split = second_dest != 255;
            for (uint i = 0; i < components; ++i) {
                uint target = 255;
                if (!split || i < 2) target = dest == 255 ? 255U : U32(dest) + i;
                else target = second_dest + (i - 2);
                if (target != 255)
                    emit(0x5c98000000000000UL | predicate | target | (U64(15) << 39) | (U64(250 + i) << 20));
            }
            break;
        }
        case 0x364: { // TLD4.B (gather) with the handle in a register: packed 2D (x, y | handle).
            // Like TEX 0x361 the handle follows the coordinates; no offsets, depth compare or F16.
            uint mask = insn.Bits(72, 4), second_dest = insn.Bits(64, 8);
            uint source_a = insn.Bits(24, 8), source_b = insn.Bits(32, 8);
            uint limit = uses_uniform_bank ? high_uniform_base : 246U;
            uint components = U32(BitOperations.PopCount(mask));
            bool split = second_dest != 255;
            uint low_count = split ? Math.Min(components, 2U) : components;
            if (B(insn.Bits(40, 19)) || !B(insn.Bits(59, 1)) || !B(insn.Bits(60, 1)) || insn.Bits(61, 3) != 1 ||
                B(insn.Bits(76, 5)) || insn.Bits(81, 3) != 7 || B(insn.Bits(89, 1)) || B(insn.Bits(91, 1)) ||
                mask == 0 || source_a == 255 || source_a + 2 > limit || source_b >= limit ||
                (dest != 255 && dest + low_count > limit) ||
                B((split && (components <= 2 || second_dest + components - 2 > limit))))
                return fail(index, "native bindless texture gather mode");
            void copy(uint target, uint source) {
                emit(0x5c98000000000000UL | predicate | target | (U64(15) << 39) | (U64(source) << 20));
            };
            copy(250, source_a);
            copy(251, source_a + 1);
            copy(246, source_b);
            // Maxwell TLD4.B: handle in Rb, component at 38.
            emit(0xdec0000000000000UL | predicate | 250 | (U64(250) << 8) | (U64(246) << 20) |
                 (U64(2) << 28) | (U64(mask) << 31) | (U64(insn.Bits(87, 2)) << 38) |
                 (U64(insn.Bits(90, 1)) << 49) | (U64(7) << 51));
            for (uint i = 0; i < components; ++i) {
                uint target = !split || i < 2 ? (dest == 255 ? 255U : U32(dest) + i)
                                                  : second_dest + (i - 2);
                if (target != 255) copy(target, 250 + i);
            }
            break;
        }
        case 0xb63: case 0xf63: { // TLD4 (gather); component at bits 87-88.
            // 0xf63: the handle in a uniform register (bit 91), Maxwell TLD4.B with the handle in Rb.
            bool bindless = opcode == 0xf63;
            uint handle = bindless ? uniform_register(insn.Bits(40, 6)) : 255;
            uint mask = insn.Bits(72, 4), second_dest = insn.Bits(64, 8);
            uint source_a = insn.Bits(24, 8), source_b = insn.Bits(32, 8);
            uint binding = insn.Bits(54, 5);
            // Bit 76: a texel offset after the coordinates (x, y in Ra, Ra+1, offsets in Rb, as
            // for TEX); Maxwell TLD4 takes it as AOFFI (bits 54-55) from Rb.
            bool offsets = insn.Bits(76, 1) != 0;
            // Bits 76-77 = 2: per-tap offsets (PTP), x and y in Ra, Ra+1 and the packed offsets in
            // Rb, Rb+1. The host gather needs them as constants, so they must be proven immediates.
            bool per_tap = insn.Bits(76, 2) == 2;
            bool depth = insn.Bits(78, 1) != 0;
            bool half = insn.Bits(79, 1) != 0;
            uint register_limit = uses_uniform_bank ? high_uniform_base : 246U;
            bool array = insn.Bits(61, 3) == 5;
            if (bindless && array) {
                uint count = U32(BitOperations.PopCount(mask));
                uint first_count = second_dest == 255 ? count : Math.Min(count, 2U);
                uint second_count = count - first_count;
                if (!B(insn.Bits(60, 1)) ||
                    (insn[2] & ~0x05800fffU) != 0x081e0000U ||
                    source_a + 2 > register_limit || source_b >= register_limit ||
                    (dest != 255 && dest + first_count > register_limit) ||
                    (second_dest != 255 && second_dest + second_count > register_limit) ||
                    (dest != 255 && second_dest != 255 && second_count != 0 &&
                     second_dest < dest + first_count && dest < second_dest + second_count) ||
                    B((insn[3] & 0xc00001ffU)))
                    return fail(index, "native uniform array texture gather mode");
            }
            if (B((insn.Bits(61, 3) != 1 && !array)) || B((B(insn.Bits(77, 1)) && !per_tap)) || insn.Bits(81, 3) != 7 ||
                B(insn.Bits(59, 1)) ||
                B((depth && (B(bindless) || B(array) || B(offsets) || B(per_tap) || B(half) ||
                           !B(insn.Bits(60, 1)) || B(insn.Bits(80, 1)) || B(insn.Bits(91, 1)) ||
                           (source_a != 255 && source_a + 2 > register_limit) ||
                           (source_b != 255 && source_b >= register_limit)))) ||
                B((array && !bindless && (!B(insn.Bits(60, 1)) ||
                           (insn[2] & ~0x058000ffU) != 0x001e0f00U ||
                           source_a + 2 > register_limit || source_b >= register_limit ||
                           dest + 2 > register_limit || second_dest + 2 > register_limit ||
                           (second_dest < dest + 2 && dest < second_dest + 2) ||
                           B((insn[3] & 0xc00001ffU))))) ||
                B((half && (B(bindless) || B(offsets) || !B(insn.Bits(60, 1)) || B(insn.Bits(80, 1)) ||
                          B(insn.Bits(91, 1)) ||
                          (source_b != 255 && source_b >= register_limit) ||
                          dest >= register_limit ||
                          // Up to two taps pack into dest alone (mask 3 with no second register).
                          B((BitOperations.PopCount(mask) > 2 && second_dest >= register_limit))))) ||
                B((offsets && ((bindless && (array || source_a + 2 > register_limit || source_b >= register_limit)) ||
                             source_b == 255 ||
                             (source_a != 255 && source_a >= 245)))) ||
                B((per_tap && (B(bindless) || B(depth) || B(insn.Bits(80, 1)) || B(insn.Bits(91, 1))))) ||
                B(insn.Bits(89, 1)) || mask == 0 ||
                B((bindless ? (handle >= 246 || B(insn.Bits(46, 13)) || !B(insn.Bits(91, 1)))
                          : (insn.Bits(40, 14) > 0x1fff || binding >= 18))))
                return fail(index, "native texture gather mode");
            // A second descriptor buffer is gathered bindless (TLD4.B), its handle loaded from that
            // buffer, as TEX does; the shaders mixing two buffers were rejected before.
            bool second_buffer = !bindless && !bind_texture_buffer(binding);
            if (per_tap) {
                if (!B(insn.Bits(60, 1)) || mask != 15 || source_a + 1 >= register_limit ||
                    source_b + 1 >= register_limit || (dest != 255 && dest + 1 >= register_limit) ||
                    (second_dest != 255 && second_dest + 1 >= register_limit))
                    return fail(index, "native per-tap gather operands");
                uint?[] packed_offsets = new uint?[2];
                bool imad_offsets = false;
                bool predicate_clobbered = false;
                if (!bounded_control)
                    return fail(index, "native per-tap gather control-flow proof");
                for (uint cursor = index; cursor > 0 && (!(packed_offsets[0]).HasValue || !(packed_offsets[1]).HasValue);) {
                    if (control_entries[cursor])
                        return fail(index, "native per-tap gather control-flow proof");
                    var definition = code[--cursor];
                    if (definition.Bits(12, 4) == 15) continue;
                    uint op = definition.Bits(0, 12), target = definition.Bits(16, 8);
                    uint written_predicates = ~0U;
                    switch (op) {
                    case 0x802: case 0x805: case 0x819: case 0x823: case 0x91a:
                    case 0x945: case 0x947:
                        written_predicates = 0;
                        break;
                    case 0x212: case 0x812: case 0x224: case 0x424: case 0x624:
                    case 0x824: case 0xb63:
                        written_predicates = 1U << (int)(definition.Bits(81, 3));
                        break;
                    case 0x810: case 0x81c:
                        written_predicates = (1U << (int)(definition.Bits(81, 3))) |
                                             (1U << (int)(definition.Bits(84, 3)));
                        break;
                    }
                    predicate_clobbered |= (written_predicates & (1U << (int)((native_predicate & 7)))) != 0;
                    if (op == 0xb63) {
                        uint definition_count = U32(BitOperations.PopCount(definition.Bits(72, 4)));
                        uint high = definition.Bits(64, 8);
                        bool packed = B(definition.Bits(79, 1));
                        uint first_count = packed ? (high == 255 ? (definition_count + 1) / 2 : 1U) : high == 255 ? definition_count : Math.Min(definition_count, 2U);
                        uint second_count = packed ? (definition_count > 2 ? 1U : 0U) : definition_count > 2 ? definition_count - 2 : 0U;
                        for (uint i = 0; i < 2; ++i) {
                            if ((packed_offsets[i]).HasValue) continue;
                            uint reg = source_b + i;
                            if ((target != 255 && reg >= target && reg < target + first_count) ||
                                (high != 255 && reg >= high && reg < high + second_count))
                                return fail(index, "native per-tap gather offset clobber");
                        }
                        continue;
                    }
                    var count = write_count(definition);
                    switch (op) {
                    case 0x827: case 0x307: case 0x211: case 0x811: case 0xa11: case 0x624: case 0x831:
                    case 0x832: case 0x224:
                        count = 1;
                        break;
                    case 0x805:
                        count = 2;
                        break;
                    case 0x945: case 0x947: case 0x91a:
                        count = 0;
                        break;
                    }
                    if (!(count).HasValue) return fail(index, "native per-tap gather offset proof");
                    for (uint i = 0; i < 2; ++i) {
                        if ((packed_offsets[i]).HasValue || target == 255 || source_b + i < target || source_b + i >= target + count.Value)
                            continue;
                        bool move = op == 0x802 && !B(definition.Bits(24, 8)) && definition[2] == 0xf00;
                        bool imad = op == 0x424 && definition.Bits(24, 8) == 255 && definition[2] == 0x078e00ff;
                        bool same_guard = imad && definition.Bits(12, 4) == native_predicate &&
                                                !predicate_clobbered;
                        if (B((!move && !imad)) || B((definition.Bits(12, 4) != 7 && !same_guard)) ||
                            B((definition[3] & 0xc00001ffU)))
                            return fail(index, "native per-tap gather nonconstant offsets");
                        imad_offsets |= imad;
                        packed_offsets[i] = definition[1];
                    }
                }
                if (!half && !imad_offsets) {
                    bool terminal = false;
                    foreach (var instruction in code) {
                        if (instruction.Bits(12, 4) == 15) continue;
                        uint op = instruction.Bits(0, 12);
                        if (op == 0x94d && instruction.Bits(12, 4) == 7) {
                            terminal = true;
                            break;
                        }
                        if (op != 0x326 && op != 0x802 && op != 0x307 && op != 0x421 &&
                            op != 0xa20 && op != 0xb63 && op != 0x221 && op != 0x823 &&
                            op != 0x423 && op != 0x223 && op != 0x308 && op != 0x220 && op != 0x820)
                            return fail(index, "native per-tap gather control-flow proof");
                    }
                    if (!terminal) return fail(index, "native per-tap gather terminal proof");
                }
                foreach (var packed in packed_offsets) {
                    if (!(packed).HasValue) return fail(index, "native per-tap gather missing offsets");
                    // Each byte is a signed 6-bit offset; the gather reads only the low six bits.
                    for (uint shift = 0; shift < 32; shift += 8) {
                        uint value = (packed.Value >> (int)(shift)) & 255;
                        if (value > 31 && value < 224)
                            return fail(index, "native per-tap gather offset range");
                    }
                }
                for (uint i = 0; i < packed_offsets.Size(); ++i)
                    emit(0x010000000007f000UL | (248 + i) | (U64(packed_offsets[i].Value) << 20));
            }
            // Two coordinates: x in Ra, y in Rb (the coordinate list splits evenly across them);
            // with an offset, x and y in Ra, Ra+1 and the offset in Rb.
            uint y = !offsets && !per_tap && !depth ? source_b : source_a == 255 ? 255U : source_a + 1;
            emit(0x5c98000000000000UL | predicate | 250 | (U64(15) << 39) | (U64(source_a) << 20));
            emit(0x5c98000000000000UL | predicate | 251 | (U64(15) << 39) |
                 (U64(array ? source_a + 1 : y) << 20));
            if (array)
                emit(0x5c98000000000000UL | predicate | 252 | (U64(15) << 39) | (U64(source_b) << 20));
            if (second_buffer) {
                // TLD4.B meta registers: the handle first, then the offsets (AOFFI one register,
                // PTP the two packed in R248, R249) and the depth reference. With PTP the handle
                // goes in R247 so the packed offsets stay where they were written.
                uint meta = per_tap ? 247U : 246U;
                emit(0x4c98000000070000UL | meta | (U64(15) << 39) | (U64(insn.Bits(40, 14)) << 20) |
                     (U64(binding) << 34));
                if (offsets || depth)
                    emit(0x5c98000000000000UL | predicate | 247 | (U64(15) << 39) | (U64(source_b) << 20));
                emit(0xdec0000000000000UL | predicate | 250 | (U64(250) << 8) | (U64(meta) << 20) |
                     (U64(array ? 3U : 2U) << 28) | (U64(mask) << 31) |
                     (U64(per_tap ? 2U : offsets ? 1U : 0U) << 36) | (U64(insn.Bits(87, 2)) << 38) |
                     (U64(insn.Bits(90, 1)) << 49) | (U64(depth) << 50) | (U64(7) << 51));
            } else if (bindless) {
                // Meta registers as in the second-buffer form: the handle, then the AOFFI offsets.
                emit(0x5c98000000000000UL | predicate | 246 | (U64(15) << 39) | (U64(handle) << 20));
                if (offsets)
                    emit(0x5c98000000000000UL | predicate | 247 | (U64(15) << 39) | (U64(source_b) << 20));
                emit(0xdec0000000000000UL | predicate | 250 | (U64(250) << 8) | (U64(246) << 20) |
                     (U64(array ? 3U : 2U) << 28) | (U64(mask) << 31) |
                     (U64(offsets ? 1U : 0U) << 36) | (U64(insn.Bits(87, 2)) << 38) |
                     (U64(insn.Bits(90, 1)) << 49) | (U64(7) << 51));
            } else {
            if (offsets || depth)
                emit(0x5c98000000000000UL | predicate | 246 | (U64(15) << 39) | (U64(source_b) << 20));
            emit(0xc800000000000000UL | predicate | 250 | (U64(250) << 8) |
                 (U64(per_tap ? 248U : offsets || depth ? 246U : 255U) << 20) | (U64(array ? 3U : 2U) << 28) | (U64(mask) << 31) |
                 (U64(insn.Bits(40, 14)) << 36) | (U64(insn.Bits(90, 1)) << 49) | (U64(depth) << 50) | (U64(7) << 51) |
                 (U64(per_tap ? 2U : offsets ? 1U : 0U) << 54) | (U64(insn.Bits(87, 2)) << 56));
            }
            if (half) {
                // The gather writes its taps packed from R250; an odd last tap pairs with RZ.
                uint taps = U32(BitOperations.PopCount(mask));
                for (uint i = 0; i * 2 < taps; ++i) {
                    emit(0x5ca8000000000000UL | predicate | 246 | (U64(1) << 8) | (U64(2) << 10) |
                         (U64(250 + i * 2) << 20));
                    emit(0x5ca8000000000000UL | predicate | 247 | (U64(1) << 8) | (U64(2) << 10) |
                         (U64(i * 2 + 1 < taps ? 251 + i * 2 : 255U) << 20));
                    emit(0x5c18000000000000UL | predicate | (i == 0 ? dest : second_dest) |
                         (U64(247) << 8) | (U64(246) << 20) | (U64(16) << 39));
                }
                break;
            }
            uint components = U32(BitOperations.PopCount(mask));
            bool split = second_dest != 255;
            for (uint i = 0; i < components; ++i) {
                uint target = 255;
                if (!split || i < 2) target = dest == 255 ? 255U : U32(dest) + i;
                else target = second_dest + (i - 2);
                if (target != 255)
                    emit(0x5c98000000000000UL | predicate | target | (U64(15) << 39) | (U64(250 + i) << 20));
            }
            break;
        }
        case 0xb60: case 0xf60: case 0x361: { // TEX; 0xf60: handle in a uniform register; 0x361: TEX.B
            // Bindless forms become Maxwell TEX.B, the handle first among the meta registers (the
            // texture pass tracks it back to its constant buffer words). 0x361 lists the handle
            // right after the coordinates, then the LOD and the offsets (see general_tex).
            bool bindless = opcode != 0xb60;
            uint handle = opcode == 0xf60 ? uniform_register(insn.Bits(40, 6)) : 255;
            if (bindless && ((opcode == 0xf60 && (handle == ~0U || handle >= 246 || B(insn.Bits(46, 13)))) ||
                             (opcode == 0x361 && (B(insn.Bits(40, 19)) || insn.Bits(87, 3) > 3))))
                return fail(index, "native bindless texture mode");
            uint mask = insn.Bits(72, 4);
            uint second_dest = insn.Bits(64, 8);
            uint[] rg_masks = {1, 2, 4, 8, 3, 9, 10, 12};
            uint[] rgba_masks = {7, 11, 13, 14, 15};
            uint swizzle = ~0U;
            bool split = second_dest != 255;
            bool depth_compare = B(insn.Bits(78, 1));
            uint dimension = insn.Bits(61, 3), lod = insn.Bits(87, 3);
            uint source_a = insn.Bits(24, 8), source_b = insn.Bits(32, 8);
            if (!bindless && dimension == 5 && depth_compare && lod == 0 && !split && mask == 1 &&
                (result.RequiredTextureBuffer == ~0U ||
                 result.RequiredTextureBuffer == insn.Bits(54, 5))) {
                if (!B(insn.Bits(60, 1)) || B(insn.Bits(76, 2)) || insn.Bits(81, 3) != 7 ||
                    B(insn.Bits(59, 1)) || insn.Bits(40, 14) > 0x1fff || insn.Bits(54, 5) >= 18 ||
                    B((source_a & 1)) || B((source_b & 1)) || source_a >= 248 || source_b >= 248 || dest >= 248)
                    return fail(index, "native array depth comparison sample mode");
                if (!bind_texture_buffer(insn.Bits(54, 5)))
                    return fail(index, "native texture descriptor buffer conflict");
                for (uint i = 0; i < 3; ++i) {
                    uint source = i < 2 ? source_a + i : source_b;
                    emit(0x5c98000000000000UL | predicate | (250 + i) |
                         (U64(15) << 39) | (U64(source) << 20));
                }
                emit(0xc000000000000000UL | predicate | dest | (U64(250) << 8) |
                     (U64(source_b + 1) << 20) | (U64(3) << 28) | (U64(1) << 31) |
                     (U64(insn.Bits(40, 14)) << 36) | (U64(insn.Bits(90, 1)) << 49) |
                     (U64(1) << 50) | (U64(7) << 51));
                break;
            }
            // General form: gather the native operand list (array index first, coordinates,
            // then LOD/bias and the depth reference) into Maxwell TEX's coordinate and meta
            // registers, sample into scratch and copy the packed components out. Native packs
            // up to four values as halves of Ra and Rb (1+1, 2+1, 2+2), five or more as 4+n.
            bool general_tex() {
                uint[] coordinate_count = {1, 2, 3, 3, 2, 3, 0, 4};
                uint[] maxwell_type = {0, 2, 4, 6, 1, 3, 0, 7};
                bool offsets = insn.Bits(76, 1) != 0;
                // Bit 59 is .B, set in 0x361's encoding.
                if (dimension == 6 || B(insn.Bits(77, 1)) || insn.Bits(81, 3) != 7 ||
                    insn.Bits(59, 1) != (opcode == 0x361 ? 1U : 0U) ||
                    B((!bindless && (insn.Bits(40, 14) > 0x1fff || insn.Bits(54, 5) >= 18))) ||
                    (lod > 3 && lod != 6 && lod != 7) || (depth_compare && mask != 1) || mask == 0)
                    return false;
                uint coordinates = coordinate_count[dimension];
                bool has_lod = lod == 2 || lod == 3 || lod == 6 || lod == 7;
                // 0x361's handle is one more value after the coordinates: 2D.DC is (x, y | handle,
                // reference), ARRAY_2D is (layer, x | y, handle); with five or more values the
                // coordinates keep Ra (CUBE.LL is (x, y, z | handle, LOD)).
                uint handle_slot = opcode == 0x361 ? 1U : 0U;
                uint count = coordinates + handle_slot + (has_lod ? 1U : 0U) + (offsets ? 1U : 0U) +
                                  (depth_compare ? 1U : 0U);
                // Unpacked 0x361 (bit 60 clear): Ra holds the coordinates, Rb the handle and the rest.
                uint in_a = opcode == 0x361 && !B(insn.Bits(60, 1)) ? coordinates
                               : count <= 4 ? (count + 1) / 2 : opcode == 0x361 ? coordinates : 4;
                uint value(uint i) {
                    if (i < in_a) return source_a == 255 ? 255U : source_a + i;
                    return source_b == 255 ? 255U : source_b + (i - in_a);
                };
                if ((source_a != 255 && source_a + in_a > 246) ||
                    (count > in_a && source_b != 255 && source_b + (count - in_a) > 246))
                    return false;
                uint texture_handle = opcode == 0x361 ? value(coordinates) : handle;
                if (bindless && texture_handle >= 246) return false;
                // A second descriptor buffer is read bindless: TEX.B takes the handle, loaded from
                // that buffer, as its first meta register.
                uint binding = insn.Bits(54, 5);
                bool second_buffer = !bindless && !bind_texture_buffer(binding);
                bool tex_b = bindless || second_buffer;
                void copy(uint target, uint source) {
                    emit(0x5c98000000000000UL | predicate | target | (U64(15) << 39) | (U64(source) << 20));
                };
                // Coordinates to R250.., then LOD/bias, offsets and reference to R246...
                for (uint i = 0; i < coordinates; ++i) copy(250 + i, value(i));
                uint meta = 246;
                uint next = coordinates + handle_slot;
                if (bindless) copy(meta++, texture_handle);
                else if (second_buffer)
                    emit(0x4c98000000070000UL | meta++ | (U64(15) << 39) | (U64(insn.Bits(40, 14)) << 20) |
                         (U64(binding) << 34));
                if (has_lod) copy(meta++, value(next++));
                if (offsets) copy(meta++, value(next++));
                if (depth_compare) copy(meta++, value(next++));
                if (tex_b) {
                    // Maxwell TEX.B: AOFFI at 36 and the LOD mode at 37 instead of 54 and 55.
                    emit(0xde80000000000000UL | predicate | 250 | (U64(250) << 8) | (U64(246) << 20) |
                         (U64(maxwell_type[dimension]) << 28) | (U64(mask) << 31) | (U64(offsets) << 36) |
                         (U64(lod) << 37) | (U64(insn.Bits(90, 1)) << 49) | (U64(depth_compare) << 50) |
                         (U64(7) << 51));
                } else {
                emit(0xc000000000000000UL | predicate | 250 | (U64(250) << 8) |
                     (U64(meta == 246 ? 255U : 246U) << 20) | (U64(maxwell_type[dimension]) << 28) |
                     (U64(mask) << 31) | (U64(insn.Bits(40, 14)) << 36) |
                     (U64(insn.Bits(90, 1)) << 49) | (U64(depth_compare) << 50) | (U64(7) << 51) |
                     (U64(offsets) << 54) | (U64(lod) << 55));
                }
                uint components = U32(BitOperations.PopCount(mask));
                if (B(insn.Bits(79, 1))) {
                    // FP16 results: (c0, c1) packed into the first destination, (c2, c3) into
                    // the second, the first of each pair in the low half as Maxwell TEXS.F16 packs
                    // (F2F writes the half in the low 16 bits; ISCADD Rd = (Ra << 16) + Rb merges).
                    void pack(uint target, uint low, uint high) {
                        if (target == 255) return;
                        emit(0x5ca8000000000000UL | predicate | 246 | (U64(1) << 8) | (U64(2) << 10) | (U64(low) << 20));
                        emit(0x5ca8000000000000UL | predicate | 247 | (U64(1) << 8) | (U64(2) << 10) | (U64(high) << 20));
                        emit(0x5c18000000000000UL | predicate | target | (U64(247) << 8) | (U64(246) << 20) | (U64(16) << 39));
                    };
                    pack(U32(dest), 250, components > 1 ? 251U : 255U);
                    if (components > 2) {
                        pack(split ? second_dest : (dest == 255 ? 255U : U32(dest) + 1),
                             252, components > 3 ? 253U : 255U);
                    }
                    return true;
                }
                for (uint i = 0; i < components; ++i) {
                    uint target = 255;
                    if (!split) target = dest == 255 ? 255U : U32(dest) + i;
                    else if (i < 2) target = dest == 255 ? 255U : U32(dest) + i;
                    else target = second_dest + (i - 2);
                    if (target != 255) copy(target, 250 + i);
                }
                return true;
            };
            if (split) {
                for (uint i = 0; i < rgba_masks.Size(); ++i) if (rgba_masks[i] == mask) swizzle = i;
            } else {
                for (uint i = 0; i < rg_masks.Size(); ++i) if (rg_masks[i] == mask) swizzle = i;
            }
            // SM70 scalar coordinates and split RG/BA destinations match TEXS.
            // Descriptor offsets are words in both encodings, but only native
            // explicitly encodes the constant-buffer binding.
            if (B(bindless) || !B(insn.Bits(60, 1)) || (dimension != 1 && dimension != 5) ||
                swizzle == ~0U || B(insn.Bits(76, 2)) ||
                insn.Bits(81, 3) != 7 || B((lod > 1 && (lod != 3 || dimension != 1 || depth_compare))) ||
                B((lod == 3 && (B((source_a & 1)) || source_a >= 249))) ||
                B(insn.Bits(59, 1)) ||
                insn.Bits(40, 14) > 0x1fff || insn.Bits(54, 5) != 2 ||
                dest == 255 || B((B(mask & (mask - 1)) && B((B((dest & 1)) || dest >= 254)))) ||
                B((split && mask == 15 && (B((second_dest & 1)) || second_dest >= 254))) ||
                B((depth_compare && (dimension != 1 || B(split) || mask != 1 || B((source_a & 1)) || source_a >= 249))) ||
                B((dimension == 5 && (B((source_a & 1)) || source_a >= 249))) || B(bindless) ||
                !bind_texture_buffer(2)) {
                if (!general_tex()) return fail(index, "native texture mode or descriptor binding");
                break;
            }
            emit((0xd800000000000000UL & ~(U64(insn.Bits(79, 1)) << 59)) | predicate | dest | (U64(insn.Bits(24, 8)) << 8) |
                 (U64(insn.Bits(32, 8)) << 20) | (U64(insn.Bits(64, 8)) << 28) |
                 (U64(insn.Bits(40, 14)) << 36) | (U64(insn.Bits(90, 1)) << 49) |
                 (U64(swizzle) << 50) | (U64(dimension == 5 ? (B(lod) ? 8U : 7U) : depth_compare ? (B(lod) ? 6U : 4U) : (lod == 3 ? 3U : B(lod) ? 2U : 1U)) << 53));
            break;
        }
        case 0x816: { // Captured PRMT half-word concatenation
            if (insn[1] > 0xffff || B((insn[2] & ~0xffU))) return fail(index, "native permutation mode");
            if (insn[1] != 0x5410 && insn[1] != 0x5432 && insn[1] != 0x7610 && insn[1] != 0x7632) {
                // Byte i of the result is byte (s & 7) of c:a, or its sign bit repeated when s & 8.
                uint permute_a = insn.Bits(24, 8), c = insn.Bits(64, 8);
                if (permute_a >= 250 && permute_a != 255) return fail(index, "native permutation source");
                mov_imm(250, 0);
                for (uint i = 0; i < 4; ++i) {
                    uint select = (insn[1] >> (int)((i * 4))) & 0xf;
                    uint source = B(select & 4) ? c : permute_a, position = (select & 3) * 8;
                    emit(0x3800000000000000UL | predicate | 251 | (U64(source) << 8) |
                         (U64(B(select & 8) ? (position + 7) | (1U << 8) : position | (8U << 8)) << 20) |
                         (U64((select >> 3) & 1) << 48));
                    emit(0x36f0000000000000UL | predicate | 250 | (U64(251) << 8) |
                         (U64((i * 8) | (8U << 8)) << 20) | (U64(250) << 39));
                }
                emit(@base(0x5c10000000000000UL) | (U64(250) << 8) | (U64(255) << 20));
                break;
            }
            uint a = insn.Bits(24, 8), b = insn.Bits(64, 8);
            if (insn[1] != 0x5432 && insn[1] != 0x7632) {
                emit(0x0400000000000000UL | predicate | 250 | (U64(a) << 8) | (U64(0xffff) << 20));
            } else {
                emit(0x3828000000000000UL | predicate | 250 | (U64(a) << 8) | (U64(16) << 20));
            }
            if (insn[1] == 0x7610 || insn[1] == 0x7632) {
                emit(0x0400000000000000UL | predicate | 251 | (U64(b) << 8) | (U64(0xffff0000) << 20));
                emit(@base(0x5c10000000000000UL) | (U64(250) << 8) | (U64(251) << 20));
            } else {
                emit(@base(0x5c18000000000000UL) | (U64(b) << 8) | (U64(250) << 20) | (U64(16) << 39));
            }
            break;
        }
        case 0x307: case 0xb07: { // FRND.F32, round to an integral floating-point value (0xb07: cbuf)
            if (opcode == 0x307 &&
                (insn[2] == 0x00100800 || insn[2] == 0x0010c800)) {
                if (B(insn.Bits(24, 8)) || B((insn[1] & ~0x100000ffU)) ||
                    B((insn[3] & 0xc00001ffU)))
                    return fail(index, "native half round mode");
                emit(@base(0x5ca8000000000000UL) | (U64(1) << 8) | (U64(1) << 10) |
                     (U64(insn.Bits(32, 8)) << 20) | (U64(8 + insn.Bits(78, 2)) << 39) |
                     (U64(insn.Bits(60, 1)) << 41));
                break;
            }
            if (insn.Bits(75, 2) != 2 || insn.Bits(84, 2) != 2 || B(insn.Bits(81, 3)) ||
                B(insn.Bits(86, 6))) return fail(index, "native floating round format");
            if (opcode == 0xb07 && !valid_cbuf()) return fail(index, "native constant buffer address");
            emit(@base(opcode == 0xb07 ? 0x4ca8000000000000UL : 0x5ca8000000000000UL) | (U64(2) << 8) | (U64(2) << 10) |
                 (opcode == 0xb07 ? cbuf() : U64(insn.Bits(32, 8)) << 20) | (U64(8 + insn.Bits(78, 2)) << 39) |
                 (U64(insn.Bits(80, 1)) << 44) | (U64(insn.Bits(63, 1)) << 45) |
                 (U64(insn.Bits(62, 1)) << 49));
            break;
        }
        case 0x324: case 0x924: {
            if (!geometry_stage) return fail(index, "native geometry output outside geometry stage");
            uint type = insn.Bits(78, 2);
            if (B(insn[2] & ~0xc000U))
                return fail(index, "native geometry output mode");
            if (type == 0 && (opcode != 0x324 || dest != 255 || insn[1] != 255))
                return fail(index, "native geometry finalization mode");
            ulong handle = U64(insn.Bits(24, 8)) << 8;
            ulong flags = U64(type) << 39;
            if (opcode == 0x324) {
                if (B(insn[1] & ~0xffU))
                    return fail(index, "native geometry output stream");
                emit(@base(0xfbe0000000000000UL) | handle | (U64(insn[1]) << 20) | flags);
            } else {
                if (insn[1] > 0x7ffff)
                    return fail(index, "native geometry output stream");
                emit(@base(0xf6e0000000000000UL) | handle | (U64(insn[1]) << 20) | flags);
            }
            break;
        }
        case 0x321: // ALD
        case 0x322: { // AST
            bool patch = insn.Bits(76, 1) != 0;
            bool output = insn.Bits(79, 1) != 0;
            uint address = insn.Bits(40, 10);
            uint components = insn.Bits(74, 2) + 1;
            bool tessellation_coordinates =
                tessellation_stage == Nx2TessellationStage.Evaluation && opcode == 0x321 &&
                !patch && address >= 0x2f0 && address + components * 4 <= 0x2f8;
            if (B(insn.Bits(77, 1)) || (patch && tessellation_stage == Nx2TessellationStage.None) ||
                B((output && ((!patch && !tessellation_coordinates) || opcode != 0x321))) ||
                B((patch && opcode == 0x321 &&
                 output != (tessellation_stage == Nx2TessellationStage.Control))) ||
                (patch && opcode == 0x322 && tessellation_stage != Nx2TessellationStage.Control)) {
                return fail(index, "native indexed/patch attribute access");
            }
            if (B(address % 4) || address + components * 4 > 0x400 || insn.Bits(24, 8) != 255) {
                return fail(index, "native attribute address or indexing");
            }
            // a[0x5c] has no Maxwell meaning (Ampere uses it for a per-primitive hint such as the
            // shading rate). Nothing reads it back; dropping the store keeps full-rate shading.
            if (opcode == 0x322 && address == 0x5c && components == 1) break;
            uint value_reg = opcode == 0x321 ? U32(dest) : insn.Bits(32, 8);
            if (value_reg == 255) {
                if (opcode == 0x321) break; // load into RZ: nothing to do
                // AST of RZ writes zeros: store them from zeroed scratch registers.
                for (uint i = 0; i < components; ++i) mov_imm(250 + i, 0);
                value_reg = 250;
            }
            if (value_reg + components > 255) return fail(index, "native attribute register range");
            uint vertex = opcode == 0x321 ? insn.Bits(32, 8) : insn.Bits(64, 8);
            ulong translated = (opcode == 0x321 ? 0xefd8000000000000UL : 0xeff0000000000000UL) |
                predicate | value_reg | (U64(255) << 8) | (U64(address) << 20) |
                (U64(patch) << 31) | (U64(output) << 32) |
                (U64(vertex) << 39) | (U64(components - 1) << 47);
            result.Instructions.Add(translated);
            break;
        }
        case 0x387: case 0x983: case 0x987: {
            // The size is bits 73-75 (0-3 = U8, S8, U16, S16, then 4, 5, 6 = 32, 64, 128 bits).
            // A load's bit 85 (.LU) is a cache hint.
            bool store = opcode != 0x983;
            uint size = insn.Bits(73, 3);
            bool uniform_address = insn.Bits(91, 1) != 0;
            uint data = insn.Bits(32, 8), offset = insn.Bits(40, 24);
            uint address = insn.Bits(24, 8);
            uint words = size == 6 ? 4 : size == 5 ? 2U : 1U, value = store ? data : U32(dest);
            uint bytes = size < 2 ? 1 : size < 4 ? 2U : 4U;
            uint register_limit = uses_uniform_bank ? high_uniform_base : 246U;
            uint value_limit = uses_uniform_bank ? high_uniform_base : 248U;
            uint allowed = (store ? 0xe00U : 0x200e00U) |
                (uniform_address ? 0x08000000U | (store ? 0x3fU : 0U) : 0U);
            if ((address != 255 && address >= register_limit) || B((B(store) && B(insn.Bits(16, 8)))) ||
                B((!store && B(data) && !uniform_address)) ||
                B((value != 255 && (value + words > value_limit || B((value & (words - 1)))))) ||
                (!store && value == 255) || (opcode == 0x987 && !uniform_address) ||
                B((offset & (bytes - 1))) || offset + bytes * words > 0x800000 || size > 6 ||
                (insn[2] & ~allowed) != 0x00100000U)
                return fail(index, "native local scalar spill mode");
            if (uniform_address) {
                uint uniform = uniform_register(store ? insn.Bits(64, 6) : data);
                if (uniform == ~0U) return fail(index, "native local spill uniform address");
                if (address == 255) {
                    address = uniform;
                } else if (uniform != 255) {
                    emit(0x5c10000000000000UL | predicate | 252 | (U64(uniform) << 8) |
                         (U64(address) << 20));
                    address = 252;
                }
            }
            if (!store && words > 1 && address >= value && address < value + words) {
                emit(0x5c98000000000000UL | predicate | 252 | (U64(15) << 39) | (U64(address) << 20));
                address = 252;
            }
            for (uint word = 0; word < words; ++word)
                emit((store ? 0xef50000000000000UL : 0xef40000000000000UL) | predicate |
                     (value == 255 ? 255U : value + word) | (U64(address) << 8) |
                     (U64(offset + 4 * word) << 20) | (U64(size < 4 ? size : 4U) << 48));
            break;
        }
        case 0x803: { // P2R PR, register merge source, immediate mask, byte select (76-77)
            if (insn[1] > 0x7f || B((insn[2] & ~0x3000U)))
                return fail(index, "native predicate-to-register mode");
            emit(@base(0x38e8000000000000UL) | (U64(insn.Bits(24, 8)) << 8) |
                 (U64(insn[1]) << 20) | (U64(insn.Bits(76, 2)) << 41));
            break;
        }
        case 0x804: { // R2P PR, register, immediate mask, byte select (76-77)
            if (B(insn.Bits(16, 8)) || insn[1] > 0x7f || B((insn[2] & ~0x3000U)))
                return fail(index, "native register-to-predicate mode");
            emit(0x38f0000000000000UL | predicate | (U64(insn.Bits(24, 8)) << 8) |
                 (U64(insn[1]) << 20) | (U64(insn.Bits(76, 2)) << 41));
            break;
        }
        case 0xc02: // MOV uniform register
        case 0x202: // MOV register
        case 0x802: // MOV immediate
        case 0xa02: { // MOV constant buffer
            if (insn.Bits(72, 4) != 15 || B((insn.Bits(76, 16) & ~(opcode == 0xc02 ? 0x8000U : 0U)))) return fail(index, "native MOV modifiers");
            if (opcode == 0x802) {
                result.Instructions.Add(@base(0x0100000000000000UL) | (U64(15) << 12) |
                    (U64(insn[1]) << 20));
            } else if (opcode == 0xc02) {
                // Any uniform register (Hyrule Warriors: MOV R18, UR17; MOV R105, UR42).
                uint source = insn[1] < 64 ? uniform_register(insn[1]) : ~0U;
                if (source == ~0U || insn[2] != 0x08000f00) return fail(index, "native uniform move source");
                emit(@base(0x5c98000000000000UL) | (U64(15) << 39) | (U64(source) << 20));
            } else if (opcode == 0x202) {
                if (B(insn.Bits(40, 24))) return fail(index, "native MOV register modifiers");
                result.Instructions.Add(@base(0x5c98000000000000UL) | (U64(15) << 39) |
                    (U64(insn.Bits(32, 8)) << 20));
            } else {
                if (!valid_cbuf() || B(insn.Bits(62, 2))) return fail(index, "native MOV constant buffer");
                result.Instructions.Add(@base(0x4c98000000000000UL) | (U64(15) << 39) | cbuf());
            }
            break;
        }
        case 0x223: case 0x423: case 0x623: case 0x823: case 0xa23: case 0xc23: case 0xe23: { // FFMA
            if (B(insn.Bits(76, 1)) || B((insn.Bits(81, 11) & ~(opcode == 0xe23 || opcode == 0xc23 ? 0x400U : 0U)))) return fail(index, "native FFMA denormal/source mode");
            uint a = operand(0), b = operand(1), c = operand(2);
            emit(@base(0x5980000000000000UL) | (U64(a) << 8) | (U64(b) << 20) | (U64(c) << 39) |
                (U64(insn.Bits(77, 1)) << 50) | (U64(insn.Bits(78, 2)) << 51) | (U64(insn.Bits(80, 1)) << 53));
            break;
        }
        case 0x220: case 0x820: case 0xa20: case 0xc20: { // FMUL
            uint scale = insn.Bits(84, 3);
            if (B(insn.Bits(76, 1)) || scale == 0 || B(insn.Bits(81, 3)) ||
                B(insn.Bits(87, 4)) || insn.Bits(91, 1) != U32(opcode == 0xc20) ||
                B((scale != 4 && (!B(insn.Bits(80, 1)) || B(insn.Bits(78, 2))))))
                return fail(index, "native FMUL scale/mode");
            uint a = operand(0), b = operand(1);
            emit(@base(0x5c68000000000000UL) | (U64(a) << 8) | (U64(b) << 20) |
                (U64(insn.Bits(78, 2)) << 39) | (U64(new uint[] {0, 3, 2, 1, 0, 6, 5, 4}[scale]) << 41) | (U64(insn.Bits(80, 1)) << 44) | (U64(insn.Bits(77, 1)) << 50));
            break;
        }
        case 0x221: case 0x421: case 0x621: case 0x821: case 0xa21: case 0xe21: { // FADD
            if (B(insn.Bits(81, 10)) || insn.Bits(91, 1) != U32(opcode == 0xe21))
                return fail(index, "native FADD mode");
            uint a = operand(0), b = operand(insn.Bits(9, 3) == 2 || insn.Bits(9, 3) == 3 || insn.Bits(9, 3) == 7 ? 2U : 1U);
            emit(@base(0x5c58000000000000UL) | (U64(a) << 8) | (U64(b) << 20) |
                (U64(insn.Bits(78, 2)) << 39) | (U64(insn.Bits(80, 1)) << 44) | (U64(insn.Bits(77, 1)) << 50));
            break;
        }
        case 0x209: case 0x809: case 0xa09: case 0xc09: { // FMNMX, including a uniform source
            if (B(insn.Bits(74, 6)) || B(insn.Bits(82, 5)) || B((opcode != 0xc09 && B(insn.Bits(91, 1)))))
                return fail(index, "native floating min/max mode");
            uint a = operand(0), b = operand(1);
            // .NAN (bit 81): an unordered pair gives the canonical NaN. R252 = ~0 when both are numbers.
            bool propagate_nan = B(insn.Bits(81, 1));
            if (propagate_nan)
                emit(0x5800000000000000UL | predicate | 252 | (U64(a) << 8) | (U64(b) << 20) | (U64(7) << 39) | (U64(7) << 48));
            emit(@base(0x5c60000000000000UL) | (U64(a) << 8) | (U64(b) << 20) |
                 (U64(insn.Bits(87, 3)) << 39) | (U64(insn.Bits(90, 1)) << 42) |
                 (U64(insn.Bits(80, 1)) << 44));
            if (propagate_nan) {
                emit(@base(0x5c40000000000000UL) | (dest << 8) | (U64(252) << 20) | (U64(7) << 48));
                emit(0x0400000000000000UL | predicate | 252 | (U64(252) << 8) | (U64(0xffffffffU) << 20) | (U64(2) << 53));
                emit(0x0400000000000000UL | predicate | 252 | (U64(252) << 8) | (U64(0x7fffffffU) << 20));
                emit(@base(0x5c40000000000000UL) | (dest << 8) | (U64(252) << 20) | (U64(1) << 41) | (U64(7) << 48));
            }
            break;
        }
        case 0x308: case 0x908: case 0xb08: case 0xd08: {
            uint operation = insn.Bits(74, 6);
            if (opcode == 0x908 && (B(insn.Bits(24, 8)) || B((insn[2] & ~0xfc00U))))
                return fail(index, "native immediate MUFU mode");
            // Bit 91 is part of the uniform-register form's encoding.
            if (operation > 9 || operation == 6 || operation == 7 ||
                (operation == 9 && B(insn.Bits(73, 1))) ||
                insn.Bits(80, 12) != (opcode == 0xd08 ? 0x800U : 0U))
                return fail(index, "native MUFU mode");
            // SM70+ MUFU.COS/SIN take the angle in turns (compilers multiply by 1/2pi first);
            // Eden's Maxwell MUFU takes radians (its RRO is a plain move), so scale by 2pi.
            bool turns = operation == 0 || operation == 1;
            uint to_radians(uint source) {
                emit(0x1e00000000000000UL | predicate | 250 | (U64(source) << 8) |
                     (U64(0x40c90fdbU) << 20));
                return 250U;
            };
            if (B(insn.Bits(73, 1))) {
                if (opcode != 0x308 || insn.Bits(60, 2) > 1)
                    return fail(index, "native half special-function source");
                emit(0x5ca8000000000000UL | predicate | 250 | (U64(2) << 8) | (U64(1) << 10) |
                     (U64(insn.Bits(32, 8)) << 20) | (U64(insn.Bits(60, 1)) << 41) |
                     (U64(insn.Bits(63, 1)) << 45) | (U64(insn.Bits(62, 1)) << 49));
                if (turns) to_radians(250);
                emit(0x5080000000000000UL | predicate | 250 | (U64(250) << 8) | (U64(operation) << 20));
                emit(@base(0x5ca8000000000000UL) | (U64(1) << 8) | (U64(2) << 10) | (U64(250) << 20));
            } else {
                uint src = opcode == 0xd08 ? uniform_register(insn.Bits(32, 8)) : operand(1);
                if (src == ~0U) return fail(index, "native MUFU uniform source");
                if (turns) src = to_radians(src);
                emit(@base(0x5080000000000000UL) | (U64(src) << 8) | (U64(operation) << 20));
            }
            break;
        }
        case 0x526: { // IPA.PASS / CONSTANT with the attribute indexed by a uniform register
            uint index_register = uniform_register(insn.Bits(24, 8));
            if (index_register == ~0U || insn.Bits(32, 8) != 255 ||
                (insn.Bits(76, 4) != 0 && insn.Bits(76, 4) != 4) || insn.Bits(81, 3) != 7)
                return fail(index, "native indexed IPA mode");
            if (index_register == 255) {
                emit(@base(0xe000000000000000UL) | (U64(255) << 8) | (U64(255) << 20) |
                     (U64(insn.Bits(64, 8)) << 30) | (U64(insn.Bits(78, 1)) << 55));
                break;
            }
            // Maxwell IPA.IDX reads the whole byte address from the index register.
            emit(0x1c00000000000000UL | predicate | 250 | (U64(index_register) << 8) |
                 (U64(insn.Bits(64, 8) * 4) << 20));
            emit(@base(0xe000000000000000UL) | (U64(250) << 8) | (U64(255) << 20) |
                 (U64(insn.Bits(64, 8)) << 30) | (U64(1) << 38) | (U64(insn.Bits(78, 1)) << 55));
            break;
        }
        case 0x326: { // IPA.PASS / CONSTANT, default or centroid sample location
            // Bits 76-77 are the sample mode (title 1 interpolates most varyings at the centroid),
            // bit 78 the constant mode; Maxwell keeps them at bits 52-53 and 55.
            if (insn.Bits(32, 8) != 255 || insn.Bits(76, 2) > 1 || B(insn.Bits(79, 1)) ||
                insn.Bits(81, 3) != 7)
                return fail(index, "native IPA interpolation mode");
            emit(@base(0xe000000000000000UL) | (U64(255) << 8) | (U64(255) << 20) | (U64(insn.Bits(64, 8)) << 30) |
                 (U64(insn.Bits(76, 2)) << 52) | (U64(insn.Bits(78, 1)) << 55));
            break;
        }
        case 0x81a: {
            if (B((insn[2] & ~0x200U)) || insn[1] > 32 || dest >= 255)
                return fail(index, "native sign extend mode");
            emit(@base(0x3800000000000000UL) | (U64(insn.Bits(24, 8)) << 8) |
                 (U64(insn[1] << 8) << 20) | (U64(insn.Bits(73, 1)) << 48));
            break;
        }
        case 0x299: { // USHF by a uniform amount, one register shifted (SHL/SHR, bit 75 wraps).
            uint dst = uniform_register(insn.Bits(16, 8)), low = uniform_register(insn.Bits(24, 8));
            uint shift = uniform_register(insn.Bits(32, 8)), high = uniform_register(insn.Bits(64, 8));
            bool right = insn.Bits(76, 1) != 0, high_result = insn.Bits(80, 1) != 0;
            if (dst == ~0U || dst == 255 || low == ~0U || shift == ~0U || high == ~0U ||
                insn.Bits(73, 2) != 3 ||
                (insn[2] & ~0x000118ffU) != 0x08000600U)
                return fail(index, "native uniform register shift mode");
            uint source;
            if (high_result && low == 255) source = high;        // (high:0) >> or << s, high word
            else if (!high_result && high == 255) source = low;  // (0:low) >> or << s, low word
            else return fail(index, "native uniform funnel shift");
            emit((right ? 0x5c28000000000000UL : 0x5c48000000000000UL) | predicate | dst |
                 (U64(source) << 8) | (U64(shift) << 20) | (U64(insn.Bits(75, 1)) << 39));
            break;
        }
        case 0x499: {
            uint target = uniform_register(U32(dest));
            uint shift = uniform_register(insn.Bits(64, 8));
            if (insn.Bits(24, 8) != 63 || (insn[2] & ~0x8ffU) != 0x08010600 ||
                B((insn[3] & 0xc00001ffU)) || target == ~0U || target == 255 || shift == ~0U)
                return fail(index, "native uniform immediate high-word shift mode");
            mov_imm(250, insn[1]);
            emit(0x5c48000000000000UL | predicate | target | (U64(250) << 8) |
                 (U64(shift) << 20) | (U64(insn.Bits(75, 1)) << 39));
            break;
        }
        case 0x419: { // SHF.L.U32.HI Rd, RZ, Rb, imm: W uses Rb & 31; otherwise min(Rb, 32).
            if (insn.Bits(24, 8) != 255 || (insn[2] & ~0x8ffU) != 0x00010600 ||
                (!B(insn.Bits(75, 1)) && B((insn[3] & 0xc00001ffU))))
                return fail(index, "native funnel shift immediate mode");
            mov_imm(250, insn[1]);
            emit(@base(0x5c48000000000000UL) | (U64(250) << 8) | (U64(insn.Bits(64, 8)) << 20) |
                 (U64(insn.Bits(75, 1)) << 39));
            break;
        }
        case 0x805: { // CS2R SRZ
            if (insn.Bits(72, 8) != 255 || !B(insn.Bits(80, 1)) || dest >= 249) return fail(index, "native special-register read");
            mov_imm(U32(dest), 0);
            mov_imm(U32(dest) + 1, 0);
            break;
        }
        case 0x845: case 0x906: {
            bool round_up = opcode == 0x906;
            if (opcode == 0x906 && !B(insn.Bits(24, 8)) && !B((insn[3] & 0xc00001ffU)) &&
                (insn[2] & ~0x1c00U) == 0x00200000U && insn.Bits(75, 2) == 1) {
                // I2F.F16.U32/S32 immediate (from Orchid), format at 75-76. Only round to nearest:
                // the half conversion is emitted without a rounding mode.
                mov_imm(250, insn[1]);
                emit(@base(0x5cb8000000000000UL) | (U64(1) << 8) | (U64(2) << 10) |
                     (U64(insn.Bits(74, 1)) << 13) | (U64(250) << 20));
                break;
            }
            if (B(insn.Bits(24, 8)) || B((insn[3] & 0xc00001ffU)) ||
                (insn[2] & ~0xc400U) != 0x00201000U)
                return fail(index, "native immediate integer-to-float conversion mode");
            if (insn[2] != (round_up ? 0x00209000U : 0x0020d000U)) {
                mov_imm(250, insn[1]);
                emit(@base(0x5cb8000000000000UL) | (U64(2) << 8) | (U64(2) << 10) |
                     (U64(insn.Bits(74, 1)) << 13) | (U64(250) << 20) |
                     (U64(insn.Bits(78, 2)) << 39));
                break;
            }
            uint value = insn[1];
            uint converted = 0;
            if (B(value)) {
                uint shift = U32(BitOperations.LeadingZeroCount(value));
                uint normalized = value << (int)(shift);
                converted = ((127U + 31U - shift) << 23) | ((normalized >> 8) & 0x7fffffU);
                converted += U32(round_up && (normalized & 255U) != 0);
            }
            mov_imm(U32(dest), converted);
            break;
        }
        case 0x306: case 0xd06: { // I2F.F32.U8, round-to-nearest (0xd06: uniform source)
            uint source = opcode == 0xd06 ? uniform_register(insn.Bits(32, 8)) : insn.Bits(32, 8);
            ulong is_signed = insn.Bits(74, 1);
            if (opcode == 0x306 &&
                (insn[2] == 0x00200c00 || insn[2] == 0x00209400)) {
                bool half = insn[2] == 0x00200c00;
                if (B(insn.Bits(24, 8)) || B((insn[1] & ~0xffU)) || B((insn[3] & 0xc00001ffU)))
                    return fail(index, "native scalar integer-to-float conversion mode");
                emit(@base(0x5cb8000000000000UL) | (U64(half ? 1U : 2U) << 8) | (U64(2) << 10) |
                     (U64(1) << 13) | (U64(source) << 20) | (U64(half ? 0U : 2U) << 39));
                break;
            }
            if (((opcode == 0x306 && (insn[2] & ~0x400U) == 0x00101000) ||
                 (opcode == 0xd06 && (insn[2] & ~0x400U) == 0x08101000U && !B(insn.Bits(61, 1)) &&
                  !B(insn.Bits(24, 8)) && !B((insn[3] & 0xc00001ffU)))) &&
                !B((insn[1] & 0xcfffff00U)) && source != ~0U) {
                // I2F.U16/S16 Rd, Rs(.H1): signedness and the halfword selector are independent.
                emit(@base(0x5cb8000000000000UL) | (U64(2) << 8) | (U64(1) << 10) |
                     (U64(insn.Bits(74, 1)) << 13) | (U64(source) << 20) |
                     (U64(insn.Bits(60, 2) * 2) << 41));
                break;
            }
            // I2F.{U,S}8 Rd, Rs.B0-B3: the byte selector (bits 60-61) as Maxwell's (41-42).
            if ((insn[2] & ~0x400U) != (opcode == 0xd06 ? 0x08001000U : 0x1000U) ||
                B((insn[1] & 0xcfffff00U)) || source == ~0U)
                return fail(index, "native byte-to-float conversion mode");
            emit(@base(0x5cb8000000000000UL) | (U64(2) << 8) | (is_signed << 13) | (U64(source) << 20) |
                 (U64(insn.Bits(60, 2)) << 41));
            break;
        }
        case 0x245: case 0xa45: case 0xc45: { // I2FP.F32.U32
            // Bits 78-79: rounding (RN/RM/RP/RZ), as Maxwell I2F's bits 39-40.
            if ((insn[2] & ~(0x400U | 0xc000U | (opcode == 0xc45 ? 0x08000000U : 0U))) != 0x00201000) return fail(index, "native packed integer-to-float format");
            uint src = operand(1, false);
            emit(@base(0x5cb8000000000000UL) | (U64(2) << 8) | (U64(2) << 10) | (U64(insn.Bits(74, 1)) << 13) | (U64(src) << 20) |
                 (U64(insn.Bits(78, 2)) << 39));
            break;
        }
        case 0x207: case 0x807: case 0xa07: case 0xc07:
        case 0x208: case 0x808: case 0xa08: { // Bitwise SEL / FSEL
            // FSEL applies the floating operand modifiers (negate/absolute) before selecting.
            bool floating = (opcode & 0x1ff) == 0x008;
            uint a = operand(0, floating), b = operand(1, floating);
            emit(@base(0x5ca0000000000000UL) | (U64(a) << 8) | (U64(b) << 20) |
                (U64(insn.Bits(87, 3)) << 39) | (U64(insn.Bits(90, 1)) << 42));
            break;
        }
        case 0x20a: case 0x80a: case 0xa0a: case 0xc0a: { // FSET.BF, combined with Pc (bits 87-90) by bits 74-75 (0xc0a: uniform b)
            if (B(insn.Bits(64, 8)) || insn.Bits(74, 2) == 3 || B(insn.Bits(81, 6)) || B((opcode != 0xc0a && B(insn.Bits(91, 1)))))
                return fail(index, "native floating comparison output mode");
            uint a = operand(0), b = operand(1);
            emit(@base(0x5800000000000000UL) | (U64(a) << 8) | (U64(b) << 20) |
                 (U64(insn.Bits(87, 4)) << 39) | (U64(insn.Bits(74, 2)) << 45) |
                 (U64(insn.Bits(76, 4)) << 48) | (U64(1) << 52) | (U64(insn.Bits(80, 1)) << 55));
            break;
        }
        case 0x234: case 0x434: case 0x634: { // HSETP2 with a register (B swizzle 60-61, abs 62,
                      // neg 63), an immediate half pair or a constant buffer (same B fields):
                      // A swizzle 74-75 (neg 72, abs 73),
                      // combine 69-70, H_AND 71, compare 76-79, FTZ 80, P/Q 81-86, input 87-90.
            ulong source = U64(250) << 20;
            if (opcode == 0x234) {
                if (B((insn[1] & 0x0fffff00U)) || B(insn.Bits(64, 5)))
                    return fail(index, "native half comparison mode");
                source = (U64(insn.Bits(32, 8)) << 20) | (U64(insn.Bits(60, 2)) << 28) |
                         (U64(insn.Bits(62, 1)) << 30) | (U64(insn.Bits(63, 1)) << 31);
            } else if (opcode == 0x634) {
                // Mario Kart World: HSETP2.GT.AND P1, PT, RZ.H0_H0, c[0x7][0xfc].H1_H1, !P1.
                if (B(insn.Bits(64, 5))) return fail(index, "native half comparison mode");
                if (materialize(2, 255, 250, false, false) == 255) break;
                source |= (U64(insn.Bits(60, 2)) << 28) | (U64(insn.Bits(62, 1)) << 30) |
                          (U64(insn.Bits(63, 1)) << 31);
            } else {
                mov_imm(250, insn[1]);
            }
            emit(0x5d20000000000000UL | predicate | insn.Bits(84, 3) | (U64(insn.Bits(81, 3)) << 3) |
                 (U64(insn.Bits(80, 1)) << 6) | (U64(insn.Bits(24, 8)) << 8) | source |
                 (U64(insn.Bits(76, 4)) << 35) | (U64(insn.Bits(87, 3)) << 39) |
                 (U64(insn.Bits(90, 1)) << 42) | (U64(insn.Bits(72, 1)) << 43) |
                 (U64(insn.Bits(73, 1)) << 44) | (U64(insn.Bits(69, 2)) << 45) |
                 (U64(insn.Bits(74, 2)) << 47) | (U64(insn.Bits(71, 1)) << 49));
            break;
        }
        case 0x20b: case 0x80b: case 0xa0b: case 0xc0b: // FSETP
        case 0x20c: case 0x80c: case 0xa0c: case 0xc0c: { // ISETP
            bool floating = (opcode & 0x1ff) == 0xb;
            if (!floating && B(insn.Bits(72, 1))) {
                // ISETP.EX (bit 72): the high-word half of a 64-bit compare. The low words'
                // result comes in as the predicate at bits 68-70 (negated by 71), e.g.
                // ISETP.NE P0 = lo != 0, then ISETP.NE.EX P0 = (hi != 0) OR P0. EQ: high equal
                // AND low; NE: high different OR low; the orderings: high strictly ordered OR
                // (high equal AND low).
                uint compare = insn.Bits(76, 3);
                uint destination = insn.Bits(81, 3);
                uint destination_b = insn.Bits(84, 3);
                uint low = insn.Bits(68, 4);
                uint accumulate = insn.Bits(87, 4);
                uint combine_mode = insn.Bits(74, 2);
                if (compare == 0 || compare == 7 || combine_mode == 3)
                    return fail(index, "native extended integer comparison mode");
                uint compare_a = operand(0, false), compare_b = operand(1, false);
                uint temporary = 0;
                while (temporary == destination || temporary == destination_b ||
                       temporary == (low & 7) || temporary == (accumulate & 7) ||
                       temporary == insn.Bits(12, 3))
                    ++temporary;
                uint mask = 1U << (int)(temporary);
                emit(0x38e8000000070000UL | 253 | (U64(255) << 8) | (U64(mask) << 20));
                ulong isetp(uint op, uint input, uint combine_mode) {
                    return 0x5b60000000000000UL | predicate | 7 | (U64(temporary) << 3) |
                           (U64(compare_a) << 8) | (U64(compare_b) << 20) | (U64(input & 7) << 39) |
                           (U64(input >> 3) << 42) | (U64(combine_mode) << 45) |
                           (U64(insn.Bits(73, 1)) << 48) | (U64(op) << 49);
                };
                const uint And = 0, Or = 1, Lt = 1, Eq = 2, Gt = 4, Ne = 5;
                if (compare == Eq) {
                    emit(isetp(Eq, low, And));
                } else if (compare == Ne) {
                    emit(isetp(Ne, low, Or));
                } else {
                    emit(isetp(Eq, low, And));
                    emit(isetp(compare <= 3 ? Lt : Gt, temporary, Or));
                }
                emit(0x5090000000000000UL | predicate | destination_b |
                     (U64(destination) << 3) | (U64(temporary) << 12) | (U64(7) << 29) |
                     (U64(accumulate & 7) << 39) | (U64(accumulate >> 3) << 42) |
                     (U64(combine_mode) << 45));
                emit(0x38f0000000070000UL | (U64(253) << 8) | (U64(mask) << 20));
                break;
            }
            uint a = operand(0, floating), b = operand(1, floating);
            uint combine = insn.Bits(74, 2);
            if (combine > 2) return fail(index, "native predicate combination");
            ulong out_ = (floating ? 0x5bb0000000000000UL : 0x5b60000000000000UL) | predicate |
                insn.Bits(84, 3) | (U64(insn.Bits(81, 3)) << 3) | (U64(a) << 8) | (U64(b) << 20) |
                (U64(insn.Bits(87, 3)) << 39) | (U64(insn.Bits(90, 1)) << 42) | (U64(combine) << 45);
            if (floating) out_ |= (U64(insn.Bits(80, 1)) << 47) | (U64(insn.Bits(76, 4)) << 48);
            else out_ |= (U64(insn.Bits(73, 1)) << 48) | (U64(insn.Bits(76, 3)) << 49);
            emit(out_);
            break;
        }
        case 0x212: case 0x812: case 0xa12: case 0xc12: { // LOP3.LUT, any table, optional P = (result != 0)
            uint lut = insn.Bits(72, 8);
            uint pred_target = insn.Bits(81, 3);
            if (B(insn.Bits(80, 1)) || insn.Bits(87, 4) != 15 ||
                B((opcode == 0x212 && B(insn.Bits(40, 24)))) || B((opcode == 0xa12 && B(insn.Bits(62, 2)))))
                return fail(index, "native LUT/predicate mode");
            uint b = 255;
            if (opcode == 0x212) {
                b = insn.Bits(32, 8);
            } else if (opcode == 0x812) {
                mov_imm(251, insn[1]);
                b = 251;
            } else if (opcode == 0xc12) {
                b = uniform_register(insn.Bits(32, 8));
                if (b == ~0U) return fail(index, "native uniform logic source");
            } else {
                b = operand(1, false);
            }
            uint a = insn.Bits(24, 8), c = insn.Bits(64, 8);
            emit(0x5be0000000000000UL | predicate | dest | (U64(a) << 8) | (U64(b) << 20) |
                 (U64(lut) << 28) | (U64(pred_target == 7 ? 0U : 3U) << 36) | (U64(c) << 39) |
                 (U64(pred_target) << 48));
            break;
        }
        case 0x213: case 0xa13: case 0xc13: { // IABS: max(x, 0 - x), signed.
            if (B(insn[2] & ~(opcode == 0xc13 ? 0x08000000U : 0U)))
                return fail(index, "native integer absolute mode");
            uint source = operand(1, false);
            emit(0x5c10000000000000UL | predicate | 250 | (U64(255) << 8) | (U64(source) << 20) |
                 (U64(1) << 48));
            emit(@base(0x5c20000000000000UL) | (U64(source) << 8) | (U64(250) << 20) |
                 (U64(15) << 39) | (U64(1) << 48));
            break;
        }
        case 0x217: case 0x817: case 0xa17: case 0xc17: { // IMNMX register, immediate, cbuf or uniform operand.
            if ((insn[2] & ~(0x04000200U | (opcode == 0xc17 ? 0x08000000U : 0U))) != 0x03800000 ||
                (opcode != 0xa17 && opcode != 0x817 && insn[1] > 255U) ||
                (opcode == 0xa17 && (!valid_cbuf() || B(insn.Bits(62, 2)))))
                return fail(index, "native integer min/max mode");
            // An immediate is a signed 20-bit value: 19 bits at 20, the sign in bit 56.
            bool short_immediate = opcode == 0x817 &&
                                         (insn[1] <= 0x7ffffU || insn[1] >= 0xfff80000U);
            if (opcode == 0x817 && !short_immediate) mov_imm(251, insn[1]);
            uint source = opcode == 0xc17   ? uniform_register(insn[1])
                               : opcode == 0xa17 ? operand(1, false)
                               : opcode == 0x817 ? (short_immediate ? insn[1] & 0x7ffffU : 251U)
                                                 : insn[1];
            if (source == ~0U) return fail(index, "native uniform min/max operand");
            emit(@base(short_immediate ? 0x3820000000000000UL : 0x5c20000000000000UL) |
                 (U64(insn.Bits(24, 8)) << 8) | (U64(source) << 20) |
                 (U64(short_immediate ? insn[1] >> 31 : 0U) << 56) |
                 (U64(insn.Bits(87, 4)) << 39) | (U64(insn.Bits(73, 1)) << 48));
            break;
        }
        case 0x819: { // SHF.HI, immediate shift
            bool is_signed = insn.Bits(73, 2) == 2;
            if (!B(insn.Bits(80, 1)) && B(insn.Bits(76, 1)) && insn.Bits(73, 2) == 1 &&
                !B(insn.Bits(75, 1)) && insn[1] <= 32) {
                // SHF.R.U64 funnel: the translator reads bits 20-38 as the immediate, so the U64
                // clamp (bit 38) would turn every shift into 63; shifts up to 32 need no clamp.
                emit(@base(0x38f8000000000000UL) | (U64(insn.Bits(24, 8)) << 8) |
                     (U64(insn[1]) << 20) | (U64(insn.Bits(64, 8)) << 39));
                break;
            }
            if (!B(insn.Bits(80, 1)) && !B(insn.Bits(76, 1)) && insn.Bits(64, 8) == 255 &&
                insn.Bits(73, 2) == 3 && !B(insn.Bits(75, 1)) && insn[1] <= 32) {
                emit(@base(0x3848000000000000UL) | (U64(insn.Bits(24, 8)) << 8) | (U64(insn[1]) << 20));
                break;
            }
            if (!B(insn.Bits(80, 1)) && B(insn.Bits(76, 1)) && insn.Bits(64, 8) == 255 &&
                B((insn.Bits(73, 2) == 3 || is_signed)) && !B(insn.Bits(75, 1)) && insn[1] <= 32) {
                // SHF.R low word with a zero high word: a logical right shift.
                emit(@base(0x3828000000000000UL) | (U64(insn.Bits(24, 8)) << 8) | (U64(insn[1]) << 20));
                break;
            }
            // U64 left shifts through 32 have the same high word as a 32-bit funnel shift.
            bool u64_left = insn.Bits(73, 2) == 1 && !B(insn.Bits(76, 1));
            if (B((insn.Bits(73, 2) != 3 && !is_signed && !u64_left)) || B(insn.Bits(75, 1)) || !B(insn.Bits(80, 1)) || insn[1] > 32) return fail(index, "native funnel-shift mode");
            uint low = insn.Bits(24, 8), high = insn.Bits(64, 8), shift = insn[1];
            if (B(insn.Bits(76, 1))) {
                if (low != 255) return fail(index, "native nonzero low right-shift word");
                emit(@base(0x3828000000000000UL) | (U64(high) << 8) | (U64(shift) << 20) |
                     (U64(is_signed) << 48));
            } else {
                if (is_signed) return fail(index, "native signed left funnel shift");
                emit(@base(0x36f8000000000000UL) | (U64(low) << 8) | (U64(high) << 39) | (U64(shift) << 20));
            }
            break;
        }
        case 0x211: case 0x811: case 0xa11: case 0xc11: { // LEA (0xa11: cbuf, 0xc11: uniform Rb)
            if (B(insn.Bits(80, 1))) {
                // LEA.HI Rd, Ra, Rb (or an immediate, 0x811), Rc, s: Rb + high word of (Rc:Ra << s);
                // bit 73 sign-extends Ra (Rc is RZ) and .X (bit 74) adds the carry predicate in bits 87-90.
                bool extended = insn.Bits(74, 1) != 0;
                uint high = insn.Bits(64, 8);
                if (B(insn.Bits(72, 1)) || insn.Bits(81, 3) != 7 ||
                    B((high != 255 && B(insn.Bits(73, 1)))) ||
                    B((opcode == 0x211 && (B(insn.Bits(62, 1)) || B((B(insn.Bits(63, 1)) && B(extended)))))) ||
                    B((opcode == 0xa11 && B(insn.Bits(62, 2)))) ||
                    B((opcode == 0xc11 && B((insn[1] & ~0x3fU)))))
                    return fail(index, "native high effective address mode");
                uint lea_b = opcode == 0xc11 ? uniform_register(insn.Bits(32, 8)) : insn.Bits(32, 8);
                if (opcode == 0xa11) lea_b = operand(1, false);
                if (lea_b == ~0U) return fail(index, "native uniform LEA register");
                if (opcode == 0x811) {
                    lea_b = B(insn[1]) ? 251U : 255U;
                    if (lea_b == 251) mov_imm(251, insn[1]);
                }
                ulong shift = (U64(32 - insn.Bits(75, 5)) << 20) | (U64(insn.Bits(73, 1)) << 48);
                // The high word is a right shift of Ra, or with Rc a left funnel shift (SHF.L).
                void high_word(ulong target) {
                    if (high == 255) emit(target | 0x3828000000000000UL | (U64(insn.Bits(24, 8)) << 8) | shift);
                    else emit(target | 0x36f8000000000000UL | (U64(insn.Bits(24, 8)) << 8) | (U64(high) << 39) |
                              (U64(insn.Bits(75, 5)) << 20));
                };
                if (lea_b == 255 && !extended) {
                    high_word(@base(0));
                } else {
                    high_word(predicate | 250);
                    if (extended) {
                        mov_imm(252, 1);
                        emit(0x5ca0000000000000UL | predicate | 252 | (U64(252) << 8) | (U64(255) << 20) |
                             (U64(insn.Bits(87, 3)) << 39) | (U64(insn.Bits(90, 1)) << 42));
                        emit(0x5c10000000000000UL | predicate | 250 | (U64(250) << 8) | (U64(252) << 20));
                    }
                    emit(@base(0x5c10000000000000UL) | (U64(250) << 8) | (U64(lea_b) << 20) |
                         (U64(opcode == 0x211 ? insn.Bits(63, 1) : 0U) << 48));
                }
                break;
            }
            // A carry out (bits 81-83) is the low half of a 64-bit address; IADD3.X adds it next.
            uint carry_out = insn.Bits(81, 3);
            if (B(insn.Bits(72, 1)) || B(insn.Bits(74, 1)) || B(insn.Bits(80, 1)) ||
                B((carry_out != 7 && ((opcode != 0x811 && B(insn.Bits(63, 1))) ||
                                    (rewritten_uniform && opcode != 0x211)))) ||
                B(((opcode == 0x211 || opcode == 0xa11) && B(insn.Bits(62, 1)))) || B((opcode == 0xc11 && B((insn[1] & ~0x8000003fU)))))
                return fail(index, "native LEA carry/high mode");
            // 0x211 bit 63 negates Rb (LEA Rd, Ra, -Rb, s), as Maxwell ISCADD's bit 48.
            uint b = opcode == 0xc11 ? uniform_register(insn.Bits(32, 8)) : insn.Bits(32, 8);
            if (opcode == 0xa11) b = operand(1, false);
            if (b == ~0U) return fail(index, "native uniform LEA register");
            if (opcode == 0x811) { mov_imm(251, insn[1]); b = 251; }
            if (opcode == 0xa11 && carry_out != 7) {
                // The cbuf is snapshotted in R251. Write the result before the carry predicate,
                // which can also be the instruction's guard.
                emit(@base(0x5c18000000000000UL) | (U64(insn.Bits(24, 8)) << 8) |
                     (U64(b) << 20) | (U64(insn.Bits(75, 5)) << 39));
                emit(0x5b60000000000000UL | predicate | 7 | (U64(carry_out) << 3) |
                     (dest << 8) | (U64(b) << 20) | (U64(7) << 39) | (U64(1) << 49));
                break;
            }
            if (carry_out != 7) {
                // The sum in R250, carry = sum < Rb (unsigned), as IADD3's carry out; a uniform
                // predicate goes through P(6-n) into R239.
                uint target = rewritten_uniform ? 6 - carry_out : carry_out, mask = 1U << (int)(target);
                emit(0x5c18000000000000UL | predicate | 250 | (U64(insn.Bits(24, 8)) << 8) |
                     (U64(b) << 20) | (U64(insn.Bits(75, 5)) << 39));
                if (rewritten_uniform) emit(0x38e8000000070000UL | 253 | (U64(255) << 8) | (U64(mask) << 20));
                emit(0x5b60000000000000UL | predicate | 7 | (U64(target) << 3) | (U64(250) << 8) |
                     (U64(b) << 20) | (U64(7) << 39) | (U64(1) << 49));
                if (rewritten_uniform) {
                    emit(0x38e8000000070000UL | 239 | (U64(239) << 8) | (U64(mask) << 20));
                    emit(0x38f0000000070000UL | (U64(253) << 8) | (U64(mask) << 20));
                }
                emit(@base(0x5c98000000000000UL) | (U64(15) << 39) | (U64(250) << 20));
                break;
            }
            emit(@base(0x5c18000000000000UL) | (U64(insn.Bits(24, 8)) << 8) |
                 (U64(b) << 20) | (U64(insn.Bits(75, 5)) << 39) |
                 (U64(opcode != 0x811 ? insn.Bits(63, 1) : 0U) << 48));
            break;
        }
        case 0x210: case 0x810: case 0xa10: case 0xc10: { // IADD3 with zero third source and no carry outputs
            if (B(rewritten_uniform) && opcode == 0x210 && B(insn.Bits(74, 1)) &&
                insn.Bits(81, 6) == 0x3f) {
                if (B(guarded_uniform) || (insn[2] & ~0x0781e0ffU) != 0x007e0400U ||
                    B((insn[1] & ~0xffU)))
                    return fail(index, "native uniform extended add mode");
                emit(0x5c10000000000000UL | predicate | 250 | (U64(insn.Bits(24, 8)) << 8) |
                     (U64(insn.Bits(32, 8)) << 20));
                emit(0x5c10000000000000UL | predicate | 250 | (U64(250) << 8) |
                     (U64(insn.Bits(64, 8)) << 20));
                uint mask = 0;
                foreach (uint bit in new uint[] {77U, 87U}) {
                    uint source = insn.Bits(bit, 3);
                    if (source != 7) mask |= 1U << (int)((6 - source));
                }
                save_predicate(mask);
                emit(0x38f0000000070000UL | (U64(239) << 8) | (U64(mask) << 20));
                mov_imm(252, 1);
                foreach (uint bit in new uint[] {77U, 87U}) {
                    uint source = insn.Bits(bit, 3);
                    emit(0x5ca0000000000000UL | predicate | 251 | (U64(252) << 8) |
                         (U64(255) << 20) | (U64(source == 7 ? 7U : 6 - source) << 39) |
                         (U64(insn.Bits(bit + 3, 1)) << 42));
                    emit(0x5c10000000000000UL | predicate | 250 | (U64(250) << 8) |
                         (U64(251) << 20));
                }
                restore_predicate(mask);
                emit(@base(0x5c98000000000000UL) | (U64(15) << 39) | (U64(250) << 20));
                break;
            }
            if (rewritten_uniform && opcode == 0x210 && insn.Bits(81, 6) != 0x3f) {
                uint first = insn.Bits(81, 3), second = insn.Bits(84, 3);
                if (B(guarded_uniform) || (insn[2] & ~0x007e00ffU) != 0x0781e000U ||
                    B((insn[1] & ~0xffU)) || (first != 7 && first == second))
                    return fail(index, "native uniform add carry mode");
                emit(0x5c10000000000000UL | predicate | 250 | (U64(insn.Bits(24, 8)) << 8) |
                     (U64(insn.Bits(32, 8)) << 20) | (U64(1) << 47));
                emit(0x5c10000000000000UL | predicate | 251 | (U64(255) << 8) |
                     (U64(255) << 20) | (U64(1) << 43));
                emit(0x5c10000000000000UL | predicate | 250 | (U64(250) << 8) |
                     (U64(insn.Bits(64, 8)) << 20) | (U64(1) << 47));
                emit(0x5c10000000000000UL | predicate | 251 | (U64(251) << 8) |
                     (U64(255) << 20) | (U64(1) << 43));
                uint mask = (first == 7 ? 0U : 1U << (int)((6 - first))) |
                                 (second == 7 ? 0U : 1U << (int)((6 - second)));
                save_predicate(mask);
                emit(@base(0x5c98000000000000UL) | (U64(15) << 39) | (U64(250) << 20));
                for (uint output = 0; output < 2; ++output) {
                    uint target = B(output) ? second : first;
                    if (target == 7) continue;
                    mov_imm(252, output + 1);
                    emit(0x5b60000000000000UL | predicate | 7 | (U64(6 - target) << 3) |
                         (U64(251) << 8) | (U64(252) << 20) | (U64(7) << 39) | (U64(6) << 49));
                }
                emit(0x38e8000000070000UL | 239 | (U64(239) << 8) | (U64(mask) << 20));
                restore_predicate(mask);
                break;
            }
            // 64-bit adds: IADD3 with a carry-out predicate (bits 81-83), then IADD3.X (bit 74) with
            // a carry-in predicate (bits 87-89, negate 90). Uniform forms keep UPn in R239 bit 6-n.
            if ((opcode == 0x210 || opcode == 0x810) && insn.Bits(64, 8) == 255 && !B(insn.Bits(73, 1)) &&
                !B(insn.Bits(75, 2)) && insn.Bits(84, 3) == 7 && insn.Bits(77, 4) == 15 && !B(insn.Bits(72, 1)) &&
                (insn.Bits(81, 3) != 7 || B(insn.Bits(74, 1))) && (opcode == 0x810 || !B(insn.Bits(63, 1)))) {
                bool carry_in = insn.Bits(74, 1) != 0;
                uint carry_out = insn.Bits(81, 3);
                uint a = insn.Bits(24, 8);
                if ((carry_in && carry_out != 7) || (opcode == 0x210 && B((insn[1] & ~0xffU))))
                    return fail(index, "native integer carry mode");
                if (opcode == 0x810) {
                    emit(0x1c00000000000000UL | predicate | 250 | (U64(a) << 8) | (U64(insn[1]) << 20));
                } else {
                    emit(0x5c10000000000000UL | predicate | 250 | (U64(a) << 8) | (U64(insn.Bits(32, 8)) << 20));
                }
                void save(uint mask) { emit(0x38e8000000070000UL | 253 | (U64(255) << 8) | (U64(mask) << 20)); };
                void restore(uint mask) { emit(0x38f0000000070000UL | (U64(253) << 8) | (U64(mask) << 20)); };
                if (!carry_in) {
                    // carry = (a + b) < a, unsigned
                    uint target = rewritten_uniform ? 6 - carry_out : carry_out;
                    uint carry_mask = 1U << (int)(target);
                    if (rewritten_uniform) save(carry_mask);
                    emit(0x5b60000000000000UL | predicate | 7 | (U64(target) << 3) | (U64(250) << 8) |
                         (U64(a) << 20) | (U64(7) << 39) | (U64(1) << 49));
                    if (rewritten_uniform) {
                        emit(0x38e8000000070000UL | 239 | (U64(239) << 8) | (U64(carry_mask) << 20));
                        restore(carry_mask);
                    }
                    emit(@base(0x5c98000000000000UL) | (U64(15) << 39) | (U64(250) << 20));
                    break;
                }
                uint condition = insn.Bits(87, 3);
                uint source = rewritten_uniform && condition != 7 ? 6 - condition : condition;
                uint mask = 1U << (int)(source);
                mov_imm(252, 1);
                if (rewritten_uniform && condition != 7) {
                    save(mask);
                    emit(0x38f0000000070000UL | (U64(239) << 8) | (U64(mask) << 20));
                }
                emit(0x5ca0000000000000UL | predicate | 251 | (U64(252) << 8) | (U64(255) << 20) |
                     (U64(source) << 39) | (U64(insn.Bits(90, 1)) << 42));
                if (rewritten_uniform && condition != 7) restore(mask);
                emit(@base(0x5c10000000000000UL) | (U64(250) << 8) | (U64(251) << 20));
                break;
            }
            // Three sources, negations or two carries: the sum builds up in R253 (.CC) with the carry
            // count in R254 (.X). Pu/Pv (bits 81/84) are set when the count reaches 1/2, and IADD3.X
            // adds both carry-ins (bits 87, 77). A negated source is ~x, plus 1 unless extended.
            bool extended = insn.Bits(74, 1) != 0;
            bool negate_a = B(insn.Bits(72, 1)), negate_b = opcode != 0x810 && B(insn.Bits(63, 1)), negate_c = B(insn.Bits(75, 1));
            if ((extended || insn.Bits(81, 6) != 0x3f) &&
                (insn.Bits(64, 8) != 255 || insn.Bits(84, 3) != 7 || negate_a || negate_b || negate_c ||
                 (extended && insn.Bits(77, 4) != 15))) {
                uint first_output = insn.Bits(81, 3), second_output = insn.Bits(84, 3);
                if (B(rewritten_uniform) || B(guarded_uniform) || B(insn.Bits(73, 1)) || B(insn.Bits(76, 1)) ||
                    (first_output != 7 && first_output == second_output) ||
                    B((!extended && (insn.Bits(77, 4) != 15 || insn.Bits(87, 4) != 15))) ||
                    B((opcode == 0x210 && B((insn[1] & ~0x800000ffU)))) || B((opcode == 0xa10 && B(insn.Bits(62, 1)))) ||
                    B((opcode == 0xc10 && B((insn[1] & ~0x8000003fU)))))
                    return fail(index, "native three-input carry mode");
                uint a = insn.Bits(24, 8), b = operand(1, false), c = insn.Bits(64, 8);
                void invert(ref uint source, uint target) {
                    emit(0x0400000000000000UL | predicate | target | (U64(source) << 8) | (U64(0xffffffffU) << 20) | (U64(2) << 53));
                    source = target;
                };
                if (negate_a) invert(ref a, 250);
                if (negate_b) invert(ref b, 251);
                if (negate_c) invert(ref c, 252);
                void accumulate(uint source) {
                    emit(0x5c10000000000000UL | predicate | 253 | (U64(253) << 8) | (U64(source) << 20) | (U64(1) << 47));
                    emit(0x5c10000000000000UL | predicate | 254 | (U64(254) << 8) | (U64(255) << 20) | (U64(1) << 43));
                };
                emit(0x5c10000000000000UL | predicate | 253 | (U64(a) << 8) | (U64(b) << 20) | (U64(1) << 47));
                emit(0x5c10000000000000UL | predicate | 254 | (U64(255) << 8) | (U64(255) << 20) | (U64(1) << 43));
                accumulate(c);
                if (extended) {
                    foreach (uint carry in new uint[] {insn.Bits(87, 4), insn.Bits(77, 4)}) {
                        if (carry == 15) continue;
                        mov_imm(250, 1);
                        if (carry != 7)
                            emit(0x5ca0000000000000UL | predicate | 250 | (U64(250) << 8) | (U64(255) << 20) |
                                 (U64(carry & 7) << 39) | (U64(carry >> 3) << 42));
                        accumulate(250);
                    }
                } else if (U32(negate_a) + U32(negate_b) + U32(negate_c) is uint ones && ones != 0) {
                    mov_imm(250, ones);
                    accumulate(250);
                }
                if (dest != 255) emit(@base(0x5c98000000000000UL) | (U64(253) << 20) | (U64(15) << 39));
                // An output that is also the guard predicate is written last.
                bool guard_first = (native_predicate & 7) != 7 && first_output == (native_predicate & 7);
                foreach (uint output in new uint[] {guard_first ? 1U : 0U, guard_first ? 0U : 1U}) {
                    uint target = B(output) ? second_output : first_output;
                    if (target == 7) continue;
                    mov_imm(250, output + 1);
                    emit(0x5b60000000000000UL | predicate | 7 | (U64(target) << 3) | (U64(254) << 8) |
                         (U64(250) << 20) | (U64(7) << 39) | (U64(6) << 49));
                }
                break;
            }
            if ((opcode == 0xa10 || opcode == 0xc10) && insn.Bits(81, 3) != 7) { // cbuf or uniform b
                if (insn.Bits(64, 8) != 255 || B(insn.Bits(73, 4)) || insn.Bits(84, 7) != 0x7f ||
                    insn.Bits(77, 4) != 15 || B(insn.Bits(63, 1)) || B(insn.Bits(72, 1)) ||
                    B((opcode == 0xc10 && B((insn[1] & ~0x3fU)))))
                    return fail(index, "native constant add carry mode");
                uint b = operand(1, false);
                emit(@base(0x5c10000000000000UL) | (U64(insn.Bits(24, 8)) << 8) | (U64(b) << 20));
                emit(0x5b60000000000000UL | predicate | 7 | (U64(insn.Bits(81, 3)) << 3) |
                    (dest << 8) | (U64(b) << 20) | (U64(7) << 39) | (U64(1) << 49));
                break;
            }
            if ((opcode == 0xa10 || opcode == 0xc10) && B(insn.Bits(74, 1))) { // IADD3.X Ra + cbuf or UR + carry predicate
                if ((insn[2] & ~(0x07800000U | (opcode == 0xc10 ? 0x08000000U : 0U))) != 0x007fe4ffU ||
                    (opcode == 0xc10 && B((insn[1] & ~0x3fU))))
                    return fail(index, "native extended constant add mode");
                uint a = insn.Bits(24, 8), b = operand(1, false);
                mov_imm(250, 1);
                emit(0x5ca0000000000000UL | predicate | 250 | (U64(250) << 8) | (U64(255) << 20) |
                    (U64(insn.Bits(87, 3)) << 39) | (U64(insn.Bits(90, 1)) << 42));
                if (a != 255) emit(0x5c10000000000000UL | predicate | 250 | (U64(a) << 8) | (U64(250) << 20));
                emit(@base(0x5c10000000000000UL) | (U64(b) << 8) | (U64(250) << 20));
                break;
            }
            // Third source, optionally negated (bit 75; 63 and 72 negate b and a).
            if (insn.Bits(64, 8) != 255 && (insn.Bits(73, 4) & ~4U) == 0 && insn.Bits(77, 7) == 0x7f && insn.Bits(84, 7) == 0x7f) {
                uint b = operand(1, false);
                emit(0x5c10000000000000UL | predicate | 250 | (U64(insn.Bits(24, 8)) << 8) |
                    (U64(b) << 20) | (U64(negate_b) << 48) | (U64(insn.Bits(72, 1)) << 49));
                emit(@base(0x5c10000000000000UL) | (U64(250) << 8) | (U64(insn.Bits(64, 8)) << 20) |
                     (U64(insn.Bits(75, 1)) << 48));
                break;
            }
            if (insn.Bits(64, 8) != 255 || B(insn.Bits(73, 4)) || insn.Bits(77, 7) != 0x7f || insn.Bits(84, 7) != 0x7f) return fail(index, "native integer carry/third source");
            if (opcode == 0xa10 || opcode == 0xc10) {
                if (opcode == 0xc10 && B((insn[1] & ~0x8000003fU))) return fail(index, "native uniform add operand");
                uint b = operand(1, false);
                emit(@base(0x5c10000000000000UL) | (U64(insn.Bits(24, 8)) << 8) | (U64(b) << 20) |
                    (U64(insn.Bits(63, 1)) << 48) | (U64(insn.Bits(72, 1)) << 49));
            } else if (opcode == 0x210) {
                if (B(insn[1] & ~0x800000ffU)) return fail(index, "native integer add register modifiers");
                emit(@base(0x5c10000000000000UL) | (U64(insn.Bits(24, 8)) << 8) |
                     (U64(insn.Bits(32, 8)) << 20) | (U64(insn.Bits(63, 1)) << 48) | (U64(insn.Bits(72, 1)) << 49));
            } else {
                emit(@base(0x1c00000000000000UL) | (U64(insn.Bits(24, 8)) << 8) | (U64(insn[1]) << 20) | (U64(insn.Bits(72, 1)) << 56));
            }
            break;
        }
        case 0x949: case 0x958: { // Captured BRX / BRXU with absolute native-PC table values
            if (insn.Bits(12, 4) != 7 || (insn[2] & ~0x3ffffU) != (opcode == 0x958 ? 0x0b800000U : 0x03800000U))
                return fail(index, "native computed branch mode");
            ulong raw = insn[1] | (U64(insn[2] & 0x3ffff) << 32);
            long relative = S64(raw << 14) >> 14;
            if (S64(index) * 16 + 16 + relative != 0)
                return fail(index, "native computed branch relative base");
            long offset = -S64(address_of(U32(result.Instructions.Size()))) - 8;
            if (offset < -0x800000) return fail(index, "native computed branch range");
            uint source = opcode == 0x958 ? uniform_register(insn.Bits(24, 8)) : insn.Bits(24, 8);
            if (source == ~0U) return fail(index, "native uniform computed branch source");
            emit(0xe25000000000000fUL | predicate | (U64(source) << 8) |
                 ((U64(offset) & 0xffffff) << 20));
            computed_branch = true;
            break;
        }
        case 0x944: { // Verified CALL.REL.NOINC with R55 holding the return PC
            // A predicated NOINC forward transfer can terminate a loop at its
            // reconvergence point without a return-link setup or matching RET.
            if (native_predicate != 7 && insn[1] == 16 && insn[2] == 0x03c00000 &&
                index + 2 < code.Size() && code[index + 1].Bits(0, 12) == 0x947) {
                relocations.Add(new Relocation(U32(result.Instructions.Size()), index + 2));
                emit(0xe24000000000000fUL | predicate);
                break;
            }
            bool adjacent = index != 0 &&
                (code[index - 1][0] & ~0x00ff0000U) == 0x00007802 &&
                code[index - 1].Bits(16, 8) < 250 &&
                code[index - 1][1] == (index + 1) * 16 &&
                code[index - 1][2] == 0xf00;
            if (index == 0 || insn[0] != 0x7944 ||
                (insn[2] & ~0x3ffffU) != 0x03c00000 || (!adjacent && !bounded_control))
                return fail(index, "native call linkage mode");
            ulong raw = insn[1] | (U64(insn[2] & 0x3ffff) << 32);
            long relative = S64(raw << 14) >> 14;
            long target = S64(index) * 16 + 16 + relative;
            if (target < 0 || B(U32(target % 16))) return fail(index, "native call destination alignment");
            uint link = adjacent ? code[index - 1].Bits(16, 8) : bounded_links[index];
            for (int call = 0; call < call_targets.Size(); ++call) {
                if (call_targets[call] == target / 16 && call_links[call] != link)
                    return fail(index, "native inconsistent call linkage");
            }
            call_targets.Add(U32(target / 16));
            call_links.Add(link);
            relocations.Add(new Relocation(U32(result.Instructions.Size()), target / 16, true));
            emit(0xe26000000007000fUL);
            break;
        }
        case 0x950: { // Verified RET.REL.NODEC epilogue restoring the call link
            uint return_reg = insn.Bits(24, 8);
            // The return register is the link itself, or the last write to it in this function
            // is a copy of the link (MOV or IMAD.MOV.U32 Rd, RZ, RZ, Rlink).
            bool copies_link(Nx2Instruction prior) {
                if (prior.Bits(16, 8) != return_reg || prior.Bits(12, 4) != 7) return false;
                if ((prior[0] & 0xff00ffffU) == 0x00007202U)
                    return prior[1] == active_link.Value && prior[2] == 0xf00;
                if ((prior[0] & 0xff00ffffU) == 0xff007224U)
                    return prior[1] == 0xff && prior[2] == (0x078e0000U | active_link.Value);
                return false;
            };
            bool linked = (active_link).HasValue && return_reg == active_link.Value;
            for (uint prior = index; (active_link).HasValue && !linked && prior-- > active_entry;) {
                if (copies_link(code[prior])) linked = true;
                else if (code[prior].Bits(16, 8) == return_reg) break;
            }
            if (!main_exited || !linked ||
                (insn[0] & 0x00ffffffU) != 0x00007950 || return_reg >= 249 ||
                (insn[2] & ~0x3ffffU) != 0x03c00000 || !barrier_tokens.Empty())
                return fail(index, "native return linkage mode");
            ulong raw = insn[1] | (U64(insn[2] & 0x3ffff) << 32);
            long relative = S64(raw << 14) >> 14;
            if (S64(index) * 16 + 16 + relative != 0)
                return fail(index, "native return relative base");
            emit(0xe32000000007000fUL);
            bool complete = !computed_branch && geometry_exit == null;
            foreach (uint target in call_targets) complete &= target <= index;
            if (complete) return finish(index);
            break;
        }
        case 0x945: case 0x941: case 0x942: case 0x948: // (+ WARPSYNC)
            // BSSY / BSYNC / BREAK only manage warp reconvergence: BSYNC waits for the other
            // threads and falls through, BREAK drops the thread from that wait. Eden runs threads
            // independently, so all three are no-ops (a SYNC would wrongly jump, and a branch
            // out of a lowered SSY region would leave its token on Eden's flow stack).
            break;
        case 0x947: { // BRA
            // The offset's two low bits: .DIV (2) branches when the warp has diverged, .CONV (3)
            // when its active threads are the mask in the uniform register at 24-31 (bit 91;
            // URZ without it). The compiler puts warp-synchronous fast paths behind them (shuffles
            // on a converged warp; the other path calls a WARPSYNC helper). Eden runs the warp's
            // threads converged there: .CONV always branches and .DIV never does.
            uint mode = insn.Bits(32, 2);
            if ((mode <= 1 && B(insn.Bits(91, 1))) ||
                (mode == 1 && (B(insn.Bits(82, 5)) || B(insn.Bits(92, 13)))))
                return fail(index, "native branch convergence mode");
            if (mode == 2) break;
            ulong branch_predicate = predicate;
            if (opcode == 0x947 && insn.Bits(87, 4) != 7) {
                if (insn.Bits(12, 4) != 7) {
                    relocations.Add(new Relocation(U32(result.Instructions.Size()), index + 1));
                    emit(0xe24000000000000fUL | (U64(insn.Bits(12, 4) ^ 8U) << 16));
                }
                branch_predicate = U64(insn.Bits(87, 4)) << 16;
            } else if (insn.Bits(87, 4) != 7) {
                return fail(index, "native extra branch condition");
            }
            ulong raw = (insn[1] & ~3U) | (U64(insn[2] & 0x3ffff) << 32);
            long relative = S64(raw << 14) >> 14;
            long target = S64(index) * 16 + 16 + relative;
            if (target < 0 || B(U32(target % 16))) return fail(index, "native branch destination alignment");
            relocations.Add(new Relocation(U32(result.Instructions.Size()), target / 16));
            emit(0xe24000000000000fUL | branch_predicate);
            if (computed_branch && target == S64(index) * 16 && branch_predicate == (U64(7) << 16))
                return finish(index); // the closing self-branch
            break;
        }
        case 0x34e: {
            if (!geometry_stage) return fail(index, "unsupported native opcode");
            if (!B(native_code_address) || B(insn.Bits(24, 8)) || B(insn[1]) || B(insn[2]) ||
                B((dest != 255 && dest + 1 >= (uses_uniform_bank ? high_uniform_base : 246U))) ||
                native_predicate != 7)
                return fail(index, "native program counter load mode");
            if (dest == 255) break;
            ulong address = native_code_address + U64(index) * 16U;
            mov_imm(U32(dest), U32(address));
            mov_imm(U32(dest) + 1, U32(address >> 32));
            break;
        }
        case 0x923: case 0x927: { // ISBERD / ISBEWR: task and mesh programs' stage buffer.
            if (geometry_stage || tessellation_stage != Nx2TessellationStage.None) {
                if (opcode != 0x923 || B(insn[1]) || B(insn[2]) ||
                    insn.Bits(24, 8) == 255)
                    return fail(index, tessellation_stage != Nx2TessellationStage.None
                                           ? "native tessellation stage buffer read mode"
                                           : "native geometry stage buffer read mode");
                emit(@base(0x5c98000000000000UL) | (U64(15) << 39) |
                     (U64(insn.Bits(24, 8)) << 20));
                break;
            }
            // Ampere: Ra 24-31, offset 40-63, ISBERD dest 16-23, ISBEWR data 32-39; 75 = .32 (else
            // U8), 76-77 = 3 .ATTR (0 the map region), 78 = .SKEW, 79 = .O (the output buffer).
            // Kept as an nx2 pseudo instruction (Maxwell 0xefc0, free there) that the frontend
            // reads for the task or mesh stage: data/dest 0-7, Ra 8-15, offset 20-43, .32 44,
            // .ATTR 45, .SKEW 46, .O 47, write 48. Addresses are linear bytes in the region.
            bool write = opcode == 0x927;
            uint region = insn.Bits(76, 2);
            bool output = insn.Bits(79, 1) != 0;
            if ((insn[2] & ~0xf800U) != 0 || (region != 0 && region != 3) || write != output ||
                (!write && region != 3) || (!write && dest == 255))
                return fail(index, "native stage buffer access mode");
            emit(0xefc0000000000000UL | predicate | (write ? U64(insn.Bits(32, 8)) : dest) |
                 (U64(insn.Bits(24, 8)) << 8) | (U64(insn.Bits(40, 24)) << 20) |
                 (U64(insn.Bits(75, 1)) << 44) | (U64(region == 3) << 45) |
                 (U64(insn.Bits(78, 1)) << 46) | (U64(output) << 47) | (U64(write) << 48));
            break;
        }
        case 0x94d: { // EXIT
            if (geometry_exit != null && index * 16U < geometry_exit.Handler) {
                relocations.Add(new Relocation(U32(result.Instructions.Size()),
                                       S64(geometry_exit.Handler / 16U)));
                emit(0xe24000000000000fUL | predicate);
                bool pending_geometry_branch = AnyOf(relocations, (Relocation relocation) => {
                        return !relocation.call && relocation.target > index &&
                               relocation.target !=
                                   S64(geometry_exit.Handler / 16U);
                    });
                if (geometry_exit.HasReturns && native_predicate == 7 && !main_exited && !pending_geometry_branch &&
                    !call_targets.Empty()) {
                    foreach (uint target in call_targets) {
                        if (target <= index)
                            return fail(index, "native call target overlaps main program");
                    }
                    main_exited = true;
                }
                break;
            }
            if (B(insn.Bits(84, 2))) {
                return fail(index, "native conditional/special EXIT");
            }
            if (insn.Bits(87, 4) != 7) {
                // @Pg EXIT Pc: exits when both hold; skip over a Pc-predicated EXIT unless Pg.
                if (native_predicate != 7) {
                    relocations.Add(new Relocation(U32(result.Instructions.Size()), index + 1));
                    emit(0xe24000000000000fUL | (U64(native_predicate ^ 8U) << 16));
                }
                emit(0xe30000000000000fUL | (U64(insn.Bits(87, 4)) << 16));
                break;
            }
            result.Instructions.Add(0xe30000000000000fUL | predicate);
            if (insn.Bits(12, 4) != 7) break;
            if (!barrier_tokens.Empty()) return fail(index, "native unclosed reconvergence scope");
            if (call_targets.Empty()) {
                bool pending_any_branch = AnyOf(relocations, (Relocation relocation) => { return relocation.target > index; });
                if (!pending_any_branch && !computed_branch) return finish(index);
                break;
            }
            // An EXIT with main-program branches still pending past it (not calls) ends one path
            // of the main program; the functions start after the last of them.
            bool pending_main_branch = AnyOf(relocations, (Relocation relocation) => { return !relocation.call && relocation.target > index; });
            if (!main_exited && pending_main_branch) break;
            if (main_exited) {
                // A library function may end the shader instead of returning (Eden inlines
                // calls, so this is an ordinary exit at that point).
                bool complete = NoneOf(relocations, (Relocation relocation) => { return relocation.target > index; });
                foreach (uint target in call_targets) complete &= target <= index;
                if (complete && !computed_branch) return finish(index);
                break;
            }
            foreach (uint target in call_targets) {
                if (target <= index) return fail(index, "native call target overlaps main program");
            }
            // The main program goes on past an EXIT that a branch still jumps over.
            bool pending_branch = AnyOf(relocations, (Relocation relocation) => {
                    return relocation.target > index &&
                           !call_targets.Contains(U32(relocation.target));
                });
            if (!pending_branch) main_exited = true;
            break;
        }
        default:
            return fail(index, "unsupported native opcode");
        }
        if (!operand_error.Empty()) return fail(index, operand_error);
        if (B(output_mask)) {
            emit(0x38e8000000070000UL | 239 | (U64(239) << 8) | (U64(output_mask) << 20));
            emit(0x38f0000000070000UL | (U64(254) << 8) | (U64(output_mask) << 20));
        }
        if (guarded_uniform) restore_predicate(guard_mask);
        if (!pressure.Suffix.Empty()) {
            // Branches that skip the rest of this instruction land on its copies back.
            for (int relocation = relocation_start; relocation < relocations.Size(); ++relocation) {
                var pending = relocations[relocation];
                if (!pending.call && pending.output_index >= instruction_start && pending.target == index + 1)
                    pending.output_target = U32(result.Instructions.Size());
            }
            result.Instructions.AddRange(pressure.Suffix);
        }
    }
    return fail(U32(code.Size()), main_exited ? "native program lacks captured library return" : "native program has no unconditional EXIT");
}
}
}
