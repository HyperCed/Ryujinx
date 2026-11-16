using System;
using System.Collections.Generic;
using System.Numerics;

namespace Ryujinx.Graphics.Gpu.Engine.Nx2
{
    /// <summary>
    /// Interpreter for the TU104/GA10x macro method expander (MME64) that Switch 2 NVN drives 3D and
    /// compute state through. Each 96-bit instruction has two ALUs and two outputs.
    /// Instruction semantics adapted from Mesa's mme_tu104_sim.c and mme_tu104.c
    /// (Copyright © 2022 Collabora Ltd., MIT).
    /// </summary>
    class TuringMacroInterpreter
    {
        public const int DataRamSize = 0xc00;

        private const int RegisterCount = 24;
        private const uint StepLimit = 1000000;

        private struct Decoded
        {
            public uint Raw0, Raw1, Raw2;
            public bool Valid;
            public uint Imm0, Imm1;
            public byte Op0, Op1, Dst0, Dst1, SrcA0, SrcA1, SrcB0, SrcB1;
            public byte OutMethod0, OutMethod1, OutEmit0, OutEmit1;
            public byte PredReg, PredMode;
            // Lane/output enable masks when the predicate register is non-zero / zero.
            public byte MaskTrue, MaskFalse;
            public bool End, Load0, Load1;
        }

        private static readonly string[] _predicates =
        [
            "UUUU", "TTTT", "FFFF", "TTUU", "FFUU", "TFUU", "TUUU", "FUUU",
            "UUTT", "UUTF", "UUTU", "UUFT", "UUFF", "UUFU", "UUUT", "UUUF",
        ];

        // Decoded instructions by PC, re-decoded whenever their words differ.
        private Decoded[] _decoded = [];
        private readonly uint[] _regs = new uint[RegisterCount];

        public readonly record struct Result(bool Success, uint Pc, int Consumed, string Error);

        private static Decoded Decode(uint bits0, uint bits1, uint bits2)
        {
            uint Field(int low, int count)
            {
                ulong value = (low / 32) switch
                {
                    0 => bits0 | ((ulong)bits1 << 32),
                    1 => bits1 | ((ulong)bits2 << 32),
                    _ => bits2,
                };

                return (uint)((value >> (low % 32)) & ((1UL << count) - 1));
            }

            Decoded d = new()
            {
                Raw0 = bits0,
                Raw1 = bits1,
                Raw2 = bits2,
                Valid = true,
                Imm0 = Field(30, 16),
                Imm1 = Field(66, 16),
                Op0 = (byte)Field(10, 5),
                Op1 = (byte)Field(46, 5),
                Dst0 = (byte)Field(15, 5),
                Dst1 = (byte)Field(51, 5),
                SrcA0 = (byte)Field(20, 5),
                SrcA1 = (byte)Field(56, 5),
                SrcB0 = (byte)Field(25, 5),
                SrcB1 = (byte)Field(61, 5),
                OutMethod0 = (byte)Field(82, 3),
                OutMethod1 = (byte)Field(89, 3),
                OutEmit0 = (byte)Field(85, 4),
                OutEmit1 = (byte)Field(92, 4),
                PredReg = (byte)Field(5, 5),
                PredMode = (byte)Field(1, 4),
                End = Field(0, 1) != 0,
                MaskTrue = 15,
                MaskFalse = 15,
            };

            if (d.PredMode != 0)
            {
                d.MaskTrue = d.MaskFalse = 0;

                for (int i = 0; i < 4; i++)
                {
                    if (_predicates[d.PredMode][i] != 'T')
                    {
                        d.MaskTrue |= (byte)(1 << i);
                    }

                    if (_predicates[d.PredMode][i] != 'F')
                    {
                        d.MaskFalse |= (byte)(1 << i);
                    }
                }
            }

            bool Loads(int reg, int output)
            {
                return d.PredReg == reg || d.SrcA0 == reg || d.SrcA1 == reg || d.SrcB0 == reg || d.SrcB1 == reg ||
                       d.OutMethod0 == output || d.OutMethod1 == output || d.OutEmit0 == output || d.OutEmit1 == output;
            }

            d.Load0 = Loads(28, 3);
            d.Load1 = Loads(29, 4);

            return d;
        }

