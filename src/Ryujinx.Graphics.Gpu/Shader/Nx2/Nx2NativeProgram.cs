using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Gpu.Memory;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.Gpu.Shader.Nx2
{
    /// <summary>
    /// Reads native (Switch 2) programs from GPU memory.
    /// </summary>
    static class Nx2NativeProgram
    {
        /// <summary>
        /// Size of the header in front of a native graphics program; compute programs have none.
        /// </summary>
        public const int GraphicsHeaderSize = 128;

        private static readonly string _dumpDirectory = Environment.GetEnvironmentVariable("RYUJINX_NX2_SHADER_DUMP");
        private static readonly HashSet<ulong> _dumped = [];

        /// <summary>
        /// FNV-1a over the instruction bytes, the program key Lichii names dumps and overrides with.
        /// </summary>
        public static ulong Key(ReadOnlySpan<Nx2Instruction> code)
        {
            ulong hash = 14695981039346656037UL;

            foreach (byte value in MemoryMarshal.AsBytes(code))
            {
                hash = (hash ^ value) * 1099511628211UL;
            }

            return hash;
        }

        /// <summary>
        /// Reads the instructions of the program whose header starts at <paramref name="programBase"/>,
        /// up to its end.
        /// </summary>
        /// <returns>The instructions, or null with <paramref name="error"/> set</returns>
        public static Nx2Instruction[] Capture(MemoryManager memoryManager, ulong programBase, int headerSize, out string error)
        {
            List<Nx2Instruction> code = [];
            Nx2ProgramCapture capture = new();

            for (uint index = 0; index < Nx2ProgramCapture.MaximumInstructions; index++)
            {
                ulong address = programBase + (ulong)headerSize + index * 16UL;

                if (!memoryManager.IsMapped(address) || !memoryManager.IsMapped(address + 15))
                {
                    // The program may end before a branch target past its last EXIT: stop at the end
                    // of the mapping and let the lowering judge what was read.
                    if (code.Count != 0)
                    {
                        break;
                    }

                    error = $"unmapped native instruction at 0x{address:X}";

                    return null;
                }

                Nx2Instruction instruction = memoryManager.Read<Nx2Instruction>(address);

                code.Add(instruction);

                Nx2ProgramCapture.Result result = capture.Observe(instruction, index);

                if (result == Nx2ProgramCapture.Result.InvalidTarget)
                {
                    error = $"native branch outside the program at 0x{address:X}";

                    return null;
                }

                if (result == Nx2ProgramCapture.Result.Complete)
                {
                    break;
                }
            }

            error = null;

            return [.. code];
        }

        /// <summary>
        /// RYUJINX_NX2_SHADER_DUMP=&lt;directory&gt;: writes each program once as &lt;key&gt;-&lt;stage&gt;-&lt;address&gt;.bin
        /// (instructions) and, for graphics programs, .header.bin, to test the lowering offline.
        /// </summary>
        public static void Dump(MemoryManager memoryManager, ulong programBase, int headerSize, string stage, ReadOnlySpan<Nx2Instruction> code)
        {
            if (_dumpDirectory == null)
            {
                return;
            }

            ulong key = Key(code);

            lock (_dumped)
            {
                if (!_dumped.Add(key ^ programBase))
                {
                    return;
                }
            }

            try
            {
                Directory.CreateDirectory(_dumpDirectory);

                string path = Path.Combine(_dumpDirectory, $"{key:x16}-{stage}-{programBase:x}");

                File.WriteAllBytes(path + ".bin", MemoryMarshal.AsBytes(code).ToArray());

                if (headerSize != 0)
                {
                    File.WriteAllBytes(path + ".header.bin", memoryManager.GetSpan(programBase, headerSize).ToArray());
                }
            }
            catch (IOException exception)
            {
                Logger.Error?.Print(LogClass.Gpu, $"Cannot dump the native program at 0x{programBase:X}: {exception.Message}");
            }
        }
    }
}
