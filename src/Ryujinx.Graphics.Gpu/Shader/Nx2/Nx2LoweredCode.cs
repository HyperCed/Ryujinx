using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Gpu.Shader.Nx2
{
    /// <summary>
    /// Lays out lowered native programs as Maxwell code for the shader translator.
    /// </summary>
    public static class Nx2LoweredCode
    {
        /// <summary>
        /// Size of the Maxwell shader program header in front of graphics code.
        /// </summary>
        public const int HeaderSize = 0x50;

        private const ulong Nop = 0x50b0000000070f00UL;

        /// <summary>
        /// Builds the code the translator reads at the program address: for graphics programs the
        /// Maxwell header (the first 0x50 bytes of the native header), then the instructions.
        /// </summary>
        /// <remarks>
        /// The lowering places branch offsets for code that starts at 0x50 with a scheduling word at
        /// every 32-byte aligned program offset. The translator expects one every 32 bytes from the
        /// start of the code instead, so the code starts with a scheduling word and a NOP: everything
        /// after moves by 16 bytes, which keeps the relative branches and puts the scheduling words
        /// where the translator skips them.
        /// </remarks>
        public static byte[] Build(ReadOnlySpan<byte> header, Nx2LoweredProgram lowered)
        {
            int headerWords = header.IsEmpty ? 0 : HeaderSize / 8;
            int count = lowered.Instructions.Count;

            ulong[] code = new ulong[headerWords + 2 + count + count / 3 + 4];
            int position = 0;

            if (headerWords != 0)
            {
                MemoryMarshal.Cast<byte, ulong>(header[..HeaderSize]).CopyTo(code);
                position = headerWords;
            }

            code[position++] = 0; // Scheduling word.
            code[position++] = Nop;

            // Lowered positions as the lowering counts them: from 0x50, a scheduling word at every
            // 32-byte aligned offset.
            int lichiiOffset = HeaderSize;

            foreach (ulong instruction in lowered.Instructions)
            {
                if (lichiiOffset % 32 == 0)
                {
                    code[position++] = 0;
                    lichiiOffset += 8;
                }

                code[position++] = instruction;
                lichiiOffset += 8;
            }

            code[position++] = 0;

            return MemoryMarshal.AsBytes(code.AsSpan(0, position)).ToArray();
        }
    }
}
