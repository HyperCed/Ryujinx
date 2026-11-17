using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Shader.Nx2
{
    // Port of Lichii's shader_recompiler/frontend/ampere/geometry_exit.h.

    /// <summary>
    /// The exit envelope of a native geometry program: setup instructions that install an exit
    /// handler, the body, and the handler that finalizes the output.
    /// </summary>
    sealed class Nx2GeometryExit
    {
        public const uint SetupInstructions = 5;

        public readonly List<(uint Offset, uint Predicate)> Exits = [];
        public uint Handler;
        public bool HasReturns;
        public uint ErrorOffset;
        public string Error;

        public bool Ok => string.IsNullOrEmpty(Error);

        public static Nx2GeometryExit Analyze(Nx2Instruction[] code)
        {
            const uint ModeMask = 0xc00001ffU;
            const int Setup = (int)SetupInstructions;

            Nx2GeometryExit result = new();

            Nx2GeometryExit Fail(int index, string reason)
            {
                result.Exits.Clear();
                result.Handler = 0;
                result.HasReturns = false;
                result.ErrorOffset = (uint)(index * 16);
                result.Error = reason;

                return result;
            }

            uint Bits(int index, uint offset, uint count) => code[index].Bits(offset, count);

            bool Is(int index, uint word0, uint word1, uint word2)
            {
                Nx2Instruction words = code[index];

                return words[0] == word0 && words[1] == word1 && words[2] == word2 && (words[3] & ModeMask) == 0;
            }

            bool IsExit(int index)
            {
                Nx2Instruction words = code[index];

                return (words[0] & ~0xf000U) == 0x94d && words[1] == 0 && words[2] == 0x03800000 &&
                       (words[3] & ModeMask) == 0 && Bits(index, 12, 4) != 15;
            }

            bool IsReturn(int index)
            {
                Nx2Instruction words = code[index];
                ulong raw = words[1] | ((ulong)(words[2] & 0x3ffff) << 32);
                long relative = (long)(raw << 14) >> 14;

                return (words[0] & 0x00ffffffU) == 0x00007950 && Bits(index, 24, 8) < 249 &&
                       (words[2] & ~0x3ffffU) == 0x03c00000 && (words[3] & ModeMask) == 0 &&
                       (long)(index + 1) * 16 + relative == 0;
            }

            int count = code.Length;

            if (count < Setup + 4)
            {
                return Fail(count, "native exit envelope truncated");
            }

            uint target = code[1][1];

            if (!Is(0, 0x734e, 0, 0) || !Is(1, 0x37802, target, 0xf00) || !Is(2, 0x7210, 3, 0x07f1e0ff) ||
                !(Is(3, 0x01017210, 0xff, 0x007fe4ff) || Is(3, 0xff017224, 0xff, 0x000e0401)) ||
                !Is(4, 0x7357, 0, 0))
            {
                return Fail(0, "native exit handler installation");
            }

            int handler = (int)(target / 16);

            if (target % 16 != 0 || handler <= Setup + 1 || handler + 3 != count)
            {
                return Fail(1, "native exit handler location");
            }

            if (!Is(handler, 0x00ff7324, 0xff, 0) || !Is(handler + 1, 0x7957, 0, 0) ||
                !Is(handler + 2, 0x794d, 0, 0x03800000))
            {
                return Fail(handler, "native exit handler body");
            }

            if (!(IsExit(handler - 1) && Bits(handler - 1, 12, 4) == 7) && !IsReturn(handler - 1))
            {
                return Fail(handler - 1, "native exit handler reached by fallthrough");
            }

            for (int index = Setup; index < handler; index++)
            {
                uint opcode = Bits(index, 0, 12), family = opcode & 0x1ff;

                if (family == 0x14e || (family >= 0x154 && family <= 0x157))
                {
                    return Fail(index, "native exit handler state access in body");
                }

                if (opcode == 0x94d)
                {
                    if (!IsExit(index))
                    {
                        return Fail(index, "native body EXIT mode");
                    }

                    result.Exits.Add(((uint)(index * 16), Bits(index, 12, 4)));

                    continue;
                }

                if (IsReturn(index))
                {
                    result.HasReturns = true;

                    continue;
                }

                if (family < 0x140 || family >= 0x160 || opcode == 0x941 || opcode == 0x942 || opcode == 0x948)
                {
                    continue;
                }

                Nx2Instruction words = code[index];
                bool noincCall = opcode == 0x944 && (words[2] & 0x400000) != 0;

                if (opcode != 0x945 && opcode != 0x947 && !noincCall)
                {
                    return Fail(index, "native control flow in exit envelope body");
                }

                ulong raw = words[1] | ((ulong)(words[2] & 0x3ffff) << 32);
                long relative = opcode == 0x945 ? (int)words[1] : (long)(raw << 14) >> 14;
                long destination = (long)index * 16 + 16 + relative;

                if (destination % 16 != 0 || destination < Setup * 16 || destination >= (long)handler * 16)
                {
                    return Fail(index, "native branch leaves exit envelope body");
                }
            }

            uint liveRegisters = 0b1011, livePredicates = 0b1;

            for (int index = Setup; liveRegisters != 0 || livePredicates != 0; index++)
            {
                uint opcode = Bits(index, 0, 12), family = opcode & 0x1ff;
                uint form = (opcode >> 9) & 7, guard = Bits(index, 12, 4);
                uint reads = 0, readPredicates = 0, writes = 0, writePredicates = 0;

                void ReadRegister(uint reg)
                {
                    if (reg < 32)
                    {
                        reads |= 1U << (int)reg;
                    }
                }

                void WriteRegister(uint reg)
                {
                    if (reg < 32)
                    {
                        writes |= 1U << (int)reg;
                    }
                }

                void ReadPredicate(uint pred)
                {
                    if (pred < 7)
                    {
                        readPredicates |= 1U << (int)pred;
                    }
                }

                void WritePredicate(uint pred)
                {
                    if (pred < 7)
                    {
                        writePredicates |= 1U << (int)pred;
                    }
                }

                void ReadSources(bool hasC)
                {
                    ReadRegister(Bits(index, 24, 8));

                    if (form == 1)
                    {
                        ReadRegister(Bits(index, 32, 8));
                    }

                    if (form == 2 || form == 3 || form == 7)
                    {
                        ReadRegister(Bits(index, 64, 8));
                    }

                    if (hasC && (form == 1 || form == 4 || form == 5 || form == 6))
                    {
                        ReadRegister(Bits(index, 64, 8));
                    }
                }

                ReadPredicate(guard & 7);

                if (IsExit(index))
                {
                    if (guard != 7)
                    {
                        return Fail(index, "native instruction before exit setup state is dead");
                    }

                    // The handler finalizes the output through R0; the rest of the setup state may stay
                    // live when the body ends without reading it.
                    if ((liveRegisters & 1) != 0 || (readPredicates & livePredicates) != 0)
                    {
                        return Fail(index, "native exit handler reads exit setup state");
                    }

                    break;
                }

                if (opcode == 0x919 || opcode == 0x923)
                {
                    if (opcode == 0x923)
                    {
                        ReadRegister(Bits(index, 24, 8));
                    }

                    WriteRegister(Bits(index, 16, 8));
                }
                else if (opcode == 0x805 || opcode == 0xab9 || opcode == 0x945)
                {
                }
                else if (opcode == 0x381 || opcode == 0x981)
                {
                    uint address = Bits(index, 24, 8), size = Bits(index, 73, 3);

                    if (opcode == 0x381)
                    {
                        ReadPredicate(7 - Bits(index, 64, 3));
                    }

                    if (address != 255)
                    {
                        ReadRegister(address);
                        ReadRegister(address + 1);
                    }

                    uint results = size < 5 ? 1U : size == 5 ? 2U : size == 6 ? 4U : 0U;

                    for (uint i = 0; i < results; i++)
                    {
                        WriteRegister(Bits(index, 16, 8) + i);
                    }
                }
                else if (family == 0x002 && (form == 1 || form == 4 || form == 5 || form == 6))
                {
                    if (form == 1)
                    {
                        ReadRegister(Bits(index, 32, 8));
                    }

                    WriteRegister(Bits(index, 16, 8));
                }
                else if ((family == 0x024 || family == 0x016) && form != 0)
                {
                    ReadSources(true);

                    if (family == 0x024 && Bits(index, 74, 1) != 0)
                    {
                        ReadPredicate(Bits(index, 87, 3));
                    }

                    WriteRegister(Bits(index, 16, 8));
                }
                else if (family == 0x105 && (form == 1 || form == 5 || form == 6))
                {
                    // F2I: register, constant buffer or uniform source in operand b; a is unused.
                    if (form == 1)
                    {
                        ReadRegister(Bits(index, 32, 8));
                    }

                    WriteRegister(Bits(index, 16, 8));
                }
                else if (family == 0x011 && form != 0)
                {
                    // LEA (.X reads the carry predicate), optional carry out.
                    ReadSources(true);

                    if (Bits(index, 74, 1) != 0)
                    {
                        ReadPredicate(Bits(index, 87, 3));
                    }

                    WriteRegister(Bits(index, 16, 8));
                    WritePredicate(Bits(index, 81, 3));
                }
                else if (opcode == 0xb82)
                {
                    // LDC with a register index; a 64-bit load writes a pair.
                    if (Bits(index, 24, 8) != 255)
                    {
                        ReadRegister(Bits(index, 24, 8));
                    }

                    WriteRegister(Bits(index, 16, 8));

                    if (Bits(index, 73, 3) == 5)
                    {
                        WriteRegister(Bits(index, 16, 8) + 1);
                    }
                }
                else if (family == 0x012 && form != 0)
                {
                    // LOP3.LUT with an optional predicate result.
                    ReadSources(true);
                    WriteRegister(Bits(index, 16, 8));
                    WritePredicate(Bits(index, 81, 3));
                }
                else if (opcode == 0x321)
                {
                    // ALD: loads 1-4 components into Rd.. from the vertex in b, indexed by a unless RZ.
                    ReadRegister(Bits(index, 24, 8));
                    ReadRegister(Bits(index, 32, 8));

                    for (uint i = 0; i <= Bits(index, 74, 2); i++)
                    {
                        WriteRegister(Bits(index, 16, 8) + i);
                    }
                }
                else if (opcode == 0x324 || opcode == 0x924)
                {
                    // OUT.EMIT/CUT: reads the output handle in a (and a register stream in b), writes the
                    // next handle to Rd.
                    ReadSources(false);
                    WriteRegister(Bits(index, 16, 8));
                }
                else if (opcode == 0x322)
                {
                    // AST: stores 1-4 components from operand b for the vertex in c; writes nothing.
                    uint value = Bits(index, 32, 8), components = Bits(index, 74, 2) + 1;

                    if (value != 255)
                    {
                        for (uint i = 0; i < components; i++)
                        {
                            ReadRegister(value + i);
                        }
                    }

                    ReadRegister(Bits(index, 64, 8));
                }
                else if (family == 0x00c && form != 0 && Bits(index, 72, 1) == 0)
                {
                    ReadSources(false);
                    ReadPredicate(Bits(index, 87, 3));
                    WritePredicate(Bits(index, 81, 3));
                    WritePredicate(Bits(index, 84, 3));
                }
                else
                {
                    return Fail(index, "native instruction before exit setup state is dead");
                }

                if ((reads & liveRegisters) != 0 || (readPredicates & livePredicates) != 0)
                {
                    return Fail(index, "native body reads exit setup state");
                }

                if (guard == 7)
                {
                    liveRegisters &= ~writes;
                    livePredicates &= ~writePredicates;
                }
            }

            result.Handler = target;

            return result;
        }
    }
}