        /// <summary>
        /// Runs a macro.
        /// </summary>
        /// <param name="code">Uploaded macro code, three words per instruction</param>
        /// <param name="start">Index of the first instruction</param>
        /// <param name="parameters">The load FIFO; <paramref name="emit"/> may append to it (MME_DMA_READ_FIFOED)</param>
        /// <param name="dataRam">Data RAM of the channel</param>
        /// <param name="read">Reads a register of the engine the macro runs on</param>
        /// <param name="emit">Writes a method of the engine the macro runs on; returns false if unsupported</param>
        /// <returns>Whether the call succeeded, and why not</returns>
        public Result Execute(ReadOnlySpan<uint> code, uint start, List<uint> parameters, uint[] dataRam, Func<uint, uint> read, Func<uint, uint, bool> emit)
        {
            uint[] regs = _regs;
            uint carry = 0, method = 0, increment = 0;
            uint pc = start, loopCount = 0, loopStart = 0, loopEnd = 0;
            int consumed = 0;
            bool endNext = false, stopped = false;
            string error = null;

            Array.Clear(regs);

            if (_decoded.Length < code.Length / 3)
            {
                Array.Resize(ref _decoded, code.Length / 3);
            }

            Span<uint> load = stackalloc uint[2];
            Span<uint> alu = stackalloc uint[2];
            Span<uint> xs = stackalloc uint[2];
            Span<uint> ys = stackalloc uint[2];

            for (uint steps = 0; steps < StepLimit; steps++)
            {
                if ((long)pc * 3 + 2 >= code.Length)
                {
                    error = "instruction outside uploaded RAM";
                    break;
                }

                uint bits0 = code[(int)pc * 3 + 2], bits1 = code[(int)pc * 3 + 1], bits2 = code[(int)pc * 3];
                ref Decoded d = ref _decoded[pc];

                if (!d.Valid || d.Raw0 != bits0 || d.Raw1 != bits1 || d.Raw2 != bits2)
                {
                    d = Decode(bits0, bits1, bits2);
                }

                load.Clear();
                alu.Clear();

                for (int i = 0; i < 2; i++)
                {
                    if (i == 0 ? d.Load0 : d.Load1)
                    {
                        if (consumed >= parameters.Count)
                        {
                            error ??= "parameter FIFO underflow";
                            break;
                        }

                        load[i] = parameters[consumed++];
                    }
                }

                uint imm0 = d.Imm0, imm1 = d.Imm1;
                uint load0 = load[0], load1 = load[1];

                // Both ALUs read the register file before either destination is committed.
                uint Get(uint reg, int lane, ref string err)
                {
                    if (reg < RegisterCount)
                    {
                        // Registers start a call at zero; macros test them before writing them.
                        return regs[reg];
                    }

                    switch (reg)
                    {
                        case 24:
                            return 0;
                        case 25:
                            return (uint)(short)(lane == 0 ? imm0 : imm1);
                        case 26:
                            return (uint)(short)(lane == 0 ? imm1 : imm0);
                        case 27:
                            return (imm0 << 16) | imm1;
                        case 28:
                            return load0;
                        case 29:
                            return load1;
                        default:
                            err ??= "invalid source register";
                            return 0;
                    }
                }

                uint mask = d.PredMode != 0 ? (Get(d.PredReg, 0, ref error) != 0 ? d.MaskTrue : d.MaskFalse) : 15u;

                xs.Clear();
                ys.Clear();

                for (int lane = 0; lane < 2; lane++)
                {
                    if ((mask & (1u << lane)) != 0)
                    {
                        xs[lane] = Get(lane == 0 ? d.SrcA0 : d.SrcA1, lane, ref error);
                        ys[lane] = Get(lane == 0 ? d.SrcB0 : d.SrcB1, lane, ref error);
                    }
                }

                uint nextPc = pc + 1;
                bool branchCancelsEnd = false;

                for (int lane = 0; lane < 2 && error == null; lane++)
                {
                    if ((mask & (1u << lane)) == 0)
                    {
                        continue;
                    }

                    uint x = xs[lane], y = ys[lane];
                    uint imm = lane == 0 ? imm0 : imm1;
                    uint op = lane == 0 ? d.Op0 : d.Op1;
                    uint dst = lane == 0 ? d.Dst0 : d.Dst1;
                    uint value = 0;

                    bool Condition(uint comparison)
                    {
                        return comparison switch
                        {
                            17 or 25 => (int)x < (int)y,
                            18 or 26 => x < y,
                            19 or 27 => (int)x <= (int)y,
                            20 or 28 => x <= y,
                            _ => x == y,
                        };
                    }

                    switch (op)
                    {
                        case 0:
                            value = x + y;
                            carry = value < x ? 1u : 0u;
                            break;
                        case 1:
                            value = x + y + carry;
                            break;
                        case 2:
                            value = x - y;
                            carry = value > x ? 1u : 0u;
                            break;
                        case 3:
                            value = x - y - carry;
                            break;
                        case 4:
                        case 6:
                            {
                                ulong a = op == 4 ? (ulong)(long)(int)x : x;
                                ulong b = op == 4 ? (ulong)(long)(int)y : y;
                                ulong product = a * b;

                                value = (uint)product;
                                carry = (uint)(product >> 32);
                                break;
                            }
                        case 5:
                            value = carry;
                            break;
                        case 7:
                            // EXTENDED: x = 0x1000 is the load barrier, nothing to do in a sequential interpreter.
                            if (x != 0x1000)
                            {
                                error ??= "unsupported extended operation";
                            }
                            break;
                        case 8:
                            value = (uint)BitOperations.LeadingZeroCount(x);
                            break;
                        case 9:
                            value = x << (int)(y & 31);
                            break;
                        case 10:
                            value = x >> (int)(y & 31);
                            break;
                        case 11:
                            value = (uint)((int)x >> (int)(y & 31));
                            break;
                        case 12:
                            value = x & y;
                            break;
                        case 13:
                            value = ~(x & y);
                            break;
                        case 14:
                            value = x | y;
                            break;
                        case 15:
                            value = x ^ y;
                            break;
                        case 16:
                            {
                                uint dest = (imm >> 10) & 63, count = (imm >> 5) & 31, source = imm & 31;

                                if (dest >= 32)
                                {
                                    error ??= "invalid merge destination";
                                    break;
                                }

                                uint bitsMask = (1u << (int)count) - 1;

                                value = (x & ~(bitsMask << (int)dest)) | (((y >> (int)source) & bitsMask) << (int)dest);
                                break;
                            }
                        case 17:
                        case 18:
                        case 19:
                        case 20:
                        case 21:
                            value = Condition(op) ? ~0u : 0;
                            break;
                        case 22:
                            value = read((x + y) & 0xffff);
                            break;
                        case 23:
                            if (loopCount != 0 || imm < 2)
                            {
                                error ??= "invalid loop";
                                break;
                            }

                            // The native SDK emits zero-count loops around FIFO reads; these must skip the body.
                            if (x == 0)
                            {
                                nextPc = pc + imm;
                                break;
                            }

                            loopCount = x - 1;
                            loopStart = pc;
                            loopEnd = pc + imm - 1;
                            break;
                        case 24:
                            nextPc = pc + (imm & 0x7fff);
                            break;
                        case 25:
                        case 26:
                        case 27:
                        case 28:
                        case 29:
                            if (Condition(op) == ((imm & 0x8000) != 0))
                            {
                                int offset = (int)(imm << 19) >> 19;

                                if (offset == -4096)
                                {
                                    stopped = true;
                                }
                                else
                                {
                                    nextPc = (uint)((int)pc + offset);

                                    // Native back edges use bit 14 to suppress the fallthrough termination
                                    // when the branch is taken.
                                    branchCancelsEnd = (imm & 0x4000) != 0;
                                }
                            }
                            break;
                        case 30:
                            if (x >= dataRam.Length)
                            {
                                error ??= "data RAM read out of bounds";
                                break;
                            }

                            value = dataRam[x];
                            break;
                        case 31:
                            if (x >= dataRam.Length)
                            {
                                error ??= "data RAM write out of bounds";
                                break;
                            }

                            dataRam[x] = y;
                            break;
                    }

                    alu[lane] = value;

                    if (dst < RegisterCount)
                    {
                        regs[dst] = value;
                    }
                    else if (dst != 24)
                    {
                        error ??= "invalid destination register";
                    }
                }

                uint alu0 = alu[0], alu1 = alu[1];

                uint Output(uint selector, ref string err)
                {
                    switch (selector)
                    {
                        case 1:
                            return alu0;
                        case 2:
                            return alu1;
                        case 3:
                            return load0;
                        case 4:
                            return load1;
                        case 5:
                            return imm0;
                        case 6:
                            return imm1;
                        case 8:
                            return imm0 >> 12;
                        case 9:
                            return imm1 >> 12;
                        case 10:
                            return (imm0 << 16) | imm1;
                        default:
                            err ??= "invalid output selector";
                            return 0;
                    }
                }

                for (int lane = 0; lane < 2 && error == null; lane++)
                {
                    if ((mask & (4u << lane)) == 0)
                    {
                        continue;
                    }

                    uint outMethod = lane == 0 ? d.OutMethod0 : d.OutMethod1;
                    uint outEmit = lane == 0 ? d.OutEmit0 : d.OutEmit1;

                    if (outMethod != 0)
                    {
                        uint address = Output(outMethod, ref error);

                        method = address & 0xfff;
                        increment = (address >> 12) & 15;
                    }

                    if (outEmit != 0)
                    {
                        uint value = Output(outEmit, ref error);

                        if (error == null && !emit(method, value))
                        {
                            error = "unsupported emitted method";
                        }

                        method += increment;
                    }
                }

                if (error != null)
                {
                    break;
                }

                if (endNext || stopped)
                {
                    return new Result(true, pc, consumed, null);
                }

                endNext = _decoded[pc].End && !branchCancelsEnd;

                if (loopCount != 0 && pc == loopEnd)
                {
                    loopCount--;
                    nextPc = loopStart + 1;
                }

                pc = nextPc;
            }

            return new Result(false, pc, consumed, error ?? "instruction limit exceeded");
        }
    }
}
