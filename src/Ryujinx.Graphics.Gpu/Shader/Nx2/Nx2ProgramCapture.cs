using System;

namespace Ryujinx.Graphics.Gpu.Shader.Nx2
{
    /// <summary>
    /// A 128-bit Ampere (SM86) instruction.
    /// </summary>
    public struct Nx2Instruction
    {
        public uint W0, W1, W2, W3;

        public Nx2Instruction(uint w0, uint w1, uint w2, uint w3)
        {
            W0 = w0;
            W1 = w1;
            W2 = w2;
            W3 = w3;
        }

        public uint this[int index]
        {
            readonly get => index switch
            {
                0 => W0,
                1 => W1,
                2 => W2,
                _ => W3,
            };
            set
            {
                switch (index)
                {
                    case 0: W0 = value; break;
                    case 1: W1 = value; break;
                    case 2: W2 = value; break;
                    default: W3 = value; break;
                }
            }
        }

        public uint this[uint index]
        {
            readonly get => this[(int)index];
            set => this[(int)index] = value;
        }

        public readonly uint Bits(uint offset, uint count)
        {
            ulong bits = this[(int)(offset / 32)];

            if (offset / 32 + 1 < 4)
            {
                bits |= (ulong)this[(int)(offset / 32 + 1)] << 32;
            }

            return (uint)((bits >> (int)(offset % 32)) & ((1UL << (int)count) - 1));
        }

        public readonly uint Opcode => W0 & 0xfff;
    }

    /// <summary>
    /// Finds where a native program ends while its instructions are read one after the other:
    /// the last EXIT (or the RET after it) past every forward branch and call target, or the
    /// self-branch that ends a program with a jump table.
    /// </summary>
    public sealed class Nx2ProgramCapture
    {
        public enum Result
        {
            Continue,
            Complete,
            InvalidTarget,
        }

        public const int MaximumInstructions = 65536;

        private readonly Nx2Instruction[] _exitSetup = new Nx2Instruction[5];
        private uint _lastCallTarget;
        private uint _lastForwardTarget;
        private bool _mainExited;
        private bool _computedBranch;

        public Result Observe(Nx2Instruction instruction, uint index)
        {
            uint opcode = instruction.Opcode;

            if (index < _exitSetup.Length)
            {
                _exitSetup[index] = instruction;

                // The geometry exit envelope: a program counter load, then a jump over the setup.
                if (index + 1 == _exitSetup.Length &&
                    Array.TrueForAll(_exitSetup, setup => (setup.W3 & 0xc00001ffU) == 0) &&
                    _exitSetup[0].W0 == 0x0000734e && _exitSetup[0].W1 == 0 && _exitSetup[0].W2 == 0 &&
                    _exitSetup[1].W0 == 0x00037802 && _exitSetup[1].W2 == 0xf00 &&
                    _exitSetup[2].W0 == 0x00007210 && _exitSetup[2].W1 == 3 && _exitSetup[2].W2 == 0x07f1e0ff &&
                    _exitSetup[3].W1 == 0xff &&
                    ((_exitSetup[3].W0 == 0x01017210 && _exitSetup[3].W2 == 0x007fe4ff) ||
                     (_exitSetup[3].W0 == 0xff017224 && _exitSetup[3].W2 == 0x000e0401)) &&
                    instruction.W0 == 0x00007357 && instruction.W1 == 0 && instruction.W2 == 0)
                {
                    uint target = _exitSetup[1].W1;

                    if (target < _exitSetup.Length * 16 || target % 16 != 0 || target >= MaximumInstructions * 16)
                    {
                        return Result.InvalidTarget;
                    }

                    _lastForwardTarget = Math.Max(_lastForwardTarget, target / 16);
                }
            }

            if (opcode == 0x944 || opcode == 0x947 || opcode == 0x945)
            {
                // BRA keeps its .DIV/.CONV mode in the offset's two low bits.
                uint low = instruction.W1 & (opcode == 0x947 ? ~3U : ~0U);
                ulong raw = low | ((ulong)(instruction.W2 & 0x3ffff) << 32);
                long relative = opcode == 0x945 ? (int)instruction.W1 : (long)(raw << 14) >> 14;
                long target = (long)index * 16 + 16 + relative;

                if (target < 0 || target % 16 != 0 || target >= MaximumInstructions * 16)
                {
                    return Result.InvalidTarget;
                }

                // CALL.REL.NOINC (bit 86) pushes no return address, but its callee still returns
                // with RET.REL.NODEC through the link register set ahead of the call.
                if (opcode == 0x944)
                {
                    _lastCallTarget = Math.Max(_lastCallTarget, (uint)(target / 16));
                }
                else
                {
                    _lastForwardTarget = Math.Max(_lastForwardTarget, (uint)(target / 16));
                }

                // The compiler ends every program with an unconditional branch to itself; with a jump
                // table (whose targets live in a constant buffer) that is the only reliable end.
                if (opcode == 0x947 && _computedBranch && instruction.Bits(12, 4) == 7 && target == (long)index * 16)
                {
                    return Result.Complete;
                }
            }

            if (opcode == 0x949 || opcode == 0x958)
            {
                _computedBranch = true;
            }

            if (_computedBranch)
            {
                return Result.Continue;
            }

            if (opcode == 0x94d && instruction.Bits(12, 4) == 7 && index >= _lastForwardTarget)
            {
                if (index >= _lastCallTarget)
                {
                    return Result.Complete;
                }

                _mainExited = true;
            }

            if (opcode == 0x950 && instruction.Bits(12, 4) == 7 && _mainExited &&
                index >= _lastCallTarget && index >= _lastForwardTarget)
            {
                return Result.Complete;
            }

            return Result.Continue;
        }
    }
}
