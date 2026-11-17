using Ryujinx.Graphics.Gpu.Memory;
using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Ryujinx.Graphics.Gpu.Shader.Nx2
{
    /// <summary>
    /// A native (Switch 2) program ready for the shader translator.
    /// </summary>
    sealed class Nx2ShaderProgram
    {
        /// <summary>
        /// The program as it is in guest memory (header and instructions), which the shader cache
        /// compares with memory to find the program again.
        /// </summary>
        public readonly byte[] NativeCode;

        /// <summary>
        /// Maxwell code the translator reads at the program address.
        /// </summary>
        public readonly byte[] LoweredCode;

        public Nx2ShaderProgram(byte[] nativeCode, byte[] loweredCode)
        {
            NativeCode = nativeCode;
            LoweredCode = loweredCode;
        }
    }

    /// <summary>
    /// The exception for a native program the lowering cannot translate.
    /// </summary>
    sealed class Nx2ShaderException : Exception
    {
        public Nx2ShaderException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Captures and lowers native programs, once per program.
    /// </summary>
    static class Nx2ShaderPrograms
    {
        private static readonly ConcurrentDictionary<(ulong Key, ulong Address, int Stage), object> _lowered = new();

        /// <summary>
        /// Address the empty fragment program stands at (no guest memory is read there).
        /// </summary>
        public const ulong EmptyFragmentAddress = 0xffff_ffff_0000UL;

        /// <summary>
        /// A fragment program without outputs, for pipelines the game draws without one (depth-only
        /// passes): Maxwell always has one, and MoltenVK crashes creating pipelines without it.
        /// </summary>
        public static readonly Nx2ShaderProgram EmptyFragment = CreateEmptyFragment();

        private static Nx2ShaderProgram CreateEmptyFragment()
        {
            byte[] header = new byte[Nx2LoweredCode.HeaderSize];

            // SPH type 2 (pixel), version 3, shader type 5 (pixel); no outputs.
            BitConverter.TryWriteBytes(header, 2 | (3 << 5) | (5 << 10));

            Nx2LoweredProgram lowered = new();
            lowered.Instructions.Add(0xe30000000007000fUL); // EXIT

            return new Nx2ShaderProgram([], Nx2LoweredCode.Build(header, lowered));
        }

        /// <summary>
        /// Gets the program at <paramref name="address"/>, lowered for the given stage.
        /// </summary>
        /// <param name="memoryManager">GPU memory of the channel</param>
        /// <param name="address">Address of the program (its native header for graphics programs)</param>
        /// <param name="stageIndex">Graphics stage index (0 vertex to 4 fragment), or -1 for compute</param>
        /// <exception cref="Nx2ShaderException">The program cannot be lowered</exception>
        public static Nx2ShaderProgram Get(MemoryManager memoryManager, ulong address, int stageIndex, bool meshStage = false)
        {
            bool compute = stageIndex < 0;
            int headerSize = compute ? 0 : Nx2NativeProgram.GraphicsHeaderSize;

            Nx2Instruction[] code = Nx2NativeProgram.Capture(memoryManager, address, headerSize, out string error);

            if (code == null)
            {
                throw new Nx2ShaderException(error);
            }

            if (compute)
            {
                Nx2NativeProgram.Dump(memoryManager, address, 0, "cs", code);
            }

            // Task and mesh programs lower like vertex programs, keeping their stage buffer accesses.
            int loweringStage = meshStage ? MeshStageIndex : stageIndex;

            object entry = _lowered.GetOrAdd((Nx2NativeProgram.Key(code), address, loweringStage), _ => Lower(memoryManager, address, headerSize, loweringStage, code));

            return entry as Nx2ShaderProgram ?? throw new Nx2ShaderException((string)entry);
        }

        /// <summary>
        /// Stage index task and mesh programs are lowered (and cached) with.
        /// </summary>
        private const int MeshStageIndex = 10;

        private static object Lower(MemoryManager memoryManager, ulong address, int headerSize, int stageIndex, Nx2Instruction[] code)
        {
            Nx2LoweredProgram lowered = Nx2Lowering.LowerToMaxwell(code, new Nx2LoweringOptions
            {
                Geometry = stageIndex == 3,
                Tensor = true,
                NativeCodeAddress = address + (ulong)headerSize,
                VerifiedTraps = Enumerable.Range(0, code.Length).Where(i => code[i].Opcode == 0x95c).Select(i => (uint)i * 16).ToArray(),
                TessellationStage = stageIndex switch
                {
                    1 => Nx2TessellationStage.Control,
                    2 => Nx2TessellationStage.Evaluation,
                    _ => Nx2TessellationStage.None,
                },
            });

            if (!lowered.Ok)
            {
                return $"{lowered.Error} at +0x{lowered.ErrorOffset:x}";
            }

            // Task and mesh programs keep their stage buffer accesses as a pseudo instruction that only
            // a mesh pipeline can run.
            if (stageIndex != MeshStageIndex)
            {
                foreach (ulong instruction in lowered.Instructions)
                {
                    if ((instruction >> 52) == 0xefc)
                    {
                        return "task or mesh program outside a mesh pipeline";
                    }
                }
            }

            int nativeSize = headerSize + (int)lowered.SourceSize;
            byte[] nativeCode = memoryManager.GetSpan(address, nativeSize).ToArray();

            ReadOnlySpan<byte> header = headerSize == 0 ? ReadOnlySpan<byte>.Empty : nativeCode.AsSpan(0, Nx2LoweredCode.HeaderSize);

            return new Nx2ShaderProgram(nativeCode, Nx2LoweredCode.Build(header, lowered));
        }
    }
}
