using System.Collections.Generic;

namespace Ryujinx.Graphics.Gpu.Shader.Nx2
{
    // Port of Lichii's shader_recompiler/frontend/ampere/register_pressure.h.

    /// <summary>
    /// One native instruction whose high registers are borrowed into aligned low quartets, with the
    /// copies to run before and after it.
    /// </summary>
    sealed class Nx2RegisterPressure
    {
        public Nx2Instruction Instruction;
        public readonly List<ulong> Prefix = [];
        public readonly List<ulong> Suffix = [];
        public bool Valid = true;

        public Nx2RegisterPressure(Nx2Instruction instruction)
        {
            Instruction = instruction;
        }

        /// <summary>
        /// A register copy between banks of 256 registers (a MOV whose unused Ra field names the banks).
        /// </summary>
        public static ulong NativeRegisterCopy(uint destinationBank, uint destination, uint sourceBank, uint source)
        {
            return 0x5c98000000070000UL | (10UL << 44) | destination | ((ulong)destinationBank << 8) |
                   ((ulong)sourceBank << 10) | ((ulong)source << 20);
        }

        /// <summary>
        /// Preserves high guest registers in SSA while borrowing aligned low quartets for one native
        /// instruction. The expanded instruction owns its temporary bank.
        /// </summary>
        public static Nx2RegisterPressure Prepare(Nx2Instruction input, uint reserved)
        {
            Nx2RegisterPressure result = new(input);

            if (reserved < 4 || reserved > 246 || (reserved & 3) != 0)
            {
                result.Valid = false;

                return result;
            }

            List<(uint Bit, uint Count)> operands = [];

            void Operand(uint bit, uint count = 1) => operands.Add((bit, count));

            uint opcode = input.Bits(0, 12);

            switch (opcode)
            {
                case 0xa02:
                case 0xc3e:
                case 0x802:
                case 0xb08:
                case 0xd08:
                case 0x908:
                case 0x919:
                    Operand(16);
                    break;
                case 0xa0c:
                case 0x804:
                case 0x80b:
                case 0x80c:
                    Operand(24);
                    break;
                case 0x20c:
                case 0x20b:
                    Operand(24);
                    Operand(32);
                    break;
                case 0x807:
                case 0x808:
                case 0x923:
                case 0x820:
                case 0xa20:
                case 0x421:
                    Operand(16);
                    Operand(24);
                    break;
                case 0x207:
                case 0x208:
                case 0x220:
                case 0x221:
                case 0x209:
                    Operand(16);
                    Operand(24);
                    Operand(32);
                    break;
                case 0x805:
                    Operand(16, 2);
                    break;
                case 0xc17:
                case 0x817:
                    Operand(16);
                    Operand(24);
                    break;
                case 0xa24:
                case 0xc24: // IMAD with a uniform multiplier: Rd, Ra and the addend Rc are registers
                case 0xa23:
                case 0x623:
                case 0x423:
                    Operand(16);
                    Operand(24);
                    Operand(64);
                    break;
                case 0x621:
                    Operand(16);
                    Operand(24);
                    break;
                case 0x321:
                    Operand(16, input.Bits(74, 2) + 1);
                    Operand(24);
                    Operand(32);
                    break;
                case 0x322:
                    Operand(24);
                    Operand(32, input.Bits(74, 2) + 1);
                    break;
                case 0x927:
                    Operand(24);
                    Operand(32);
                    break;
                case 0x9a8: // ATOMG: result in Rd, data operand at bit 32.
                    Operand(16);
                    Operand(32);
                    break;
                case 0xb6c:
                case 0xf6c: // TXD: results in Rd/Rd+1 and the second pair, coordinates in Ra (Ra+2 is the layer
                            // of ARRAY_2D), gradients in Rb..Rb+3.
                    Operand(16, 2);
                    Operand(24, input.Bits(61, 3) == 5 ? 3U : 2U);
                    Operand(32, 4);
                    Operand(64, 2);
                    break;
                case 0x224:
                case 0x223:
                    Operand(16);
                    Operand(24);
                    Operand(32);
                    Operand(64);
                    break;
                case 0x81a:
                    Operand(16);
                    Operand(24);
                    break;
                case 0x308:
                case 0x202:
                case 0x243:
                case 0x245:
                case 0x305:
                case 0x306:
                case 0x307:
                    Operand(16);
                    Operand(32);
                    break;
                case 0xb05:
                case 0xd05:
                    Operand(16);
                    break;
                case 0x59f:
                    Operand(24, input.Bits(61, 3) == 1 ? 1U : input.Bits(61, 3) == 5 ? 3U : 2U);
                    Operand(32);
                    break;
                case 0x98e:
                    Operand(24, input.Bits(72, 1) != 0 ? 2U : 1U);
                    Operand(32, input.Bits(73, 3) == 2 || input.Bits(73, 3) == 5 ? 2U : 1U);
                    break;
                case 0x433:
                case 0x633:
                    Operand(16);
                    Operand(24);
                    break;
                case 0x233:
                    Operand(16);
                    Operand(24);
                    Operand(32);
                    break;
                case 0x810:
                case 0xa10:
                case 0xc10:
                case 0x812:
                case 0xa12: // LOP3.LUT, constant buffer and uniform B: no register in B's field
                case 0xc12:
                case 0x824:
                case 0x823:
                case 0x831:
                case 0x424:
                case 0x624:
                    Operand(16);
                    Operand(24);
                    Operand(64);
                    break;
                case 0x230:
                case 0x231:
                    Operand(16);
                    Operand(24);
                    Operand(32);
                    Operand(64);
                    break;
                case 0x419:
                case 0x819:
                case 0xc19: // SHF with a uniform shift amount: Rd and the low and high words are registers
                    Operand(16);
                    Operand(24);
                    Operand(64);
                    break;
                case 0x210:
                case 0x211:
                case 0x212:
                case 0x219: // SHF, register form: Rd, low word, shift amount, high word
                    Operand(16);
                    Operand(24);
                    Operand(32);
                    Operand(64);
                    break;
                case 0xa11:
                    Operand(16);
                    Operand(24);
                    Operand(64);
                    break;
                case 0x225:
                case 0x227:
                case 0x625:
                case 0x825:
                    Operand(16, opcode == 0x227 ? 1U : 2U);
                    Operand(24);
                    if (opcode == 0x225 || opcode == 0x227)
                    {
                        Operand(32);
                        Operand(64, 2);
                    }
                    if (opcode == 0x625)
                    {
                        Operand(64);
                    }
                    if (opcode == 0x825)
                    {
                        Operand(64, 2);
                    }
                    break;
                case 0x83b:
                    Operand(16, 1U << (int)input.Bits(72, 2));
                    Operand(24);
                    break;
                case 0x23c:
                    Operand(16, 2);
                    Operand(24, input.Bits(75, 1) != 0 ? 4U : 2U);
                    Operand(32, input.Bits(75, 1) != 0 ? 2U : 1U);
                    Operand(64, 2);
                    break;
                case 0x388:
                case 0x984:
                case 0x988:
                    {
                        uint width = input.Bits(73, 3), count = width == 6 ? 4U : width == 5 ? 2U : 1U;
                        Operand(opcode == 0x984 ? 16U : 32U, count);
                        Operand(24);
                        break;
                    }
                case 0x387:
                case 0x983:
                case 0x987:
                    {
                        uint width = input.Bits(73, 3), count = width == 6 ? 4U : width == 5 ? 2U : 1U;
                        Operand(opcode == 0x983 ? 16U : 32U, count);
                        Operand(24);
                        break;
                    }
                case 0x381:
                case 0x386:
                case 0x981:
                case 0x986:
                    {
                        uint width = input.Bits(73, 3), count = width == 6 ? 4U : width == 5 ? 2U : 1U;
                        Operand(opcode == 0x386 || opcode == 0x986 ? 32U : 16U, count);
                        Operand(24, opcode == 0x381 || opcode == 0x386 || input.Bits(90, 1) != 0 ? 2U : 1U);
                        break;
                    }
                default:
                    result.Valid = false;
                    return result;
            }

            bool[] used = new bool[256];
            bool[] groups = new bool[64];

            foreach ((uint bit, uint count) in operands)
            {
                uint reg = input.Bits(bit, 8);

                if (reg == 255)
                {
                    continue;
                }

                if (reg + count > 255 ||
                    (reg < reserved && reg + count > reserved) ||
                    (reg >= reserved && reg / 4 != (reg + count - 1) / 4))
                {
                    result.Valid = false;

                    return result;
                }

                for (uint component = 0; component < count; component++)
                {
                    used[reg + component] = true;

                    if (reg + component >= reserved)
                    {
                        groups[(reg + component) / 4] = true;
                    }
                }
            }

            uint[] mapped = new uint[64];

            for (uint group = 0; group < groups.Length; group++)
            {
                if (!groups[group])
                {
                    continue;
                }

                uint borrow = reserved;

                while (borrow >= 4)
                {
                    borrow -= 4;

                    if (!used[borrow] && !used[borrow + 1] && !used[borrow + 2] && !used[borrow + 3])
                    {
                        break;
                    }
                }

                if (used[borrow] || used[borrow + 1] || used[borrow + 2] || used[borrow + 3])
                {
                    result.Valid = false;

                    return result;
                }

                mapped[group] = borrow;

                for (uint component = 0; component < 4; component++)
                {
                    uint low = borrow + component, high = group * 4 + component;

                    used[low] = true;
                    result.Prefix.Add(NativeRegisterCopy(2, low, 0, low));
                    result.Prefix.Add(NativeRegisterCopy(0, low, 1, high));
                    result.Suffix.Add(NativeRegisterCopy(1, high, 0, low));
                    result.Suffix.Add(NativeRegisterCopy(0, low, 2, low));
                }
            }

            foreach ((uint bit, uint _) in operands)
            {
                uint reg = input.Bits(bit, 8);

                if (reg == 255 || !groups[reg / 4])
                {
                    continue;
                }

                uint replacement = mapped[reg / 4] + reg % 4;
                int word = (int)(bit / 32), shift = (int)(bit % 32);

                result.Instruction[word] = (result.Instruction[word] & ~(255U << shift)) | (replacement << shift);
            }

            return result;
        }
    }
}
